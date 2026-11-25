using BIMassist.Commands;
using Autodesk.Revit.UI;
using Autodesk.Revit.Attributes;
using System.Text.RegularExpressions;

var command = new AvaTemplateCommand();
var app = new UIApplication();
var data = new ExternalCommandData { Application = app };
string message = "";
var result = command.Execute(data, ref message, new Autodesk.Revit.DB.ElementSet());
if (result != Result.Succeeded || !ReferenceEquals(app, BIMassist.Views.WpfOwner.LastOwner)
    || BIMassist.Views.WpfOwner.LastWindow == null)
    throw new Exception("AVA command must show the template editor owned by this Revit application.");
BIMassist.Views.WpfOwner.ThrowOnShow = true;
result = command.Execute(data, ref message, new Autodesk.Revit.DB.ElementSet());
if (result != Result.Failed || !message.Contains("test window failure", StringComparison.Ordinal))
    throw new Exception("Window failure must return Failed with the real error, not false success.");
var attribute = (TransactionAttribute?)Attribute.GetCustomAttribute(typeof(AvaTemplateCommand), typeof(TransactionAttribute));
if (attribute?.Mode != TransactionMode.ReadOnly)
    throw new Exception("The file-based AVA editor must not permit model transactions.");

string? root = AppContext.BaseDirectory;
while (root != null && !File.Exists(Path.Combine(root, "App.cs"))) root = Directory.GetParent(root)?.FullName;
if (root == null) throw new Exception("Repository root not found.");
string ribbon = File.ReadAllText(Path.Combine(root, "App.cs"));
var ava = Regex.Match(ribbon, "new\\s+PulldownButtonData\\(\\s*\"AVA\"\\s*,\\s*\"BGK/GAEB\"\\s*\\)");
int info = ribbon.IndexOf("new PushButtonData(\"BIMassist Info\"", StringComparison.Ordinal);
if (!ava.Success || info <= ava.Index) throw new Exception("AVA group must precede Info.");
int nextGroup = ribbon.IndexOf("new PushButtonData(\"Metadaten\"", ava.Index, StringComparison.Ordinal);
if (nextGroup <= ava.Index || nextGroup >= info) throw new Exception("Metadata ribbon group boundary was not found.");
string segment = ribbon[ava.Index..nextGroup];
int viewer = segment.IndexOf("new PushButtonData(\"AVA LV-Viewer\", \"LV-Viewer / X82\"", StringComparison.Ordinal);
if (viewer < 0 || !segment[viewer..].Contains("BIMassist.Commands.AvaLvViewerCommand", StringComparison.Ordinal)
    || segment.IndexOf("new PushButtonData(\"AVA Vorlagen\"", StringComparison.Ordinal) >= viewer
    || viewer >= segment.IndexOf("new PushButtonData(\"AVA Mengenermittlung\"", StringComparison.Ordinal))
    throw new Exception("The X82 LV viewer must be available in BGK/GAEB before quantity takeoff.");
string viewerCommand = File.ReadAllText(Path.Combine(root, "Commands", "AvaLvViewerCommand.cs"));
if (!viewerCommand.Contains("TransactionMode.ReadOnly", StringComparison.Ordinal)
    || !viewerCommand.Contains("new FileOpenDialog(\"GAEB-LV (*.x82)", StringComparison.Ordinal)
    || !viewerCommand.Contains("LvX82ViewerWindow.Show(path", StringComparison.Ordinal))
    throw new Exception("The LV viewer command must open a selected X82 through the read-only Revit file dialog.");
if (!segment.Contains("Image = GetImageSource(\"label_16px.png\")", StringComparison.Ordinal)
    || !segment.Contains("LargeImage = GetImageSource(\"label_24px.png\")", StringComparison.Ordinal)
    || segment.IndexOf("Image = GetImageSource", StringComparison.Ordinal) > segment.IndexOf("new PushButtonData(\"BGK-Manager\"", StringComparison.Ordinal)
    || segment.IndexOf("LargeImage = GetImageSource", StringComparison.Ordinal) > segment.IndexOf("new PushButtonData(\"BGK-Manager\"", StringComparison.Ordinal)
    || !segment.Contains("new PushButtonData(\"BGK-Manager\", \"BGK-Manager\"", StringComparison.Ordinal)
    || !segment.Contains("BIMassist.Commands.AvaTemplateCommand", StringComparison.Ordinal)
    || !segment.Contains("BIMassist.Commands.AvaQuantityTakeoffCommand", StringComparison.Ordinal)
    || !segment.Contains("Vorlagen verwalten", StringComparison.Ordinal))
    throw new Exception("BGK/GAEB group must carry the former BGK icon while its commands remain icon-free.");
