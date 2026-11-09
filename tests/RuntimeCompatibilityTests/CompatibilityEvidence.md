# Revit 2026 portable runtime regression guard

## Run

```text
dotnet build BIMassist.csproj -c Release -p:DeployToRevit=false
dotnet run --project tests/RuntimeCompatibilityTests/RuntimeCompatibilityTests.csproj -- Release
dotnet run --project tests/RuntimeCompatibilityTests/Net8Smoke/Net8Smoke.csproj -p:UseProductionAssembly=true -- bin/Release/net8.0-windows/BIMassist.dll
```

The metadata inspector uses .NET 10 only as an inspection tool. The production DLL must target .NET 8. It never loads Revit APIs or substitutes API stubs.

`Net8Smoke` targets `net8.0-windows`, uses real WPF on an STA thread, and pins roll-forward to `LatestPatch`. It checks the actual runtime major is 8 and WPF is 8. Its production mode references the already-built DLL (without a project reference), verifies the loaded copy's SHA-256 equals the explicit artifact argument, exercises schema 1/2/3 parsing and strict missing/null/unknown/duplicate shape checks, nullable drafts, optional snapshot metadata, null write/copy guards, file roundtrips, and WPF window/position-editor construction. It checks that no Revit APIs were loaded. This is file-only smoke coverage, **not a Revit-host test**.

For parser development without a complete production build:

```text
dotnet run --project tests/RuntimeCompatibilityTests/Net8Smoke/Net8Smoke.csproj
```

This default links only `Ava/Core/*.cs` and `Ava/Views/*.cs`, deliberately excluding the API adapter directory. Its output identifies the linked test assembly; do not present this mode as verification of the production artifact.

## Parser regression evidence from this session

- Initial `.NET 8` smoke build failed with CS0117 for both newer serializer options. Replacing them with a .NET 8 metadata resolver passed the shape/migration smoke.
- A subsequent test-first write/copy check failed with `Accepted non-nullable profile name`. A `NullabilityInfoContext`-based getter guard restored those protections. The source-linked smoke then passed **676 assertions** under **.NET 8.0.28**, WPF **8.0.0.0**.
- Existing net10 suites passed: AvaMappingGroupTests (10), AvaConstantTests (7), AvaIndependentQuantityTests (7), AvaFormulaTests (69), AvaTakeoffTests (21), AvaX31Tests (11).
- Pinned API metadata/hash guard passed. Both baseline API Authenticode signatures were rechecked: **Valid**, `Autodesk, Inc.`, signer thumbprint `0932D021CDF8AF7BA3C4EF6D6434B069A12502DC`.
- The Autodesk transition article above was re-read in this session: its table names Revit **2026.5**, it describes the runtime move from .NET 8 to .NET 10, and it calls for testing existing add-ins for breaking changes. It does not prove this add-in runs in either host.
- Integration fixes: removed the implicit global WinForms import to avoid WPF dialog ambiguity; converted kilometers through the original API's meters plus explicit scaling; excluded `Ava/Revit/**` from file-only test-project globs. The complete Release build now passes with **0 errors / 974 existing warnings**. The metadata inspector verifies the production **2026.1.48.0** DLL targets .NET 8, references `System.Runtime 8.0.0.0` and both Revit APIs **26.0.10.0**, and ships no Autodesk host DLLs. The production-artifact smoke hash-checks its loaded copy and passes **678 assertions** under actual **.NET 8.0.28 / WPF 8.0.0.0**. All top-level test projects pass. This is still not an interactive Revit or target-PC acceptance.

Microsoft primary references for the compatible approach:

- https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/required-properties (`JsonPropertyInfo.IsRequired` contract metadata; constructor switch starts in .NET 9).
- https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/nullable-annotations (serialization/deserialization nullability option starts in .NET 9; collection elements require separate validation).

For reference/configuration checks before a production build is available:

