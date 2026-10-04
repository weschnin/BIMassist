using System.Reflection;
using ModelContextProtocol.Server;
using BIMassist.Mcp.Server.Tools;

namespace BIMassist.Mcp.Server.Tests.Tools;

public sealed class RevitToolCatalogTests
{
    [Fact]
    public void Exposes_the_twelve_bridge_operations_available_in_the_read_mvp()
    {
        string[] names = typeof(RevitTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>())
            .Where(attribute => attribute is not null)
            .Select(attribute => attribute!.Name!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[]
        {
            "revit_export_metadata_snapshot",
            "revit_get_document_context",
            "revit_get_family_metadata",
            "revit_get_parameter_metadata",
            "revit_get_parameter_values",
            "revit_get_status",
            "revit_list_elements",
            "revit_list_families",
            "revit_list_parameters",
            "revit_list_project_bindings",
            "revit_list_shared_definitions",
            "revit_search_document_parameters"
        }, names);
    }

    [Fact]
    public void Every_published_tool_declares_behavior_annotations_explicitly()
    {
        McpServerToolAttribute[] attributes = typeof(RevitTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>())
            .Where(attribute => attribute is not null)
            .Cast<McpServerToolAttribute>()
            .ToArray();

        Assert.NotEmpty(attributes);
        Assert.All(attributes, attribute =>
        {
            Assert.True(attribute.ReadOnly);
            Assert.False(attribute.Destructive);
            Assert.True(attribute.Idempotent);
        });
    }
}
