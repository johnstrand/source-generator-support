using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using NuGet.Versioning;
using SdkCollector.Models;

namespace SdkCollector.Services;

internal sealed record NuGetMatchResult(
    string? MaxStableVersion,
    string? MaxPrereleaseVersion);

internal sealed class NuGetRoslynClient
{
    private const string CodeAnalysisCommonIndexUrl = "https://api.nuget.org/v3-flatcontainer/microsoft.codeanalysis.common/index.json";
    private const string CodeAnalysisCSharpIndexUrl = "https://api.nuget.org/v3-flatcontainer/microsoft.codeanalysis.csharp/index.json";

    private readonly HttpClient _httpClient;

    public NuGetRoslynClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<NuGetPackageCatalog> RefreshCatalogAsync(
        NuGetPackageCatalog? existingCatalog,
        bool forceRecheck,
        CancellationToken ct)
    {
        List<NuGetPackageEntry> codeAnalysis = await RefreshPackageAsync(
            packageId: "microsoft.codeanalysis.common",
            dllFileName: "Microsoft.CodeAnalysis.dll",
            indexUrl: CodeAnalysisCommonIndexUrl,
            existingEntries: existingCatalog?.CodeAnalysis,
            forceRecheck: forceRecheck,
            ct: ct).ConfigureAwait(false);

        List<NuGetPackageEntry> codeAnalysisCSharp = await RefreshPackageAsync(
            packageId: "microsoft.codeanalysis.csharp",
            dllFileName: "Microsoft.CodeAnalysis.CSharp.dll",
            indexUrl: CodeAnalysisCSharpIndexUrl,
            existingEntries: existingCatalog?.CodeAnalysisCSharp,
            forceRecheck: forceRecheck,
            ct: ct).ConfigureAwait(false);

        return new NuGetPackageCatalog
        {
            CodeAnalysis = codeAnalysis,
            CodeAnalysisCSharp = codeAnalysisCSharp
        };
    }

    internal async Task<List<NuGetPackageEntry>> RefreshPackageAsync(
        string packageId,
        string dllFileName,
        string indexUrl,
        IReadOnlyList<NuGetPackageEntry>? existingEntries,
        bool forceRecheck,
        CancellationToken ct)
    {
        string json = await _httpClient.GetStringAsync(indexUrl, ct).ConfigureAwait(false);
        NuGetIndexResponse? index = JsonSerializer.Deserialize<NuGetIndexResponse>(json);
        if (index is null || index.Versions.Count == 0)
        {
            throw new InvalidDataException($"NuGet flatcontainer index for '{packageId}' returned no versions.");
        }

        Dictionary<string, NuGetPackageEntry> existingByVersion = forceRecheck || existingEntries is null
            ? new Dictionary<string, NuGetPackageEntry>(StringComparer.OrdinalIgnoreCase)
            : existingEntries.ToDictionary(e => e.Version, StringComparer.OrdinalIgnoreCase);

        var parsedVersions = index.Versions
            .Select(v => NuGetVersion.TryParse(v, out NuGetVersion? nv) ? (Raw: v, Parsed: nv) : default)
            .Where(x => x.Parsed is not null && (x.Parsed.Major > 3 || (x.Parsed.Major == 3 && x.Parsed.Minor >= 8)))
            .ToList();

        // Determine candidate versions: all stable versions + highest prerelease per (Major, Minor) when newer than stable.
        var stableVersions = parsedVersions
            .Where(x => !x.Parsed!.IsPrerelease)
            .ToList();

        var maxStableByMinor = stableVersions
            .GroupBy(x => (x.Parsed!.Major, x.Parsed.Minor))
            .ToDictionary(g => g.Key, g => g.MaxBy(x => x.Parsed!)!.Parsed!);

        var latestPrereleases = parsedVersions
            .Where(x => x.Parsed!.IsPrerelease)
            .GroupBy(x => (x.Parsed!.Major, x.Parsed.Minor))
            .Select(g => g.MaxBy(x => x.Parsed!)!)
            .Where(x => !maxStableByMinor.TryGetValue((x.Parsed!.Major, x.Parsed.Minor), out NuGetVersion? maxStable)
                        || x.Parsed > maxStable)
            .ToList();

        var targetVersions = stableVersions
            .Concat(latestPrereleases)
            .OrderBy(x => x.Parsed!)
            .ToList();

        var results = new ConcurrentBag<NuGetPackageEntry>();
        var toInspect = new List<(string Raw, NuGetVersion Parsed)>();

        foreach ((string raw, NuGetVersion? parsed) in targetVersions)
        {
            if (existingByVersion.TryGetValue(raw, out NuGetPackageEntry? cached) &&
                !string.IsNullOrWhiteSpace(cached.AssemblyVersion))
            {
                results.Add(cached);
            }
            else
            {
                toInspect.Add((raw, parsed!));
            }
        }

        if (toInspect.Count > 0)
        {
            Console.WriteLine($"Inspecting {toInspect.Count} NuGet package(s) for {packageId}...");
            await Parallel.ForEachAsync(
                toInspect,
                new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct },
                async (item, token) =>
                {
                    string verLower = item.Parsed.ToNormalizedString().ToLowerInvariant();
                    string nupkgUrl = $"https://api.nuget.org/v3-flatcontainer/{packageId}/{verLower}/{packageId}.{verLower}.nupkg";
                    byte[] nupkgBytes = await DownloadWithRetryAsync(nupkgUrl, token).ConfigureAwait(false);
                    using var ms = new MemoryStream(nupkgBytes, writable: false);
                    using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

                    ZipArchiveEntry? dllEntry = FindDllInNupkg(zip, dllFileName);
                    if (dllEntry is null)
                    {
                        throw new InvalidDataException($"Could not find '{dllFileName}' inside '{nupkgUrl}'.");
                    }

                    using Stream entryStream = dllEntry.Open();
                    using var dllMs = new MemoryStream();
                    await entryStream.CopyToAsync(dllMs, token).ConfigureAwait(false);
                    AssemblyVersionDetails details = PeVersionReader.ReadFromBytes(dllMs.ToArray(), dllFileName);

                    results.Add(new NuGetPackageEntry
                    {
                        Version = item.Raw,
                        AssemblyVersion = details.AssemblyVersion,
                        IsPrerelease = item.Parsed.IsPrerelease,
                        NuGetUrl = $"https://www.nuget.org/packages/{GetDisplayPackageName(packageId)}/{item.Raw}"
                    });
                }).ConfigureAwait(false);
        }

