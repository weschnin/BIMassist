using System.Xml.Linq;

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
XDocument manifest = XDocument.Load(Path.Combine(root, "BIMassist.addin"));
XElement[] registrations = manifest.Root?.Elements("AddIn").ToArray()
    ?? throw new Exception("BIMassist manifest is missing its root element.");
if (registrations.Length != 1
    || (string?)registrations[0].Element("Name") != "BIMassist"
    || (string?)registrations[0].Element("Assembly") != "BIMassist.dll"
    || (string?)registrations[0].Element("FullClassName") != "BIMassist.App")
    throw new Exception("BIMassist.addin must register only BIMassist.App from BIMassist.dll.");

XDocument project = XDocument.Load(Path.Combine(root, "BIMassist.csproj"));
if (HasMcpDependency(project))
    throw new Exception("BIMassist must not depend on an MCP server or bridge.");

XDocument directBridgeReference = XDocument.Parse(project.ToString());
directBridgeReference.Root!.Add(new XElement("ItemGroup",
    new XElement("Reference", new XAttribute("Include", "RevitMCP.RevitBridge"))));
if (!HasMcpDependency(directBridgeReference))
    throw new Exception("Regression: a direct RevitMCP.RevitBridge reference was not rejected.");

XDocument bridgeHintPath = XDocument.Parse(project.ToString());
bridgeHintPath.Root!.Add(new XElement("ItemGroup",
    new XElement("Reference", new XAttribute("Include", "OtherAssembly"),
        new XElement("HintPath", @"..\RevitMCP.RevitBridge.dll"))));
if (!HasMcpDependency(bridgeHintPath))
    throw new Exception("Regression: a bridge dependency in HintPath was not rejected.");

XDocument rootSdkReference = XDocument.Parse(project.ToString());
rootSdkReference.Root!.SetAttributeValue("Sdk", "RevitMCP.Sdk");
if (!HasMcpDependency(rootSdkReference))
    throw new Exception("Regression: an MCP SDK on the Project root was not rejected.");

XElement deploy = project.Descendants("Target")
    .Single(element => (string?)element.Attribute("Name") == "PostBuild");
if (HasUnsafeDeployment(deploy))
    throw new Exception("Deployment must copy only BIMassist.dll and BIMassist.addin.");

XElement extraManifestCopy = new(deploy);
XAttribute command = extraManifestCopy.Element("Exec")!.Attribute("Command")!;
command.Value += "\nxcopy /y /d \"$(ProjectDir)RevitMCP.RevitBridge.addin\" \"C:\\RevitAddins\"";
if (!HasUnsafeDeployment(extraManifestCopy))
    throw new Exception("Regression: an extra RevitMCP.RevitBridge.addin copy was not rejected.");

XElement extraTask = new(deploy);
extraTask.Add(new XElement("Exec", new XAttribute("Command", "echo unintended")));
if (!HasUnsafeDeployment(extraTask))
    throw new Exception("Regression: an additional deployment task was not rejected.");

XElement extraCommand = new(deploy);
extraCommand.Element("Exec")!.Attribute("Command")!.Value += "\necho unintended";
if (!HasUnsafeDeployment(extraCommand))
    throw new Exception("Regression: an additional shell command was not rejected.");

string solution = File.ReadAllText(Path.Combine(root, "BIMassist.sln"));
if (solution.Contains("RevitMCP", StringComparison.OrdinalIgnoreCase)
    || solution.Contains("BIMassist.Mcp", StringComparison.OrdinalIgnoreCase))
    throw new Exception("BIMassist solution must not contain MCP projects.");

Console.WriteLine("BIMassist manifest, project, solution and deployment are separate from RevitMCP.");

static bool HasMcpDependency(XDocument project) =>
    project.Root!.DescendantsAndSelf().SelectMany(element => element.Attributes().Select(attribute => attribute.Value)
            .Concat(element.HasElements ? [] : [element.Value]).Append(element.Name.LocalName))
        .Any(value => value.Contains("MCP", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Bridge", StringComparison.OrdinalIgnoreCase));

static bool HasUnsafeDeployment(XElement deploy)
{
    XElement[] tasks = deploy.Elements().ToArray();
    if (tasks.Length != 1 || tasks[0].Name.LocalName != "Exec")
        return true;

    string command = (string?)tasks[0].Attribute("Command") ?? "";
    string[] lines = command.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    const string firstCopy = "xcopy /y /d \"$(TargetPath)\" \"";
    if (lines.Length != 2 || !lines[0].StartsWith(firstCopy, StringComparison.Ordinal)
        || !lines[0].EndsWith('"'))
        return true;

    string destination = lines[0][firstCopy.Length..^1];
    return !Path.IsPathFullyQualified(destination)
        || destination.IndexOfAny(['"', '%', '!', '&', '|', '<', '>']) >= 0
        || lines[1] != $"xcopy /y /d \"$(ProjectDir)BIMassist.addin\" \"{destination}\"";
}
