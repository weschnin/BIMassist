# BIMassist MCP add-on

## Document-wide parameter search (read-only)

`revit_search_document_parameters` searches parameter names in the exact open document identified by `sessionId` and `documentKey`. Supply `request.nameContains`, `request.includeTypes` and `request.page.pageSize` (1–1,000); `request.categoryId` and a continuation `request.page.cursor` are optional. Each match contains the element target, Revit-derived stable parameter identity, storage type and read-only flag. Search results are not write authorization: inspect the target and current value before using the separately gated test-only string Plan/Apply path.

The search rejects requests that exceed its bounded element/parameter/match scan or five-second Revit callback budget rather than returning an incomplete `totalCount`. The pipe wait has an eight-second deadline; a disconnected or timed-out request cancels its scan. Each page rescans the document, so broad filters and repeated pages can be expensive. A matching parameter without a representable Revit identity (including a project parameter without a data type) fails the entire search with `OPERATION_NOT_SUPPORTED`; stale parameters with no Definition have no name to match and are skipped. A page whose serialized bridge response exceeds 1 MiB fails with `LIMIT_EXCEEDED`, so reduce `pageSize`; scan/time overruns also return `LIMIT_EXCEEDED` when the connection remains available. Narrow the name and/or category and retry. Continuation cursors are bound to the query, document and revision; changed models require starting a new search. No transaction or write is performed by this tool.

## Test-only parameter writes

The current string-parameter Plan/Apply workflow is test-only. Keep both independent gates closed except while testing a disposable, saved local `.rvt` file. Production models, family documents, cloud models, workshared models, network paths, and paths containing filesystem reparse points (including symbolic links and junctions) are rejected by the Revit bridge.

Configure the MCP server process with:

- `BIMASSIST_MCP_ENABLE_TEST_WRITES=1`
- `BIMASSIST_MCP_TEST_DOCUMENT_KEY=<the exact current document key returned by Revit MCP>`

Configure the **Revit process itself** with:

- `BIMASSIST_MCP_ENABLE_TEST_WRITES=1`
- `BIMASSIST_MCP_TEST_DOCUMENT_PATH=<full path to the one disposable .rvt file>`

Revit must be started after its environment variables are set so it inherits the bridge-side gate. The server-side document key and Revit-side file-path allowlist are separate checks; enabling one does not enable the other. Apply still requires the Revit-owned confirmation dialog for the exact plan. Do not use this test-only flow for production models.
