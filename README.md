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
| .NET Band | Latest SDK | Support | Max NuGet (`Microsoft.CodeAnalysis.CSharp`) | SDK `AssemblyVersion` | VS / MSBuild | C# |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| `11.0.1xx` | `11.0.100` | STS (active) | [`5.9.0`](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/5.9.0) *(awaiting newer NuGet)* | `5.12.0.0` | Visual Studio 2026 (v18.12) (MSBuild `18.12.2`) | 15.0 |
| `10.0.4xx` | `10.0.404` | LTS (active) | [`5.9.0`](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/5.9.0) | `5.9.0.0` | Visual Studio 2026 (v18.11) (MSBuild `18.9.15`) | 14.0 |
| `10.0.3xx` | `10.0.303` | LTS (active) | [`5.6.0`](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/5.6.0) | `5.6.0.0` | Visual Studio 2026 (v18.8) (MSBuild `18.6.14`) | 14.0 |
| `10.0.2xx` | `10.0.204` | LTS (active) | [`5.3.0`](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/5.3.0) | `5.3.0.0` | Visual Studio 2026 (v18.5) (MSBuild `18.3.3`) | 14.0 |
| `10.0.1xx` | `10.0.115` | LTS (active) | [`5.0.0`](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/5.0.0) | `5.0.0.0` | Visual Studio 2026 (v18.5) (MSBuild `18.0.37`) | 14.0 |
| `9.0.3xx` | `9.0.321` | EOL (2026-11-10) | [`4.14.0`](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/4.14.0) | `4.14.0.0` | Visual Studio 2022 (v17.14) (MSBuild `17.14.60`) | 13.0 |
| `9.0.2xx` | `9.0.205` | EOL (2026-11-10) | [`4.13.0`](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/4.13.0) | `4.13.0.0` | Visual Studio 2022 (v17.13) (MSBuild `17.13.25`) | 13.0 |
| `9.0.1xx` | `9.0.124` | EOL (2026-11-10) | [`4.12.0`](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/4.12.0) | `4.12.0.0` | Visual Studio 2026 (v17.12) (MSBuild `17.12.77`) | 13.0 |
| `8.0.4xx` | `8.0.428` | EOL (2026-11-10) | [`4.11.0`](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/4.11.0) | `4.11.0.0` | Visual Studio 2022 (v17.14) (MSBuild `17.11.71`) | 12.0 |
| `8.0.3xx` | `8.0.319` | EOL (2026-11-10) | [`4.10.0`](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/4.10.0) | `4.10.0.0` | Visual Studio 2022 (v17.10) (MSBuild `17.10.46`) | 12.0 |
| `8.0.2xx` | `8.0.206` | EOL (2026-11-10) | [`4.9.2`](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/4.9.2) | `4.9.0.0` | Visual Studio 2022 (v17.9) (MSBuild `17.9.8`) | 12.0 |
| `8.0.1xx` | `8.0.134` | EOL (2026-11-10) | [`4.8.0`](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/4.8.0) | `4.8.0.0` | Visual Studio 2022 (v17.12) (MSBuild `17.8.64`) | 12.0 |
| `7.0.4xx` | `7.0.410` | EOL (2024-05-14) | [`4.7.0`](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/4.7.0) | `4.7.0.0` | Visual Studio 2022 (v17.7) (MSBuild `17.7.6`) | 11.0 |
| `7.0.3xx` | `7.0.317` | EOL (2024-05-14) | [`4.6.0`](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/4.6.0) | `4.6.0.0` | Visual Studio 2022 (v17.6) (MSBuild `17.6.11`) | 11.0 |
| `7.0.2xx` | `7.0.203` | EOL (2024-05-14) | [`4.5.0`](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/4.5.0) | `4.5.0.0` | Visual Studio 2022 (v17.5) (MSBuild `17.5.1`) | 11.0 |
| `7.0.1xx` | `7.0.120` | EOL (2024-05-14) | [`4.4.0`](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/4.4.0) | `4.4.0.0` | Visual Studio 2022 (v17.4) (MSBuild `17.3.4`) | 11.0 |
| `6.0.4xx` | `6.0.428` | EOL (2024-11-12) | [`4.3.1`](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/4.3.1) | `4.3.0.0` | Visual Studio 2022 (v17.3) (MSBuild `17.3.4`) | 10.0 |
| `6.0.3xx` | `6.0.321` | EOL (2024-11-12) | [`4.2.0`](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/4.2.0) | `4.2.0.0` | Visual Studio 2022 (v17.2) (MSBuild `17.2.2`) | 10.0 |
| `6.0.2xx` | `6.0.203` | EOL (2024-11-12) | [`4.1.0`](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/4.1.0) | `4.1.0.0` | Visual Studio 2022 (v17.1) (MSBuild `17.1.1`) | 10.0 |
| `6.0.1xx` | `6.0.136` | EOL (2024-11-12) | [`4.0.1`](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/4.0.1) | `4.0.0.0` | Visual Studio 2022 (v17.0) (MSBuild `17.0.4`) | 10.0 |
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
