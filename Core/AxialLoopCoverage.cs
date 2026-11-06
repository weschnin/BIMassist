namespace BIMassist.Core;

internal readonly record struct AxialLoopPoint(double Angle, double Radius, double Height);

/// <summary>Rejects cutouts and sectors: a boundary loop must wind once around one axis
/// at constant height and radius, not just intersect a finite sampling grid.</summary>
internal static class AxialLoopCoverage
{
    internal static bool IsCompleteCircle(IReadOnlyList<AxialLoopPoint> points, double tolerance)
    {
        if (points.Count < 5 || tolerance <= 0) return false;
        double radius = points[0].Radius, height = points[0].Height;
        if (!double.IsFinite(radius) || radius <= tolerance || !double.IsFinite(height)) return false;
        double total = 0;
        int direction = 0;
        for (int i = 0; i < points.Count; i++)
        {
            AxialLoopPoint point = points[i];
            if (!double.IsFinite(point.Angle) || !double.IsFinite(point.Radius)
                || !double.IsFinite(point.Height)
                || Math.Abs(point.Radius - radius) > tolerance
                || Math.Abs(point.Height - height) > tolerance)
                return false;
            if (i == 0) continue;
            double delta = Math.Atan2(Math.Sin(point.Angle - points[i - 1].Angle),
                Math.Cos(point.Angle - points[i - 1].Angle));
            if (Math.Abs(delta) > Math.PI / 2 || Math.Abs(delta) < 1e-10) return false;
            int stepDirection = Math.Sign(delta);
            if (direction != 0 && direction != stepDirection) return false;
            direction = stepDirection;
            total += delta;
        }
        return Math.Abs(Math.Abs(total) - 2 * Math.PI) <= 0.01
            && Math.Abs(Math.Atan2(Math.Sin(points[^1].Angle - points[0].Angle),
                Math.Cos(points[^1].Angle - points[0].Angle))) <= 0.01;
    }
}
