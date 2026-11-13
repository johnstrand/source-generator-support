using SdkCollector.Models;
using SdkCollector.Services;
using Xunit;

namespace SdkCollector.Tests;

public sealed class ReadmeUpdaterTests
{
    [Fact]
    public void UpdateReadme_IsDeterministicAndIdempotent()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"readme_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string readmePath = Path.Combine(tempDir, "README.md");
            var catalog = new CatalogDocument
            {
                Channels =
                [
                    new ChannelCatalogEntry
                    {
                        ChannelVersion = "10.0",
                        ReleaseType = "lts",
                        SupportPhase = "active",
                        FeatureBands =
                        [
                            new FeatureBandCatalogEntry
                            {
                                Band = "10.0.4xx",
                                LatestSdkVersion = "10.0.404",
                                RoslynAssemblyVersion = "5.9.0.0",
                                CodeAnalysisCSharpNuGetVersion = "5.9.0",
                                VsSupport = "Visual Studio 2026 (v18.11)",
                                MSBuildVersion = "18.11.4",
                                CSharpVersion = "14.0"
                            }
                        ]
                    }
                ]
            };

            bool firstChange = ReadmeUpdater.UpdateReadme(readmePath, catalog);
            bool secondChange = ReadmeUpdater.UpdateReadme(readmePath, catalog);

            Assert.True(firstChange);
            Assert.False(secondChange);

            string content = File.ReadAllText(readmePath);
            Assert.Contains(ReadmeUpdater.StartMarker, content);
            Assert.Contains("10.0.4xx", content);
            Assert.Contains("5.9.0", content);
            Assert.Contains(ReadmeUpdater.EndMarker, content);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
