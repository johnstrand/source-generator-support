using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using SdkCollector.Services;
using Xunit;

namespace SdkCollector.Tests;

public sealed class RemoteZipInspectorTests
{
    [Fact]
    public async Task InspectSdkZipAsync_UsesRangeRequestsWhenSupported()
    {
        byte[] sampleDllBytes = File.ReadAllBytes(typeof(PeVersionReader).Assembly.Location);
        byte[] zipBytes = BuildFakeSdkZip("10.0.404", sampleDllBytes);

        var handler = new RangeMockHandler(zipBytes, supportRanges: true);
        using var httpClient = new HttpClient(handler);
        var inspector = new RemoteZipInspector(httpClient);

        InspectedSdkAssemblies result = await inspector.InspectSdkZipAsync(
            "10.0.404",
            "https://example.test/dotnet-sdk-10.0.404-win-x64.zip",
            expectedSha512Hex: null,
            CancellationToken.None);

        Assert.False(result.UsedFallbackFullDownload);
        Assert.Equal("1.0.0.0", result.CodeAnalysis.AssemblyVersion);
        Assert.Equal("1.0.0.0", result.CodeAnalysisCSharp.AssemblyVersion);
        Assert.NotNull(result.MSBuild);
        Assert.Equal("1.0.0.0", result.MSBuild!.AssemblyVersion);
        Assert.True(handler.RangeRequests >= 2);
        Assert.Equal(0, handler.FullRequests);
    }

    [Fact]
    public async Task InspectSdkZipAsync_FallsBackToFullDownloadAndVerifiesSha512WhenRangeUnsupported()
    {
        byte[] sampleDllBytes = File.ReadAllBytes(typeof(PeVersionReader).Assembly.Location);
        byte[] zipBytes = BuildFakeSdkZip("10.0.404", sampleDllBytes);
        string sha512 = Convert.ToHexString(SHA512.HashData(zipBytes));

        var handler = new RangeMockHandler(zipBytes, supportRanges: false);
        using var httpClient = new HttpClient(handler);
        var inspector = new RemoteZipInspector(httpClient);

        InspectedSdkAssemblies result = await inspector.InspectSdkZipAsync(
            "10.0.404",
            "https://example.test/dotnet-sdk-10.0.404-win-x64.zip",
            expectedSha512Hex: sha512,
            CancellationToken.None);

        Assert.True(result.UsedFallbackFullDownload);
        Assert.Equal("1.0.0.0", result.CodeAnalysisCSharp.AssemblyVersion);
        Assert.True(handler.FullRequests >= 1);
    }

    [Fact]
    public async Task InspectSdkZipAsync_ThrowsOnSha512MismatchInFallbackMode()
    {
        byte[] sampleDllBytes = File.ReadAllBytes(typeof(PeVersionReader).Assembly.Location);
        byte[] zipBytes = BuildFakeSdkZip("10.0.404", sampleDllBytes);

        var handler = new RangeMockHandler(zipBytes, supportRanges: false);
        using var httpClient = new HttpClient(handler);
        var inspector = new RemoteZipInspector(httpClient);

        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.InspectSdkZipAsync(
            "10.0.404",
            "https://example.test/dotnet-sdk-10.0.404-win-x64.zip",
            expectedSha512Hex: new string('0', 128),
            CancellationToken.None));
    }

    private static byte[] BuildFakeSdkZip(string sdkVersion, byte[] dllBytes)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, $"sdk/{sdkVersion}/Roslyn/bincore/Microsoft.CodeAnalysis.dll", dllBytes);
            WriteEntry(zip, $"sdk/{sdkVersion}/Roslyn/bincore/Microsoft.CodeAnalysis.CSharp.dll", dllBytes);
            WriteEntry(zip, $"sdk/{sdkVersion}/MSBuild.dll", dllBytes);
            WriteEntry(zip, "unrelated/readme.txt", "hello"u8.ToArray());
        }
        return ms.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string path, byte[] data)
    {
        ZipArchiveEntry entry = zip.CreateEntry(path, CompressionLevel.Fastest);
        using Stream s = entry.Open();
        s.Write(data);
    }

    private sealed class RangeMockHandler : HttpMessageHandler
    {
        private readonly byte[] _data;
        private readonly bool _supportRanges;

        public int RangeRequests { get; private set; }
        public int FullRequests { get; private set; }

        public RangeMockHandler(byte[] data, bool supportRanges)
        {
            _data = data;
            _supportRanges = supportRanges;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_supportRanges && request.Headers.Range?.Ranges.FirstOrDefault() is RangeItemHeaderValue range)
            {
                RangeRequests++;
                long start = range.From ?? 0;
                long end = Math.Min(_data.Length - 1, range.To ?? (_data.Length - 1));
                int len = (int)(end - start + 1);
                byte[] slice = _data.AsSpan((int)start, len).ToArray();

                var resp = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(slice)
                };
                resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, _data.Length);
                return Task.FromResult(resp);
            }

            FullRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_data)
            });
        }
    }
}
