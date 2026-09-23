namespace Utnm.BowtieKernel;

public readonly record struct P2(double X, double Y)
{
  public static P2 operator +(P2 a, P2 b) => new(a.X + b.X, a.Y + b.Y);
  public static P2 operator -(P2 a, P2 b) => new(a.X - b.X, a.Y - b.Y);
  public static P2 operator *(P2 a, double k) => new(a.X * k, a.Y * k);
  public double Dot(P2 b) => X * b.X + Y * b.Y;
  public double Cross(P2 b) => X * b.Y - Y * b.X;
  public double Length => Math.Sqrt(X * X + Y * Y);
  public P2 Unit() { var l = Length; return l < 1e-15 ? this : new P2(X / l, Y / l); }
  public P2 Left() => new(-Y, X);
}

public enum Side { Left, Right }

/// <summary>One horizontal element of a baseline. Curvature is signed: positive turns left.</summary>
public sealed record Element(double Length, double K0, double K1)
{
  public static Element Line(double length) => new(length, 0, 0);
  /// <summary>radius &gt; 0; left = true turns left.</summary>
  public static Element Arc(double length, double radius, bool left) => new(length, (left ? 1 : -1) / radius, (left ? 1 : -1) / radius);
  /// <summary>Clothoid from radius rIn to rOut (double.PositiveInfinity = straight).</summary>
  public static Element Spiral(double length, double rIn, double rOut, bool left)
  {
    double K(double r) => double.IsInfinity(r) ? 0.0 : (left ? 1 : -1) / r;
    return new Element(length, K(rIn), K(rOut));
  }
}

/// <summary>
/// A baseline held as dense samples (station, x, y, tangent angle). An angle point is two samples at the same station with
/// different angles. Everything the solver needs from the baseline goes through here, so the plugin only has to fill the
/// arrays from Civil 3D (StationOffsetElevationToXYZ + GetDirectionAtStation) and offline tests can build them exactly.
/// </summary>
public sealed class SampledBaseline
{
  public readonly double[] S, X, Y, Th;
  /// <summary>Unwrapped tangent angle with the corner jumps removed (for smooth curvature).</summary>
  private readonly double[] _smooth;
  /// <summary>Unwrapped tangent angle including the jumps.</summary>
  private readonly double[] _unwrapped;
  private readonly double[] _cos, _sin;
  public readonly List<(int Index, double Station, double Jump)> Corners = new();

  public const double CornerThreshold = 0.5 * Math.PI / 180.0;

  public SampledBaseline(double[] s, double[] x, double[] y, double[] th)
  {
    if (s.Length < 2 || x.Length != s.Length || y.Length != s.Length || th.Length != s.Length)
      throw new ArgumentException("A baseline needs at least two samples and arrays of one length.");
    S = s; X = x; Y = y; Th = th;
    _smooth = new double[s.Length];
    _unwrapped = new double[s.Length];
    _smooth[0] = _unwrapped[0] = th[0];
    _cos = th.Select(Math.Cos).ToArray();
    _sin = th.Select(Math.Sin).ToArray();
    for (var i = 1; i < s.Length; i++)
    {
      var d = Wrap(th[i] - th[i - 1]);
      _unwrapped[i] = _unwrapped[i - 1] + d;
      var corner = s[i] - s[i - 1] < 1e-9 && Math.Abs(d) > CornerThreshold;
      if (corner) Corners.Add((i, s[i], d));
      _smooth[i] = _smooth[i - 1] + (corner ? 0.0 : d);
    }
  }

  public double Start => S[0];
  public double End => S[^1];

  public static double Wrap(double a)
  {
    while (a > Math.PI) a -= 2 * Math.PI;
    while (a <= -Math.PI) a += 2 * Math.PI;
    return a;
  }

  // ---------------------------------------------------------------- builders

