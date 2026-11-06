using BIMassist.Core;

var cone = new[] { new AxialEnvelopeSegment(new AxialPoint(0, 2), new AxialPoint(4, 1)) };
Check("Ein konischer Mantel erhält zwei Abschlussflächen", cone,
    true, new[] { new AxialPoint(0, 2), new AxialPoint(4, 1) });
var wholeRing = Enumerable.Range(0, 73)
    .Select(index => new AxialLoopPoint(2 * Math.PI * index / 72, 2, 4)).ToArray();
if (!AxialLoopCoverage.IsCompleteCircle(wholeRing, 0.001))
    throw new Exception("Ein voller Kreisrand muss akzeptiert werden.");
Console.WriteLine("PASS vollständiger Kreisrand");
var backtracking = new[] { 0.0, Math.PI / 3, 2 * Math.PI / 3, Math.PI / 3,
    2 * Math.PI / 3, Math.PI, 4 * Math.PI / 3, 5 * Math.PI / 3, 2 * Math.PI }
    .Select(angle => new AxialLoopPoint(angle, 2, 4)).ToArray();
if (AxialLoopCoverage.IsCompleteCircle(backtracking, 0.001))
    throw new Exception("Ein zurücklaufender, überlappender Kreisrand darf nicht als ganze Außenkante gelten.");
Console.WriteLine("PASS zurücklaufender Rand wird abgewiesen");

void Check(string name, AxialEnvelopeSegment[] segments, bool expected, AxialPoint[]? points = null)
{
    bool actual = AxialEnvelopeProfile.TryOrder(segments, 0.001, out var ordered, out var diagnostic);
    if (actual != expected || (points != null && !ordered.SequenceEqual(points)))
        throw new Exception(name + ": " + diagnostic);
    Console.WriteLine("PASS " + name);
}
