using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMassist.Core;

/// <summary>
/// Requires a one-to-one match between picked faces and every face of one existing solid.
/// A match can reuse the already-closed source solid without inheriting any unselected wall.
/// </summary>
public static class ExactSelectedSolidFaces
{
    public static bool Match(IEnumerable<IEnumerable<Triangle3d>> selected,
        IEnumerable<IEnumerable<Triangle3d>> source, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(source);
        if (!double.IsFinite(tolerance) || tolerance <= 0)
            throw new ArgumentOutOfRangeException(nameof(tolerance));
        List<Triangle3d[]> picked = selected.Select(face => face.ToArray()).ToList();
        List<Triangle3d[]> candidates = source.Select(face => face.ToArray()).ToList();
        if (picked.Count == 0 || picked.Count != candidates.Count
            || picked.Any(face => face.Length == 0) || candidates.Any(face => face.Length == 0))
            return false;
        var used = new bool[candidates.Count];
        foreach (Triangle3d[] face in picked)
        {
            int match = -1;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (used[i] || !SameFace(face, candidates[i], tolerance)) continue;
                match = i;
                break;
            }
            if (match < 0) return false;
            used[match] = true;
        }
        return true;
    }

    private static bool SameFace(Triangle3d[] picked, Triangle3d[] source, double tolerance)
    {
        if (picked.Length != source.Length) return false;
        var consumed = new bool[source.Length];
        foreach (Triangle3d triangle in picked)
        {
            int match = -1;
            for (int i = 0; i < source.Length; i++)
            {
                if (consumed[i] || !SameTriangle(triangle, source[i], tolerance)) continue;
                match = i;
                break;
            }
            if (match < 0) return false;
            consumed[match] = true;
        }
        return true;
    }

    private static bool SameTriangle(Triangle3d first, Triangle3d second, double tolerance)
    {
        Point3d[] a = { first.A, first.B, first.C };
        Point3d[] b = { second.A, second.B, second.C };
        for (int rotation = 0; rotation < 3; rotation++)
        for (int direction = -1; direction <= 1; direction += 2)
        {
            if (Near(a[0], b[rotation], tolerance)
                && Near(a[1], b[(rotation + direction + 3) % 3], tolerance)
                && Near(a[2], b[(rotation + 2 * direction + 6) % 3], tolerance))
                return true;
        }
        return false;
    }

    private static bool Near(Point3d a, Point3d b, double tolerance)
        => (a - b).Length <= tolerance;
}
