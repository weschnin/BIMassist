# BIMassist MCP – Phase-0-Integrationsplan

> **Für Hermes:** Nach Freigabe Phase 1 testgetrieben und strikt phasenweise umsetzen. Keine Phase überspringen; nach jeder Phase Build, relevante Tests, Bericht und Freigabepunkt beachten.

**Ziel:** Einen lokalen, agentenunabhängigen MCP-Server für Hermes Agent und OpenAI Codex als separat installierbares BIMassist-MCP-Add-on entwickeln, ohne BIMassist Base an MCP zu koppeln.

**Architektur:** Ein Revit-freier STDIO-MCP-Server spricht über ein versioniertes JSON-Protokoll und eine benutzer-/prozessgebundene Windows Named Pipe mit einem separaten Revit-Bridge-Add-in. Nur die Bridge referenziert Revit 2026; sämtliche Revit-Zugriffe laufen über eine serialisierte Request-Queue und genau einen langlebigen `ExternalEvent` im gültigen Revit-Kontext. Schreibzugriffe verwenden ausschließlich Plan/Apply mit Revision, Plan-Hash, Ablaufzeit, Freigabe und Idempotenz.

**Tech Stack:** .NET 8 (`net8.0`/`net8.0-windows`), x64, Autodesk Revit 2026 API, offizielles C# MCP SDK (`ModelContextProtocol`, bei Implementierungsbeginn zentral gepinnt), `System.Text.Json`, Microsoft.Extensions.Hosting/Logging, Windows Named Pipes, xUnit.

---

## 1. Phase-0-Status und Schutzbereich

- Repository: `C:\Users\wesch\source\repos\BIMassist`
- Branch: `Entwicklung`, Stand `7e6fb1a`, Tracking `origin/Entwicklung`
- Vorhandene Nutzeränderungen, die weder verworfen noch überschrieben, gestaged oder in MCP-Commits aufgenommen werden dürfen:
  - `M App.cs`
  - `M BIMassist.csproj`
  - `M Commands/SolidsToNewFamilyCommand.cs`
  - `M Core/FamilyGeometryTools.cs`
  - `?? Commands/CleanSectionsOnSheetsCommand.cs`
- `App.cs` und `Commands/CleanSectionsOnSheetsCommand.cs` sind ausdrücklich benutzereigene, MCP-fremde Änderungen.
- Die offene Revit-Laufzeitprüfung des Thin-Solid-Geometriepfads bleibt ein separater Arbeitsstrang. MCP-Arbeiten ändern oder refaktorieren diesen Pfad nicht.
- In Phase 0 wurde kein Produktcode geändert, nichts gestaged, committed, gepusht, released oder in Revit deployed.
- `git diff --check`: ohne Befund.
- Verifikation ohne Deployment: `dotnet msbuild BIMassist.csproj /t:Compile /v:minimal` → Exitcode `0`. Ein vollständiges `dotnet build` wurde in Phase 0 bewusst nicht erneut gestartet, weil der vorhandene `PostBuild` ungefragt nach `%APPDATA%\Autodesk\Revit\Addins\2026` kopiert.

## 2. Repository-Karte / Ist-Architektur

### 2.1 Projekte und Build

| Bestandteil | Ist-Zustand | Bewertung für MCP |
|---|---|---|
| `BIMassist.sln` | Enthält nur `BIMassist.csproj`; Debug/Release Any CPU | Beibehalten und später um MCP-Projekte plus Solution-Filter ergänzen |
| `BIMassist.csproj` | `net8.0-windows`, WPF, WinForms, Nullable, x64; Version `2026.1.6` im Arbeitsbaum | Bleibt BIMassist Base; darf keine MCP-SDK-/Server-Abhängigkeit erhalten |
| `BIMassist - Kopie.csproj` | Nicht in der Solution; abweichender PostBuild nach `C:\ProgramData` | Nicht als Build-/Release-Eingang verwenden; gesonderte spätere Bereinigungsentscheidung |
| Revit-Referenzen | `RevitAPI.dll` und `RevitAPIUI.dll` aus Revit 2026; Dateien lokal vorhanden | Bridge übernimmt dieselbe API-Version, aber mit konfigurierbarem `RevitInstallDir` und `Private=false` |
| `BIMassist.addin` | Application-Add-in, Einstieg `BIMassist.App`, eigene AddInId | Base-Manifest bleibt unverändert; Bridge erhält eigenes Manifest und eigene AddInId |
| PostBuild | Kopiert jeden Build nach benutzerspezifischem Addins-Pfad | Später auf explizites `DeployToRevit=true` umstellen; nicht heimlich in Phase 1 |
| Tests / CI / Packaging | Keine Testprojekte, keine Workflows, kein Installer/Staging | Für MCP neu und getrennt aufbauen |
| Repository-Anweisungen | Kein `AGENTS.md`; README nur Überschrift; alter Copilot-Hinweis | Projektanweisung und ADRs werden maßgebliche MCP-Dokumentation |

