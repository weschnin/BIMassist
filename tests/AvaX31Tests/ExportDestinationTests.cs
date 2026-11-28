using BIMassist.Ava.Core;

internal static class ExportDestinationTests
{
    public static void Run(Action<string, Action> test)
    {
        test("checked picker protects portable source bytes and basenames before offering overwrite", () => InDirectory(directory =>
        {
            var original = System.Text.Encoding.UTF8.GetBytes("immutable source catalog snapshot");
            var source = new SourceStamp("source.x82", "x82", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)));
            var sourceDirectory = Directory.CreateDirectory(Path.Combine(directory, "elsewhere")).FullName;
            foreach (var name in new[] { "source.x82", "renamed.x31", "renamed.xlsx", "modified/SOURCE.X82" })
            {
                var path = Path.Combine(sourceDirectory, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var bytes = name.StartsWith("modified/", StringComparison.Ordinal) ? new byte[] { 3, 2, 1 } : original;
                File.WriteAllBytes(path, bytes);
                try
                {
                    ChooseWithSource(path, source, _ => throw new Exception("Source catalog was offered for overwrite"));
                    throw new Exception("Source catalog acquired overwrite permission");
                }
                catch (InvalidDataException ex)
                {
                    if (!ex.Message.Contains("Dateinamen", StringComparison.Ordinal)) throw new Exception("Missing new-output-name guidance");
                }
                if (!bytes.SequenceEqual(File.ReadAllBytes(path))) throw new Exception("Source changed during selection");
            }
            var unrelated = Path.Combine(directory, "unrelated.x82");
            File.WriteAllText(unrelated, "previous measured output");
            var confirmations = 0;
            ChooseWithSource(unrelated, source, _ => { confirmations++; return true; })!
                .Save(unrelated, temp => File.WriteAllText(temp, "new export"));
            Equal(1, confirmations); Equal("new export", File.ReadAllText(unrelated));
            var newPath = Path.Combine(directory, "source.x82");
            ChooseWithSource(newPath, source, _ => throw new Exception("New path prompted"))!
                .Save(newPath, temp => File.WriteAllText(temp, "new export"));
            Equal("new export", File.ReadAllText(newPath));
        }));
        test("shared destination publishes real XLSX output after explicit replacement consent", () => InDirectory(directory =>
        {
            var path = Path.Combine(directory, "table.xlsx");
            var table = new AvaQuantityTable([new("value", "Quantity", 100)], [new object?[] { 5m }]);
            var newDestination = AvaExportDestination.Choose(path, [], _ => throw new Exception("New path prompted"))!;
            newDestination.Save(path, temporary => AvaQuantityXlsxWriter.Save(temporary, table));
            var previous = File.ReadAllBytes(path);
            if (AvaExportDestination.Choose(path, [], _ => false) != null) throw new Exception("Cancellation ignored");
            if (!previous.SequenceEqual(File.ReadAllBytes(path))) throw new Exception("Cancel altered workbook");
            var replacement = AvaExportDestination.Choose(path, [], _ => true)!;
            replacement.Save(path, temporary => AvaQuantityXlsxWriter.Save(temporary,
                table with { Rows = [new object?[] { 9m }] }));
            using var workbook = System.IO.Compression.ZipFile.OpenRead(path);
            using var reader = new StreamReader(workbook.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            if (!reader.ReadToEnd().Contains(">9<", StringComparison.Ordinal)) throw new Exception("Workbook replacement lost measured value");
            Equal(1, Directory.GetFiles(directory).Length);
        }));
        test("overwrite consent is stale if destination bytes change even with restored metadata", () => InDirectory(directory =>
        {
            var path = Path.Combine(directory, "existing.x82");
            File.WriteAllText(path, "old bytes");
            var timestamp = File.GetLastWriteTimeUtc(path);
            var destination = AvaExportDestination.Choose(path, [], _ => true)!;
            File.WriteAllText(path, "new bytes");
            File.SetLastWriteTimeUtc(path, timestamp);
            ExpectIo(() => destination.Save(path, temporary => File.WriteAllText(temporary, "export")));
            Equal("new bytes", File.ReadAllText(path));
            Equal(1, Directory.GetFiles(directory).Length);
        }));
        test("overwrite consent binds the state before confirmation and is rechecked after prepare", () => InDirectory(directory =>
        {
            var path = Path.Combine(directory, "existing.x82");
            File.WriteAllText(path, "before prompt");
            var destination = AvaExportDestination.Choose(path, [], _ => { File.WriteAllText(path, "during prompt"); return true; })!;
            ExpectIo(() => destination.Save(path, temporary => File.WriteAllText(temporary, "export")));
            Equal("during prompt", File.ReadAllText(path));
            destination = AvaExportDestination.Choose(path, [], _ => true)!;
            ExpectIo(() => destination.Save(path, temporary =>
            {
                File.WriteAllText(temporary, "export");
                File.WriteAllText(path, "during prepare");
            }));
            Equal("during prepare", File.ReadAllText(path));
            Equal(1, Directory.GetFiles(directory).Length);
        }));
        test("overwrite consent is single use even when content stays identical", () => InDirectory(directory =>
        {
            var path = Path.Combine(directory, "existing.x82");
            File.WriteAllText(path, "export");
            var destination = AvaExportDestination.Choose(path, [], _ => true)!;
            destination.Save(path, temporary => File.WriteAllText(temporary, "export"));
            ExpectIo(() => destination.Save(path, temporary => File.WriteAllText(temporary, "second export")));
            Equal("export", File.ReadAllText(path));
        }));
        test("cancel and default new-file choice never replace even an empty picker file", () => InDirectory(directory =>
        {
            var path = Path.Combine(directory, "picker.x82");
            File.WriteAllBytes(path, []);
            var confirms = 0;
            var cancelled = AvaExportDestination.Choose(path, [], _ => { confirms++; return false; });
            if (cancelled != null) throw new Exception("Cancel returned an authorized destination");
            Equal(1, confirms);
            ExpectIo(() => AvaExportDestination.NewFile(path).Save(path, temp => File.WriteAllText(temp, "export")));
            Equal(0L, new FileInfo(path).Length);
            Equal(1, Directory.GetFiles(directory).Length);
        }));
        test("new destination creation races never acquire overwrite permission", () => InDirectory(directory =>
        {
            var path = Path.Combine(directory, "new.x82");
            var destination = AvaExportDestination.Choose(path, [], _ => throw new Exception("New path prompted"))!;
            ExpectIo(() => destination.Save(path, temporary =>
            {
                File.WriteAllText(temporary, "export");
                File.WriteAllText(path, "concurrent writer");
            }));
            Equal("concurrent writer", File.ReadAllText(path));
        }));
        test("protected project template workspace and LV paths cannot request consent including case and dot aliases", () => InDirectory(directory =>
        {
            foreach (var name in new[] { "project.rvt", "template.json", "workspace.json", "source.x82" })
            {
                var path = Path.Combine(directory, name);
                File.WriteAllText(path, "protected");
                foreach (var selected in new[] { path, path.ToUpperInvariant(), Path.Combine(directory, ".", name) })
                {
                    try
                    {
                        AvaExportDestination.Choose(selected, [path], _ => throw new Exception("Protected path prompted"));
                        throw new Exception("Protected path allowed");
                    }
                    catch (InvalidDataException) { }
                }
                Equal("protected", File.ReadAllText(path));
            }
        }));
        test("ambiguous Windows trailing-dot space and stream paths fail closed before consent", () => InDirectory(directory =>
        {
            var path = Path.Combine(directory, "source.x82");
            File.WriteAllText(path, "protected");
            foreach (var selected in new[] { path + ".", path + " ", path + ":stream" })
            {
                try
                {
                    AvaExportDestination.Choose(selected, [path], _ => true);
                    throw new Exception("Ambiguous Windows path acquired permission");
                }
                catch (InvalidDataException) { }
            }
            Equal("protected", File.ReadAllText(path));
        }));
        test("failed preparation and path-mismatched consent preserve all existing bytes", () => InDirectory(directory =>
        {
            var path = Path.Combine(directory, "existing.x82");
            var other = Path.Combine(directory, "other.x82");
            File.WriteAllText(path, "old"); File.WriteAllText(other, "other");
            var destination = AvaExportDestination.Choose(path, [], _ => true)!;
            try
            {
                destination.Save(path, temporary => { File.WriteAllText(temporary, "partial invalid output"); throw new InvalidDataException("invalid"); });
                throw new Exception("Invalid preparation succeeded");
            }
            catch (InvalidDataException) { }
            try
            {
                destination.Save(other, temporary => File.WriteAllText(temporary, "wrong target"));
                throw new Exception("Consent transferred to other path");
            }
            catch (InvalidDataException) { }
            Equal("old", File.ReadAllText(path)); Equal("other", File.ReadAllText(other));
            Equal(2, Directory.GetFiles(directory).Length);
        }));
    }

    private static AvaExportDestination? ChooseWithSource(string path, SourceStamp source, Func<string, bool> confirm) =>
        AvaExportDestination.Choose(path, [], confirm, source);

    private static void InDirectory(Action<string> action)
    {
        var directory = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR")!, "export-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory); } finally { Directory.Delete(directory, true); }
    }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void ExpectIo(Action action) { try { action(); } catch (IOException) { return; } throw new Exception("Expected IOException; destination was replaced"); }
}