  public static SampledBaseline FromElements(IEnumerable<Element> elements, P2 start, double startAngle, double startStation = 0.0, double ds = 0.05)
  {
    var s = new List<double> { startStation };
    var x = new List<double> { start.X };
    var y = new List<double> { start.Y };
    var th = new List<double> { startAngle };
    foreach (var e in elements)
    {
      var n = Math.Max(1, (int)Math.Round(e.Length / ds));
      var h = e.Length / n;
      var th0 = th[^1];
      double Theta(double u) => th0 + e.K0 * u + (e.K1 - e.K0) * u * u / (2 * e.Length);
      for (var i = 0; i < n; i++)
      {
        // Simpson over the step, in 4 sub-steps
        double ix = 0, iy = 0;
        const int m = 4;
        var hh = h / m;
        for (var k = 0; k < m; k++)
        {
          var a = i * h + k * hh;
          ix += hh / 6 * (Math.Cos(Theta(a)) + 4 * Math.Cos(Theta(a + hh / 2)) + Math.Cos(Theta(a + hh)));
          iy += hh / 6 * (Math.Sin(Theta(a)) + 4 * Math.Sin(Theta(a + hh / 2)) + Math.Sin(Theta(a + hh)));
        }
        s.Add(s[^1] + h); x.Add(x[^1] + ix); y.Add(y[^1] + iy); th.Add(Theta((i + 1) * h));
      }
    }
    return new SampledBaseline(s.ToArray(), x.ToArray(), y.ToArray(), th.ToArray());
  }

  /// <summary>Straight segments between vertices; every interior vertex is an angle point.</summary>
  public static SampledBaseline FromPolyline(IReadOnlyList<P2> vertices, double startStation = 0.0, double ds = 0.05)
  {
    var s = new List<double>(); var x = new List<double>(); var y = new List<double>(); var th = new List<double>();
    var station = startStation;
    for (var v = 0; v + 1 < vertices.Count; v++)
    {
      var a = vertices[v]; var b = vertices[v + 1];
      var len = (b - a).Length;
      if (len < 1e-9) continue;
      var ang = Math.Atan2(b.Y - a.Y, b.X - a.X);
      var n = Math.Max(1, (int)Math.Round(len / ds));
      for (var i = 0; i <= n; i++)
      {
        s.Add(station + len * i / n); x.Add(a.X + (b.X - a.X) * i / n); y.Add(a.Y + (b.Y - a.Y) * i / n); th.Add(ang);
      }
      station += len;
    }
    return new SampledBaseline(s.ToArray(), x.ToArray(), y.ToArray(), th.ToArray());
  }

  /// <summary>Any smooth parametric curve; stations are chord lengths over a fine subdivision.</summary>
  public static SampledBaseline FromFunction(Func<double, P2> f, double t0, double t1, int n, double startStation = 0.0)
  {
    var s = new double[n + 1]; var x = new double[n + 1]; var y = new double[n + 1]; var th = new double[n + 1];
    var dt = (t1 - t0) / n;
    for (var i = 0; i <= n; i++)
    {
      var p = f(t0 + i * dt);
      x[i] = p.X; y[i] = p.Y;
      s[i] = i == 0 ? startStation : s[i - 1] + Math.Sqrt(Math.Pow(x[i] - x[i - 1], 2) + Math.Pow(y[i] - y[i - 1], 2));
      var a = f(t0 + i * dt - dt * 1e-3); var b = f(t0 + i * dt + dt * 1e-3);
      th[i] = Math.Atan2(b.Y - a.Y, b.X - a.X);
    }
    return new SampledBaseline(s, x, y, th);
  }