Installiertes SDK: `.NET SDK 10.0.301`; alle Produktprojekte zielen weiterhin auf .NET 8.

### 2.2 Vorhandene Revit-Lifecycle-Muster

- `App.cs`
  - `OnStartup(...)`: Ribbon, `ViewActivated`, Dockable Panes.
  - `OnShutdown(...)`: Event-Abmeldung, ViewModel-Cleanup, Schließen ausgewählter modeless Fenster.
- `Commands/ElementCoordinatesCommand.cs`, `Views/SectionBoxExternalEventHandler.cs`, `ViewModels/MetadataViewModel.cs`, `ViewModels/MainViewModel.cs`, `Services/ExternalEvents.cs` enthalten Beispiele für `ExternalEvent`.
- Diese Klassen sind konzeptionelle Referenzen, aber keine stabile Bridge-Bibliothek: Sie sind an UI, statische Zustände, aktive Dokumente und konkrete ViewModels gekoppelt.
- Die MCP-Bridge wird deshalb als eigenes `IExternalApplication`-Add-in gebaut und nicht in `BIMassist.App` eingehängt.

### 2.3 Vorhandene Parameterlogik

| Thema | Vorhandene Stellen | Wiederverwendung |
|---|---|---|
| FamilyManager / EditFamily | `ViewModels/BgkAssignExternalEventHandler.cs`, `Helpers/FamilyFileHelper.cs`, `Views/MaterialFavoritesWindow.xaml.cs`, `Core/FamilyGeometryTools.cs` | API-Muster studieren; nicht direkt referenzieren |
| Shared-Parameter-Datei lesen | `ViewModels/MainViewModel.cs` mit `OpenSharedParameterFile()` | Nur Teilmuster; kein sicherer Wechsel/Wiederherstellen von `SharedParametersFilename` |
| Projektbindungen | Keine `BindingMap`-/`Insert`-/`ReInsert`-Implementierung | Vollständig neu im Bridge-Adapter |
| Parameterwerte | Einzelne String-/Material-/Double-Schreiber und Read-back-Muster | Spezielle Verifikation übernehmen, generischen typisierten Codec neu bauen |
| Einheiten | Viele direkte `UnitUtils`-Aufrufe, teils `UnitFormatUtils.Format` | Zentralen Spec-/Unit-Adapter neu bauen |
| Transaktionen | Viele lokale `Transaction`-Blöcke, kein allgemeiner Runner | Atomaren Plan-Runner neu bauen |
| Family Reload | `JtFamilyLoadOptions` überschreibt pauschal | Für MCP nicht unverändert verwenden; Reload-Konflikte explizit planen |

**Ergebnis:** Es existiert keine ausreichend entkoppelte Parameter-Domänenschicht. Version 0.1 soll BIMassist Base deshalb nicht refaktorieren oder referenzieren. Revit-spezifische Adapter werden zunächst im Bridge-Projekt implementiert. Eine spätere gemeinsame Core-Bibliothek ist nur nach eigenem ADR und nachgewiesenem Nutzen vorgesehen.

## 3. Zielstruktur mit konkreten Projekten und Dateien

