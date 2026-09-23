namespace Utnm.BowtieKernel;

/// <summary>
/// What of a valley line belongs in the corridor surface as a breakline, and the plain geometry used to check that the
/// surface is closed and follows it. The corridor surface is a TIN of the section points: between two stations it is a
/// straight triangle, and where the valley bends between two section ends (the hinge, a bench edge, the drain corner) the
/// triangles cut across it - 2-18 cm off the valley on FL-02 / FL-03. The valley line as a breakline makes the TIN follow it.
/// Only the part of the valley that is a seam between the two sides' daylight is wanted:
/// - not the level run-on past its end (beyond both sides' daylight, outside the corridor);
/// - not the stretch inside the centre part (the drain): the valley starts at the bend point on the baseline, and across the
///   drain it would put a lid on the channel at lane level.
/// </summary>
public static class SurfaceLines
{
  /// <summary>
  /// The parts of 'line' where clearance(p) &gt;= 0 (outside the centre part), after the run-on is cut off. The cut points are
  /// found to 1 mm along each segment; the level is interpolated along the segment. Parts shorter than minLength are dropped.
  /// </summary>
  public static List<List<P3>> Trim(IReadOnlyList<P3> line, double? runOn, Func<P2, double> clearance, double minLength = 0.05, double step = 0.05)
  {
    var src = Refresh.WithoutRunOn(line, runOn);
    var parts = new List<List<P3>>();
    if (src.Count < 2) return parts;
    List<P3>? cur = null;
    bool Inside(P3 p) => clearance(new P2(p.X, p.Y)) >= 0;
    P3 Lerp(P3 a, P3 b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
    P3 Cut(P3 a, P3 b, bool aInside)
    {
      double lo = 0, hi = 1;
      for (var k = 0; k < 40; k++)
      {
        var mid = 0.5 * (lo + hi);
        if (Inside(Lerp(a, b, mid)) == aInside) lo = mid; else hi = mid;
      }
      return Lerp(a, b, 0.5 * (lo + hi));
    }
    void Close() { if (cur != null && Length(cur) >= minLength) parts.Add(cur); cur = null; }
    if (Inside(src[0])) cur = new List<P3> { src[0] };
    for (var i = 1; i < src.Count; i++)
    {
      var a = src[i - 1]; var b = src[i];
      var len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
      var n = Math.Max(1, (int)Math.Ceiling(len / step));
      var prev = a; var prevIn = Inside(a);
      for (var k = 1; k <= n; k++)
      {
        var q = k == n ? b : Lerp(a, b, (double)k / n);
        var qIn = Inside(q);
        if (qIn != prevIn)
        {
          var c = Cut(prev, q, prevIn);
          if (prevIn) { cur!.Add(c); Close(); }
          else cur = new List<P3> { c };
        }
        prevIn = qIn; prev = q;
      }
      if (prevIn) { if (cur == null) cur = new List<P3>(); if (cur.Count == 0 || !Same(cur[^1], b)) cur.Add(b); }
    }
    Close();
    return parts;
  }

  /// <summary>A short fingerprint of a polyline's points rounded to the millimetre (FNV-1a, 64 bit, hex): kept in the surface
  /// breakline's description so an unchanged valley is not taken out and put back (and the corridor rebuilt) for nothing.</summary>
  public static string Fingerprint(IReadOnlyList<P3> line)
  {
    unchecked
    {
      var h = 14695981039346656037UL;
      void Add(long v) { for (var k = 0; k < 8; k++) { h ^= (byte)(v >> (8 * k)); h *= 1099511628211UL; } }
      Add(line.Count);
      foreach (var p in line) { Add((long)Math.Round(p.X * 1000)); Add((long)Math.Round(p.Y * 1000)); Add((long)Math.Round(p.Z * 1000)); }
      return h.ToString("x16");
    }
  }

  /// <summary>Plan length of a polyline.</summary>
  public static double Length(IReadOnlyList<P3> line)
  {
    var len = 0.0;
    for (var i = 1; i < line.Count; i++) len += Math.Sqrt((line[i].X - line[i - 1].X) * (line[i].X - line[i - 1].X) + (line[i].Y - line[i - 1].Y) * (line[i].Y - line[i - 1].Y));
    return len;
  }

  private static bool Same(P3 a, P3 b) => Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9 && Math.Abs(a.Z - b.Z) < 1e-9;

  /// <summary>Points every 'step' metres along a polyline, and every vertex.</summary>
  public static List<P3> Samples(IReadOnlyList<P3> line, double step = 0.25)
  {
    var pts = new List<P3>();
    if (line.Count == 0) return pts;
    pts.Add(line[0]);
    for (var i = 1; i < line.Count; i++)
    {
      var a = line[i - 1]; var b = line[i];
      var len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
      var n = Math.Max(1, (int)Math.Ceiling(len / step));
      for (var k = 1; k <= n; k++) { var t = (double)k / n; pts.Add(new P3(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t)); }
    }
    return pts;
  }

  /// <summary>Nearest plan distance from p to a baseline given by its samples, and the station there.</summary>
  public static (double Station, double Distance) NearestOnBaseline(SampledBaseline bl, P2 p)
  {
    var best = double.PositiveInfinity; var at = bl.Start;
    for (var i = 1; i < bl.S.Length; i++)
    {
      double ax = bl.X[i - 1], ay = bl.Y[i - 1], bx = bl.X[i], by = bl.Y[i];
      var ex = bx - ax; var ey = by - ay; var l2 = ex * ex + ey * ey;
      var t = l2 < 1e-18 ? 0 : Math.Clamp(((p.X - ax) * ex + (p.Y - ay) * ey) / l2, 0, 1);
      var qx = ax + ex * t - p.X; var qy = ay + ey * t - p.Y;
      var d = qx * qx + qy * qy;
      if (d < best) { best = d; at = bl.S[i - 1] + (bl.S[i] - bl.S[i - 1]) * t; }
    }
    return (at, Math.Sqrt(best));
  }

  /// <summary>Plan area of a closed polygon (shoelace; the closing vertex may be repeated or not).</summary>
  public static double PolygonArea(IReadOnlyList<P2> ring)
  {
    var n = ring.Count;
    if (n >= 2 && Math.Abs(ring[0].X - ring[n - 1].X) < 1e-9 && Math.Abs(ring[0].Y - ring[n - 1].Y) < 1e-9) n--;
    var a = 0.0;
    for (var i = 0; i < n; i++) { var p = ring[i]; var q = ring[(i + 1) % n]; a += p.X * q.Y - q.X * p.Y; }
    return Math.Abs(a) / 2;
  }

  /// <summary>Largest plan distance between two closed polygons, both ways (a Hausdorff distance on their edges): how far a
  /// stored boundary is from the corridor's outline as it is now.</summary>
  public static double RingDeviation(IReadOnlyList<P2> a, IReadOnlyList<P2> b, double step = 0.5)
  {
    if (a.Count < 2 || b.Count < 2) return double.PositiveInfinity;
    IReadOnlyList<P3> A = Close(a), B = Close(b);
    return Refresh.Deviation(A, B, step).Plan;
  }

  private static List<P3> Close(IReadOnlyList<P2> r)
  {
    var l = r.Select(p => new P3(p.X, p.Y, 0)).ToList();
    if (Math.Abs(r[0].X - r[^1].X) > 1e-9 || Math.Abs(r[0].Y - r[^1].Y) > 1e-9) l.Add(new P3(r[0].X, r[0].Y, 0));
    return l;
  }
}
