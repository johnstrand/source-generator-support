using System.Text;
using SdkCollector.Models;

namespace SdkCollector.Services;

internal static class ReadmeUpdater
{
    public const string StartMarker = "<!-- SUMMARY_TABLE_START -->";
    public const string EndMarker = "<!-- SUMMARY_TABLE_END -->";

    public static string BuildSummaryTableMarkdown(CatalogDocument catalog)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| .NET Band | Latest SDK | Support | Max NuGet (`Microsoft.CodeAnalysis.CSharp`) | SDK `AssemblyVersion` | VS / MSBuild | C# |");
        sb.AppendLine("| :--- | :--- | :--- | :--- | :--- | :--- | :--- |");

        foreach (ChannelCatalogEntry channel in catalog.Channels)
        {
            string support = string.Equals(channel.SupportPhase, "eol", StringComparison.OrdinalIgnoreCase)
                ? $"EOL ({channel.EolDate ?? "ended"})"
                : $"{channel.ReleaseType.ToUpperInvariant()} ({channel.SupportPhase})";

            foreach (FeatureBandCatalogEntry band in channel.FeatureBands)
            {
                string nugetCol = FormatNuGetColumn(band);
                string vsCol = FormatVsColumn(band);
                string csharpCol = string.IsNullOrWhiteSpace(band.CSharpVersion) ? "—" : band.CSharpVersion;

                sb.Append("| `").Append(band.Band).Append("` | `").Append(band.LatestSdkVersion).Append("` | ")
                  .Append(support).Append(" | ")
                  .Append(nugetCol).Append(" | `")
                  .Append(band.RoslynAssemblyVersion).Append("` | ")
                  .Append(vsCol).Append(" | ")
                  .Append(csharpCol).AppendLine(" |");
            }
        }

        return sb.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    public static bool UpdateReadme(string readmePath, CatalogDocument catalog)
    {
        string tableMarkdown = BuildSummaryTableMarkdown(catalog);
        string replacementBlock = $"{StartMarker}\n{tableMarkdown}\n{EndMarker}";

        string existing = (File.Exists(readmePath)
            ? File.ReadAllText(readmePath, Encoding.UTF8)
            : CreateDefaultReadmeTemplate()).Replace("\r\n", "\n");

        int startIdx = existing.IndexOf(StartMarker, StringComparison.Ordinal);
        int endIdx = existing.IndexOf(EndMarker, StringComparison.Ordinal);

        string updated;
        if (startIdx >= 0 && endIdx > startIdx)
        {
            int afterEnd = endIdx + EndMarker.Length;
            updated = string.Concat(existing[..startIdx], replacementBlock, existing[afterEnd..]);
        }
        else
        {
            updated = $"{existing.TrimEnd('\n')}\n\n## Compatibility Summary\n\n{replacementBlock}\n";
        }

        updated = updated.Replace("\r\n", "\n");
        if (!updated.EndsWith('\n'))
        {
            updated += "\n";
        }

        if (File.Exists(readmePath) && string.Equals(existing, updated, StringComparison.Ordinal))
        {
            return false;
        }

        File.WriteAllText(readmePath, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return true;
    }

    private static string FormatNuGetColumn(FeatureBandCatalogEntry band)
    {
        if (string.IsNullOrWhiteSpace(band.CodeAnalysisCSharpNuGetVersion))
        {
            return band.CodeAnalysisCSharpPrereleaseNuGetVersion is not null
                ? $"`{band.CodeAnalysisCSharpPrereleaseNuGetVersion}` (prerelease)"
                : "—";
        }

        string link = $"[`{band.CodeAnalysisCSharpNuGetVersion}`](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/{band.CodeAnalysisCSharpNuGetVersion})";
        if (band.NuGetLag)
        {
            return band.CodeAnalysisCSharpPrereleaseNuGetVersion is not null
                ? $"{link} *(awaiting NuGet; pre `{band.CodeAnalysisCSharpPrereleaseNuGetVersion}`)*"
                : $"{link} *(awaiting newer NuGet)*";
        }

        return link;
    }

    private static string FormatVsColumn(FeatureBandCatalogEntry band)
    {
        if (!string.IsNullOrWhiteSpace(band.VsSupport) && !string.IsNullOrWhiteSpace(band.MSBuildVersion))
        {
            return $"{band.VsSupport} (MSBuild `{band.MSBuildVersion}`)";
        }

        if (!string.IsNullOrWhiteSpace(band.VsSupport))
        {
            return band.VsSupport;
        }

        if (!string.IsNullOrWhiteSpace(band.VsVersion))
        {
            return $"VS / MSBuild `{band.VsVersion}`";
        }

        return string.IsNullOrWhiteSpace(band.MSBuildVersion) ? "—" : $"MSBuild `{band.MSBuildVersion}`";
    }

    private static string CreateDefaultReadmeTemplate() =>
        """
        # .NET SDK → Roslyn (`Microsoft.CodeAnalysis`) Compatibility Matrix

        **Interactive Table & Multi-Targeting Helper:** https://johnstrand.github.io/source-generator-support/

        Automated daily tracker mapping official `.NET` SDK releases and feature bands to the exact `Microsoft.CodeAnalysis.dll`, `Microsoft.CodeAnalysis.CSharp.dll`, `MSBuild.dll`, and highest installable `Microsoft.CodeAnalysis*` NuGet package versions.

        ## How It Works

        1. Fetches `releases-index.json` and per-channel `releases.json` from `builds.dotnet.microsoft.com`.
        2. Inspects the `win-x64` `.zip` archives using **HTTP `Range` (`206 Partial Content`)** requests (reading only the ZIP Central Directory and the target DLLs, ~4.5 MB per SDK instead of ~330 MB, with automatic full-download + SHA-512 fallback).
        3. Reads PE metadata (`AssemblyVersion`, `FileVersion`, `InformationalVersion`, and Roslyn commit SHA) in-memory via `System.Reflection.Metadata.PEReader`.
        4. Cross-references NuGet.org (`Microsoft.CodeAnalysis` and `Microsoft.CodeAnalysis.CSharp`) so patch releases like `4.9.2` and NuGet publication lags (when a brand-new .NET SDK ships before the matching Roslyn NuGet package is published) are surfaced accurately.
        5. Runs daily via GitHub Actions, committing changes and redeploying GitHub Pages only when data changes.

        ## Summary Matrix

        <!-- SUMMARY_TABLE_START -->
        <!-- SUMMARY_TABLE_END -->

        ## Running Locally

        ```bash
        # Normal daily update (scans active non-EOL channels, retains historical channels)
        dotnet run --project src/SdkCollector -c Release -- --repo-root .

        # One-time backfill down to .NET 6.0
        dotnet run --project src/SdkCollector -c Release -- --repo-root . --backfill-min-channel 6.0
        ```

        ## Raw Dataset

        - [`site/data/sdk-roslyn-versions.json`](site/data/sdk-roslyn-versions.json)
        """;
}
