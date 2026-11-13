using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using SdkCollector.Models;
using SdkCollector.Services;
using Xunit;

namespace SdkCollector.Tests;

public sealed class CatalogBuilderTests
{
    [Fact]
    public void SelectSdkCandidates_DropsPreviewsWhenBandHasStableAndKeepsPreviewOnlyBand()
    {
        var doc = new ChannelReleasesDocument
        {
            ChannelVersion = "11.0",
            Releases =
            [
                new ReleaseEntry
                {
                    ReleaseDate = "2026-11-10",
                    ReleaseVersion = "11.0.0",
                    Sdks =
                    [
                        MakeSdk("11.0.100"),
                        MakeSdk("11.0.200-preview.1.26501.1")
                    ]
                },
                new ReleaseEntry
                {
                    ReleaseDate = "2026-10-15",
                    ReleaseVersion = "11.0.0-rc.2",
                    Sdks =
                    [
                        MakeSdk("11.0.100-rc.2.26401.1")
                    ]
                }
            ]
        };

        List<CatalogBuilder.SdkCandidate> candidates = CatalogBuilder.SelectSdkCandidates(doc);

        Assert.Contains(candidates, c => c.Sdk.Version == "11.0.100");
        Assert.DoesNotContain(candidates, c => c.Sdk.Version == "11.0.100-rc.2.26401.1");
        Assert.Contains(candidates, c => c.Sdk.Version == "11.0.200-preview.1.26501.1");
    }

    [Fact]
    public void MatchNuGetVersions_RespectsAssemblyVersionAndPrereleaseLag()
    {
        var packages = new List<NuGetPackageEntry>
        {
            new() { Version = "4.9.0", AssemblyVersion = "4.9.0.0", IsPrerelease = false },
            new() { Version = "4.9.2", AssemblyVersion = "4.9.0.0", IsPrerelease = false },
            new() { Version = "5.9.0", AssemblyVersion = "5.9.0.0", IsPrerelease = false },
            new() { Version = "5.12.0-1.final", AssemblyVersion = "5.12.0.0", IsPrerelease = true }
        };

        NuGetMatchResult match49 = NuGetRoslynClient.MatchNuGetVersions("4.9.0.0", packages);
        Assert.Equal("4.9.2", match49.MaxStableVersion);
        Assert.Null(match49.MaxPrereleaseVersion);

        NuGetMatchResult match512 = NuGetRoslynClient.MatchNuGetVersions("5.12.0.0", packages);
        Assert.Equal("5.9.0", match512.MaxStableVersion);
        Assert.Equal("5.12.0-1.final", match512.MaxPrereleaseVersion);
    }