int takeoff = segment.IndexOf("new PushButtonData(\"AVA Mengenermittlung\", \"Mengenermittlung / X31\"", StringComparison.Ordinal);
int review = segment.IndexOf("new PushButtonData(\"AVA Mengenermittlung kontrollieren oder prüfen\", \"Mengenermittlung kontrollieren oder prüfen\"", StringComparison.Ordinal);
if (takeoff < 0 || review <= takeoff || !segment[review..].Contains("BIMassist.Commands.AvaX31ReviewCommand", StringComparison.Ordinal))
    throw new Exception("X31 review command must be the final entry in the AVA group after quantity export.");
string reviewCommand = File.ReadAllText(Path.Combine(root, "Commands", "AvaX31ReviewCommand.cs"));
if (!reviewCommand.Contains("sourceDocument.Equals(activeDocument)", StringComparison.Ordinal)
    || reviewCommand.Contains("ReferenceEquals(uiDocument.Document, sourceDocument)", StringComparison.Ordinal))
    throw new Exception("X31 review must compare Revit documents by their API equality, not managed wrapper reference identity.");
int activeValidityGuard = reviewCommand.IndexOf("if (!activeDocument.IsValidObject)", StringComparison.Ordinal);
int documentMatchGuard = reviewCommand.IndexOf("if (!sourceDocument.Equals(activeDocument))", StringComparison.Ordinal);
if (activeValidityGuard < 0 || documentMatchGuard <= activeValidityGuard
    || reviewCommand[activeValidityGuard..documentMatchGuard].Contains("activeDocument.Title", StringComparison.Ordinal))
    throw new Exception("An invalid active Revit document must be handled before reading its title.");
int isolateCall = reviewCommand.IndexOf("IsolateElementsTemporary(allIds)", StringComparison.Ordinal);
int transactionStart = reviewCommand.IndexOf("transaction.Start()", StringComparison.Ordinal);
int transactionCommit = reviewCommand.IndexOf("transaction.Commit()", StringComparison.Ordinal);
if (isolateCall < 0 || transactionStart < 0 || transactionCommit < 0
    || !(transactionStart < isolateCall && isolateCall < transactionCommit)
    || !reviewCommand.Contains("TransactionStatus.Committed", StringComparison.Ordinal))
    throw new Exception("Temporary isolation must execute in a checked Revit transaction within the external event.");
if (!reviewCommand.Contains("TransactionStatus.Pending", StringComparison.Ordinal)
    || !reviewCommand.Contains("transaction.RollBack()", StringComparison.Ordinal))
    throw new Exception("Pending Revit failure handling cannot be reported as a final failure; failed isolation must roll back explicitly.");
int transactionCreate = reviewCommand.IndexOf("var transaction = new Transaction(document, \"AVA – X31 temporär isolieren\")", StringComparison.Ordinal);
int pendingTransactionEnd = transactionCreate >= 0
    ? reviewCommand.IndexOf("private bool ClearPreviousTemporaryIsolation", transactionCreate, StringComparison.Ordinal)
    : -1;
if (transactionCreate < 0 || pendingTransactionEnd <= transactionCreate
    || reviewCommand[transactionCreate..pendingTransactionEnd].Contains("using var transaction =", StringComparison.Ordinal)
    || !reviewCommand.Contains("app.Idling += OnIsolationTransactionIdling", StringComparison.Ordinal)
    || !reviewCommand.Contains("app.Idling -= OnIsolationTransactionIdling", StringComparison.Ordinal)
    || !reviewCommand.Contains("SetForcedModalHandling(true)", StringComparison.Ordinal))
    throw new Exception("An unfinished Revit transaction must be retained until failure handling finishes, not disposed on Pending.");
if (!reviewCommand.Contains("catch (Exception rollbackError)", StringComparison.Ordinal)
    || !reviewCommand.Contains("catch (Exception statusError)", StringComparison.Ordinal)
    || reviewCommand.Contains("pendingIsolationNeedsRollback", StringComparison.Ordinal)
    || !reviewCommand.Contains("Rücknahme nicht bestätigt", StringComparison.Ordinal)
    || !reviewCommand.Contains("handler.Window = null", StringComparison.Ordinal))
    throw new Exception("A failed synchronous rollback must not await Idling while a transaction remains Started or report success; a closed window must be released.");
if (reviewCommand.IndexOf("app.Idling += OnIsolationTransactionIdling", StringComparison.Ordinal) > transactionStart
    || !reviewCommand.Contains("pendingIsolationFinalStatus", StringComparison.Ordinal)
    || !reviewCommand.Contains("catch (Exception cleanupError)", StringComparison.Ordinal))
    throw new Exception("Subscribe to Idling before committing; keep the final status and retry cleanup if an event or Dispose call fails.");
if (reviewCommand.Contains("window?.ShowStatus(", StringComparison.Ordinal)
    || !reviewCommand.Contains("Revit hat die Isolation übernommen;", StringComparison.Ordinal))
    throw new Exception("A closing status window must not throw into Revit, and a cleanup error must not misreport a confirmed commit as failure.");
Console.WriteLine("PASS AVA ribbon placement, icon-free BGK-Manager entry and read-only AVA template command wiring (test doubles, not Revit runtime)");
