using System.IO.Compression;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SdkCollector.Models;

namespace SdkCollector.Services;

internal sealed record InspectedSdkAssemblies(
    AssemblyVersionDetails CodeAnalysis,
    AssemblyVersionDetails CodeAnalysisCSharp,
    AssemblyVersionDetails? MSBuild,
    bool UsedFallbackFullDownload);

internal sealed partial class RemoteZipInspector
{
    private readonly HttpClient _httpClient;

    public RemoteZipInspector(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<InspectedSdkAssemblies> InspectSdkZipAsync(
        string sdkVersion,
        string zipUrl,
        string? expectedSha512Hex,
        CancellationToken ct)
    {
        try
        {
            await using BufferedHttpRangeStream rangeStream = await BufferedHttpRangeStream
                .CreateAsync(_httpClient, zipUrl, ct)
                .ConfigureAwait(false);
            using var archive = new ZipArchive(rangeStream, ZipArchiveMode.Read, leaveOpen: true);
            return InspectArchive(archive, sdkVersion, usedFallback: false);
        }
        catch (Exception ex) when (ex is RangeNotSupportedException or InvalidDataException)
        {
            return await InspectViaFullDownloadAsync(sdkVersion, zipUrl, expectedSha512Hex, ct).ConfigureAwait(false);
        }
    }

    internal static InspectedSdkAssemblies InspectArchive(ZipArchive archive, string sdkVersion, bool usedFallback)
    {
        ZipArchiveEntry codeAnalysisEntry = FindEntry(
            archive,
            $"sdk/{sdkVersion}/Roslyn/bincore/Microsoft.CodeAnalysis.dll",
            CodeAnalysisDllRegex())
            ?? throw new InvalidDataException($"Could not locate Microsoft.CodeAnalysis.dll inside SDK '{sdkVersion}' archive.");

        ZipArchiveEntry codeAnalysisCSharpEntry = FindEntry(
            archive,
            $"sdk/{sdkVersion}/Roslyn/bincore/Microsoft.CodeAnalysis.CSharp.dll",
            CodeAnalysisCSharpDllRegex())
            ?? throw new InvalidDataException($"Could not locate Microsoft.CodeAnalysis.CSharp.dll inside SDK '{sdkVersion}' archive.");

        ZipArchiveEntry? msbuildEntry = FindEntry(
            archive,
            $"sdk/{sdkVersion}/MSBuild.dll",
            MSBuildDllRegex());

        byte[] codeAnalysisBytes = ExtractAndVerifyCrc(codeAnalysisEntry);
        byte[] codeAnalysisCSharpBytes = ExtractAndVerifyCrc(codeAnalysisCSharpEntry);
        byte[]? msbuildBytes = msbuildEntry is not null ? ExtractAndVerifyCrc(msbuildEntry) : null;

        AssemblyVersionDetails codeAnalysis = PeVersionReader.ReadFromBytes(codeAnalysisBytes, "Microsoft.CodeAnalysis.dll");
        AssemblyVersionDetails codeAnalysisCSharp = PeVersionReader.ReadFromBytes(codeAnalysisCSharpBytes, "Microsoft.CodeAnalysis.CSharp.dll");
        AssemblyVersionDetails? msbuild = msbuildBytes is not null
            ? PeVersionReader.ReadFromBytes(msbuildBytes, "MSBuild.dll")
            : null;

        return new InspectedSdkAssemblies(codeAnalysis, codeAnalysisCSharp, msbuild, usedFallback);
    }

    private async Task<InspectedSdkAssemblies> InspectViaFullDownloadAsync(
        string sdkVersion,
        string zipUrl,
        string? expectedSha512Hex,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(), $"dotnet-sdk-{sdkVersion}-{Guid.NewGuid():N}.zip");
        try
        {
            using (HttpResponseMessage response = await _httpClient.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using Stream netStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using FileStream fileStream = new(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await netStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(expectedSha512Hex))
            {
                await using FileStream verifyStream = File.OpenRead(tempZip);
                byte[] hashBytes = await SHA512.HashDataAsync(verifyStream, ct).ConfigureAwait(false);
                string actualHex = Convert.ToHexString(hashBytes);
                if (!string.Equals(actualHex, expectedSha512Hex.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"SHA-512 mismatch for '{zipUrl}'. Expected {expectedSha512Hex}, got {actualHex}.");
                }
            }

            using ZipArchive archive = ZipFile.OpenRead(tempZip);
            return InspectArchive(archive, sdkVersion, usedFallback: true);
        }
        finally
        {
            try { File.Delete(tempZip); } catch { /* ignore cleanup error */ }
        }
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string exactPath, Regex fallbackRegex)
    {
        ZipArchiveEntry? regexMatch = null;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string normalized = entry.FullName.Replace('\\', '/');
            if (string.Equals(normalized, exactPath, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }

            if (regexMatch is null && fallbackRegex.IsMatch(normalized))
            {
                regexMatch = entry;
            }
        }

        return regexMatch;
    }

    private static byte[] ExtractAndVerifyCrc(ZipArchiveEntry entry)
    {
        using Stream entryStream = entry.Open();
        using var ms = new MemoryStream(entry.Length > 0 && entry.Length < int.MaxValue ? (int)entry.Length : 0);
        entryStream.CopyTo(ms);
        byte[] data = ms.ToArray();

        if (entry.Crc32 != 0)
        {
            uint actualCrc = Crc32.HashToUInt32(data);
            if (actualCrc != entry.Crc32)
            {
                throw new InvalidDataException($"CRC-32 mismatch extracting '{entry.FullName}': expected 0x{entry.Crc32:X8}, got 0x{actualCrc:X8}.");
            }
        }

        return data;
    }

    [GeneratedRegex(@"^sdk/[^/]+/Roslyn/bincore/Microsoft\.CodeAnalysis\.dll$", RegexOptions.IgnoreCase)]
    private static partial Regex CodeAnalysisDllRegex();

    [GeneratedRegex(@"^sdk/[^/]+/Roslyn/bincore/Microsoft\.CodeAnalysis\.CSharp\.dll$", RegexOptions.IgnoreCase)]
    private static partial Regex CodeAnalysisCSharpDllRegex();

    [GeneratedRegex(@"^sdk/[^/]+/MSBuild\.dll$", RegexOptions.IgnoreCase)]
    private static partial Regex MSBuildDllRegex();
}
