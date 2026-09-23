namespace Utnm.BowtieKernel;

/// <summary>
/// The evidence bowtie_check needs beyond "do any two links cross in plan" - a test that can only ever falsify (a clip that cuts
/// every link back to a stub scores 0 crossings and is wrong). Plain geometry on what the built corridor gives: the points where
/// each inside section ends, the valley lines the repairs are mapped to, the applied stations.
/// </summary>
public static class BuiltCheck
{
  /// <summary>Runs of consecutive entries that are true (index ranges, inclusive). The corridor feature line of a code joins a
  /// point only to the next station that also carries the code; a station without it breaks the line, so a loop test must never
  /// draw a chord across it.</summary>
  public static List<(int From, int To)> Runs(IReadOnlyList<bool> has)
  {
    var runs = new List<(int, int)>();
    var start = -1;
    for (var i = 0; i < has.Count; i++)
    {
      if (has[i]) { if (start < 0) start = i; continue; }
      if (start >= 0) { runs.Add((start, i - 1)); start = -1; }
    }
    if (start >= 0) runs.Add((start, has.Count - 1));
    return runs;
  }

  /// <summary>Plan distance from p to the nearest of several polylines, and that line's level there.</summary>
  public static (double Distance, double Z) Nearest(IReadOnlyList<IReadOnlyList<P3>> lines, P3 p)
  {
    var best = (Distance: double.PositiveInfinity, Z: double.NaN);
    foreach (var line in lines)
    {
      if (line.Count == 0) continue;
      var n = Refresh.Nearest(line, p);
      if (n.Distance < best.Distance) best = n;
    }
    return best;
  }

  /// <summary>
  /// The points in the order they lie along the valley lines: by line, then by distance along it from its start. The sections
  /// of both legs end on the one valley line, interleaved along it; the corridor surface between them is triangulated from
  /// those points, so its crease along the valley is (close to) the chain of straight chords in this order.
  /// </summary>
  public static List<int> OrderAlong(IReadOnlyList<P3> points, IReadOnlyList<IReadOnlyList<P3>> lines)
  {
    var keys = new List<(int Index, int Line, double Along)>();
    for (var k = 0; k < points.Count; k++)
    {
      var p = points[k];
      int bestLine = -1; double bestD = double.PositiveInfinity, bestAlong = 0;
      for (var li = 0; li < lines.Count; li++)
      {
        var line = lines[li]; var along = 0.0;
        for (var i = 1; i < line.Count; i++)
        {
          var a = line[i - 1]; var b = line[i];
          var ex = b.X - a.X; var ey = b.Y - a.Y; var l2 = ex * ex + ey * ey; var l = Math.Sqrt(l2);
          var t = l2 < 1e-18 ? 0 : Math.Clamp(((p.X - a.X) * ex + (p.Y - a.Y) * ey) / l2, 0, 1);
          var qx = a.X + ex * t - p.X; var qy = a.Y + ey * t - p.Y;
          var d = Math.Sqrt(qx * qx + qy * qy);
          if (d < bestD - 1e-12) { bestD = d; bestLine = li; bestAlong = along + t * l; }
          along += l;
        }
      }
      keys.Add((k, bestLine, bestAlong));
    }
    return keys.OrderBy(x => x.Line).ThenBy(x => x.Along).Select(x => x.Index).ToList();
  }