```text
BIMassist/
  BIMassist.csproj                         # bestehende Base, unverlagert
  BIMassist.sln                            # später alle Projekte; keine zweite Solution
  BIMassist.Base.slnf                      # nur BIMassist.csproj
  BIMassist.Mcp.slnf                       # nur MCP-Projekte und MCP-Tests

  mcp/
    Directory.Build.props                  # MCP-Versionen, Nullable, TreatWarningsAsErrors für neue Projekte
    Directory.Packages.props               # zentral gepinnte MCP-/Testpakete
    src/
      BIMassist.Mcp.Contracts/
        BIMassist.Mcp.Contracts.csproj
        Protocol/ProtocolVersions.cs
        Protocol/BridgeRequest.cs
        Protocol/BridgeResponse.cs
        Protocol/BridgeError.cs
        Sessions/SessionDescriptor.cs
        Documents/DocumentDescriptor.cs
        Parameters/ParameterIdentity.cs
        Parameters/ParameterMetadata.cs
        Parameters/ParameterValue.cs
        Parameters/ParameterTarget.cs
        Changes/ChangePlan.cs
        Changes/ChangeOperation.cs
        Changes/ApplyChangePlanRequest.cs
        Serialization/McpJsonContext.cs

      BIMassist.Mcp.Server/
        BIMassist.Mcp.Server.csproj
        Program.cs
        Configuration/ServerOptions.cs
        Bridge/IBridgeClient.cs
        Bridge/NamedPipeBridgeClient.cs
        Bridge/SessionDiscovery.cs
        Tools/StatusTools.cs
        Tools/DocumentTools.cs
        Tools/FamilyTools.cs
        Tools/ParameterTools.cs
        Tools/SharedParameterTools.cs
        Tools/BindingTools.cs
        Tools/SnapshotTools.cs
        Tools/PlanTools.cs
        Tools/ApplyTools.cs
        Validation/RequestValidators.cs
        Logging/StdioSafeLogging.cs

      BIMassist.Mcp.RevitBridge/
        BIMassist.Mcp.RevitBridge.csproj
        App.cs
        BIMassist.Mcp.RevitBridge.addin
        Lifecycle/BridgeRuntime.cs
        Transport/PipeEndpointName.cs
        Transport/NamedPipeListener.cs
        Transport/CurrentUserPipeSecurity.cs
        Dispatch/BridgeRequestQueue.cs
        Dispatch/BridgeExternalEventHandler.cs
        Dispatch/QueuedBridgeRequest.cs
        Sessions/RevitSessionRegistry.cs
        Documents/DocumentKeyFactory.cs
        Documents/DocumentRevisionService.cs
        Adapters/StatusAdapter.cs
        Adapters/DocumentContextAdapter.cs
        Adapters/FamilyQueryAdapter.cs
        Adapters/ParameterQueryAdapter.cs
        Adapters/SharedDefinitionAdapter.cs
        Adapters/ProjectBindingAdapter.cs
        Adapters/ParameterValueAdapter.cs
        Adapters/UnitValueAdapter.cs
        Changes/ChangePlanStore.cs
        Changes/ChangePlanner.cs
        Changes/ChangePlanExecutor.cs
        Changes/IdempotencyStore.cs
        Families/FamilyEditSession.cs
        Families/McpFamilyLoadOptions.cs
        Transactions/RevitTransactionRunner.cs

    tests/
      BIMassist.Mcp.Contracts.Tests/
        BIMassist.Mcp.Contracts.Tests.csproj
      BIMassist.Mcp.Server.Tests/
        BIMassist.Mcp.Server.Tests.csproj
      BIMassist.Mcp.RevitBridge.Tests/
        BIMassist.Mcp.RevitBridge.Tests.csproj
      BIMassist.Mcp.IntegrationTests/
        BIMassist.Mcp.IntegrationTests.csproj

  packaging/
    base/
      BaseFileList.json
    mcp-addon/
      AddonManifest.json
      hermes.config.example.yaml
      codex.config.example.toml
      McpAddonFileList.json
    bundle/
      BundleFileList.json
    scripts/
      Stage-Base.ps1
      Stage-McpAddon.ps1
      Stage-Bundle.ps1
      Validate-Artifacts.ps1
      Write-Checksums.ps1

  docs/mcp/
    architecture.md
    protocol.md
    tools.md
    install-hermes.md
    install-codex.md
    troubleshooting.md
    testing.md
    adr/
      0001-modular-monorepo-addon.md
      0002-stdio-mcp-named-pipe-bridge.md
      0003-plan-apply-write-model.md
```

Pfadnamen dürfen bei der Implementierung nur geändert werden, wenn bestehende Repository-Konventionen oder ein technischer Konflikt dies verlangen; die Projekt- und Abhängigkeitsgrenzen sind verbindlich.

## 4. Abhängigkeitsgraph

### Vorher

```text
BIMassist.sln
└── BIMassist.csproj
    ├── RevitAPI.dll
    ├── RevitAPIUI.dll
    ├── WPF
    └── Windows Forms
```

### Nachher

```text
BIMassist.csproj (Base)
├── RevitAPI / RevitAPIUI
└── KEINE Referenz auf BIMassist.Mcp.* oder ModelContextProtocol

BIMassist.Mcp.Contracts
└── plattformneutrale .NET-Basispakete

BIMassist.Mcp.Server
├── BIMassist.Mcp.Contracts
├── ModelContextProtocol
└── Microsoft.Extensions.Hosting/Logging

BIMassist.Mcp.RevitBridge
├── BIMassist.Mcp.Contracts
├── RevitAPI / RevitAPIUI
└── KEINE Referenz auf ModelContextProtocol oder BIMassist.Mcp.Server

Tests
├── jeweiliges Produktprojekt
└── Testdoubles/Fixtures; keine produktiven RVT/RFA-Dateien verändern
```

**Nachweis der Base-Unabhängigkeit:**

1. `dotnet build BIMassist.csproj -c Release -v:minimal -p:DeployToRevit=false`
2. Artefaktprüfung: Base-Staging enthält keine Datei mit `Mcp`, kein `ModelContextProtocol*.dll`, keine Agentenkonfiguration und kein Bridge-Manifest.
3. Base-Smoke-Test in Revit 2026 bei nicht installiertem MCP-Add-on.
4. `BIMassist.csproj` enthält keine `ProjectReference` oder `PackageReference` auf MCP-Projekte/-SDK.

## 5. Laufzeit- und Protokolldesign

### 5.1 Prozessfluss

```text
Hermes Agent / OpenAI Codex
  -> MCP über STDIO
BIMassist.Mcp.Server.exe
  -> versionierte JSON-Nachrichten über lokale Windows Named Pipe
BIMassist.Mcp.RevitBridge.addin
  -> thread-sichere Queue + ExternalEvent.Raise()
BridgeExternalEventHandler.Execute(UIApplication)
  -> Revit API / Transaktion im gültigen Revit-Kontext
```