```text
dotnet run --project tests/RuntimeCompatibilityTests/RuntimeCompatibilityTests.csproj -- --configuration-only
```

`--inspect <dll>...` prints assembly, framework and relevant references without enforcing compatibility. `--verify <BIMassist.dll>...` enforces the production .NET 8/reference contract on an explicit artifact (including rejecting older .NET 10 builds). The normal invocation also inspects evaluated MSBuild defaults, baseline hashes, and CopyLocal metadata; use it for release verification.

## Why original APIs, not the locally installed update

Official Autodesk sources checked in the browser:

- https://aps.autodesk.com/blog/call-preview-testing-revit-20262025-migration-net-10 (April 30, 2026): Revit 2025/2026 originally use .NET 8; existing add-ins mostly continue working after the runtime transition, with exceptions requiring testing.
- https://blog.autodesk.io/autodesk-desktop-products-2025-2026-net-10-updates/ (July 24, 2026, updated August 18): identifies Revit 2026.5 as the .NET 10 update, states no API changes from this runtime migration and warns about .NET 10 breaking changes. This is not a guarantee of every add-in's runtime behavior.
- https://aps.autodesk.com/blog/revit-automation-engine-upgrading-revit-20265-and-net-10-september-21-2026 (August 21, 2026): further confirmation of Revit 2026.5/.NET 10 and the need to validate existing applications.

The installed `C:/Program Files/Autodesk/Revit 2026/RevitAPI.runtimeconfig.json` specifies `net10.0` and Microsoft.NETCore.App / WindowsDesktop.App / AspNetCore.App 10.0.0. Installed `RevitAPI.dll` and `RevitAPIUI.dll` are **26.5.10.0**, target `.NETCoreApp,Version=v10.0`, and reference `System.Runtime 10.0.0.0`. Simply switching the add-in TFM while retaining these references does not restore .NET 8 compatibility.

## Reference provenance and reproducibility

`Autodesk.Revit.SDK [2026.0.0.9999]` is **community-distributed**, not an Autodesk-published NuGet package (nuspec author: Zhmayev Yaroslav; repository: CodeCavePro/RevitSDK). Its two selected binaries are genuine original Autodesk-signed APIs, not generated reference stubs. `Get-AuthenticodeSignature` returned **Valid** and signer `Autodesk, Inc.` for both. Both are assembly version **26.0.10.0**, target `.NETCoreApp,Version=v8.0`, and reference `System.Runtime 8.0.0.0`. The test locks their SHA-256 hashes as well as inspecting metadata.

- RevitAPI.dll SHA-256: `1C6289C879DA13AFEB37A2DAA19F53EF6E68559CC6BC48ECFA405D3E471D88FA`
- RevitAPIUI.dll SHA-256: `58A9A1D7C2AD586647908D1449A5BC64ED27C464F2479D17AF2FFF2DA264D4BE`
- Package: https://www.nuget.org/packages/Autodesk.Revit.SDK/2026.0.0.9999
- Download endpoint: https://api.nuget.org/v3-flatcontainer/autodesk.revit.sdk/2026.0.0.9999/autodesk.revit.sdk.2026.0.0.9999.nupkg

Autodesk's official SDK was also downloaded from the link on https://aps.autodesk.com/developer/overview/revit-api:
https://autodesk-adn-transfer.s3.us-west-2.amazonaws.com/ADN+Extranet/Revit/REVIT_2026_SDK.msi
Its MSI File table does **not** include RevitAPI.dll or RevitAPIUI.dll; the SDK MSI alone is insufficient to recover baseline API binaries. No vendor binaries are stored in this repository. NuGet restores compile-only references into its local cache. Package automatic assets are excluded, both explicit API references are `Private=false`, and no fallback to the developer's Revit installation exists.

This guard verifies configuration, genuine baseline reference identity, and production dependency metadata. It does not replace interactive acceptance in both an original .NET 8 Revit 2026 installation and Revit 2026.5+.