  /// <summary>
  /// How closely the built surface follows the valley line between the points where sections end on it. The corridor has
  /// section points only at its applied stations; between two neighbouring valley ends (in the order they lie along the line)
  /// the surface is a straight chord, and where the valley line bends in between (the drain corner, the hinge, a bench edge)
  /// the chord cuts across it. At a small bend the sections cross the valley at a flat angle, so ends 1 m apart in station can
  /// be several metres apart along the valley. Each chord is sampled every 'step' metres; returns the largest plan distance and
  /// the largest level difference from the valley line, and the pair of stations where the plan distance is largest.
  /// </summary>
  public static JoinGap ChainDeviation(IReadOnlyList<(double Station, P3 P)> chain, IReadOnlyList<IReadOnlyList<P3>> lines, double step = 0.1)
  {
    double plan = 0, level = 0, longest = 0; double? atA = null, atB = null; var chords = 0;
    for (var i = 0; i + 1 < chain.Count; i++)
    {
      var (sa, a) = chain[i]; var (sb, b) = chain[i + 1];
      chords++;
      var len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
      longest = Math.Max(longest, len);
      var n = Math.Max(1, (int)Math.Ceiling(len / Math.Max(step, 1e-3)));
      for (var k = 0; k <= n; k++)
      {
        var t = (double)k / n;
        var q = new P3(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
        var (d, z) = Nearest(lines, q);
        if (d > plan) { plan = d; atA = sa; atB = sb; }
        if (!double.IsNaN(z)) level = Math.Max(level, Math.Abs(q.Z - z));
      }
    }
    return new JoinGap(plan, level, atA, atB, chords, longest);
  }

  /// <summary>
  /// Whether the applied stations can show a bowtie at an angle point at all. The two legs' sections overlap only within
  /// PI ± reach·tan(turn/2); two links cross in plan only when one station of each leg lies in that window. With the stations
  /// further apart than that (a 5 m frequency against a 3 m overlap), the built corridor has no section in the overlap and the
  /// crossing test sees nothing - that is missing evidence, not a clean bend.
  /// </summary>
  public static Coverage AngleCoverage(double pi, double reach, double turnRadians, IReadOnlyList<double> stations, double eps = 1e-3)
  {
    var half = Math.Max(0, reach) * Math.Tan(Math.Abs(turnRadians) / 2);
    double? before = null, after = null;
    foreach (var s in stations)
    {
      if (s < pi - eps && s >= pi - half && (before == null || s > before)) before = s;
      if (s > pi + eps && s <= pi + half && (after == null || s < after)) after = s;
    }
    var nearestBefore = stations.Where(s => s < pi - eps).DefaultIfEmpty(double.NaN).Max();
    var nearestAfter = stations.Where(s => s > pi + eps).DefaultIfEmpty(double.NaN).Min();
    return new Coverage(half, before.HasValue && after.HasValue, before, after,
      double.IsNaN(nearestBefore) ? null : nearestBefore, double.IsNaN(nearestAfter) ? null : nearestAfter);
  }

  /// <summary>On a curve every inside section passes through the centre of curvature, so two sections on the arc cross as soon
  /// as they reach past the radius. Evidence needs the reach to stay inside the radius, or two applied stations on the arc.</summary>
  public static Coverage CurveCoverage(double from, double to, double reach, double turnRadians, IReadOnlyList<double> stations)
  {
    var radius = Math.Abs(turnRadians) > 1e-9 ? (to - from) / Math.Abs(turnRadians) : double.PositiveInfinity;
    var on = stations.Where(s => s >= from && s <= to).ToList();
    if (reach < radius) return new Coverage(0, true, on.Count > 0 ? on.Min() : null, on.Count > 0 ? on.Max() : null, null, null, radius);
    return new Coverage(0.5 * (to - from), on.Count >= 2, on.Count > 0 ? on.Min() : null, on.Count > 1 ? on.Max() : null, null, null, radius);
  }

  /// <summary>Proper plan crossings (more than 'tol' from every end) of a segment with a polyline; the parameters along the segment.</summary>
  public static List<double> SegmentPolyline(P3 a, P3 b, IReadOnlyList<P3> line, double tol)
  {
    var hits = new List<double>();
    var ex = b.X - a.X; var ey = b.Y - a.Y; var le = Math.Sqrt(ex * ex + ey * ey);
    if (le < 1e-12) return hits;
    for (var i = 0; i + 1 < line.Count; i++)
    {
      var c = line[i]; var d = line[i + 1];
      var fx = d.X - c.X; var fy = d.Y - c.Y; var lf = Math.Sqrt(fx * fx + fy * fy);
      if (lf < 1e-12) continue;
      var den = ex * fy - ey * fx;
      if (Math.Abs(den) < 1e-12) continue;
      var wx = c.X - a.X; var wy = c.Y - a.Y;
      var t = (wx * fy - wy * fx) / den; var u = (wx * ey - wy * ex) / den;
      // the segment's own ends decide 'touching' (a link ending on the valley line); along the valley line only its two
      // extreme ends count as ends, not the vertices between its segments
      var uEndTol = tol / lf;
      var atLineStart = i == 0 && u <= uEndTol; var atLineEnd = i + 2 == line.Count && u >= 1 - uEndTol;
      if (t * le > tol && (1 - t) * le > tol && u >= -1e-9 && u <= 1 + 1e-9 && !atLineStart && !atLineEnd) hits.Add(t);
    }
    return hits;
  }
}

public sealed record JoinGap(double Plan, double Level, double? BetweenA, double? BetweenB, int Chords, double LongestChord);

public sealed record Coverage(double HalfLength, bool Covered, double? StationBefore, double? StationAfter, double? NearestBefore, double? NearestAfter, double? Radius = null);