### 5.2 Bridge-Regeln

- Pipe-Endpunkt enthält Windows-Benutzer-SID und Revit-Prozess-ID; keine Netzwerk-Pipe.
- Restriktive ACL: aktueller Benutzer und erforderliche Systemkonten, kein `Everyone`/anonymer Zugriff.
- Genau eine Queue und ein langlebiges `ExternalEvent` pro Revit-Prozess.
- Pipe-Thread darf nur deserialisieren, Schema/Größe validieren, enqueuen und auf eine DTO-Antwort warten.
- Keine langlebigen `Document`, `UIDocument`, `Element`, `Parameter`, `Reference`, `GeometryObject` oder `UIApplication` außerhalb des ExternalEvent-Aufrufs.
- Jeder dokumentbezogene Request enthält `sessionId`, `documentKey` und bei Änderungen `expectedRevision`.
- Request-/Response-Korrelation über `requestId`; Schreibwiederholungen zusätzlich über `idempotencyKey`.
- Timeouts markieren Requests eindeutig als `queued`, `running`, `completed`, `cancelled-before-start` oder `outcome-unknown`; ein Client-Timeout löst niemals automatisch einen zweiten Schreibvorgang aus.
- OnShutdown-Reihenfolge: Annahme stoppen → Cancellation setzen → Listener schließen → Queue ablehnen/abschließen → Event-Abonnements lösen → Ressourcen freigeben. Danach keine Revit-Callbacks aus Hintergrundthreads.

### 5.3 Antwort- und Fehlervertrag

```json
{
  "requestId": "...",
  "success": true,
  "result": {},
  "warnings": [],
  "error": null,
  "documentRevision": "...",
  "durationMs": 0
}
```

Mindestens folgende stabile Fehlercodes werden in Contracts definiert und getestet:

- `REVIT_NOT_RUNNING`
- `NO_COMPATIBLE_SESSION`
- `NO_ACTIVE_DOCUMENT`
- `SESSION_NOT_FOUND`
- `DOCUMENT_NOT_FOUND`
- `DOCUMENT_CHANGED`
- `DOCUMENT_NOT_WRITABLE`
- `TARGET_NOT_FOUND`
- `AMBIGUOUS_TARGET`
- `PARAMETER_READ_ONLY`
- `FORMULA_CONTROLLED`
- `TYPE_MISMATCH`
- `UNIT_ERROR`
- `WORKSHARING_OWNERSHIP`
- `PLAN_NOT_FOUND`
- `PLAN_EXPIRED`
- `PLAN_HASH_MISMATCH`
- `IDEMPOTENCY_CONFLICT`
- `REQUEST_TIMEOUT`
- `TRANSACTION_FAILED`
- `PROTOCOL_VERSION_MISMATCH`

## 6. Parameter-Domänenmodell

### 6.1 Identität

Priorität für `ParameterIdentity`:

1. Shared-Parameter-GUID.
2. Stabile Built-in-/API-Kennung aus Revit 2026.
3. `ParameterElement`-/Definition-ID plus Owner-Kontext und Definitionsmerkmale.
4. Sichtbarer Name nur zur Suche/Anzeige; niemals stiller Schreib-Fallback.

### 6.2 Typisierte Werte

`ParameterValue` ist eine diskriminierte DTO-Repräsentation für:

- `String`
- `Integer`
- `Double` mit `specTypeId`, deklarierter Eingabeeinheit, internem Rohwert und formatiertem Wert
- `ElementId` mit stabiler Zielbeschreibung/UniqueId, wenn verfügbar
- Ja/Nein als eigener logischer Wert auf Integer-Basis
- Material-, Typ- und Elementreferenzen als eindeutig validierte Referenzobjekte
- `hasValue`, `isReadOnly`, `formula`, blockierender Grund

Keine generische Übergabe an `Parameter.SetValueString`. Mehrdeutige Namen werden abgelehnt.

### 6.3 Plan/Apply

- Read-Tools öffnen keine Transaktion.
- Plan-Tools verändern das Modell nicht.
- `ChangePlan` enthält vorher/nachher, Zielidentitäten, Warnungen, `expectedRevision`, Ablaufzeit und kanonischen Plan-Hash.
- Plan-Speicher ist sitzungs-/dokumentgebunden und begrenzt.
- Apply akzeptiert ausschließlich `planId`, Plan-Hash, `expectedRevision`, `idempotencyKey` und explizite Freigabe-Metadaten.
- Direkt vor Apply werden Sitzung, Dokument, Revision, Zielidentität, Schreibbarkeit, Worksharing und Definition erneut geprüft.
- Standard ist vollständige Atomizität. Bei einem Fehler Rollback der gesamten Transaktion/TransactionGroup; keine stillen Teilerfolge.
- Ergebnis enthält ausschließlich tatsächlich verifizierte Änderungen und die neue Dokumentrevision.

## 7. Toolkatalog Version 0.1

Identisch für Hermes und Codex:

1. `revit_get_status`
2. `revit_get_document_context`
3. `revit_list_families`
4. `revit_get_family_metadata`
5. `revit_list_parameters`
6. `revit_get_parameter_metadata`
7. `revit_list_shared_definitions`
8. `revit_list_project_bindings`
9. `revit_get_parameter_values`
10. `revit_export_metadata_snapshot`
11. `revit_plan_set_parameter_values`
12. `revit_plan_add_shared_parameter_to_family`
13. `revit_plan_bind_shared_parameter`
14. `revit_apply_change_plan`

Listen erhalten Filter, deterministische Sortierung, `pageSize` mit hartem Maximum und Cursor/Pagination. Version 0.1 enthält keinen beliebigen Code-Runner, kein generisches Löschen, kein `mass_update` und kein stilles Laden/Speichern/Überschreiben von RVT/RFA-Dateien.

## 8. Build-, Test- und Release-Targets

### 8.1 Geplante Targets

| Target | Zweck | Ergebnis |
|---|---|---|
| `BuildBase` | Base ohne Deployment bauen | `BIMassist.dll`; keine MCP-/Autodesk-Laufzeitdateien |
| `BuildMcpContracts` | Verträge bauen | Revit-freie Contracts-DLL |
| `BuildMcpServer` | STDIO-Server publishen | self-contained oder framework-dependent EXE nach freigegebener Packaging-Entscheidung |
| `BuildMcpBridge` | Revit-Bridge bauen | Bridge-DLL + separates `.addin` |
| `TestMcpUnit` | reine .NET-Tests | Contracts-/Server-/Adapter-Tests |
| `TestMcpIntegration` | In-Process-Protokoll/STDIO/Pipe-Testdoubles | reproduzierbare Integration ohne produktive Revit-Datei |
| `StageBase` | Base-Payload erstellen | nur Base-Dateiliste |
| `StageMcpAddon` | Add-on-Payload erstellen | Server, Contracts, Bridge, Manifest, Vorlagen, Doku |
| `StageBundle` | getrennte Payloads kombinieren | Bundle ohne technische Verschmelzung |
| `ValidateArtifacts` | Dateiliste, Version, Manifest, verbotene Dateien | harter Release-Gate |
| `WriteChecksums` | SHA-256 je Artefakt | Prüfsummendateien |

### 8.2 Vorgeschlagene Release-Artefakte

- `BIMassist-Base-2026-2026.1.6.zip`
- `BIMassist-MCP-2026-0.1.0.zip`
- `BIMassist-Bundle-2026-2026.1.6+mcp-0.1.0.zip`

Kompatibilitätsmanifest des Add-ons:

```json
{
  "product": "BIMassist MCP Add-on",
  "addonVersion": "0.1.0",
  "bridgeProtocolVersion": "1",
  "schemaVersion": "1",
  "revitMajor": 2026,
  "bimassistVersionRange": ">=2026.1.6 <2027.0.0"
}
```

Base-Version wird nur erhöht, wenn Base-Dateien tatsächlich geändert werden. MCP Add-on und Bridge-Protokoll werden unabhängig versioniert.

### 8.3 CI

- GitHub-hosted Windows-Runner: Contracts/Server bauen, reine Tests, JSON-Schemas, STDIO-Framing, Packaging- und Dateilistenprüfung.
- Self-hosted Windows-Runner mit legal installierter Revit-2026-Umgebung: Bridge-Build und kontrollierte Revit-Smoke-/Integrationstests.
- Kein Einchecken oder Verpacken von `RevitAPI.dll`/`RevitAPIUI.dll`.
- CI-Matrix baut Base und MCP Add-on getrennt; ein Artefakttest schlägt fehl, sobald MCP-Dateien im Base-Paket vorkommen.

## 9. Testplan

### 9.1 Automatisierbare Tests

**Contracts**

- JSON-Roundtrip aller DTOs.
- Fehlende Pflichtfelder, unbekannte Operationen, unbekannte Enum-/Discriminator-Werte.
- Protokoll-/Schema-Versionskonflikt.
- Kanonische Plan-Serialisierung und stabiler Hash.
- Große Listen, Pagination, Maximalmengen und Payload-Limits.
- Stabilität aller Fehlercodes.

**Server**

- MCP-Initialisierung und exakt 14 Tools.
- Identische Toolnamen/-schemas für Hermes und Codex.
- STDOUT enthält nur MCP-Framing; Logs ausschließlich stderr/Datei.
- Schema-Validierung vor Bridge-Aufruf.
- Bridge nicht erreichbar, mehrere Sessions, Timeout, Cancellation und Outcome-Unknown.
- Read-/Plan-/Write-Annotationen; Apply als schreibend markiert.
- Keine parallelen Schreibaufrufe bei `supports_parallel_tool_calls=false` und serverseitige Serialisierung unabhängig vom Client.

**Pipe/Bridge-Infrastruktur**