  /// <summary>
  /// From a host that can only answer "where is station s" (Civil 3D: Baseline.StationOffsetElevationToXYZ). Points are read
  /// every 0.01 m; a sample is kept every 0.05 m with the direction of the central chord (+/- 0.01 m). An angle point is found
  /// where two consecutive 0.01 m chords differ by more than 1 degree (a smooth curve would need a radius under 0.6 m for
  /// that), placed at the intersection of the clean chords either side of it, and stored as two samples at one station.
  /// </summary>
  public static SampledBaseline FromStationFunction(Func<double, P2> pointAt, double from, double to)
  {
    const double fine = 0.01; const int every = 5;
    var n = Math.Max(10, (int)Math.Floor((to - from) / fine + 1e-9));
    var px = new double[n + 1]; var py = new double[n + 1];
    for (var i = 0; i <= n; i++) { var p = pointAt(Math.Min(to, from + i * fine)); px[i] = p.X; py[i] = p.Y; }
    double Chord(int a, int b) => Math.Atan2(py[b] - py[a], px[b] - px[a]);

    var corners = new List<(double Station, double X, double Y, double Before, double After)>();
    for (var j = 2; j + 2 <= n; j++)
    {
      if (Math.Abs(Wrap(Chord(j, j + 1) - Chord(j - 1, j))) <= Math.PI / 180) continue;
      // the corner lies within (j-1, j+1); the clean chords are (j-2, j-1) and (j+1, j+2)
      var before = Chord(j - 2, j - 1); var after = Chord(j + 1, j + 2);
      var den = Math.Cos(before) * Math.Sin(after) - Math.Sin(before) * Math.Cos(after);
      if (Math.Abs(den) < 1e-9) continue;
      var t = ((px[j + 2] - px[j - 2]) * Math.Sin(after) - (py[j + 2] - py[j - 2]) * Math.Cos(after)) / den;
      corners.Add((from + (j - 2) * fine + t, px[j - 2] + Math.Cos(before) * t, py[j - 2] + Math.Sin(before) * t, before, after));
      j += 3;
    }

    var s = new List<double>(); var x = new List<double>(); var y = new List<double>(); var th = new List<double>();
    var next = 0;
    for (var i = 0; i <= n; i += every)
    {
      var st = from + i * fine;
      while (next < corners.Count && corners[next].Station <= st + 1e-9)
      {
        var c = corners[next++];
        if (s.Count > 0 && c.Station - s[^1] < 1e-6) { s.RemoveAt(s.Count - 1); x.RemoveAt(x.Count - 1); y.RemoveAt(y.Count - 1); th.RemoveAt(th.Count - 1); }
        s.Add(c.Station); x.Add(c.X); y.Add(c.Y); th.Add(c.Before);
        s.Add(c.Station); x.Add(c.X); y.Add(c.Y); th.Add(c.After);
      }
      if (s.Count > 0 && st - s[^1] < 1e-6) continue;
      // next to an angle point the central chord would straddle it: use that leg's own direction
      double? dir = null;
      foreach (var c in corners) if (Math.Abs(c.Station - st) < 3 * fine) dir = st < c.Station ? c.Before : c.After;
      s.Add(st); x.Add(px[i]); y.Add(py[i]); th.Add(dir ?? Chord(Math.Max(0, i - 1), Math.Min(n, i + 1)));
    }
    return new SampledBaseline(s.ToArray(), x.ToArray(), y.ToArray(), th.ToArray());
  }

  // ---------------------------------------------------------------- queries

  /// <summary>Index i with S[i] &lt;= s &lt;= S[i+1] and S[i+1] &gt; S[i]. 'before' picks the interval ending at a corner station.</summary>
  private int Interval(double s, bool before)
  {
    // last index whose station is <= s (or < s when 'before')
    var lo = 0; var hi = S.Length - 1;
    while (lo < hi)
    {
      var mid = (lo + hi + 1) / 2;
      if (before ? S[mid] < s : S[mid] <= s) lo = mid; else hi = mid - 1;
    }
    var i = Math.Min(lo, S.Length - 2);
    if (before) { while (i > 0 && S[i + 1] - S[i] < 1e-9) i--; }
    else { while (i < S.Length - 2 && S[i + 1] - S[i] < 1e-9) i++; }
    while (i > 0 && S[i + 1] - S[i] < 1e-9) i--;
    return i;
  }

