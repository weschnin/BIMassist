# BIMassist MCP add-on

## Test-only parameter writes

The current string-parameter Plan/Apply workflow is test-only. Keep both independent gates closed except while testing a disposable, saved local `.rvt` file. Production models, family documents, cloud models, workshared models, network paths, and paths containing filesystem reparse points (including symbolic links and junctions) are rejected by the Revit bridge.

Configure the MCP server process with:

- `BIMASSIST_MCP_ENABLE_TEST_WRITES=1`
- `BIMASSIST_MCP_TEST_DOCUMENT_KEY=<the exact current document key returned by Revit MCP>`

Configure the **Revit process itself** with:

- `BIMASSIST_MCP_ENABLE_TEST_WRITES=1`
- `BIMASSIST_MCP_TEST_DOCUMENT_PATH=<full path to the one disposable .rvt file>`

Revit must be started after its environment variables are set so it inherits the bridge-side gate. The server-side document key and Revit-side file-path allowlist are separate checks; enabling one does not enable the other. Apply still requires the Revit-owned confirmation dialog for the exact plan. Do not use this test-only flow for production models.
