namespace BIMassist.Ava.Core;

/// <summary>A materialized export in exactly the supplied column and row order.</summary>
public sealed record AvaQuantityTable(IReadOnlyList<AvaQuantityTableColumn> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows);
/// <param name="Width">Presentation width in device-independent pixels (as in the Revit table).</param>
public sealed record AvaQuantityTableColumn(string Key, string Header, double Width);
