using BIMassist.Core;

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected '{expected}', got '{actual}'.");
}

static void Reject(string key, string bgkName, string typeNumber, string typeName)
{
    if (Bgk3DTextSpec.TryCreate(key, bgkName, typeNumber, typeName, out _, out _))
        throw new Exception("Incomplete selection must be rejected.");
}

if (!Bgk3DTextSpec.TryCreate("KG-01", "Außenwand", "42", "WD-01", out var spec, out var error))
    throw new Exception(error);
if (spec is null) throw new Exception("No specification created.");
Equal("KG-01 Außenwand", spec.FamilyName);
Equal("42 WD-01", spec.TypeName);
Equal("KG-01 Außenwand\n42 WD-01", spec.Text);
Equal("KG-01 Außenwand", spec.AssemblyCode);
Equal("42", spec.TypeComments);
Equal("WD-01", spec.Description);
Reject(" ", "Außenwand", "42", "WD-01");
Reject("KG-01", " ", "42", "WD-01");
Reject("KG-01", "Außenwand", "42", " ");
Reject("KG-01", "Außenwand", " ", "WD-01");
if (Bgk3DTextSpec.TrySize("0", "2", out _, out _, out _)) throw new Exception("Zero text height accepted.");
if (Bgk3DTextSpec.TrySize("100", "-1", out _, out _, out _)) throw new Exception("Negative extrusion depth accepted.");
if (!Bgk3DTextSpec.TrySize("100,5", "4,5", out var height, out var depth, out error)) throw new Exception(error);
Equal(100.5, height);
Equal(4.5, depth);
var selection = new BgkSelectionSnapshot<object, long>();
object project = new();
selection.Update(project, new long[] { 17, 19 }, paneHasFocus: false);
Equal("17,19", string.Join(",", selection.Resolve(project, Array.Empty<long>())));
Equal("23", string.Join(",", selection.Resolve(project, new long[] { 23 })));
selection.Update(project, Array.Empty<long>(), paneHasFocus: true);
Equal("17,19", string.Join(",", selection.Resolve(project, Array.Empty<long>())));
selection.Update(project, Array.Empty<long>(), paneHasFocus: false);
Equal(0, selection.Resolve(project, Array.Empty<long>()).Count);
selection.Update(project, new long[] { 17 }, paneHasFocus: false);
Equal(0, selection.Resolve(new object(), Array.Empty<long>()).Count);
Console.WriteLine("BGK-3D-Text-Spezifikation: alle Prüfungen bestanden.");
