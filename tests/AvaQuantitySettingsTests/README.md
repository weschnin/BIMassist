# Project-bound quantity view settings

## Contract

- `AvaQuantityViewSettingsStorage.Load(Document)` returns null only when no settings are stored. Call inside valid Revit API context; it starts no transaction and does not create a schema on restore.
- `Save(Document, AvaQuantityViewSettings)` requires `Document.IsModifiable`. The caller owns the transaction and must roll back on any exception. Existing corrupt/duplicate storage is rejected, not replaced. In-transaction readback is mandatory.
- Storage GUID: `dcd81d44-26cc-4abd-a9da-94f234406470`; schema name `BIMassistAvaQuantityViewSettings`; scalar fields `SchemaVersion` (int, 1), `Json` (string). One DataStorage per project, reused in place; unrelated takeoff-run storage is untouched.
- `internal sealed AvaQuantityViewSettingsSession : IExternalEventHandler` has public constructor `(Document document, Action<string> report)`, `Queue(AvaQuantityViewSettings)`, `Close()`, `Execute(UIApplication)`, `GetName()`.
- Construct in a valid API context. Queue performs only codec validation/snapshot and event signalling, with no Document/Element/transaction access. Invalid DTOs throw without replacing an already queued valid snapshot; a closed session reports rejection instead.
- Queue coalesces to the last valid state. Accepted and Pending raises are outstanding signals. Denied/TimedOut/throwing raises retain the snapshot for Idling retry. Requests queued during Execute wait for Idling rather than raising from the still-active callback stack.
- Execute always targets the constructor-bound Document after validity checks, never ActiveUIDocument. A modifiable/busy document defers its snapshot; an invalid, closed, read-only or ambiguous/corrupt project reports failure. Identical persisted state starts no transaction.
- The session forces modal failure handling. Started failures are explicitly rolled back with `X31ReviewTransactionRecovery`; unresolved/Pending transactions and event ownership survive Close until terminal recovery in Idling. Success requires confirmed Committed status, complete post-commit readback, and successful transaction cleanup.
- Close stops new Queue requests but drains existing work. With no work it disposes the paired ExternalEvent immediately without model API access; the Revit Idling subscription detaches in the next API callback.

## Run

From the repository root:

```text
python tests/AvaQuantitySettingsTests/run_isolated.py
python tests/AvaQuantitySettingsTests/run_isolated.py build tests/AvaQuantitySettingsTests/ApiCompile/AvaQuantitySettingsApiCompile.csproj -c Release -v minimal
```

The runner always creates unique native Hermes scratch paths for **both** `BIMASSIST_AVA_WORKSPACE_SETTINGS` and `BIMASSIST_AVA_PARAMETER_CHOICES`. The executable test harness links unchanged production storage/session/codec/recovery sources and provides minimal Revit boundary doubles. It is intentionally run with `dotnet run`, like the repository's other dependency-free AVA executable tests; a plain `dotnet test` does not run this harness.

The separate ApiCompile project compiles those same sources against the pinned original Autodesk net8 RevitAPI/RevitAPIUI binaries, without boundary doubles or deployment. The executable harness excludes the ApiCompile subtree from SDK compile globs.

## Automated evidence

Observed RED stages: storage API NotImplementedException (0/1); transaction/version/schema/duplicate/readback rejection failures (1/6); session queue NotImplementedException (6/7); lifecycle regressions for duplicate transactions, busy retries, callback scheduling, invalid document, rollback cleanup and Pending ownership (11/18). The subsequent GREEN checkpoint passed 23/23 tests. A real API `net8.0-windows` slice build succeeded with 0 warnings and 0 errors.

## Limits

These are deterministic boundary tests and API compilation, not live Revit acceptance. Native failure UI, inactive-document transactions, worksharing permissions, actual host transaction finalizers, document close/shutdown, Undo, and real Idling/event timing still require Revit testing. No deployment or model changes were performed.

A failed pre-commit write/readback or confirmed rollback preserves the prior committed state. A mismatch detected only **after** Revit reports Committed is an unconfirmed outcome, not an assertion that the old state survived; the session reports failure and does not falsely claim rollback. If Revit never exposes a terminal transaction status, the session deliberately retains the transaction/event instead of disposing a Pending transaction or claiming completion. Idling retries use Revit's default frequency rather than a CPU-spinning timer.