- Eindeutiger Endpoint pro Benutzer/Revit-Prozess.
- Fremder Benutzer/Netzwerkzugriff abgelehnt.
- FIFO-Queue, genau eine aktive Schreiboperation.
- Shutdown bei leerer, wartender und laufender Queue.
- Client-Timeout erzeugt keine automatische Wiederholung.
- Doppelte `idempotencyKey` liefert dasselbe Ergebnis oder klaren Konflikt, niemals Doppelmutation.

**Revit-Adapter mit Testdoubles/reinen Helfern**

- Parameteridentität: Shared-GUID, Built-in-ID, kontrollierter Fallback.
- Vier StorageTypes, HasValue false, ReadOnly, Formel.
- Längen-, Flächen- und Winkeleinheiten; ungültige Einheiten abgelehnt.
- Deterministische Plan-Diffs und Precondition-Fehler.
- Binding Insert/ReInsert/Kategorieerweiterung/Binderartkonflikt.
- Family-Session-Cleanup und Reload-Policy.
- Vollständiger Rollback bei simuliertem Teilfehler.

**Packaging**

- Base enthält keine `Mcp*`-Dateien, kein `ModelContextProtocol*.dll`, kein Bridge-Manifest.
- Add-on enthält kein dupliziertes Base-Manifest.
- Keine Autodesk-DLLs, absoluten `C:\Users\wesch`-Pfade oder Benutzerkonfigurationen.
- `.addin`-Assemblypfade, AddInId, Entry Point und Versionen stimmen.

### 9.2 Manuelle Revit-2026-Testdateien

Unter einem späteren, freigegebenen `mcp/test-models/` oder außerhalb Git bei lizenz-/datenschutzrelevanten Dateien:

1. **`Mcp-ProjectParameters-2026.rvt`**
   - Shared/Built-in/Projektparameter, Instanz/Typ, String/Integer/Double/ElementId, Ja/Nein, Material, leere Werte.
2. **`Mcp-Workshared-2026.rvt`** plus lokales Testmodell
   - Ownership, nicht editierbare Ziele, geänderte Revision, Reload/Sync-Szenarien.
3. **`Mcp-FamilyParameters-2026.rfa`**
   - Typ-/Instanzparameter, Formel, Shared-GUID, mehrere FamilyTypes, schreibgeschützte Parameter.
4. **`Mcp-LoadedFamilies-2026.rvt`**
   - editierbare geladene Familie, nicht editierbare/System-/In-Place-Familie, vorhandene GUID, Reload-Konflikt.
5. **`Mcp-ProjectBindings-2026.rvt`**
   - InstanceBinding, TypeBinding, mehrere Kategorien, bestehende unvollständige Bindung.
6. **Shared-Parameter-Fixture `Mcp-SharedParameters.txt`**
   - mehrere Gruppen, vorhandene und fehlende GUIDs; nur Testkopie verändern.

Manuelle Abnahmefälle:

- Kein Revit, ein Revit-Prozess, mehrere Prozesse.
- Kein aktives Dokument, Dokumentwechsel und Dokument während Request geschlossen.
- Base-only-Installation, Add-on nachinstalliert, Add-on deaktiviert/deinstalliert.
- Hermes und Codex verbinden denselben Servertyp und sehen denselben Toolkatalog.
- Plan anzeigen, explizit freigeben, Apply, tatsächlichen Revit-Wert erneut lesen.
- Revit schließen, während Listener/Queue aktiv sind; kein Shutdown-Crash und keine hängenden Serverprozesse.

## 10. Phasen und Exit-Kriterien

### Phase 1 – Contracts

1. Testprojekte und zentrale Paket-/Build-Konfiguration anlegen.
2. Zuerst fehlschlagende Roundtrip-/Schema-/Versions-/Hash-Tests schreiben.
3. Revit-freie DTOs, Fehlercodes und JSON-Serialisierung minimal implementieren.
4. JSON-Schemas/Fixtures aus den DTOs festlegen.
5. `dotnet test` für Contracts ausführen.
6. Pflicht-Gate: bestehendes `BIMassist.csproj` ohne MCP-Abhängigkeit bauen.

**Exit:** Contracts-Tests grün; Base-Abhängigkeitsprüfung grün; keine Produktdatei aus dem MCP-Projekt im Base-Output.

### Phase 2 – Bridge-Grundlage

1. Eigenes Bridge-Add-in/Manifest und Lifecycle-Tests anlegen.
2. Pipe-Namensschema, aktuelle-Benutzer-ACL und Session Registry implementieren.
3. Queue + genau ein `ExternalEvent` implementieren.
4. Deterministisches Shutdown/Cancellation implementieren.
5. `status` und Dokumentkontext im realen Revit 2026 manuell testen.

**Exit:** Status-/Context-Aufruf funktioniert in Revit; kein API-Zugriff vom Pipe-Thread; Shutdown-Test protokolliert.

### Phase 3 – Read MVP

1. Familien-/Dokumentabfragen.
2. Parameteridentität und vollständige Metadaten.
3. Shared Definitions und Project Bindings.
4. Typisierte Werte/Units.
5. Snapshot, Pagination und Limits.

