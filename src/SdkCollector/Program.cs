using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SdkCollector.Models;
using SdkCollector.Services;

return await RunAsync(args).ConfigureAwait(false);

static async Task<int> RunAsync(string[] args)
{
    string repoRoot = Directory.GetCurrentDirectory();
    string? backfillMinChannel = null;
    bool forceRecheck = false;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--repo-root" when i + 1 < args.Length:
                repoRoot = Path.GetFullPath(args[++i]);
                break;
            case "--backfill-min-channel" when i + 1 < args.Length:
                string val = args[++i].Trim();
                if (!string.IsNullOrEmpty(val))
                {
                    backfillMinChannel = val;
                }
                break;
            case "--force-recheck":
                forceRecheck = true;
                break;
            case "--help" or "-h":
                PrintUsage();
                return 0;
            default:
                Console.Error.WriteLine($"Unknown argument: {args[i]}");
                PrintUsage();
                return 1;
        }
    }

    string dataPath = Path.Combine(repoRoot, "site", "data", "sdk-roslyn-versions.json");
    string readmePath = Path.Combine(repoRoot, "README.md");

    var jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    CatalogDocument? existingCatalog = null;
    if (File.Exists(dataPath))
    {
        string existingJson = await File.ReadAllTextAsync(dataPath, Encoding.UTF8).ConfigureAwait(false);
        existingCatalog = JsonSerializer.Deserialize<CatalogDocument>(existingJson, jsonOptions);
    }

    using var handler = new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        MaxConnectionsPerServer = 16,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };
    using var httpClient = new HttpClient(handler)
    {
        Timeout = TimeSpan.FromMinutes(10)
    };
    httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("source-generator-support-collector/1.0 (+https://github.com/johnstrand/source-generator-support)");

    var feedClient = new HttpDotNetReleaseFeedClient(httpClient);
    var zipInspector = new RemoteZipInspectorAdapter(new RemoteZipInspector(httpClient));
    var nugetClient = new NuGetRoslynClient(httpClient);
    var builder = new CatalogBuilder(feedClient, zipInspector, nugetClient, maxConcurrency: 8);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    CatalogDocument newCatalog = await builder
        .BuildAsync(existingCatalog, backfillMinChannel, forceRecheck, cts.Token)
        .ConfigureAwait(false);

    bool catalogChanged = !CatalogDocument.SemanticallyEquals(existingCatalog, newCatalog, jsonOptions);
    if (catalogChanged)
    {
        newCatalog.LastUpdatedUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);
        string serialized = JsonSerializer.Serialize(newCatalog, jsonOptions).Replace("\r\n", "\n") + "\n";
        await File.WriteAllTextAsync(dataPath, serialized, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cts.Token)
            .ConfigureAwait(false);
        Console.WriteLine($"Updated {dataPath} ({newCatalog.Channels.Sum(c => c.FeatureBands.Sum(b => b.Sdks.Count))} SDKs across {newCatalog.Channels.Count} channels).");
    }
    else
    {
        Console.WriteLine("No semantic catalog changes detected; skipping JSON write.");
    }

    bool readmeChanged = ReadmeUpdater.UpdateReadme(readmePath, newCatalog);
    Console.WriteLine(readmeChanged
        ? $"Updated {readmePath}."
        : "README.md summary table is already up to date.");

    return 0;
}

static void PrintUsage()
{
    Console.WriteLine("Usage: SdkCollector [--repo-root <path>] [--backfill-min-channel <version>] [--force-recheck]");
}
