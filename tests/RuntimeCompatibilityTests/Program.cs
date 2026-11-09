using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

if (args.FirstOrDefault() is "--inspect" or "--verify")
{
    foreach (string path in args.Skip(1)) Inspect(path, args[0] == "--verify", args[0] == "--verify");
    return;
}

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
bool configurationOnly = args.Contains("--configuration-only");
string configuration = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "Release";
var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
foreach (string argument in new[] { "msbuild", "BIMassist.csproj", "-nologo", $"-p:Configuration={configuration}", "-p:DeployToRevit=false", "-getProperty:TargetFramework,TargetPath,RevitApiReferenceVersion", "-getItem:Reference,PackageReference" })
    start.ArgumentList.Add(argument);
using var process = Process.Start(start) ?? throw new Exception("Cannot evaluate BIMassist.csproj.");
string output = process.StandardOutput.ReadToEnd();
string error = process.StandardError.ReadToEnd();
process.WaitForExit();
if (process.ExitCode != 0) throw new Exception($"Project evaluation failed: {output}\n{error}");
using var json = JsonDocument.Parse(output);
var properties = json.RootElement.GetProperty("Properties");
Require(properties.GetProperty("TargetFramework").GetString() == "net8.0-windows", "The default production target must be net8.0-windows, independently of the locally installed Revit update.");
Require(properties.GetProperty("RevitApiReferenceVersion").GetString() == "2026.0.0.9999", "Use the original Revit 2026 API baseline, not a silently upgraded SDK.");
var package = json.RootElement.GetProperty("Items").GetProperty("PackageReference").EnumerateArray()
    .Single(p => p.GetProperty("Identity").GetString() == "Autodesk.Revit.SDK");
Require(package.GetProperty("Version").GetString() == "[2026.0.0.9999]", "The API reference package must be pinned to an exact version.");
Require(package.GetProperty("ExcludeAssets").GetString() == "all" && package.GetProperty("PrivateAssets").GetString() == "all", "SDK package assets must not become automatic compile/runtime/transitive dependencies.");
var references = json.RootElement.GetProperty("Items").GetProperty("Reference").EnumerateArray().ToArray();
var baselineHashes = new Dictionary<string, string>
{
    // These original 26.0.10.0 files have valid Autodesk, Inc. Authenticode signatures.
    ["RevitAPI"] = "1C6289C879DA13AFEB37A2DAA19F53EF6E68559CC6BC48ECFA405D3E471D88FA",
    ["RevitAPIUI"] = "58A9A1D7C2AD586647908D1449A5BC64ED27C464F2479D17AF2FFF2DA264D4BE"
};
foreach (var (name, hash) in baselineHashes)
{
    var reference = references.Single(r => r.GetProperty("Identity").GetString() == name);
    Require(reference.TryGetProperty("Private", out var copyLocal) && copyLocal.GetString() == "false", $"{name} must have Private=false; Autodesk host DLLs must never ship with the add-in.");
    string path = Path.GetFullPath(reference.GetProperty("HintPath").GetString()!, root);
    Require(File.Exists(path), $"Restore the pinned baseline API: {path}");
    Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == hash, $"{name} is not the verified original Autodesk baseline binary.");
    Inspect(path, enforceBaseline: true, requireHostReferences: false);
}
Console.WriteLine("PASS: evaluated portable default, pinned genuine baseline APIs, non-copy-local host references.");
if (configurationOnly) return;
string assemblyPath = properties.GetProperty("TargetPath").GetString() ?? throw new Exception("TargetPath missing.");
Require(File.Exists(assemblyPath), $"Build the production project first: {assemblyPath}");
Inspect(assemblyPath, enforceBaseline: true, requireHostReferences: true);
string outputDirectory = Path.GetDirectoryName(assemblyPath)!;
foreach (string hostDll in new[] { "RevitAPI.dll", "RevitAPIUI.dll", "RevitAPIFoundation.dll", "AdWindows.dll" })
    Require(!File.Exists(Path.Combine(outputDirectory, hostDll)), $"Output must not include host assembly {hostDll}. Clean/rebuild if this is a stale pre-fix artifact.");
foreach (string dependency in Directory.EnumerateFiles(outputDirectory, "*.dll"))
    if (!Path.GetFullPath(dependency).Equals(Path.GetFullPath(assemblyPath), StringComparison.OrdinalIgnoreCase))
        Inspect(dependency, enforceBaseline: true, requireHostReferences: false);
Console.WriteLine("PASS: portable Revit 2026 default, non-copy-local host references, and actual production/dependency metadata.");

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void Inspect(string path, bool enforceBaseline, bool requireHostReferences)
{
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    MetadataReader metadata = pe.GetMetadataReader();
    AssemblyDefinition definition = metadata.GetAssemblyDefinition();
    Console.WriteLine($"ASSEMBLY {Path.GetFullPath(path)}: {metadata.GetString(definition.Name)} {definition.Version}");
    string? targetFramework = null;
    foreach (CustomAttributeHandle handle in definition.GetCustomAttributes())
    {
        CustomAttribute attribute = metadata.GetCustomAttribute(handle);
        if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
        MemberReference constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
        if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
        TypeReference type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
        if (metadata.GetString(type.Namespace) != "System.Runtime.Versioning" || metadata.GetString(type.Name) != "TargetFrameworkAttribute") continue;
        BlobReader blob = metadata.GetBlobReader(attribute.Value);
        Require(blob.ReadUInt16() == 1, "Invalid framework attribute.");
        targetFramework = blob.ReadSerializedString();
        Console.WriteLine($"  TargetFramework={targetFramework}");
    }
    var references = metadata.AssemblyReferences.Select(h => metadata.GetAssemblyReference(h)).ToArray();
    foreach (AssemblyReference reference in references)
    {
        string name = metadata.GetString(reference.Name);
        if (requireHostReferences || name is "System.Runtime" or "RevitAPI" or "RevitAPIUI")
            Console.WriteLine($"  REF {name} {reference.Version}");
        if (enforceBaseline && (name.StartsWith("System.", StringComparison.Ordinal) || name is "WindowsBase" or "PresentationCore" or "PresentationFramework" or "Microsoft.CSharp"))
            Require(reference.Version.Major <= 8, $"{name} {reference.Version} cannot load in a .NET 8 Revit host.");
    }
    if (!enforceBaseline) return;
    if (metadata.GetString(definition.Name) is "RevitAPI" or "RevitAPIUI")
        Require(definition.Version == new Version(26, 0, 10, 0), "Use the original Revit 2026 baseline APIs.");
    if (!requireHostReferences) return;
    Require(targetFramework == ".NETCoreApp,Version=v8.0", "Production assembly must target .NET 8.");
    Require(references.Any(r => metadata.GetString(r.Name) == "System.Runtime" && r.Version == new Version(8, 0, 0, 0)), "Production must reference System.Runtime 8.0.0.0.");
    foreach (string name in new[] { "RevitAPI", "RevitAPIUI" })
        Require(references.Any(r => metadata.GetString(r.Name) == name && r.Version == new Version(26, 0, 10, 0)), $"Production must reference the actual Revit 2026 {name} API.");
}
