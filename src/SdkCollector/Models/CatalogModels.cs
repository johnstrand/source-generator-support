using System.Text.Json;
using System.Text.Json.Serialization;

namespace SdkCollector.Models;

internal sealed class CatalogDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("lastUpdatedUtc")]
    public string LastUpdatedUtc { get; set; } = "";

    [JsonPropertyName("sourceUrls")]
    public SourceUrlsInfo SourceUrls { get; set; } = new();

    [JsonPropertyName("nuget")]
    public NuGetPackageCatalog NuGet { get; set; } = new();

    [JsonPropertyName("channels")]
    public List<ChannelCatalogEntry> Channels { get; set; } = [];

    /// <summary>
    /// Compares two catalog documents semantically while ignoring <see cref="LastUpdatedUtc"/>.
    /// </summary>
    public static bool SemanticallyEquals(CatalogDocument? left, CatalogDocument? right, JsonSerializerOptions options)
    {
        if (left is null || right is null)
        {
            return ReferenceEquals(left, right);
        }

        string? savedLeft = left.LastUpdatedUtc;
        string? savedRight = right.LastUpdatedUtc;
        try
        {
            left.LastUpdatedUtc = "";
            right.LastUpdatedUtc = "";
            string leftJson = JsonSerializer.Serialize(left, options);
            string rightJson = JsonSerializer.Serialize(right, options);
            return string.Equals(leftJson, rightJson, StringComparison.Ordinal);
        }
        finally
        {
            left.LastUpdatedUtc = savedLeft;
            right.LastUpdatedUtc = savedRight;
        }
    }
}

internal sealed class SourceUrlsInfo
{
    [JsonPropertyName("releasesIndex")]
    public string ReleasesIndex { get; set; } = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json";

    [JsonPropertyName("nugetCodeAnalysisCommon")]
    public string NuGetCodeAnalysisCommon { get; set; } = "https://api.nuget.org/v3-flatcontainer/microsoft.codeanalysis.common/index.json";

    [JsonPropertyName("nugetCodeAnalysisCSharp")]
    public string NuGetCodeAnalysisCSharp { get; set; } = "https://api.nuget.org/v3-flatcontainer/microsoft.codeanalysis.csharp/index.json";
}

internal sealed class NuGetPackageCatalog
{
    [JsonPropertyName("codeAnalysis")]
    public List<NuGetPackageEntry> CodeAnalysis { get; set; } = [];

    [JsonPropertyName("codeAnalysisCSharp")]
    public List<NuGetPackageEntry> CodeAnalysisCSharp { get; set; } = [];
}

internal sealed class NuGetPackageEntry
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("assemblyVersion")]
    public string AssemblyVersion { get; set; } = "";

    [JsonPropertyName("isPrerelease")]
    public bool IsPrerelease { get; set; }

    [JsonPropertyName("nugetUrl")]
    public string NuGetUrl { get; set; } = "";
}

internal sealed class ChannelCatalogEntry
{
    [JsonPropertyName("channelVersion")]
    public string ChannelVersion { get; set; } = "";

    [JsonPropertyName("releaseType")]
    public string ReleaseType { get; set; } = "";

    [JsonPropertyName("supportPhase")]
    public string SupportPhase { get; set; } = "";

    [JsonPropertyName("eolDate")]
    public string? EolDate { get; set; }

    [JsonPropertyName("latestRelease")]
    public string LatestRelease { get; set; } = "";

    [JsonPropertyName("latestSdk")]
    public string LatestSdk { get; set; } = "";

    [JsonPropertyName("releasesJsonUrl")]
    public string ReleasesJsonUrl { get; set; } = "";

    [JsonPropertyName("featureBands")]
    public List<FeatureBandCatalogEntry> FeatureBands { get; set; } = [];
}

internal sealed class FeatureBandCatalogEntry
{
    [JsonPropertyName("band")]
    public string Band { get; set; } = "";

    [JsonPropertyName("hasStableSdk")]
    public bool HasStableSdk { get; set; }

    [JsonPropertyName("isPrereleaseOnly")]
    public bool IsPrereleaseOnly { get; set; }

    [JsonPropertyName("latestSdkVersion")]
    public string LatestSdkVersion { get; set; } = "";

    [JsonPropertyName("latestRuntimeVersion")]
    public string? LatestRuntimeVersion { get; set; }

    [JsonPropertyName("latestReleaseDate")]
    public string? LatestReleaseDate { get; set; }

    [JsonPropertyName("roslynAssemblyVersion")]
    public string RoslynAssemblyVersion { get; set; } = "";

    [JsonPropertyName("codeAnalysisNuGetVersion")]
    public string? CodeAnalysisNuGetVersion { get; set; }