        return results
            .OrderBy(e => NuGetVersion.Parse(e.Version))
            .ToList();
    }

    internal static NuGetMatchResult MatchNuGetVersions(
        string sdkAssemblyVersionStr,
        IReadOnlyList<NuGetPackageEntry> packages)
    {
        Version sdkVersion = NormalizeVersion(sdkAssemblyVersionStr);

        NuGetPackageEntry? maxStable = null;
        NuGetVersion? maxStableNuGet = null;
        NuGetPackageEntry? maxPrerelease = null;
        NuGetVersion? maxPrereleaseNuGet = null;

        foreach (NuGetPackageEntry pkg in packages)
        {
            if (string.IsNullOrWhiteSpace(pkg.AssemblyVersion))
            {
                continue;
            }

            Version pkgAsmVersion = NormalizeVersion(pkg.AssemblyVersion);
            if (pkgAsmVersion > sdkVersion)
            {
                continue;
            }

            if (!NuGetVersion.TryParse(pkg.Version, out NuGetVersion? pkgNuGetVersion))
            {
                continue;
            }

            if (!pkg.IsPrerelease)
            {
                if (maxStableNuGet is null || pkgNuGetVersion > maxStableNuGet)
                {
                    maxStable = pkg;
                    maxStableNuGet = pkgNuGetVersion;
                }
            }
            else
            {
                if (maxPrereleaseNuGet is null || pkgNuGetVersion > maxPrereleaseNuGet)
                {
                    maxPrerelease = pkg;
                    maxPrereleaseNuGet = pkgNuGetVersion;
                }
            }
        }

        string? prereleaseResult = null;
        if (maxPrerelease is not null && maxPrereleaseNuGet is not null)
        {
            if (maxStableNuGet is null || maxPrereleaseNuGet > maxStableNuGet)
            {
                prereleaseResult = maxPrerelease.Version;
            }
        }

        return new NuGetMatchResult(maxStable?.Version, prereleaseResult);
    }

    internal static Version NormalizeVersion(string versionStr)
    {
        Version v = Version.Parse(versionStr);
        return new Version(
            Math.Max(0, v.Major),
            Math.Max(0, v.Minor),
            Math.Max(0, v.Build),
            Math.Max(0, v.Revision));
    }

    private static ZipArchiveEntry? FindDllInNupkg(ZipArchive zip, string dllFileName)
    {
        string preferred = $"lib/netstandard2.0/{dllFileName}";
        ZipArchiveEntry? match = zip.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName.Replace('\\', '/'), preferred, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        match = zip.Entries.FirstOrDefault(e =>
        {
            string norm = e.FullName.Replace('\\', '/');
            return norm.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) &&
                   norm.EndsWith($"/{dllFileName}", StringComparison.OrdinalIgnoreCase);
        });
        if (match is not null)
        {
            return match;
        }

        return zip.Entries.FirstOrDefault(e =>
            e.FullName.Replace('\\', '/').EndsWith($"/{dllFileName}", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetDisplayPackageName(string packageId) =>
        packageId.Equals("microsoft.codeanalysis.csharp", StringComparison.OrdinalIgnoreCase)
            ? "Microsoft.CodeAnalysis.CSharp"
            : "Microsoft.CodeAnalysis.Common";

    private async Task<byte[]> DownloadWithRetryAsync(string url, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                return await _httpClient.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(350 * Math.Pow(2, attempt)), ct).ConfigureAwait(false);
            }
        }

        return await _httpClient.GetByteArrayAsync(url, ct).ConfigureAwait(false);
    }

    private sealed class NuGetIndexResponse
    {
        [JsonPropertyName("versions")]
        public List<string> Versions { get; set; } = [];
    }
}
