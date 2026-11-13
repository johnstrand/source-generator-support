using System.Collections.Concurrent;
using System.Text.Json;
using NuGet.Versioning;
using SdkCollector.Models;

namespace SdkCollector.Services;

internal interface ISdkZipInspector
{
    Task<InspectedSdkAssemblies> InspectSdkZipAsync(
        string sdkVersion,
        string zipUrl,
        string? expectedSha512Hex,
        CancellationToken ct);
}

internal sealed class RemoteZipInspectorAdapter : ISdkZipInspector
{
    private readonly RemoteZipInspector _inner;

    public RemoteZipInspectorAdapter(RemoteZipInspector inner) => _inner = inner;

    public Task<InspectedSdkAssemblies> InspectSdkZipAsync(
        string sdkVersion,
        string zipUrl,
        string? expectedSha512Hex,
        CancellationToken ct) =>
        _inner.InspectSdkZipAsync(sdkVersion, zipUrl, expectedSha512Hex, ct);
}

internal interface IDotNetReleaseFeedClient
{
    Task<ReleasesIndexRoot> GetReleasesIndexAsync(CancellationToken ct);
    Task<ChannelReleasesDocument> GetChannelReleasesAsync(string releasesJsonUrl, CancellationToken ct);
}

internal sealed class HttpDotNetReleaseFeedClient : IDotNetReleaseFeedClient
{
    public const string ReleasesIndexUrl = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json";
    private readonly HttpClient _httpClient;

    public HttpDotNetReleaseFeedClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ReleasesIndexRoot> GetReleasesIndexAsync(CancellationToken ct)
    {
        string json = await _httpClient.GetStringAsync(ReleasesIndexUrl, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ReleasesIndexRoot>(json)
            ?? throw new InvalidDataException("Failed to deserialize releases-index.json.");
    }

    public async Task<ChannelReleasesDocument> GetChannelReleasesAsync(string releasesJsonUrl, CancellationToken ct)
    {
        string json = await _httpClient.GetStringAsync(releasesJsonUrl, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ChannelReleasesDocument>(json)
            ?? throw new InvalidDataException($"Failed to deserialize '{releasesJsonUrl}'.");
    }
}

internal sealed class CatalogBuilder
{
    private readonly IDotNetReleaseFeedClient _feedClient;
    private readonly ISdkZipInspector _zipInspector;
    private readonly NuGetRoslynClient _nugetClient;
    private readonly int _maxConcurrency;

    public CatalogBuilder(
        IDotNetReleaseFeedClient feedClient,
        ISdkZipInspector zipInspector,
        NuGetRoslynClient nugetClient,
        int maxConcurrency = 8)
    {
        _feedClient = feedClient;
        _zipInspector = zipInspector;
        _nugetClient = nugetClient;
        _maxConcurrency = Math.Max(1, maxConcurrency);
    }

    public async Task<CatalogDocument> BuildAsync(
        CatalogDocument? existingCatalog,
        string? backfillMinChannel,
        bool forceRecheck,
        CancellationToken ct)
    {
        Task<ReleasesIndexRoot> indexTask = _feedClient.GetReleasesIndexAsync(ct);
        Task<NuGetPackageCatalog> nugetTask = _nugetClient.RefreshCatalogAsync(
            existingCatalog?.NuGet,
            forceRecheck,
            ct);

        await Task.WhenAll(indexTask, nugetTask).ConfigureAwait(false);

        ReleasesIndexRoot indexRoot = indexTask.Result;
        NuGetPackageCatalog nugetCatalog = nugetTask.Result;

        Version? minBackfillVer = !string.IsNullOrWhiteSpace(backfillMinChannel)
            ? ParseChannelVersion(backfillMinChannel)
            : null;

        Dictionary<string, ChannelCatalogEntry> existingChannels = existingCatalog?.Channels
            .ToDictionary(c => c.ChannelVersion, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ChannelCatalogEntry>(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, SdkCatalogEntry> existingSdkByVersion = forceRecheck || existingCatalog is null
            ? new Dictionary<string, SdkCatalogEntry>(StringComparer.OrdinalIgnoreCase)
            : existingCatalog.Channels
                .SelectMany(c => c.FeatureBands)
                .SelectMany(b => b.Sdks)
                .GroupBy(s => s.SdkVersion, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var channelTasks = new List<(ReleaseChannelEntry IndexEntry, Task<ChannelReleasesDocument> DocTask)>();
        var retainedChannels = new List<ChannelCatalogEntry>();

        foreach (ReleaseChannelEntry indexEntry in indexRoot.ReleasesIndex)
        {
            bool isEol = string.Equals(indexEntry.SupportPhase, "eol", StringComparison.OrdinalIgnoreCase);
            bool inBackfillScope = minBackfillVer is not null &&
                                   ParseChannelVersion(indexEntry.ChannelVersion) >= minBackfillVer;

            if (!isEol || inBackfillScope)
            {
                channelTasks.Add((indexEntry, _feedClient.GetChannelReleasesAsync(indexEntry.ReleasesJsonUrl, ct)));
            }
            else if (existingChannels.TryGetValue(indexEntry.ChannelVersion, out ChannelCatalogEntry? existingChannel))
            {
                // Retain previously collected EOL channel and update mutable channel metadata + NuGet mappings.
                ChannelCatalogEntry updated = RefreshRetainedChannel(existingChannel, indexEntry, nugetCatalog);
                retainedChannels.Add(updated);
            }
        }

        await Task.WhenAll(channelTasks.Select(t => t.DocTask)).ConfigureAwait(false);

        var pendingInspections = new ConcurrentDictionary<string, PendingSdkWork>(StringComparer.OrdinalIgnoreCase);
        var fetchedChannelModels = new List<(ReleaseChannelEntry IndexEntry, List<SdkCandidate> Candidates)>();

        foreach ((ReleaseChannelEntry indexEntry, Task<ChannelReleasesDocument> docTask) in channelTasks)
        {
            ChannelReleasesDocument doc = docTask.Result;
            List<SdkCandidate> candidates = SelectSdkCandidates(doc);
            fetchedChannelModels.Add((indexEntry, candidates));

            foreach (SdkCandidate candidate in candidates)
            {
                if (!existingSdkByVersion.TryGetValue(candidate.Sdk.Version, out SdkCatalogEntry? cached) ||
                    string.IsNullOrWhiteSpace(cached.CodeAnalysis.AssemblyVersion) ||
                    string.IsNullOrWhiteSpace(cached.CodeAnalysisCSharp.AssemblyVersion))
                {
                    pendingInspections.TryAdd(
                        candidate.Sdk.Version,
                        new PendingSdkWork(candidate.Sdk.Version, candidate.ZipFile.Url, candidate.ZipFile.Hash));
                }
            }
        }

        var inspectedMap = new ConcurrentDictionary<string, InspectedSdkAssemblies>(StringComparer.OrdinalIgnoreCase);
        if (!pendingInspections.IsEmpty)
        {
            List<PendingSdkWork> workItems = pendingInspections.Values
                .OrderByDescending(w => NuGetVersion.Parse(w.SdkVersion))
                .ToList();

            int total = workItems.Count;
            int completed = 0;
            Console.WriteLine($"Inspecting {total} SDK archive(s) via HTTP Range...");

            await Parallel.ForEachAsync(
                workItems,
                new ParallelOptions { MaxDegreeOfParallelism = _maxConcurrency, CancellationToken = ct },
                async (work, token) =>
                {
                    InspectedSdkAssemblies inspected = await _zipInspector
                        .InspectSdkZipAsync(work.SdkVersion, work.ZipUrl, work.ExpectedSha512Hex, token)
                        .ConfigureAwait(false);

                    inspectedMap[work.SdkVersion] = inspected;
                    int done = Interlocked.Increment(ref completed);
                    if (done == total || done % 20 == 0 || inspected.UsedFallbackFullDownload)
                    {
                        string mode = inspected.UsedFallbackFullDownload ? " (full-zip fallback)" : "";
                        Console.WriteLine($"[{done}/{total}] Inspected {work.SdkVersion} -> Roslyn {inspected.CodeAnalysisCSharp.AssemblyVersion}{mode}");
                    }
                }).ConfigureAwait(false);
        }

        var finalChannels = new List<ChannelCatalogEntry>(retainedChannels);

        foreach ((ReleaseChannelEntry indexEntry, List<SdkCandidate> candidates) in fetchedChannelModels)
        {
            var sdkEntries = new List<SdkCatalogEntry>(candidates.Count);
            foreach (SdkCandidate candidate in candidates)
            {
                InspectedSdkAssemblies? inspected = null;
                if (inspectedMap.TryGetValue(candidate.Sdk.Version, out InspectedSdkAssemblies? fresh))
                {
                    inspected = fresh;
                }
                else if (existingSdkByVersion.TryGetValue(candidate.Sdk.Version, out SdkCatalogEntry? cached))
                {
                    inspected = new InspectedSdkAssemblies(cached.CodeAnalysis, cached.CodeAnalysisCSharp, cached.MSBuild, UsedFallbackFullDownload: false);
                }

                if (inspected is null)
                {
                    throw new InvalidOperationException($"Missing inspection result for SDK '{candidate.Sdk.Version}'.");
                }

                sdkEntries.Add(BuildSdkEntry(candidate, inspected, nugetCatalog));
            }

            List<FeatureBandCatalogEntry> bands = GroupIntoFeatureBands(sdkEntries);
            finalChannels.Add(new ChannelCatalogEntry
            {
                ChannelVersion = indexEntry.ChannelVersion,
                ReleaseType = indexEntry.ReleaseType,
                SupportPhase = indexEntry.SupportPhase,
                EolDate = NullIfEmpty(indexEntry.EolDate),
                LatestRelease = indexEntry.LatestRelease,
                LatestSdk = indexEntry.LatestSdk,
                ReleasesJsonUrl = indexEntry.ReleasesJsonUrl,
                FeatureBands = bands
            });
        }

        finalChannels = finalChannels
            .OrderByDescending(c => ParseChannelVersion(c.ChannelVersion))
            .ToList();

        return new CatalogDocument
        {
            SchemaVersion = 1,
            LastUpdatedUtc = existingCatalog?.LastUpdatedUtc ?? "",
            SourceUrls = new SourceUrlsInfo(),
            NuGet = nugetCatalog,
            Channels = finalChannels
        };
    }

    internal static List<SdkCandidate> SelectSdkCandidates(ChannelReleasesDocument doc)
    {
        var byVersion = new Dictionary<string, SdkCandidate>(StringComparer.OrdinalIgnoreCase);

        foreach (ReleaseEntry release in doc.Releases)
        {
            IEnumerable<SdkEntry> sdks = release.Sdks is { Count: > 0 }
                ? release.Sdks
                : release.Sdk is not null
                    ? [release.Sdk]
                    : [];

            foreach (SdkEntry sdk in sdks)
            {
                if (string.IsNullOrWhiteSpace(sdk.Version))
                {
                    continue;
                }

                ReleaseFileEntry? winX64Zip = sdk.Files.FirstOrDefault(f =>
                    string.Equals(f.Rid, "win-x64", StringComparison.OrdinalIgnoreCase) &&
                    f.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

                if (winX64Zip is null || string.IsNullOrWhiteSpace(winX64Zip.Url))
                {
                    continue;
                }

                if (!NuGetVersion.TryParse(sdk.Version, out NuGetVersion? parsedSdkVersion))
                {
                    continue;
                }

                string band = ComputeFeatureBand(parsedSdkVersion);
                var candidate = new SdkCandidate(release, sdk, parsedSdkVersion, band, winX64Zip);

                if (byVersion.TryGetValue(sdk.Version, out SdkCandidate? existing))
                {
                    byVersion[sdk.Version] = MergeDuplicateSdkCandidate(existing, candidate);
                }
                else
                {
                    byVersion[sdk.Version] = candidate;
                }
            }
        }

        var selected = new List<SdkCandidate>();
        foreach (IGrouping<string, SdkCandidate> bandGroup in byVersion.Values.GroupBy(c => c.FeatureBand, StringComparer.OrdinalIgnoreCase))
        {
            bool hasStable = bandGroup.Any(c => !c.ParsedVersion.IsPrerelease);
            IEnumerable<SdkCandidate> kept = hasStable
                ? bandGroup.Where(c => !c.ParsedVersion.IsPrerelease)
                : bandGroup;

            selected.AddRange(kept);
        }

        return selected
            .OrderByDescending(c => c.ParsedVersion)
            .ToList();
    }

    private static SdkCandidate MergeDuplicateSdkCandidate(SdkCandidate existing, SdkCandidate incoming)
    {
        // Prefer the entry with the newer release date; retain non-empty metadata fields from either.
        bool incomingNewer = string.CompareOrdinal(incoming.Release.ReleaseDate, existing.Release.ReleaseDate) >= 0;
        SdkCandidate primary = incomingNewer ? incoming : existing;
        SdkCandidate secondary = incomingNewer ? existing : incoming;

        SdkEntry mergedSdk = new()
        {
            Version = primary.Sdk.Version,
            VersionDisplay = NullIfEmpty(primary.Sdk.VersionDisplay) ?? NullIfEmpty(secondary.Sdk.VersionDisplay),
            RuntimeVersion = NullIfEmpty(primary.Sdk.RuntimeVersion) ?? NullIfEmpty(secondary.Sdk.RuntimeVersion),
            VsVersion = NullIfEmpty(primary.Sdk.VsVersion) ?? NullIfEmpty(secondary.Sdk.VsVersion),
            VsMacVersion = NullIfEmpty(primary.Sdk.VsMacVersion) ?? NullIfEmpty(secondary.Sdk.VsMacVersion),
            VsSupport = NullIfEmpty(primary.Sdk.VsSupport) ?? NullIfEmpty(secondary.Sdk.VsSupport),
            VsMacSupport = NullIfEmpty(primary.Sdk.VsMacSupport) ?? NullIfEmpty(secondary.Sdk.VsMacSupport),
            CSharpVersion = NullIfEmpty(primary.Sdk.CSharpVersion) ?? NullIfEmpty(secondary.Sdk.CSharpVersion),
            FSharpVersion = NullIfEmpty(primary.Sdk.FSharpVersion) ?? NullIfEmpty(secondary.Sdk.FSharpVersion),
            VbVersion = NullIfEmpty(primary.Sdk.VbVersion) ?? NullIfEmpty(secondary.Sdk.VbVersion),
            Files = primary.Sdk.Files
        };

        return primary with { Sdk = mergedSdk };
    }

    private static ChannelCatalogEntry RefreshRetainedChannel(
        ChannelCatalogEntry existingChannel,
        ReleaseChannelEntry indexEntry,
        NuGetPackageCatalog nugetCatalog)
    {
        List<SdkCatalogEntry> refreshedSdks = existingChannel.FeatureBands
            .SelectMany(b => b.Sdks)
            .Select(sdk =>
            {
                SdkCatalogEntry clone = CloneSdkWithUpdatedNuGet(sdk, nugetCatalog);
                return clone;
            })
            .ToList();

        return new ChannelCatalogEntry
        {
            ChannelVersion = indexEntry.ChannelVersion,
            ReleaseType = indexEntry.ReleaseType,
            SupportPhase = indexEntry.SupportPhase,
            EolDate = NullIfEmpty(indexEntry.EolDate),
            LatestRelease = indexEntry.LatestRelease,
            LatestSdk = indexEntry.LatestSdk,
            ReleasesJsonUrl = indexEntry.ReleasesJsonUrl,
            FeatureBands = GroupIntoFeatureBands(refreshedSdks)
        };
    }

    private static SdkCatalogEntry CloneSdkWithUpdatedNuGet(SdkCatalogEntry sdk, NuGetPackageCatalog nugetCatalog)
    {
        (NuGetMatchResult caMatch, NuGetMatchResult csMatch, bool nugetLag) = MatchNuGet(
            sdk.CodeAnalysis.AssemblyVersion,
            sdk.CodeAnalysisCSharp.AssemblyVersion,
            nugetCatalog);

        return new SdkCatalogEntry
        {
            SdkVersion = sdk.SdkVersion,
            FeatureBand = sdk.FeatureBand,
            IsPrerelease = sdk.IsPrerelease,
            ReleaseDate = sdk.ReleaseDate,
            ReleaseVersion = sdk.ReleaseVersion,
            RuntimeVersion = sdk.RuntimeVersion,
            Security = sdk.Security,
            ReleaseNotes = sdk.ReleaseNotes,
            VsVersion = sdk.VsVersion,
            VsSupport = sdk.VsSupport,
            ResolvedVsVersion = sdk.ResolvedVsVersion,
            ResolvedVsSupport = sdk.ResolvedVsSupport,
            CSharpVersion = sdk.CSharpVersion,
            FSharpVersion = sdk.FSharpVersion,
            VbVersion = sdk.VbVersion,
            WinX64ZipUrl = sdk.WinX64ZipUrl,
            WinX64ZipSha512 = sdk.WinX64ZipSha512,
            CodeAnalysis = sdk.CodeAnalysis,
            CodeAnalysisCSharp = sdk.CodeAnalysisCSharp,
            MSBuild = sdk.MSBuild,
            MaxCodeAnalysisNuGetVersion = caMatch.MaxStableVersion,
            MaxCodeAnalysisPrereleaseNuGetVersion = caMatch.MaxPrereleaseVersion,
            MaxCodeAnalysisCSharpNuGetVersion = csMatch.MaxStableVersion,
            MaxCodeAnalysisCSharpPrereleaseNuGetVersion = csMatch.MaxPrereleaseVersion,
            NuGetLag = nugetLag
        };
    }

    private static SdkCatalogEntry BuildSdkEntry(
        SdkCandidate candidate,
        InspectedSdkAssemblies inspected,
        NuGetPackageCatalog nugetCatalog)
    {
        (NuGetMatchResult caMatch, NuGetMatchResult csMatch, bool nugetLag) = MatchNuGet(
            inspected.CodeAnalysis.AssemblyVersion,
            inspected.CodeAnalysisCSharp.AssemblyVersion,
            nugetCatalog);

        return new SdkCatalogEntry
        {
            SdkVersion = candidate.Sdk.Version,
            FeatureBand = candidate.FeatureBand,
            IsPrerelease = candidate.ParsedVersion.IsPrerelease,
            ReleaseDate = candidate.Release.ReleaseDate,
            ReleaseVersion = candidate.Release.ReleaseVersion,
            RuntimeVersion = NullIfEmpty(candidate.Sdk.RuntimeVersion),
            Security = candidate.Release.Security,
            ReleaseNotes = NullIfEmpty(candidate.Release.ReleaseNotes),
            VsVersion = NullIfEmpty(candidate.Sdk.VsVersion),
            VsSupport = NullIfEmpty(candidate.Sdk.VsSupport),
            CSharpVersion = NullIfEmpty(candidate.Sdk.CSharpVersion),
            FSharpVersion = NullIfEmpty(candidate.Sdk.FSharpVersion),
            VbVersion = NullIfEmpty(candidate.Sdk.VbVersion),
            WinX64ZipUrl = candidate.ZipFile.Url,
            WinX64ZipSha512 = NullIfEmpty(candidate.ZipFile.Hash),
            CodeAnalysis = inspected.CodeAnalysis,
            CodeAnalysisCSharp = inspected.CodeAnalysisCSharp,
            MSBuild = inspected.MSBuild,
            MaxCodeAnalysisNuGetVersion = caMatch.MaxStableVersion,
            MaxCodeAnalysisPrereleaseNuGetVersion = caMatch.MaxPrereleaseVersion,
            MaxCodeAnalysisCSharpNuGetVersion = csMatch.MaxStableVersion,
            MaxCodeAnalysisCSharpPrereleaseNuGetVersion = csMatch.MaxPrereleaseVersion,
            NuGetLag = nugetLag
        };
    }

    internal static List<FeatureBandCatalogEntry> GroupIntoFeatureBands(IEnumerable<SdkCatalogEntry> sdks)
    {
        var bands = new List<FeatureBandCatalogEntry>();

        foreach (IGrouping<string, SdkCatalogEntry> group in sdks.GroupBy(s => s.FeatureBand, StringComparer.OrdinalIgnoreCase))
        {
            List<SdkCatalogEntry> orderedSdks = group
                .OrderByDescending(s => NuGetVersion.Parse(s.SdkVersion))
                .ToList();

            ResolveBandFallbacks(orderedSdks);

            SdkCatalogEntry latest = orderedSdks[0];
            bool hasStable = orderedSdks.Any(s => !s.IsPrerelease);

            bands.Add(new FeatureBandCatalogEntry
            {
                Band = group.Key,
                HasStableSdk = hasStable,
                IsPrereleaseOnly = !hasStable,
                LatestSdkVersion = latest.SdkVersion,
                LatestRuntimeVersion = latest.RuntimeVersion,
                LatestReleaseDate = NullIfEmpty(latest.ReleaseDate),
                RoslynAssemblyVersion = latest.CodeAnalysisCSharp.AssemblyVersion,
                CodeAnalysisNuGetVersion = latest.MaxCodeAnalysisNuGetVersion,
                CodeAnalysisPrereleaseNuGetVersion = latest.MaxCodeAnalysisPrereleaseNuGetVersion,
                CodeAnalysisCSharpNuGetVersion = latest.MaxCodeAnalysisCSharpNuGetVersion,
                CodeAnalysisCSharpPrereleaseNuGetVersion = latest.MaxCodeAnalysisCSharpPrereleaseNuGetVersion,
                NuGetLag = latest.NuGetLag,
                MSBuildVersion = FormatThreePartVersion(latest.MSBuild?.FileVersion),
                VsVersion = latest.ResolvedVsVersion,
                VsSupport = latest.ResolvedVsSupport,
                CSharpVersion = orderedSdks.Select(s => s.CSharpVersion).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
                FSharpVersion = orderedSdks.Select(s => s.FSharpVersion).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
                VbVersion = orderedSdks.Select(s => s.VbVersion).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
                Sdks = orderedSdks
            });
        }

        return bands
            .OrderByDescending(b => ParseFeatureBandOrderKey(b.Band))
            .ToList();
    }

    private static void ResolveBandFallbacks(List<SdkCatalogEntry> orderedSdksDesc)
    {
        string? latestNonEmptyVsSupport = orderedSdksDesc
            .Select(s => s.VsSupport)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        string? latestNonEmptyVsVersion = orderedSdksDesc
            .Select(s => s.VsVersion)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        // Walk descending; carry forward the closest known vs-support from earlier/later SDKs in the band.
        string? nearestKnownVsSupport = latestNonEmptyVsSupport;
        for (int i = 0; i < orderedSdksDesc.Count; i++)
        {
            SdkCatalogEntry sdk = orderedSdksDesc[i];
            if (!string.IsNullOrWhiteSpace(sdk.VsSupport))
            {
                nearestKnownVsSupport = sdk.VsSupport;
            }

            string? fallbackSupport = nearestKnownVsSupport ?? latestNonEmptyVsSupport;
            sdk.ResolvedVsSupport = NullIfEmpty(sdk.VsSupport) ?? fallbackSupport;

            string? msbuildThreePart = FormatThreePartVersion(sdk.MSBuild?.FileVersion);
            sdk.ResolvedVsVersion = NullIfEmpty(sdk.VsVersion)
                ?? msbuildThreePart
                ?? latestNonEmptyVsVersion;
        }
    }

    private static (NuGetMatchResult CodeAnalysis, NuGetMatchResult CodeAnalysisCSharp, bool NuGetLag) MatchNuGet(
        string codeAnalysisAssemblyVersion,
        string codeAnalysisCSharpAssemblyVersion,
        NuGetPackageCatalog nugetCatalog)
    {
        NuGetMatchResult caMatch = NuGetRoslynClient.MatchNuGetVersions(codeAnalysisAssemblyVersion, nugetCatalog.CodeAnalysis);
        NuGetMatchResult csMatch = NuGetRoslynClient.MatchNuGetVersions(codeAnalysisCSharpAssemblyVersion, nugetCatalog.CodeAnalysisCSharp);

        Version sdkVer = NuGetRoslynClient.NormalizeVersion(codeAnalysisCSharpAssemblyVersion);
        bool lag = true;
        if (!string.IsNullOrWhiteSpace(csMatch.MaxStableVersion) &&
            NuGetVersion.TryParse(csMatch.MaxStableVersion, out NuGetVersion? stablePkg))
        {
            lag = stablePkg.Major < sdkVer.Major ||
                  (stablePkg.Major == sdkVer.Major && stablePkg.Minor < sdkVer.Minor);
        }

        return (caMatch, csMatch, lag);
    }

    internal static string ComputeFeatureBand(NuGetVersion sdkVersion) =>
        $"{sdkVersion.Major}.{sdkVersion.Minor}.{sdkVersion.Patch / 100}xx";

    private static (int Major, int Minor, int BandNumber) ParseFeatureBandOrderKey(string band)
    {
        string[] parts = band.Split('.');
        if (parts.Length >= 3 &&
            int.TryParse(parts[0], out int major) &&
            int.TryParse(parts[1], out int minor))
        {
            string third = parts[2].Replace("xx", "", StringComparison.OrdinalIgnoreCase);
            int bandNum = int.TryParse(third, out int b) ? b : 0;
            return (major, minor, bandNum);
        }
        return (0, 0, 0);
    }

    internal static Version ParseChannelVersion(string channelVersion)
    {
        if (Version.TryParse(channelVersion.Trim(), out Version? v))
        {
            return v;
        }
        return new Version(0, 0);
    }

    internal static string? FormatThreePartVersion(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return null;
        }

        string trimmed = rawVersion.Trim();
        int spaceIdx = trimmed.IndexOf(' ');
        if (spaceIdx > 0)
        {
            trimmed = trimmed[..spaceIdx];
        }

        if (Version.TryParse(trimmed, out Version? v))
        {
            return v.Build >= 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";
        }

        return trimmed;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal sealed record SdkCandidate(
        ReleaseEntry Release,
        SdkEntry Sdk,
        NuGetVersion ParsedVersion,
        string FeatureBand,
        ReleaseFileEntry ZipFile);

    private sealed record PendingSdkWork(string SdkVersion, string ZipUrl, string? ExpectedSha512Hex);
}