    [JsonPropertyName("codeAnalysisPrereleaseNuGetVersion")]
    public string? CodeAnalysisPrereleaseNuGetVersion { get; set; }

    [JsonPropertyName("codeAnalysisCSharpNuGetVersion")]
    public string? CodeAnalysisCSharpNuGetVersion { get; set; }

    [JsonPropertyName("codeAnalysisCSharpPrereleaseNuGetVersion")]
    public string? CodeAnalysisCSharpPrereleaseNuGetVersion { get; set; }

    [JsonPropertyName("nugetLag")]
    public bool NuGetLag { get; set; }

    [JsonPropertyName("msbuildVersion")]
    public string? MSBuildVersion { get; set; }

    [JsonPropertyName("vsVersion")]
    public string? VsVersion { get; set; }

    [JsonPropertyName("vsSupport")]
    public string? VsSupport { get; set; }

    [JsonPropertyName("csharpVersion")]
    public string? CSharpVersion { get; set; }

    [JsonPropertyName("fsharpVersion")]
    public string? FSharpVersion { get; set; }

    [JsonPropertyName("vbVersion")]
    public string? VbVersion { get; set; }

    [JsonPropertyName("sdks")]
    public List<SdkCatalogEntry> Sdks { get; set; } = [];
}

internal sealed class SdkCatalogEntry
{
    [JsonPropertyName("sdkVersion")]
    public string SdkVersion { get; set; } = "";

    [JsonPropertyName("featureBand")]
    public string FeatureBand { get; set; } = "";

    [JsonPropertyName("isPrerelease")]
    public bool IsPrerelease { get; set; }

    [JsonPropertyName("releaseDate")]
    public string ReleaseDate { get; set; } = "";

    [JsonPropertyName("releaseVersion")]
    public string ReleaseVersion { get; set; } = "";

    [JsonPropertyName("runtimeVersion")]
    public string? RuntimeVersion { get; set; }

    [JsonPropertyName("security")]
    public bool Security { get; set; }

    [JsonPropertyName("releaseNotes")]
    public string? ReleaseNotes { get; set; }

    [JsonPropertyName("vsVersion")]
    public string? VsVersion { get; set; }

    [JsonPropertyName("vsSupport")]
    public string? VsSupport { get; set; }

    [JsonPropertyName("resolvedVsVersion")]
    public string? ResolvedVsVersion { get; set; }

    [JsonPropertyName("resolvedVsSupport")]
    public string? ResolvedVsSupport { get; set; }

    [JsonPropertyName("csharpVersion")]
    public string? CSharpVersion { get; set; }

    [JsonPropertyName("fsharpVersion")]
    public string? FSharpVersion { get; set; }

    [JsonPropertyName("vbVersion")]
    public string? VbVersion { get; set; }

    [JsonPropertyName("winX64ZipUrl")]
    public string WinX64ZipUrl { get; set; } = "";

    [JsonPropertyName("winX64ZipSha512")]
    public string? WinX64ZipSha512 { get; set; }

    [JsonPropertyName("codeAnalysis")]
    public AssemblyVersionDetails CodeAnalysis { get; set; } = new();

    [JsonPropertyName("codeAnalysisCSharp")]
    public AssemblyVersionDetails CodeAnalysisCSharp { get; set; } = new();

    [JsonPropertyName("msbuild")]
    public AssemblyVersionDetails? MSBuild { get; set; }

    [JsonPropertyName("maxCodeAnalysisNuGetVersion")]
    public string? MaxCodeAnalysisNuGetVersion { get; set; }

    [JsonPropertyName("maxCodeAnalysisPrereleaseNuGetVersion")]
    public string? MaxCodeAnalysisPrereleaseNuGetVersion { get; set; }

    [JsonPropertyName("maxCodeAnalysisCSharpNuGetVersion")]
    public string? MaxCodeAnalysisCSharpNuGetVersion { get; set; }

    [JsonPropertyName("maxCodeAnalysisCSharpPrereleaseNuGetVersion")]
    public string? MaxCodeAnalysisCSharpPrereleaseNuGetVersion { get; set; }

    [JsonPropertyName("nugetLag")]
    public bool NuGetLag { get; set; }
}

internal sealed class AssemblyVersionDetails
{
    [JsonPropertyName("assemblyVersion")]
    public string AssemblyVersion { get; set; } = "";

    [JsonPropertyName("fileVersion")]
    public string? FileVersion { get; set; }

    [JsonPropertyName("informationalVersion")]
    public string? InformationalVersion { get; set; }

    [JsonPropertyName("commitSha")]
    public string? CommitSha { get; set; }
}
