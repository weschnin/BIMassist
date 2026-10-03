using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.RevitBridge.Diagnostics;

namespace BIMassist.Mcp.RevitBridge.Tests.Diagnostics;

public sealed class BridgeDiagnosticLogTests
{
    [Fact]
    public void Write_records_request_and_exception_location_without_exception_message()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "hermes",
            "cache",
            "scratch",
            "bimassist-mcp-diagnostic-tests");
        string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "diagnostics.log");
        var request = new BridgeRequest
        {
            ProtocolVersion = ProtocolVersions.ProtocolVersion,
            SchemaVersion = ProtocolVersions.SchemaVersion,
            RequestId = "request-diagnostic-test",
            Operation = BridgeOperations.ListElements,
            Payload = System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone()
        };

        try
        {
            var failure = new System.Text.Json.JsonException("do-not-log-this-message");
            failure.Data["BIMassist.ElementValidationCode"] = "category-metadata-mismatch";
            failure.Data["BIMassist.ElementId"] = 2798L;
            new BridgeDiagnosticLog(path).Write(request, failure);

            string record = File.ReadAllText(path);
            Assert.Contains("request-diagnostic-test", record, StringComparison.Ordinal);
            Assert.Contains(BridgeOperations.ListElements, record, StringComparison.Ordinal);
            Assert.Contains(nameof(System.Text.Json.JsonException), record, StringComparison.Ordinal);
            Assert.Contains("elementValidation=category-metadata-mismatch elementId=2798", record, StringComparison.Ordinal);
            Assert.DoesNotContain("do-not-log-this-message", record, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
