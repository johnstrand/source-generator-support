using SdkCollector.Models;
using SdkCollector.Services;
using Xunit;

namespace SdkCollector.Tests;

public sealed class PeVersionReaderTests
{
    [Fact]
    public void ReadFromBytes_ExtractsVersionsFromSelfAssembly()
    {
        byte[] bytes = File.ReadAllBytes(typeof(PeVersionReader).Assembly.Location);

        AssemblyVersionDetails details = PeVersionReader.ReadFromBytes(bytes, "SdkCollector.dll");

        Assert.Equal("1.0.0.0", details.AssemblyVersion);
        Assert.False(string.IsNullOrWhiteSpace(details.FileVersion));
        Assert.False(string.IsNullOrWhiteSpace(details.InformationalVersion));
    }

    [Theory]
    [InlineData("5.12.0-1.26529.118+297acf930fb0f5c07fd4e51d78e521b4ad2e5d1f", "297acf930fb0f5c07fd4e51d78e521b4ad2e5d1f")]
    [InlineData("4.14.0+abc1234", "abc1234")]
    [InlineData("4.14.0", null)]
    [InlineData(null, null)]
    public void ExtractCommitSha_ParsesExpectedSha(string? infoVersion, string? expectedSha)
    {
        Assert.Equal(expectedSha, PeVersionReader.ExtractCommitSha(infoVersion));
    }
}