  public (P2 P, double Theta) At(double s, bool before = false)
  {
    s = Math.Clamp(s, Start, End);
    var i = Interval(s, before);
    var w = S[i + 1] - S[i] < 1e-12 ? 0.0 : (s - S[i]) / (S[i + 1] - S[i]);
    var th = Th[i] + Wrap(Th[i + 1] - Th[i]) * w;
    return (new P2(X[i] + (X[i + 1] - X[i]) * w, Y[i] + (Y[i + 1] - Y[i]) * w), th);
  }

  public static P2 Tangent(double theta) => new(Math.Cos(theta), Math.Sin(theta));
  public static P2 InsideNormal(double theta, Side side) => side == Side.Left ? new P2(-Math.Sin(theta), Math.Cos(theta)) : new P2(Math.Sin(theta), -Math.Cos(theta));

  private double SmoothAt(double s)
  {
    s = Math.Clamp(s, Start, End);
    var i = Interval(s, false);
    var w = S[i + 1] - S[i] < 1e-12 ? 0.0 : (s - S[i]) / (S[i + 1] - S[i]);
    return _smooth[i] + (_smooth[i + 1] - _smooth[i]) * w;
  }

  private double UnwrappedAt(double s, bool before)
  {
    s = Math.Clamp(s, Start, End);
    var i = Interval(s, before);
    var w = S[i + 1] - S[i] < 1e-12 ? 0.0 : (s - S[i]) / (S[i + 1] - S[i]);
    return _unwrapped[i] + (_unwrapped[i + 1] - _unwrapped[i]) * w;
  }

  /// <summary>Signed curvature (positive = turning left) of the smooth part of the baseline; angle points do not count.</summary>
  public double Kappa(double s, double h = 0.25)
  {
    var a = Math.Max(Start, s - h); var b = Math.Min(End, s + h);
    return b - a < 1e-9 ? 0.0 : (SmoothAt(b) - SmoothAt(a)) / (b - a);
  }

  /// <summary>Signed direction change between two stations, angle points left out.</summary>
  public double SmoothTurn(double from, double to) => SmoothAt(to) - SmoothAt(from);

  /// <summary>Signed direction change between two stations, angle points included.</summary>
  public double Turn(double from, double to) => UnwrappedAt(to, false) - UnwrappedAt(from, true);

  /// <summary>
  /// Stations whose section line passes through p on the given side, inside [from, to], before the section's focal point.
  /// f(s) = (p - c(s)) . t(s) falls through zero at such a station (df/ds = -1 + kappa * offset &lt; 0). A rising zero is a
  /// section already past its centre of curvature, and the jump at an angle point is always rising on the inside, so both
  /// are left out by taking falling zeros only.
  /// </summary>
  public List<(double S, double Offset)> Feet(P2 p, double from, double to, Side side, double maxOffset = double.MaxValue)
  {
    var result = new List<(double, double)>();
    from = Math.Max(from, Start); to = Math.Min(to, End);
    if (to <= from) return result;
    var i0 = Interval(from, false); var i1 = Interval(to, true);
    double F(int i) => (p.X - X[i]) * _cos[i] + (p.Y - Y[i]) * _sin[i];
    for (var i = i0; i <= i1; i++)
    {
      if (S[i + 1] - S[i] < 1e-9) continue;
      var fa = F(i); var fb = F(i + 1);
      if (!(fa >= 0 && fb < 0)) continue;
      double lo = S[i], hi = S[i + 1];
      for (var k = 0; k < 30; k++)
      {
        var mid = 0.5 * (lo + hi);
        var (c, th) = At(mid, false);
        var f = (p.X - c.X) * Math.Cos(th) + (p.Y - c.Y) * Math.Sin(th);
        if (f >= 0) lo = mid; else hi = mid;
      }
      var s = 0.5 * (lo + hi);
      if (s < from - 1e-9 || s > to + 1e-9) continue;
      var (cs, ths) = At(s, false);
      var off = (p - cs).Dot(InsideNormal(ths, side));
      if (off < -1e-9 || off > maxOffset) continue;
      result.Add((s, off));
    }
    return result;
  }
}