**Exit:** Definierte Testmodelle liefern stabile, paginierte Ergebnisse und maschinenlesbare Fehler.

### Phase 4 – MCP Server

1. Offizielles C# MCP SDK in zentral gepinnter Version einbinden.
2. STDIO-Host und stderr-/Datei-Logging.
3. 14 Tools mit strikten Schemas auf Bridge-Client abbilden.
4. Hermes-/Codex-Konfigurationsvorlagen generieren, nicht in Benutzerkonfigurationen schreiben.
5. Toolkatalog in beiden Clients vergleichen.

**Exit:** Hermes und Codex listen/nutzen dieselben Read-Tools; STDIO-Framing bleibt frei von Lograuschen.

### Phase 5 – Write MVP

1. Plan Store, kanonischer Hash, Ablaufzeit und Revision.
2. Plan Set Values.
3. Plan Add Shared Parameter to Family.
4. Plan Bind Shared Parameter.
5. Apply mit Idempotenz, Transaktion/TransactionGroup, Rollback und Read-back.
6. Worksharing-/FamilyDocument-/SharedParametersFilename-Fehlerpfade testen.

**Exit:** Unveränderter Plan funktioniert; stale/abgelaufener/geänderter Plan wird abgelehnt; Doppelaufruf mutiert höchstens einmal; Rollback nachgewiesen.

### Phase 6 – Packaging

1. Base-, Add-on- und Bundle-Staging getrennt implementieren.
2. Manifest-/Dateilisten-/Autodesk-DLL-/Pfadprüfungen.
3. Installations-, Deinstallations- und Konfigurationsvorlagen testen.
4. SHA-256 je Release-Artefakt erzeugen.

**Exit:** Base läuft ohne MCP; Add-on separat installier-/deinstallierbar; Bundle enthält dieselben getrennten Komponenten.

### Phase 7 – Hardening

1. Last-, Payload-, Timeout-, Abbruch- und Shutdown-Tests.
2. Auditlog und Logrotation ohne sensible Werte.
3. Diagnose-/Recovery-Dokumentation.
4. Vollständige manuelle Revit-2026-Abnahmematrix.

**Exit:** Abnahmematrix vollständig, reproduzierbar und mit realen Revit-Ergebnissen protokolliert.

## 11. Risiken und Gegenmaßnahmen

| Risiko | Schwere | Gegenmaßnahme |
|---|---:|---|
| Revit API vom Pipe-/MCP-Thread | Kritisch | Architekturtest + ausschließlich Queue/ExternalEvent |
| Shutdown-Rennen / hängender Listener | Kritisch | Cancellation, definierte Stop-Reihenfolge, Join-Timeout, Pending-Requests abschließen |
| Veraltete Dokument-/Elementreferenzen | Kritisch | Nur IDs/DTOs speichern; Sitzung/Dokument/Revision direkt vor Ausführung neu auflösen |
| Falsche Parameteridentität durch Namen | Kritisch | GUID/ID-Priorität; Mehrdeutigkeit als Fehler |
| Teilweise Schreibvorgänge | Kritisch | atomare Transaktion/TransactionGroup und vollständiger Rollback |
| Doppelmutation nach Timeout/Retry | Kritisch | Idempotency Store + Outcome-Status; keine automatische Wiederholung |
| Shared-Parameter-Datei global geändert | Hoch | alten Pfad sichern und im `finally` wiederherstellen; Dateischreiben separat freigeben |
| FamilyDocument/Reload-Konflikte | Hoch | `FamilyEditSession`, garantierter Close, explizite LoadOptions im Plan |
| Units/Spec-Verwechslung | Hoch | `ForgeTypeId`-basierter Codec, deklarierte Eingabeeinheit, Roh-/Anzeige-Rückgabe |
| Worksharing/Ownership | Hoch | Checkout-/Schreibbarkeit vor Plan und Apply erneut prüfen |
| MCP-Log auf stdout | Hoch | Console-Logging auf stderr; Framing-Test |
| MCP-Dateien in Base-Paket | Hoch | feste Dateilisten und negativer Artefakttest |
| Revit-DLLs im Paket | Hoch | `Private=false` + verbotene Dateimuster |
| Harte Benutzer-/Installationspfade | Mittel | MSBuild-Properties, Staging statt implizitem PostBuild |
| `BIMassist - Kopie.csproj` führt zu Doppelinstallation | Mittel | nicht in Solution/Release aufnehmen; später separat entscheiden |
| Bestehende Base-Fehlerpfade melden Erfolg zu früh | Mittel | MCP nutzt neue strukturierte Resultate und Read-back; keine UI-Statuslogik übernehmen |
| MCP-SDK/API ändert sich | Mittel | offizielle stabile Version zentral pinnen; Client-/Schema-Vertragstests |

## 12. Offene Entscheidungen mit Empfehlung

1. **Solution-Organisation**  
   **Empfehlung:** Eine `BIMassist.sln`, ergänzt um alle Projekte, plus `BIMassist.Base.slnf` und `BIMassist.Mcp.slnf`. Keine zweite Solution.