    [Fact]
    public async Task BuildAsync_RetainsEolChannelsAndInheritsVsSupportWithinBand()
    {
        var feedClient = new FakeFeedClient(
            index: new ReleasesIndexRoot
            {
                ReleasesIndex =
                [
                    new ReleaseChannelEntry
                    {
                        ChannelVersion = "11.0",
                        LatestRelease = "11.0.0",
                        LatestSdk = "11.0.100",
                        ReleaseType = "sts",
                        SupportPhase = "active",
                        EolDate = "2028-11-14",
                        ReleasesJsonUrl = "https://feed/11.0/releases.json"
                    },
                    new ReleaseChannelEntry
                    {
                        ChannelVersion = "9.0",
                        LatestRelease = "9.0.23",
                        LatestSdk = "9.0.321",
                        ReleaseType = "sts",
                        SupportPhase = "eol",
                        EolDate = "2026-11-10",
                        ReleasesJsonUrl = "https://feed/9.0/releases.json"
                    }
                ]
            },
            channels: new Dictionary<string, ChannelReleasesDocument>
            {
                ["https://feed/11.0/releases.json"] = new ChannelReleasesDocument
                {
                    ChannelVersion = "11.0",
                    Releases =
                    [
                        new ReleaseEntry
                        {
                            ReleaseDate = "2026-11-10",
                            ReleaseVersion = "11.0.0",
                            Sdks =
                            [
                                MakeSdk("11.0.102", vsVersion: "", vsSupport: "", csharpVersion: "15.0"),
                                MakeSdk("11.0.100", vsVersion: "18.12.0", vsSupport: "Visual Studio 2026 (v18.12)", csharpVersion: "15.0")
                            ]
                        }
                    ]
                }
            });

        var zipInspector = new FakeZipInspector(new InspectedSdkAssemblies(
            CodeAnalysis: new AssemblyVersionDetails { AssemblyVersion = "5.12.0.0", FileVersion = "5.1200.26.53018", InformationalVersion = "5.12.0+abcdef1" },
            CodeAnalysisCSharp: new AssemblyVersionDetails { AssemblyVersion = "5.12.0.0", FileVersion = "5.1200.26.53018", InformationalVersion = "5.12.0+abcdef1" },
            MSBuild: new AssemblyVersionDetails { AssemblyVersion = "15.1.0.0", FileVersion = "18.12.2.53018", InformationalVersion = "18.12.2" },
            UsedFallbackFullDownload: false));

        var nugetHandler = new StaticJsonHandler(new Dictionary<string, string>
        {
            ["https://api.nuget.org/v3-flatcontainer/microsoft.codeanalysis.common/index.json"] = """{"versions":["5.9.0"]}""",
            ["https://api.nuget.org/v3-flatcontainer/microsoft.codeanalysis.csharp/index.json"] = """{"versions":["5.9.0"]}"""
        });
        using var nugetHttp = new HttpClient(nugetHandler);
        var nugetClient = new NuGetRoslynClient(nugetHttp);

        var existing = new CatalogDocument
        {
            LastUpdatedUtc = "2026-11-12T00:00:00Z",
            NuGet = new NuGetPackageCatalog
            {
                CodeAnalysis = [new NuGetPackageEntry { Version = "5.9.0", AssemblyVersion = "5.9.0.0", IsPrerelease = false }],
                CodeAnalysisCSharp = [new NuGetPackageEntry { Version = "5.9.0", AssemblyVersion = "5.9.0.0", IsPrerelease = false }]
            },
            Channels =
            [
                new ChannelCatalogEntry
                {
                    ChannelVersion = "9.0",
                    ReleaseType = "sts",
                    SupportPhase = "active",
                    FeatureBands =
                    [
                        new FeatureBandCatalogEntry
                        {
                            Band = "9.0.3xx",
                            Sdks =
                            [
                                new SdkCatalogEntry
                                {
                                    SdkVersion = "9.0.321",
                                    FeatureBand = "9.0.3xx",
                                    CodeAnalysis = new AssemblyVersionDetails { AssemblyVersion = "4.14.0.0" },
                                    CodeAnalysisCSharp = new AssemblyVersionDetails { AssemblyVersion = "4.14.0.0" }
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var builder = new CatalogBuilder(feedClient, zipInspector, nugetClient);
        CatalogDocument result = await builder.BuildAsync(existing, backfillMinChannel: null, forceRecheck: false, CancellationToken.None);

        Assert.Equal(2, result.Channels.Count);
        ChannelCatalogEntry ch9 = result.Channels.Single(c => c.ChannelVersion == "9.0");
        Assert.Equal("eol", ch9.SupportPhase);
        Assert.Equal("2026-11-10", ch9.EolDate);

        ChannelCatalogEntry ch11 = result.Channels.Single(c => c.ChannelVersion == "11.0");
        FeatureBandCatalogEntry band11 = Assert.Single(ch11.FeatureBands);
        Assert.Equal("11.0.102", band11.LatestSdkVersion);
        Assert.Equal("Visual Studio 2026 (v18.12)", band11.VsSupport);
        Assert.Equal("18.12.2", band11.VsVersion);
        Assert.True(band11.NuGetLag);
        Assert.Equal("5.9.0", band11.CodeAnalysisCSharpNuGetVersion);

        // Verify semantic equality ignores LastUpdatedUtc.
        var jsonOpts = new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        CatalogDocument secondRun = await builder.BuildAsync(result, backfillMinChannel: null, forceRecheck: false, CancellationToken.None);
        secondRun.LastUpdatedUtc = "2099-01-01T00:00:00Z";
        Assert.True(CatalogDocument.SemanticallyEquals(result, secondRun, jsonOpts));
    }

    private static SdkEntry MakeSdk(
        string version,
        string? vsVersion = "18.12.0",
        string? vsSupport = "Visual Studio 2026 (v18.12)",
        string? csharpVersion = "15.0") => new()
        {
            Version = version,
            VsVersion = vsVersion,
            VsSupport = vsSupport,
            CSharpVersion = csharpVersion,
            Files =
            [
                new ReleaseFileEntry
                {
                    Name = $"dotnet-sdk-{version}-win-x64.zip",
                    Rid = "win-x64",
                    Url = $"https://builds.test/{version}.zip"
                }
            ]
        };

    private sealed class FakeFeedClient : IDotNetReleaseFeedClient
    {
        private readonly ReleasesIndexRoot _index;
        private readonly Dictionary<string, ChannelReleasesDocument> _channels;

        public FakeFeedClient(ReleasesIndexRoot index, Dictionary<string, ChannelReleasesDocument> channels)
        {
            _index = index;
            _channels = channels;
        }

        public Task<ReleasesIndexRoot> GetReleasesIndexAsync(CancellationToken ct) => Task.FromResult(_index);

        public Task<ChannelReleasesDocument> GetChannelReleasesAsync(string releasesJsonUrl, CancellationToken ct) =>
            Task.FromResult(_channels[releasesJsonUrl]);
    }

    private sealed class FakeZipInspector : ISdkZipInspector
    {
        private readonly InspectedSdkAssemblies _result;

        public FakeZipInspector(InspectedSdkAssemblies result) => _result = result;

        public Task<InspectedSdkAssemblies> InspectSdkZipAsync(
            string sdkVersion,
            string zipUrl,
            string? expectedSha512Hex,
            CancellationToken ct) => Task.FromResult(_result);
    }

    private sealed class StaticJsonHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses;

        public StaticJsonHandler(Dictionary<string, string> responses) => _responses = responses;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            if (_responses.TryGetValue(url, out string? body))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body)
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
