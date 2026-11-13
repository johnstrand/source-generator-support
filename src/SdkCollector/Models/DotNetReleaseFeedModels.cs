using System.Text.Json.Serialization;

namespace SdkCollector.Models;

internal sealed class ReleasesIndexRoot
{
    [JsonPropertyName("releases-index")]
    public List<ReleaseChannelEntry> ReleasesIndex { get; set; } = [];
}

internal sealed class ReleaseChannelEntry
{
    [JsonPropertyName("channel-version")]
    public string ChannelVersion { get; set; } = "";

    [JsonPropertyName("latest-release")]
    public string LatestRelease { get; set; } = "";

    [JsonPropertyName("latest-release-date")]
    public string LatestReleaseDate { get; set; } = "";

    [JsonPropertyName("latest-sdk")]
    public string LatestSdk { get; set; } = "";

    [JsonPropertyName("release-type")]
    public string ReleaseType { get; set; } = "";

    [JsonPropertyName("support-phase")]
    public string SupportPhase { get; set; } = "";

    [JsonPropertyName("eol-date")]
    public string? EolDate { get; set; }

    [JsonPropertyName("releases.json")]
    public string ReleasesJsonUrl { get; set; } = "";
}

internal sealed class ChannelReleasesDocument
{
    [JsonPropertyName("channel-version")]
    public string ChannelVersion { get; set; } = "";

    [JsonPropertyName("latest-release")]
    public string LatestRelease { get; set; } = "";

    [JsonPropertyName("latest-release-date")]
    public string LatestReleaseDate { get; set; } = "";

    [JsonPropertyName("latest-sdk")]
    public string LatestSdk { get; set; } = "";

    [JsonPropertyName("release-type")]
    public string ReleaseType { get; set; } = "";

    [JsonPropertyName("support-phase")]
    public string SupportPhase { get; set; } = "";

    [JsonPropertyName("eol-date")]
    public string? EolDate { get; set; }

    [JsonPropertyName("releases")]
    public List<ReleaseEntry> Releases { get; set; } = [];
}

internal sealed class ReleaseEntry
{
    [JsonPropertyName("release-date")]
    public string ReleaseDate { get; set; } = "";

    [JsonPropertyName("release-version")]
    public string ReleaseVersion { get; set; } = "";

    [JsonPropertyName("security")]
    public bool Security { get; set; }

    [JsonPropertyName("release-notes")]
    public string? ReleaseNotes { get; set; }

    [JsonPropertyName("sdk")]
    public SdkEntry? Sdk { get; set; }

    [JsonPropertyName("sdks")]
    public List<SdkEntry>? Sdks { get; set; }
}

internal sealed class SdkEntry
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("version-display")]
    public string? VersionDisplay { get; set; }

    [JsonPropertyName("runtime-version")]
    public string? RuntimeVersion { get; set; }

    [JsonPropertyName("vs-version")]
    public string? VsVersion { get; set; }

    [JsonPropertyName("vs-mac-version")]
    public string? VsMacVersion { get; set; }

    [JsonPropertyName("vs-support")]
    public string? VsSupport { get; set; }

    [JsonPropertyName("vs-mac-support")]
    public string? VsMacSupport { get; set; }

    [JsonPropertyName("csharp-version")]
    public string? CSharpVersion { get; set; }

    [JsonPropertyName("fsharp-version")]
    public string? FSharpVersion { get; set; }

    [JsonPropertyName("vb-version")]
    public string? VbVersion { get; set; }

    [JsonPropertyName("files")]
    public List<ReleaseFileEntry> Files { get; set; } = [];
}

internal sealed class ReleaseFileEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("rid")]
    public string Rid { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("hash")]
    public string? Hash { get; set; }
}