2. **MCP-SDK-Version**  
   **Empfehlung:** Offizielles Paket `ModelContextProtocol`; zum Analysezeitpunkt weist die NuGet-Gallery `2.2.0` als aktuelle stabile Version aus. Erst zu Beginn von Phase 4 erneut gegen offizielle C#-SDK-Dokumentation prüfen und dann zentral pinnen; kein Floating/Prerelease ohne Freigabe.

3. **Testframework**  
   **Empfehlung:** xUnit, da kein bestehendes Framework vorgegeben ist und es gut mit `dotnet test`/CI/Testdoubles funktioniert.

4. **Server-Publishing**  
   **Empfehlung:** Für 0.1 zunächst framework-dependent `win-x64`, weil Revit 2026/.NET 8 auf der Zielumgebung ohnehin vorhanden ist und das Paket kleiner bleibt. Self-contained nur, wenn Installationstests einen echten Laufzeitkonflikt zeigen.

5. **Base-Kompatibilitätsbereich**  
   **Empfehlung:** `>=2026.1.6 <2027.0.0` im Add-on-Manifest. Technische Protokollkompatibilität zusätzlich separat prüfen; keine stillschweigende Annahme aufgrund der Versionsnummer.

6. **Geteilte BIMassist-Core-Dienste**  
   **Empfehlung:** In 0.1 keine direkte Referenz auf das monolithische `BIMassist.csproj` und keine beiläufige Base-Refaktorierung. Erst nach funktionierendem Read MVP konkrete, testbare gemeinsame Logik in eine MCP-neutrale Bibliothek extrahieren.

7. **Plan-Speicherung**  
   **Empfehlung:** Prozesslokaler, begrenzter Speicher in der Bridge mit kurzer Ablaufzeit; keine persistierten schreibbaren Pläne in 0.1. Ein Revit-/Bridge-Neustart invalidiert Pläne sicher.

8. **Dokumentrevision**  
   **Empfehlung:** Sitzungsinterner monotoner Revisionszähler aus relevanten Revit-Dokumentänderungsereignissen plus Dokumentkennung; nicht Pfad/Titel allein. Exakte Ereignisstrategie in Phase 2 mit Revit-Laufzeittest festlegen.

9. **Shared-Parameter-Dateizugriff**  
   **Empfehlung:** Version 0.1 liest die konfigurierte Datei; jeder temporäre Pfadwechsel oder jede Dateischreiboperation benötigt einen eigenen Plan/Freigabepunkt. Kein globaler stiller Wechsel.

10. **`BIMassist - Kopie.csproj`**  
    **Empfehlung:** In MCP-Phasen unangetastet lassen und nicht bauen/paketieren. Spätere Entfernung nur als getrennte, ausdrücklich freigegebene Repository-Bereinigung.

Diese Empfehlungen verändern den Phase-1-Vertragsentwurf nicht materiell. Eine Benutzerentscheidung ist vor Phase 1 nur erforderlich, wenn eine Empfehlung abgelehnt wird.

## 13. Hermes- und Codex-Konfiguration

- Hermes: `mcp_servers.<name>` mit `command`, `args`, `connect_timeout`, `timeout`, `supports_parallel_tool_calls: false` und Tool-Allowlist.
- Codex: `[mcp_servers.<name>]` mit `command`, `args`, `startup_timeout_sec`, `tool_timeout_sec`, `default_tools_approval_mode = "prompt"`, `enabled_tools`.
- Vorlagen enthalten Installationspfad-Platzhalter und werden niemals automatisch in bestehende Benutzerkonfigurationen geschrieben.
- Vor Release werden Schlüssel gegen die aktuellen offiziellen Dokumentationen validiert.

## 14. Quellenbasis

- Projektanweisung: `C:\Users\wesch\Downloads\BIMassist_MCP_Projektanweisung_Hermes.pdf`
- Hermes MCP: <https://hermes-agent.nousresearch.com/docs/user-guide/features/mcp>
- Codex MCP: <https://developers.openai.com/codex/mcp/>
- Offizielles MCP C# SDK: <https://csharp.sdk.modelcontextprotocol.io/>
- MCP C# SDK Getting Started: <https://csharp.sdk.modelcontextprotocol.io/v2/concepts/getting-started.html>
- NuGet `ModelContextProtocol`: <https://www.nuget.org/packages/ModelContextProtocol>
- Autodesk Revit External Events: <https://help.autodesk.com/view/RVT/2026/ENU/?guid=Revit_API_Revit_API_Developers_Guide_Advanced_Topics_External_Events_html>

## 15. Freigabepunkt

Phase 0 endet mit diesem Plan. Vor Phase 1 werden keine Contracts-, Server-, Bridge-, Test-, Packaging- oder Solution-Dateien angelegt. Nach Freigabe beginnt ausschließlich Phase 1 – Contracts; jede spätere Phase benötigt das jeweilige Exit-Kriterium und einen Bericht im vereinbarten Format.
