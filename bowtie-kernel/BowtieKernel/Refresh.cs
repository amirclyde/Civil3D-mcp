namespace Utnm.BowtieKernel;

public readonly record struct P3(double X, double Y, double Z);

/// <summary>How far one design section is from another at the same station, over the offsets both cover.</summary>
public sealed record SectionDifference(double MaxLevel, double AtOffset, double ReachDiff, double StartDiff, double CommonFrom, double CommonTo)
{
  public bool Within(double levelTol, double reachTol) => MaxLevel <= levelTol && Math.Abs(ReachDiff) <= reachTol && Math.Abs(StartDiff) <= reachTol && CommonTo > CommonFrom;
}

/// <summary>Plain geometry used when an existing valley repair is refreshed after a design change.</summary>
public static class Refresh
{
  /// <summary>
  /// The largest plan distance and the largest level difference between two 3D polylines, both ways (a symmetric
  /// Hausdorff distance, sampled every 'step' metres along each line and at every vertex). The level difference is taken
  /// between each sample and the nearest point of the other line in plan. Two lines with the same shape but different
  /// vertices give 0.
  /// </summary>
  public static (double Plan, double Level) Deviation(IReadOnlyList<P3> a, IReadOnlyList<P3> b, double step = 0.25)
  {
    if (a.Count == 0 || b.Count == 0) return (double.PositiveInfinity, double.PositiveInfinity);
    var (pa, la) = OneWay(a, b, step);
    var (pb, lb) = OneWay(b, a, step);
    return (Math.Max(pa, pb), Math.Max(la, lb));
  }

  private static (double Plan, double Level) OneWay(IReadOnlyList<P3> from, IReadOnlyList<P3> to, double step)
  {
    double plan = 0, level = 0;
    foreach (var p in Samples(from, step))
    {
      var (d, z) = Nearest(to, p);
      plan = Math.Max(plan, d);
      level = Math.Max(level, Math.Abs(p.Z - z));
    }
    return (plan, level);
  }

  private static IEnumerable<P3> Samples(IReadOnlyList<P3> line, double step)
  {
    yield return line[0];
    for (var i = 1; i < line.Count; i++)
    {
      var a = line[i - 1]; var b = line[i];
      var len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
      var n = step > 0 ? (int)Math.Floor(len / step) : 0;
      for (var k = 1; k <= n; k++)
      {
        var t = k * step / len;
        if (t >= 1) break;
        yield return new P3(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
      }
      yield return b;
    }
  }

  /// <summary>Plan distance from p to the nearest point of the polyline, and the polyline's level there.</summary>
  public static (double Distance, double Z) Nearest(IReadOnlyList<P3> line, P3 p)
  {
    if (line.Count == 1) return (Math.Sqrt((p.X - line[0].X) * (p.X - line[0].X) + (p.Y - line[0].Y) * (p.Y - line[0].Y)), line[0].Z);
    var best = double.PositiveInfinity; var bestZ = line[0].Z;
    for (var i = 1; i < line.Count; i++)
    {
      var a = line[i - 1]; var b = line[i];
      var ex = b.X - a.X; var ey = b.Y - a.Y;
      var l2 = ex * ex + ey * ey;
      var t = l2 < 1e-18 ? 0 : Math.Clamp(((p.X - a.X) * ex + (p.Y - a.Y) * ey) / l2, 0, 1);
      var qx = a.X + ex * t; var qy = a.Y + ey * t;
      var d = Math.Sqrt((p.X - qx) * (p.X - qx) + (p.Y - qy) * (p.Y - qy));
      if (d < best - 1e-12) { best = d; bestZ = a.Z + (b.Z - a.Z) * t; }
    }
    return (best, bestZ);
  }

  /// <summary>
  /// Two sections at one station compared as design surfaces: the largest level difference over the offsets both cover
  /// (sampled every 'step' metres and at every vertex of either), and how far their start and their reach differ. At a
  /// region boundary the section of a repaired region (its clip assembly, targets cleared) and the section of the region it
  /// was cut from (the stock assembly) must agree: if they do not, the clip assembly is out of date with the stock one.
  /// </summary>
  public static SectionDifference Compare(SectionSample a, SectionSample b, double step = 0.1)
  {
    var from = Math.Max(a.StartOffset, b.StartOffset);
    var to = Math.Min(a.Reach, b.Reach);
    var maxLevel = 0.0; var at = from;
    if (to > from)
    {
      var offsets = new SortedSet<double>();
      for (var o = from; o <= to + 1e-9; o += step) offsets.Add(Math.Min(o, to));
      offsets.Add(to);
      foreach (var (o, _) in a.Template) if (o > from && o < to) offsets.Add(o);
      foreach (var (o, _) in b.Template) if (o > from && o < to) offsets.Add(o);
      foreach (var o in offsets)
      {
        var za = a.Dz(o, 0); var zb = b.Dz(o, 0);
        if (!za.HasValue || !zb.HasValue) continue;
        var d = Math.Abs(za.Value - zb.Value);
        if (d > maxLevel) { maxLevel = d; at = o; }
      }
    }
    else maxLevel = double.PositiveInfinity;
    return new SectionDifference(maxLevel, at, a.Reach - b.Reach, a.StartOffset - b.StartOffset, from, to);
  }
}
