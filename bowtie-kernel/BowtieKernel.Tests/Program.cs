using Utnm.BowtieKernel;

// Dependency-free test runner for the bowtie seam kernel. `dotnet run` runs everything; the exit code is the number of
// failed tests. Every expected value below is a closed form or comes from an independent calculation noted beside it.

var failed = 0; var passed = 0;
var notes = new List<string>();
void Note(string text) => notes.Add(text);
void Test(string name, Action body)
{
  var sw = System.Diagnostics.Stopwatch.StartNew();
  notes.Clear();
  try { body(); passed++; Console.WriteLine($"  ok    {name}  ({sw.ElapsedMilliseconds} ms)"); }
  catch (Exception ex) { failed++; Console.WriteLine($"  FAIL  {name}\n        {ex.Message}"); if (ex is not InvalidOperationException && ex.GetType() != typeof(Exception)) Console.WriteLine(ex.StackTrace); }
  foreach (var n in notes) Console.WriteLine($"        {n}");
}
void Near(double actual, double expected, double tol, string what)
{
  if (double.IsNaN(actual) || Math.Abs(actual - expected) > tol) throw new Exception($"{what}: expected {expected:0.####} +/- {tol}, got {actual:0.####}");
}
void True(bool condition, string what) { if (!condition) throw new Exception(what); }

const double Deg = Math.PI / 180;

// ---------------------------------------------------------------------------------------------------- fixtures

// A lane to the hinge, then one daylight slope to the ground (fill or cut decided at the hinge, like the stock part).
SectionSet MakeSections(SampledBaseline bl, Side side, IEnumerable<double> stations, Func<double, double> profile,
  Func<double, double, double> ground, double hinge = 3.0, double crossfall = -0.02, double slope = 0.5, Func<double, double>? hingeAt = null,
  Func<double, double>? startAt = null)
{
  var list = new List<SectionSample>();
  foreach (var s in stations)
  {
    var (c, th) = bl.At(s);
    var n = SampledBaseline.InsideNormal(th, side);
    var hg = hingeAt?.Invoke(s) ?? hinge;
    var z0 = profile(s);
    var zh = z0 + crossfall * hg;
    var ph = c + n * hg;
    var k = zh > ground(ph.X, ph.Y) ? -slope : slope;
    double D(double o) { var p = c + n * o; return zh + k * (o - hg) - ground(p.X, p.Y); }
    double a = hg, b = hg; var d0 = D(hg);
    while (b < hg + 200 && D(b) * d0 > 0) { a = b; b += 0.05; }
    for (var i = 0; i < 60; i++) { var mid = 0.5 * (a + b); if (D(mid) * d0 > 0) a = mid; else b = mid; }
    var od = 0.5 * (a + b);
    var st0 = startAt?.Invoke(s) ?? 0.0;
    list.Add(new SectionSample { Station = s, Z0 = z0, Template = { (st0, crossfall * st0), (hg, crossfall * hg), (od, zh + k * (od - hg) - z0) } });
  }
  return new SectionSet(list);
}
IEnumerable<double> Every(double from, double to, double step, params double[] extra) =>
  Enumerable.Range(0, (int)Math.Floor((to - from) / step + 1e-9) + 1).Select(i => from + i * step).Concat(extra).Distinct().OrderBy(x => x);

SampledBaseline Scs(double r, double ls, double la, double lt, bool left, double ds = 0.05)
{
  var el = new List<Element> { Element.Line(lt) };
  if (ls > 0) el.Add(Element.Spiral(ls, double.PositiveInfinity, r, left));
  el.Add(Element.Arc(la, r, left));
  if (ls > 0) el.Add(Element.Spiral(ls, r, double.PositiveInfinity, left));
  el.Add(Element.Line(lt));
  return SampledBaseline.FromElements(el, new P2(1000, 2000), 20 * Deg, 100.0, ds);
}
SampledBaseline AnglePoint(double deflectionDeg, bool left, double leg = 60)
{
  var a0 = 15 * Deg; var a1 = a0 + (left ? 1 : -1) * deflectionDeg * Deg;
  var pi = new P2(500, 800);
  return SampledBaseline.FromPolyline(new[] { pi - SampledBaseline.Tangent(a0) * leg, pi, pi + SampledBaseline.Tangent(a1) * leg }, 100.0);
}
double ClipAt(SeamResult r, double station) => r.Stations.Single(x => Math.Abs(x.Station - station) < 1e-6).ClipOffset ?? double.NaN;
void Healthy(SeamResult r)
{
  True(r.Status == SeamStatus.Ok, $"status {r.Status}: {string.Join("; ", r.Reasons)}");
  True(r.LinkCrossingsBefore > 0, "the fixture has no bowtie to begin with");
  True(r.LinkCrossingsAfter == 0, $"{r.LinkCrossingsAfter} link crossings remain after the clip");
  True(r.Stations.All(x => x.FirstCrossingIsClip), "a section meets a clip line somewhere other than where it should stop: " + string.Join(" | ", r.Warnings));
  True(r.MaxClosure < 0.005, $"the two sides differ by {r.MaxClosure:0.####} m in level along the seam");
}


var OverlapCells = new List<(double X, double Y, string Feet)>();
// Independent check of the whole point of the exercise: after clipping, is every piece of ground inside the bend covered by
// exactly one section? Counted on a plan grid, using nothing from the solver but the per-station clip offsets.
(double Before, double Overlap, double Gap) Coverage(SampledBaseline bl, SectionSet sec, SeamResult r, Side side, double cell = 0.2, double bias = 0.0)
{
  var st = r.Stations.Where(x => x.Role != "corner").OrderBy(x => x.Station).ToList();
  double Clip(double s)
  {
    var i = st.FindIndex(x => x.Station >= s);
    double Of(StationClip x) => x.ClipOffset.HasValue ? x.ClipOffset.Value + bias : x.Reach;
    if (i <= 0) return Of(i == 0 ? st[0] : st[^1]);
    var a = st[i - 1]; var b = st[i];
    // never interpolate across an angle point: each leg's clip runs down to the PI on its own side (the valley starts there)
    var corner = bl.Corners.Where(c => c.Station > a.Station && c.Station < b.Station).Select(c => (double?)c.Station).FirstOrDefault();
    if (corner.HasValue)
    {
      if (s <= corner.Value) { var wa = (s - a.Station) / (corner.Value - a.Station); var ca = a.ClipOffset.HasValue ? bias : Of(a); return Of(a) * (1 - wa) + ca * wa; }
      var wb = (s - corner.Value) / (b.Station - corner.Value); var cb = b.ClipOffset.HasValue ? bias : Of(b); return cb * (1 - wb) + Of(b) * wb;
    }
    var w = (s - a.Station) / (b.Station - a.Station);
    return Of(a) * (1 - w) + Of(b) * w;
  }
  var from = st[0].Station + 2; var to = st[^1].Station - 2;
  double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
  foreach (var x in st) { var (c, th) = bl.At(x.Station); var q = c + SampledBaseline.InsideNormal(th, side) * x.Reach;
    x0 = Math.Min(x0, Math.Min(c.X, q.X)); x1 = Math.Max(x1, Math.Max(c.X, q.X)); y0 = Math.Min(y0, Math.Min(c.Y, q.Y)); y1 = Math.Max(y1, Math.Max(c.Y, q.Y)); }
  double before = 0, overlap = 0, gap = 0;
  for (var x = x0; x <= x1; x += cell)
    for (var y = y0; y <= y1; y += cell)
    {
      // overlap is judged away from the baseline (every section starts there); a gap is judged with every section counted
      var all = bl.Feet(new P2(x, y), from, to, side);
      var feet = all.Where(f => f.Offset > 0.5).ToList();
      var natural = feet.Count(f => f.Offset <= sec.Reach(f.S));
      if (natural == 0) continue;
      if (natural > 1) before += cell * cell;
      if (feet.Count(f => f.Offset <= Clip(f.S) - 0.05) > 1) { overlap += cell * cell; OverlapCells.Add((x, y, string.Join(",", feet.Where(f => f.Offset <= Clip(f.S) - 0.05).Select(f => $"{f.S:0.00}@{f.Offset:0.00}/{Clip(f.S):0.00}")))); }
      if (all.Count(f => f.Offset <= Clip(f.S) + 0.05) == 0) gap += cell * cell;
    }
  return (before, overlap, gap);
}

// `dotnet run -- solve snapshot.json [result.json]` solves a snapshot written by the plugin instead of running the tests.
if (args.Length >= 2 && args[0] == "solve")
{
  var snap = Snapshot.Load(args[1]);
  var res = SeamSolver.Solve(snap.ToBaseline(), snap.ToSections(), snap.ToGround(), snap.BendFrom, snap.BendTo, new SolveOptions { LevelFromTarget = args.Contains("--level"), GroundCell = snap.Ground?.Cell ?? 0,
    AdjustFrom = args.Contains("--spread") ? AdjustFrom.FromHinge : AdjustFrom.LastLink, LevelRule = args.Contains("--mean") ? LevelRule.Mean : LevelRule.NoSteeper });
  var json = Snapshot.ToJson(ResultReport.Of(res));
  if (args.Length >= 3) File.WriteAllText(args[2], json); else Console.WriteLine(json);
  if (args.Contains("--raw")) foreach (var q in res.SeamRaw) Console.WriteLine($"raw {q.Kind,-6} t={q.T:0.00} u={q.U:0.000} z={q.Z:0.000}  A {q.StationA:0.00}/{q.OffsetA:0.00}  B {q.StationB:0.00}/{q.OffsetB:0.00}");
  return res.Status == SeamStatus.Ok ? 0 : 1;
}

// ---------------------------------------------------------------------------------------------------- baseline

// `dotnet run -- check a.json b.json ...` solves each snapshot as the plugin does (level from the valley line) and measures
// the result independently: ground covered twice / left uncovered after the clip, crossings, the largest level move.
if (args.Length >= 2 && args[0] == "check")
{
  foreach (var f in args.Skip(1).Where(a => !a.StartsWith("--")))
  {
    var snap = Snapshot.Load(f);
    var bl = snap.ToBaseline(); var sec = snap.ToSections();
    var res = SeamSolver.Solve(bl, sec, snap.ToGround(), snap.BendFrom, snap.BendTo, new SolveOptions { LevelFromTarget = true, GroundCell = snap.Ground?.Cell ?? 0,
      AdjustFrom = args.Contains("--spread") ? AdjustFrom.FromHinge : AdjustFrom.LastLink, LevelRule = args.Contains("--mean") ? LevelRule.Mean : LevelRule.NoSteeper });
    var cov = res.Stations.Count > 2 ? Coverage(bl, sec, res, res.Side, 0.25) : (0, 0, 0);
    var bad = res.Stations.Where(x => x.AdjustedSlope.HasValue && Math.Abs(x.AdjustedSlope.Value - (x.OwnSlope ?? 0)) > 0.2).Select(x => x.Station).ToList();
    Console.WriteLine($"{Path.GetFileNameWithoutExtension(f),-20} {res.Status,-16} cross {res.LinkCrossingsBefore,4} -> {res.LinkCrossingsAfter,-3} twice {cov.Item1:0.0} -> {cov.Item2:0.00} m2, gap {cov.Item3:0.00} m2, " +
      $"move {res.MaxLevelAdjust:0.00} m, slope change {res.MaxSlopeChange*100:0}% (spread {res.MaxSpreadGrade*100:0.0}%), steeper at {res.SlopeFlags.Where(x => x.Kind == "steeper").Select(x => x.Station).Distinct().Count()} st, fall reversed at {res.SlopeFlags.Where(x => x.Kind == "fall_reversed").Select(x => x.Station).Distinct().Count()} st, end {res.SeamEnd}, open {res.OpenEnded}{(res.Reasons.Count > 0 ? " | " + res.Reasons[0][..Math.Min(80, res.Reasons[0].Length)] : "")}");
  }
  return 0;
}

// ---------------------------------------------------------------------------------------------------- baseline

Console.WriteLine("baseline");
Test("arc end point and direction are exact", () =>
{
  var bl = SampledBaseline.FromElements(new[] { Element.Arc(12 * Math.PI / 2, 12, true) }, new P2(0, 0), 0);
  var (p, th) = bl.At(bl.End);
  Near(p.X, 12, 1e-6, "x"); Near(p.Y, 12, 1e-6, "y"); Near(th, Math.PI / 2, 1e-9, "direction");
  Near(bl.Kappa(5), 1 / 12.0, 1e-9, "curvature");
});
Test("clothoid matches the Fresnel series", () =>
{
  // x = L - L^5/(40 A^4), y = L^3/(6 A^2) - L^7/(336 A^6), A^2 = R L
  var bl = SampledBaseline.FromElements(new[] { Element.Spiral(8, double.PositiveInfinity, 12, true) }, new P2(0, 0), 0);
  var (p, th) = bl.At(8); const double a2 = 96.0;
  Near(p.X, 8 - Math.Pow(8, 5) / (40 * a2 * a2) + Math.Pow(8, 9) / (3456 * Math.Pow(a2, 4)) - Math.Pow(8, 13) / (599040 * Math.Pow(a2, 6)), 1e-6, "x");
  Near(p.Y, 512 / (6 * a2) - Math.Pow(8, 7) / (336 * Math.Pow(a2, 3)) + Math.Pow(8, 11) / (42240 * Math.Pow(a2, 5)), 1e-6, "y");
  Near(th, 8.0 / 24, 1e-9, "direction");
});
Test("feet: a section past its centre of curvature, or across an angle point, does not count", () =>
{
  var arc = SampledBaseline.FromElements(new[] { Element.Arc(12, 12, true) }, new P2(0, 0), 0);
  True(arc.Feet(new P2(1, 6), 0, 12, Side.Left).Count == 1, "a point inside the radius has one foot");
  True(arc.Feet(new P2(1, 14), 0, 12, Side.Left).Count == 0, "a point beyond the centre has none");
  var ap = AnglePoint(60, true);
  var inside = new P2(500, 800) + SampledBaseline.InsideNormal(15 * Deg + 30 * Deg, Side.Left) * 5;
  var feet = ap.Feet(inside, ap.Start, ap.End, Side.Left);
  True(feet.Count == 2 && feet[0].S < 160 && feet[1].S > 160, $"one foot on each leg, got {feet.Count}");
  Near(feet[0].Offset, 5 * Math.Cos(30 * Deg), 1e-4, "offset on the bisector = t cos(D/2)");
});

Test("baseline read through a station-to-point function (as from Civil 3D): angle point found, same seam as the exact baseline", () =>
{
  var exact = AnglePoint(47.6, true);                                     // PI at station 160, (500, 800)
  var read = SampledBaseline.FromStationFunction(st => exact.At(st).P, 118.003, 201.4);
  True(read.Corners.Count == 1, $"expected one angle point, found {read.Corners.Count}");
  Near(read.Corners[0].Station, 160, 1e-4, "angle point station"); Near(read.Corners[0].Jump, 47.6 * Deg, 1e-6, "deflection");
  var (p, _) = read.At(160); Near(p.X, 500, 1e-4, "PI x"); Near(p.Y, 800, 1e-4, "PI y");
  var sec = MakeSections(exact, Side.Left, Every(130, 190, 1), s => 10 - 0.01 * (s - 130), (_, _) => 6);
  var a = SeamSolver.Solve(exact, sec, (_, _) => 6, 159, 161); var b = SeamSolver.Solve(read, sec, (_, _) => 6, 159, 161);
  True(b.Status == SeamStatus.Ok, $"status {b.Status}: {string.Join("; ", b.Reasons)}");
  Near(a.Stations.Where(q => q.ClipOffset.HasValue).Max(q => Math.Abs(q.ClipOffset!.Value - ClipAt(b, q.Station))), 0, 0.003, "largest clip difference");

  var curve = Scs(12, 8, 6.73, 40, true);
  var fineCurve = Scs(12, 8, 6.73, 40, true, 0.001);                      // stands in for Civil 3D's exact station-to-point answer
  var readCurve = SampledBaseline.FromStationFunction(st => fineCurve.At(st).P, 100.002, 202.7);
  True(readCurve.Corners.Count == 0, "a smooth curve must not produce angle points");
  Near(readCurve.Kappa(151), 1 / 12.0, 1e-5, "curvature on the arc");
  var secC = MakeSections(curve, Side.Left, Every(101, 201, 1), s => 14 - 0.03 * (s - 100), (_, _) => 0);
  var c1 = SeamSolver.Solve(curve, secC, (_, _) => 0, 139, 164); var c2 = SeamSolver.Solve(readCurve, secC, (_, _) => 0, 139, 164);
  Near(c1.Stations.Where(q => q.ClipOffset.HasValue).Max(q => Math.Abs(q.ClipOffset!.Value - ClipAt(c2, q.Station))), 0, 0.01, "largest clip difference on the curve");
});

// ---------------------------------------------------------------------------------------------------- angle point

Console.WriteLine("angle point");
Test("level profile: the seam is the bisector and meets the ground at reach / cos(D/2)", () =>
{
  const double d = 47.6;
  var bl = AnglePoint(d, true);
  var sec = MakeSections(bl, Side.Left, Every(130, 190, 1), _ => 10, (_, _) => 6);
  var reach = 3 + (10 - 0.06 - 6) / 0.5;
  var r = SeamSolver.Solve(bl, sec, (_, _) => 6, 159, 161);
  Healthy(r);
  True(r.BendType == "angle_point" && r.Side == Side.Left, "bend type / side");
  Near(r.MaxLean, 0, 0.002, "lean off the bisector");
  var hit = r.Hit!.Value;
  Near(new P2(hit.X - 500, hit.Y - 800).Length, reach / Math.Cos(d / 2 * Deg), 0.005, "distance PI to X");
  Near(r.MeetA!.Value, 160 - reach * Math.Tan(d / 2 * Deg), 0.005, "incoming meet station");
  Near(r.MeetB!.Value, 160 + reach * Math.Tan(d / 2 * Deg), 0.005, "outgoing meet station");
  Near(r.MeetOffsetA!.Value, reach, 0.005, "meet offset");
});
Test("graded legs: the seam is the intersection line of the two slope planes", () =>
{
  const double d = 35; const double gA = -0.02, gB = -0.045, k = 0.5;
  var bl = AnglePoint(d, false);
  double Profile(double s) => s <= 160 ? 20 + gA * (s - 160) : 20 + gB * (s - 160);
  var sec = MakeSections(bl, Side.Right, Every(120, 200, 1), Profile, (_, _) => 15);
  var r = SeamSolver.Solve(bl, sec, (_, _) => 15, 159, 161);
  Healthy(r);
  // plane A: z = zPI + gA (p-PI).tA + dzh - k ((p-PI).nA - hinge); same for B. zA = zB  <=>  (p-PI).w = 0
  var tA = SampledBaseline.Tangent(15 * Deg); var tB = SampledBaseline.Tangent((15 - d) * Deg);
  var nA = SampledBaseline.InsideNormal(15 * Deg, Side.Right); var nB = SampledBaseline.InsideNormal((15 - d) * Deg, Side.Right);
  var w = tA * gA - nA * k - tB * gB + nB * k;
  var worst = r.SeamRaw.Where(q => q.Kind == "exact" && q.OffsetA > 3.05 && q.OffsetB > 3.05)
    .Max(q => Math.Abs(new P2(q.X - 500, q.Y - 800).Dot(w)) / w.Length);
  Near(worst, 0, 0.002, "distance of the seam from the plane-plane line");
  True(r.MaxLean > 0.05, "a grade break must lean the seam off the bisector");
});

// ---------------------------------------------------------------------------------------------------- curves

Console.WriteLine("curves");
Test("arc R12: seam is the bisector ray from the centre; tangent sections stop at R + x cot(D/2)", () =>
{
  var bl = Scs(12, 0, 14.73, 40, true);                       // TC at 140, CT at 154.73
  var sec = MakeSections(bl, Side.Left, Every(100, 194, 1), _ => 10, (_, _) => 0);
  var r = SeamSolver.Solve(bl, sec, (_, _) => 0, 139, 156);
  Healthy(r);
  True(r.BendType == "curve", "bend type"); Near(r.Radius!.Value, 12, 1e-3, "radius");
  var cot = 1 / Math.Tan(14.73 / 12 / 2);
  Near(ClipAt(r, 134), 12 + 6 * cot, 0.01, "6 m before the curve (closed form 20.52)");
  Near(ClipAt(r, 137), 12 + 3 * cot, 0.01, "3 m before the curve (closed form 16.26)");
  Near(ClipAt(r, 147), 12.0, 1e-4, "on the arc: every section stops at the centre itself");
  True(r.Stations.Single(x => x.Station == 147).Role == "cap", "arc sections are cap sections");
  Near(r.MaxLean, 0, 0.003, "lean on a level, symmetric bend");
  Near(r.MeetOffsetA!.Value, r.MeetOffsetB!.Value, 0.005, "both sides daylight at the same offset");
  Near(r.ApexSpread, 0, 1e-6, "apex level spread on a level profile");
});
Test("spiral-arc-spiral R12 Ls8: spiral sections stop well inside their own radius", () =>
{
  var bl = Scs(12, 8, 6.73, 40, true);                        // TS 140, SC 148, CS 154.73, ST 162.73
  var sec = MakeSections(bl, Side.Left, Every(100, 202, 1), _ => 14, (_, _) => 0);
  var r = SeamSolver.Solve(bl, sec, (_, _) => 0, 139, 164);
  Healthy(r);
  // independent brute force (nearest-foot boundary, Python, 0.01 m grid): 17.88 at TS, 13.77 four metres in, 12.57 six in
  Near(ClipAt(r, 140), 17.88, 0.02, "at the start of the spiral");
  Near(ClipAt(r, 144), 13.77, 0.02, "4 m into the spiral (own radius 24 m)");
  Near(ClipAt(r, 146), 12.57, 0.02, "6 m into the spiral (own radius 16 m)");
  True(r.Stations.Single(x => x.Station == 151).Role == "cap", "arc sections are cap sections");
});
Test("graded curve, left and right: both sides arrive at the same level; mirror image gives the mirror seam", () =>
{
  SeamResult Run(bool left)
  {
    var bl = Scs(12, 8, 6.73, 40, left);
    var sec = MakeSections(bl, left ? Side.Left : Side.Right, Every(100, 202, 1), s => 14 - 0.03 * (s - 100), (_, _) => 0);
    return SeamSolver.Solve(bl, sec, (_, _) => 0, 139, 164);
  }
  var l = Run(true); var rr = Run(false);
  Healthy(l); Healthy(rr);
  True(l.Side == Side.Left && rr.Side == Side.Right, "sides");
  // independent brute force (Python, walk each section until the other side's surface is at the same level)
  Near(ClipAt(l, 141), 18.658, 0.01, "3 % grade, station 141"); Near(ClipAt(l, 144), 15.662, 0.01, "station 144");
  Near(ClipAt(l, 162), 15.152, 0.01, "station 162"); Near(ClipAt(l, 165), 18.828, 0.01, "station 165");
  True(l.MaxLean > 0.02, $"a 3 % grade must lean the seam (got {l.MaxLean:0.###})");
  Near(rr.MaxLean, l.MaxLean, 0.002, "mirror lean");
  Near(ClipAt(rr, 144), ClipAt(l, 144), 0.002, "mirror clip offset");
  True(l.ApexSpread > 0.1, "the arc sections reach the centre at different levels on a grade: the spread must be reported");
});
Test("cut instead of fill", () =>
{
  var bl = Scs(15, 0, 19.15, 40, false);
  var sec = MakeSections(bl, Side.Right, Every(100, 199, 1), _ => 10, (_, _) => 19);
  var r = SeamSolver.Solve(bl, sec, (_, _) => 19, 139, 160);
  Healthy(r);
  True(r.Hit!.Value.Z > 18.99, "the seam ends on the ground");
});
Test("parabola: sections cross well inside their own radius of curvature, and are still caught", () =>
{
  var bl = SampledBaseline.FromFunction(t => new P2(t, t * t / 8), -12, 12, 6000);
  var s2 = bl.S[Array.FindIndex(bl.X, x => x >= -2)];        // station of t = -2: normal meets the axis after 4.472 m, R there = 5.59
  var st = Every(bl.Start + 1, bl.End - 1, 0.5, s2).ToList();
  var sec = MakeSections(bl, Side.Left, st, _ => 10, (_, _) => 8.0, hinge: 1.0, crossfall: 0);   // reach 5.0
  var mid = 0.5 * (bl.Start + bl.End);
  var r = SeamSolver.Solve(bl, sec, (_, _) => 8.0, mid - 8, mid + 8, new SolveOptions { Step = 0.1 });
  True(r.Status == SeamStatus.Ok, $"status {r.Status}: {string.Join("; ", r.Reasons)}");
  Near(ClipAt(r, s2), Math.Sqrt(20), 0.02, "clip at t = -2");
  True(r.LinkCrossingsAfter == 0, "crossings remain");
});

Test("coverage: level spiral curve - the overlap goes, and no ground is left uncovered", () =>
{
  var bl = Scs(12, 8, 6.73, 40, true);
  var sec = MakeSections(bl, Side.Left, Every(100, 202, 0.25), _ => 14, (_, _) => 0);
  var r = SeamSolver.Solve(bl, sec, (_, _) => 0, 139, 164);
  Healthy(r);
  var (before, overlap, gap) = Coverage(bl, sec, r, Side.Left);
  Note($"doubly covered before {before:0.0} m2, after {overlap:0.00} m2, uncovered after {gap:0.00} m2");
  True(before > 50, "the fixture should overlap over a large area before the clip");
  Near(overlap, 0, 0.5, "area still covered twice"); Near(gap, 0, 0.5, "area left uncovered");
});
Test("coverage: the same curve on a 3 % grade - only the small apex patch is left open", () =>
{
  var bl = Scs(12, 8, 6.73, 40, true);
  var sec = MakeSections(bl, Side.Left, Every(100, 202, 0.25), s => 14 - 0.03 * (s - 100), (_, _) => 0);
  var r = SeamSolver.Solve(bl, sec, (_, _) => 0, 139, 164);
  Healthy(r);
  var (before, overlap, gap) = Coverage(bl, sec, r, Side.Left);
  Note($"doubly covered before {before:0.0} m2, after {overlap:0.00} m2, uncovered after {gap:0.00} m2; apex mismatch {r.ApexMismatch:0.00} m, spread {r.ApexSpread:0.00} m");
  Near(overlap, 0, 0.5, "area still covered twice"); True(gap < 3.0, $"uncovered area {gap:0.0} m2 is more than an apex patch");
  True(r.Warnings.Any(w => w.Contains("apex")), "the apex patch must be reported");
});
Test("coverage: angle point", () =>
{
  var bl = AnglePoint(47.6, true);
  var sec = MakeSections(bl, Side.Left, Every(130, 190, 0.25), s => 10 - 0.01 * (s - 130), (_, _) => 6);
  var r = SeamSolver.Solve(bl, sec, (_, _) => 6, 159, 161);
  Healthy(r);
  var (before, overlap, gap) = Coverage(bl, sec, r, Side.Left);
  Note($"doubly covered before {before:0.0} m2, after {overlap:0.00} m2, uncovered after {gap:0.00} m2");
  Near(overlap, 0, 0.5, "area still covered twice"); Near(gap, 0, 0.5, "area left uncovered");
});

Test("coverage check is not blind: clips 1 m short leave a gap, clips 1 m long leave an overlap", () =>
{
  var bl = Scs(12, 8, 6.73, 40, true);
  var sec = MakeSections(bl, Side.Left, Every(100, 202, 0.25), _ => 14, (_, _) => 0);
  var r = SeamSolver.Solve(bl, sec, (_, _) => 0, 139, 164);
  var shortBy = Coverage(bl, sec, r, Side.Left, bias: -1.0); var longBy = Coverage(bl, sec, r, Side.Left, bias: 1.0);
  Note($"1 m short: {shortBy.Gap:0.0} m2 uncovered; 1 m long: {longBy.Overlap:0.0} m2 covered twice");
  True(shortBy.Gap > 5 && longBy.Overlap > 5, "the coverage check did not react to a wrong clip");
  // and at an angle point, where the check runs each leg down to the PI on its own side
  var ba = AnglePoint(40, true);
  var sa = MakeSections(ba, Side.Left, Every(130, 190, 1), s => 10 - 0.01 * (s - 130), (_, _) => 6);
  var ra = SeamSolver.Solve(ba, sa, (_, _) => 6, 159, 161);
  var ok = Coverage(ba, sa, ra, Side.Left); var sh = Coverage(ba, sa, ra, Side.Left, bias: -1.0); var lg = Coverage(ba, sa, ra, Side.Left, bias: 1.0);
  Note($"angle point at 1 m spacing: as solved {ok.Overlap:0.00} m2 twice / {ok.Gap:0.00} m2 gap; 1 m short {sh.Gap:0.0} m2 gap; 1 m long {lg.Overlap:0.0} m2 twice");
  True(ok.Overlap < 0.5 && ok.Gap < 0.5 && sh.Gap > 3 && lg.Overlap > 3, "the coverage check at an angle point is blind or wrong");
});

// ---------------------------------------------------------------------------------------------------- cut to fill

Console.WriteLine("cut changing to fill");
Test("curve in cut that runs out into fill beyond the bend: same seam as all-cut, ends sooner, exit sections daylight alone", () =>
{
  var bl = Scs(12, 8, 6.73, 40, true);                        // ST at 162.73
  var (pSt, thSt) = bl.At(162.73); var tExit = SampledBaseline.Tangent(thSt);
  double Ground(double x, double y) => 26 - 0.45 * Math.Max(0, new P2(x - pSt.X, y - pSt.Y).Dot(tExit));   // 12 m of cut, falling to road level 27 m past the curve
  var st = Every(100, 202, 0.25).ToList();
  var mixed = SeamSolver.Solve(bl, MakeSections(bl, Side.Left, st, _ => 14, Ground), (x, y) => Ground(x, y), 139, 164);
  var allCut = SeamSolver.Solve(bl, MakeSections(bl, Side.Left, st, _ => 14, (_, _) => 26), (_, _) => 26, 139, 164);
  Healthy(mixed); Healthy(allCut);
  True(mixed.CutFillChanges.Count == 1 && mixed.CutFillChanges[0] > 165, $"the fixture should change to fill once, past the curve (got {string.Join(",", mixed.CutFillChanges.Select(x => x.ToString("0.#")))})");
  True(mixed.SeamEnd == "ground", $"seam end {mixed.SeamEnd}");
  var both = mixed.Stations.Where(x => x.Role == "seam").Select(x => (x, other: allCut.Stations.Single(y => y.Station == x.Station))).Where(p => p.other.Role == "seam").ToList();
  True(both.Count > 20, "too few common seam stations to compare");
  Near(both.Max(p => Math.Abs(p.x.ClipOffset!.Value - p.other.ClipOffset!.Value)), 0, 0.005, "clip offsets against the all-cut seam");
  True(mixed.MeetB < allCut.MeetB - 0.5, $"with the ground falling away the seam should reach it sooner ({mixed.MeetB:0.##} vs {allCut.MeetB:0.##})");
  var cov = Coverage(bl, MakeSections(bl, Side.Left, st, _ => 14, Ground), mixed, Side.Left);
  Note($"doubly covered before {cov.Before:0.0} m2, after {cov.Overlap:0.00} m2, uncovered after {cov.Gap:0.00} m2; outgoing meet {mixed.MeetB:0.##} (all cut {allCut.MeetB:0.##}), change to fill at {mixed.CutFillChanges[0]:0.#}");
  Near(cov.Overlap, 0, 0.5, "area still covered twice"); Near(cov.Gap, 0, 0.5, "area left uncovered");
});
Test("angle point in cut that runs out into fill along the outgoing leg: same seam as all-cut, ends sooner", () =>
{
  var bl = AnglePoint(47.6, true);
  var tB = SampledBaseline.Tangent(62.6 * Deg); var p0 = new P2(500, 800) + tB * 3;
  double Ground(double x, double y) => 14 - 0.4 * Math.Max(0, new P2(x - p0.X, y - p0.Y).Dot(tB));          // road level 13 m past the PI
  var st = Every(130, 190, 0.25).ToList();
  var sec = MakeSections(bl, Side.Left, st, _ => 10, Ground);
  var mixed = SeamSolver.Solve(bl, sec, (x, y) => Ground(x, y), 159, 161);
  var allCut = SeamSolver.Solve(bl, MakeSections(bl, Side.Left, st, _ => 10, (_, _) => 14), (_, _) => 14, 159, 161);
  Healthy(mixed); Healthy(allCut);
  True(mixed.CutFillChanges.Count >= 1, "the fixture should change to fill");
  var both = mixed.Stations.Where(x => x.Role == "seam").Select(x => (x, other: allCut.Stations.Single(y => y.Station == x.Station))).Where(p => p.other.Role == "seam").ToList();
  True(both.Count > 10, "too few common seam stations to compare");
  Near(both.Max(p => Math.Abs(p.x.ClipOffset!.Value - p.other.ClipOffset!.Value)), 0, 0.005, "clip offsets against the all-cut seam");
  True(mixed.MeetB < allCut.MeetB - 0.3, $"the seam should reach the falling ground sooner ({mixed.MeetB:0.##} vs {allCut.MeetB:0.##})");
  var cov = Coverage(bl, sec, mixed, Side.Left);
  Note($"doubly covered before {cov.Before:0.0} m2, after {cov.Overlap:0.00} m2, uncovered after {cov.Gap:0.00} m2; outgoing meet {mixed.MeetB:0.##} (all cut {allCut.MeetB:0.##}), changes at {string.Join(", ", mixed.CutFillChanges.Select(x => x.ToString("0.#")))}");
  Near(cov.Overlap, 0, 0.3, "area still covered twice"); Near(cov.Gap, 0, 0.3, "area left uncovered");
});
Test("angle point exactly at the change from cut to fill: only the lanes overlap, the seam is the lane line and stops", () =>
{
  var bl = AnglePoint(47.6, true);
  var tMid = (SampledBaseline.Tangent(15 * Deg) + SampledBaseline.Tangent(62.6 * Deg)).Unit();
  double Ground(double x, double y) => 9.94 - 0.3 * new P2(x - 500, y - 800).Dot(tMid);      // above the road before the PI, below it after
  var sec = MakeSections(bl, Side.Left, Every(130, 190, 0.25), _ => 10, Ground);
  var r = SeamSolver.Solve(bl, sec, (x, y) => Ground(x, y), 159, 161);
  True(r.Status == SeamStatus.Ok, $"status {r.Status}: {string.Join("; ", r.Reasons)}");
  True(r.CutFillChanges.Count >= 1, "the fixture should change between cut and fill");
  True(r.LinkCrossingsBefore > 0 && r.LinkCrossingsAfter == 0, $"crossings {r.LinkCrossingsBefore} -> {r.LinkCrossingsAfter}");
  True(r.Stations.All(x => x.FirstCrossingIsClip), string.Join(" | ", r.Warnings));
  True(r.Stations.Where(x => x.ClipOffset.HasValue).All(x => x.ClipOffset <= 4.0), "only sections that cross near the hinge should be clipped");
  var cov = Coverage(bl, sec, r, Side.Left);
  Note($"doubly covered before {cov.Before:0.0} m2, after {cov.Overlap:0.00} m2, uncovered after {cov.Gap:0.00} m2; clipped {r.ClipFrom:0.##}-{r.ClipTo:0.##}, seam ends: {r.SeamEnd}, apex mismatch {r.ApexMismatch:0.00} m");
  Near(cov.Overlap, 0, 0.3, "area still covered twice"); Near(cov.Gap, 0, 0.3, "area left uncovered");
});
Test("curve, ground falling from cut to fill at different places along it: either no bowtie (shallow sections) or a clean clip", () =>
{
  var solved = 0;
  foreach (var pivot in new[] { 151.365, 160.0, 166.0, 172.0, 180.0 })
  {
    var bl = Scs(12, 8, 6.73, 40, true);
    var (pm, thm) = bl.At(pivot); var tMid = SampledBaseline.Tangent(thm);
    double Ground(double x, double y) => 14 - 0.3 * new P2(x - pm.X, y - pm.Y).Dot(tMid);    // cut before the pivot station, fill after it
    var sec = MakeSections(bl, Side.Left, Every(100, 202, 0.25), _ => 14, Ground);
    var r = SeamSolver.Solve(bl, sec, (x, y) => Ground(x, y), 139, 164);
    if (r.Status == SeamStatus.NoBowtie) { Note($"ground at road level at {pivot:0.#}: no bowtie (the sections near the apex are too shallow to reach the centre)"); continue; }
    True(r.Status == SeamStatus.Ok, $"pivot {pivot}: status {r.Status}: {string.Join("; ", r.Reasons)}"); solved++;
    var cov = Coverage(bl, sec, r, Side.Left);
    Note($"ground at road level at {pivot:0.#}: seam end {r.SeamEnd}, clipped {r.ClipFrom:0.##}-{r.ClipTo:0.##}, cap {r.CapFrom:0.##}-{r.CapTo:0.##}, crossings {r.LinkCrossingsBefore} -> {r.LinkCrossingsAfter}, level step {r.LevelStep:0.00}; twice before {cov.Before:0.0} m2, after {cov.Overlap:0.00} m2, uncovered {cov.Gap:0.00} m2");
    True(r.LinkCrossingsAfter == 0, $"pivot {pivot}: {r.LinkCrossingsAfter} crossings remain");
    True(r.Stations.All(x => x.FirstCrossingIsClip), string.Join(" | ", r.Warnings));
    True(r.MaxClosure < 0.005, $"pivot {pivot}: closure {r.MaxClosure:0.###}");
    Near(cov.Overlap, 0, 0.5, $"pivot {pivot}: area still covered twice"); Near(cov.Gap, 0, 0.5, $"pivot {pivot}: area left uncovered");
  }
  True(solved > 0, "no pivot produced a bowtie to solve");
});
Test("climbing corner, lower leg in cut and upper leg in fill: the slopes pass each other, the step is reported", () =>
{
  SeamResult Run(double grade, out (double, double, double) cov)
  {
    var bl = AnglePoint(90, true);
    var tMid = (SampledBaseline.Tangent(15 * Deg) + SampledBaseline.Tangent(105 * Deg)).Unit();
    double Ground(double x, double y) => 16 - 0.06 - 0.35 * new P2(x - 500, y - 800).Dot(tMid);
    var sec = MakeSections(bl, Side.Left, Every(130, 190, 0.25), s => 16 + grade * (s - 160), Ground);
    var r = SeamSolver.Solve(bl, sec, (x, y) => Ground(x, y), 159, 161);
    cov = Coverage(bl, sec, r, Side.Left);
    return r;
  }
  var mild = Run(0.04, out var c1); var steep = Run(0.25, out var c2);
  Note($"4 %: {mild.Status}, step {mild.LevelStep:0.00} m, end {mild.SeamEnd}, twice {c1.Item2:0.00} m2, uncovered {c1.Item3:0.00} m2");
  Note($"25 %: {steep.Status}, step {steep.LevelStep:0.00} m, end {steep.SeamEnd}, twice {c2.Item2:0.00} m2, uncovered {c2.Item3:0.00} m2");
  True(mild.Status == SeamStatus.Ok && mild.LinkCrossingsAfter == 0, $"4 %: {mild.Status} {string.Join("; ", mild.Reasons)}");
  True(steep.Status == SeamStatus.DesignConflict && steep.LevelStep > 0.3, $"25 %: {steep.Status}, step {steep.LevelStep:0.00}");
  Near(c1.Item2, 0, 0.3, "4 %: area still covered twice"); Near(c1.Item3, 0, 0.3, "4 %: area left uncovered");
});

// ---------------------------------------------------------------------------------------------------- real drawings

Console.WriteLine("snapshots from Civil 3D (BTC Road, GO-260809v5-bowtie-seam-fixture.dwg, 20 Sep 2026)");
SeamResult SolveFixture(string file, double maxLevelStep = 0.30, bool levels = false, bool spread = false)
{
  var snap = Snapshot.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", file));
  var bl = snap.ToBaseline();
  return SeamSolver.Solve(bl, snap.ToSections(3.0), snap.ToGround(), snap.BendFrom, snap.BendTo, new SolveOptions { Step = 0.1, MaxLevelStep = maxLevelStep, LevelFromTarget = levels, AdjustFrom = spread ? AdjustFrom.FromHinge : AdjustFrom.LastLink, LevelRule = LevelRule.Mean, GroundCell = snap.Ground?.Cell ?? 0 }, bl.Start, bl.End);
}
Test("curve 1 (arc R15, cut, benched slopes): matches the Valley points Civil 3D built from it", () =>
{
  var r = SolveFixture("btc-road-curve1.json");
  Healthy(r);
  // offsets read back from the rebuilt corridor after the seam and cap were mapped as ClipTarget (Nearest)
  Near(ClipAt(r, 95), 17.1232, 0.002, "station 95"); Near(ClipAt(r, 117), 16.7205, 0.002, "station 117"); Near(ClipAt(r, 116), 15.3726, 0.002, "station 116");
  // with the apex bar mapped, stations 97, 106 and 115 were all built to offset 15.0000 and the same XYZ (38295.23865, -57618.06003, 48.1700)
  foreach (var st in new[] { 97.0, 106.0, 115.0 }) Near(ClipAt(r, st), 15.0, 0.001, $"station {st}");
  var ends = new[] { 97.0, 106.0, 115.0 }.Select(st => r.Stations.Single(x => x.Station == st)).ToList();
  True(ends.All(e => Math.Abs(e.ClipX!.Value - 38295.23865) < 0.001 && Math.Abs(e.ClipY!.Value + 57618.06003) < 0.001), "the arc sections must all end at the one apex point");
  Near(r.ApexZ!.Value, 48.17, 0.001, "apex level");
  Near(r.MeetB!.Value, 118.6825, 0.002, "outgoing meet station"); Near(r.MeetOffsetB!.Value, 18.9884, 0.005, "its daylight offset");
  // the ground grid in the snapshot is 1 m, the drawing's TIN is exact: the seam end agrees with the built Daylight corner to a few mm
  Near(r.Hit!.Value.X, 38293.9727, 0.01, "seam end x"); Near(r.Hit!.Value.Y, -57622.8620, 0.01, "seam end y");
  True(r.Cap.Count == 2, "the apex bar is one straight segment");
});
Test("curve 2 (spiral-arc-spiral R12, fill, bench at the arc centre, crest at +10 % / -9 %): the sides never meet in level", () =>
{
  var r = SolveFixture("btc-road-curve2.json");
  Note($"{r.Status}: step {r.LevelStep:0.000} m, crossings {r.LinkCrossingsBefore} -> {r.LinkCrossingsAfter}, clipped {r.ClipFrom:0.##}-{r.ClipTo:0.##}");
  True(r.Status == SeamStatus.DesignConflict, $"status {r.Status}");
  Near(r.LevelStep, 0.31, 0.02, "level step");
  var accepted = SolveFixture("btc-road-curve2.json", 0.5);
  True(accepted.Status == SeamStatus.Ok, $"with the step accepted: {accepted.Status} {string.Join("; ", accepted.Reasons)}");
  True(accepted.LinkCrossingsBefore > 50 && accepted.LinkCrossingsAfter == 0, $"crossings {accepted.LinkCrossingsBefore} -> {accepted.LinkCrossingsAfter}");
  True(accepted.Stations.All(x => x.FirstCrossingIsClip), string.Join(" | ", accepted.Warnings));
  True(accepted.Stations.Count(x => x.Role == "conflict") >= 4 && accepted.MaxClosure < 0.005, "the divided stations must be reported as conflict, not as a closed seam");
});

Test("with the level taken from the valley line (mean of the two sides): curve 2 is solvable, curve 1 is not moved", () =>
{
  var c2 = SolveFixture("btc-road-curve2.json", levels: true);
  True(c2.Status == SeamStatus.Ok, $"curve 2: {c2.Status} {string.Join("; ", c2.Reasons)}");
  True(c2.LinkCrossingsAfter == 0, "crossings remain");
  // the sides are 0.31 m apart, so each is moved about half of that; both sides of any seam point get the same target level
  // except at the outer end: the valley stops where the outgoing side has reached the ground, so its level runs out from
  // the mean to that ground level over the last metre, and the incoming meet section comes down by the whole step there
  True(c2.MaxLevelAdjust > 0.10 && c2.MaxLevelAdjust < 0.30, $"largest adjustment {c2.MaxLevelAdjust:0.###}");
  True(Math.Abs(c2.EndRunOut) > 0.10 && Math.Abs(c2.EndRunOut) < 0.16, $"run-out at the valley end {c2.EndRunOut:0.###}");
  var inner = c2.Stations.Where(x => x.LevelAdjust.HasValue && x.Station > 227.5 && x.Station < 236.5).Max(x => Math.Abs(x.LevelAdjust!.Value));
  True(inner < 0.20, $"largest adjustment away from the run-out {inner:0.###}");
  for (var i = 1; i < c2.Seam.Count; i++)
  {
    var d = Math.Sqrt(Math.Pow(c2.Seam[i].X - c2.Seam[i - 1].X, 2) + Math.Pow(c2.Seam[i].Y - c2.Seam[i - 1].Y, 2));
    if (!double.IsNaN(c2.Seam[i].Z) && !double.IsNaN(c2.Seam[i - 1].Z)) True(Math.Abs(c2.Seam[i].Z - c2.Seam[i - 1].Z) <= 0.02 + 1.0 * d, $"step in the valley level at vertex {i}: {c2.Seam[i - 1].Z:0.000} -> {c2.Seam[i].Z:0.000} in {d:0.000} m");
  }
  Note("valley: " + string.Join("  ", c2.Seam.Select(q => $"{q.Kind} {q.Z:0.000}")));
  Note("adjust: " + string.Join("  ", c2.Stations.Where(x => x.LevelAdjust.HasValue).Select(x => $"{x.Station:0.##}:{x.LevelAdjust:0.000}")));
  var worst = c2.Stations.Where(x => x.AdjustedSlope.HasValue).OrderByDescending(x => Math.Abs(x.LevelAdjust!.Value)).First();
  Note($"curve 2: largest adjustment {c2.MaxLevelAdjust:0.000} m at {worst.Station:0.##} (slope {worst.OwnSlope:0.000} -> {worst.AdjustedSlope:0.000}); apex level {c2.ApexZ:0.000}");
  var pairs = c2.Stations.Where(x => x.Role == "conflict").ToList();
  True(pairs.Any(x => x.LevelAdjust > 0.05) && pairs.Any(x => x.LevelAdjust < -0.05), "one side should be raised and the other lowered");

  var c1 = SolveFixture("btc-road-curve1.json", levels: true);
  True(c1.Status == SeamStatus.Ok, $"curve 1: {c1.Status}");
  Near(c1.MaxLevelAdjust, 0, 0.005, "curve 1 is level and symmetric: nothing should move");
});

Test("level moves spread from the hinge (AdjustFrom.FromHinge): the short-link stubs go, and the limit is the slope change", () =>
{
  var last = SolveFixture("btc-road-curve2.json", levels: true);
  var spread = SolveFixture("btc-road-curve2.json", levels: true, spread: true);
  Note($"curve 2: largest slope change {last.MaxSlopeChange * 100:0.0}% with the last link taking the move, {spread.MaxSlopeChange * 100:0.0}% spread from the hinge");
  True(last.MaxSlopeChange > 0.3, "the fixture's arc centre is on a 1 m bench: the last-link slope change should be large");
  True(spread.Status == SeamStatus.Ok && spread.MaxSlopeChange < 0.05, $"spread: {spread.Status}, {spread.MaxSlopeChange:0.000}");
  True(last.Warnings.Any(w => w.Contains("spread from the hinge")), "the last-link result must point to the stub");
});

Test("spread from the hinge: links made steeper than designed, and benches whose fall reverses, are flagged; the no-steeper level flags fewer", () =>
{
  SeamResult Run(LevelRule rule)
  {
    var snap = Snapshot.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "btc-road-curve2.json"));
    var bl = snap.ToBaseline();
    return SeamSolver.Solve(bl, snap.ToSections(3.0), snap.ToGround(), snap.BendFrom, snap.BendTo,
      new SolveOptions { Step = 0.1, LevelFromTarget = true, AdjustFrom = AdjustFrom.FromHinge, LevelRule = rule, GroundCell = snap.Ground?.Cell ?? 0 }, bl.Start, bl.End);
  }
  var mean = Run(LevelRule.Mean); var ns = Run(LevelRule.NoSteeper);
  int Steep(SeamResult r) => r.SlopeFlags.Where(f => f.Kind == "steeper").Select(f => f.Station).Distinct().Count();
  Note($"curve 2: steeper than designed at {Steep(mean)} section(s) with the mean level, {Steep(ns)} with the no-steeper level; fall reversed at {mean.SlopeFlags.Count(f => f.Kind == "fall_reversed")} / {ns.SlopeFlags.Count(f => f.Kind == "fall_reversed")} link(s)");
  True(Steep(mean) > 0, "the mean level lowers the fill side at some sections: those must be flagged");
  True(mean.Warnings.Any(w => w.Contains("steeper than designed")), "the steeper slopes must be reported");
  True(Steep(ns) < Steep(mean), "the no-steeper level should flag fewer sections");
  foreach (var f in mean.SlopeFlags.Where(f => f.Kind == "steeper")) True(Math.Abs(f.NewGrade) > Math.Abs(f.DesignGrade), "a steeper flag must be steeper");
});

// ---------------------------------------------------------------------------------------------------- refusals

Console.WriteLine("refusals and controls");
Test("wide radius: no bowtie, nothing to write", () =>
{
  var bl = Scs(40, 0, 40, 40, true);
  var sec = MakeSections(bl, Side.Left, Every(100, 220, 1), _ => 10, (_, _) => 6);
  True(SeamSolver.Solve(bl, sec, (_, _) => 6, 139, 181).Status == SeamStatus.NoBowtie, "expected NoBowtie");
});
Test("reverse curve: two bends on opposite sides, and one range across both is refused", () =>
{
  var bl = SampledBaseline.FromElements(new[] { Element.Line(30), Element.Arc(12, 12, true), Element.Line(4), Element.Arc(12, 12, false), Element.Line(30) }, new P2(0, 0), 0, 100);
  var bends = BendFinder.Find(bl, bl.Start, bl.End);
  True(bends.Count == 2 && bends[0].Side == Side.Left && bends[1].Side == Side.Right, $"got {bends.Count} bends");
  Near(bends[0].From, 130, 0.25, "first bend start"); Near(bends[1].To, 158, 0.25, "second bend end");
  var sec = MakeSections(bl, Side.Left, Every(100, 188, 1), _ => 10, (_, _) => 0);
  True(SeamSolver.Solve(bl, sec, (_, _) => 0, 129, 159).Status == SeamStatus.Unsupported, "expected Unsupported");
});
Test("section change inside the bend, refused only when asked (RefuseSectionChange)", () =>
{
  var bl = AnglePoint(40, true);
  var sec = MakeSections(bl, Side.Left, Every(130, 190, 1), _ => 10, (_, _) => 6, hingeAt: s => s < 160 ? 3.0 : 3.3);
  var r = SeamSolver.Solve(bl, sec, (_, _) => 6, 159, 161, new SolveOptions { RefuseSectionChange = true });
  True(r.Status == SeamStatus.Unsupported && r.Reasons.Any(x => x.Contains("hinge")), $"status {r.Status}");
});
Test("clipped sections are refused as evidence", () =>
{
  var bl = AnglePoint(40, true);
  var sec = MakeSections(bl, Side.Left, Every(130, 190, 1), _ => 10, (_, _) => 6);
  sec.Sections[30].Clipped = true;
  True(SeamSolver.Solve(bl, sec, (_, _) => 6, 159, 161).Status == SeamStatus.Unsupported, "expected Unsupported");
});
Test("seam that never reaches the ground blocks", () =>
{
  var bl = AnglePoint(40, true);
  var sec = MakeSections(bl, Side.Left, Every(130, 190, 1), _ => 10, (_, _) => 6);
  True(SeamSolver.Solve(bl, sec, (_, _) => -50, 159, 161).Status == SeamStatus.NeverMeetsGround, "expected NeverMeetsGround");
});

// ---------------------------------------------------------------------------------------------------- benched parts

// Whole-corridor snapshots of BTC Road with the benched parts (21 Sep 2026): the original UTNMBench (its sections meet the
// ground and carry on with a terminal bench and drain, so they do not end on it) and UTNMBench v0.2 (every section ends
// on the ground). Judged as the plugin runs them (level from the valley line) and by coverage, not by crossings alone.
Console.WriteLine("benched parts (BTC Road snapshots, 21 Sep 2026)");
(SeamResult R, (double Before, double Overlap, double Gap) Cov) Bench(string file, bool spread = false)
{
  var snap = Snapshot.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", file));
  var bl = snap.ToBaseline(); var sec = snap.ToSections();
  var r = SeamSolver.Solve(bl, sec, snap.ToGround(), snap.BendFrom, snap.BendTo, new SolveOptions { LevelFromTarget = true, GroundCell = snap.Ground?.Cell ?? 0, AdjustFrom = spread ? AdjustFrom.FromHinge : AdjustFrom.LastLink, LevelRule = LevelRule.Mean });
  var cov = Coverage(bl, sec, r, r.Side, 0.25);
  Note($"{r.Status}{(spread ? " (spread from the hinge)" : "")}: crossings {r.LinkCrossingsBefore} -> {r.LinkCrossingsAfter}, twice {cov.Before:0.0} -> {cov.Overlap:0.00} m2, gap {cov.Gap:0.00} m2, largest level move {r.MaxLevelAdjust:0.00} m, slope change {r.MaxSlopeChange * 100:0.0}%, end {r.SeamEnd}, open ended {r.OpenEnded}");
  return (r, cov);
}
foreach (var (file, what, open) in new[]
{
  ("btc-bench-orig-curve1.json", "original part, curve 1 (arc R15, cut, terminal bench and drain)", true),
  ("btc-bench-orig-curve2.json", "original part, curve 2 (spiral-arc-spiral R12, fill)", true),
  ("btc-bench-v02-curve1.json", "v0.2, curve 1", false),
})
  Test($"{what}: repaired, nothing covered twice, no gap; open-ended sections recognised = {open}", () =>
  {
    var (r, cov) = Bench(file);
    True(r.Status == SeamStatus.Ok, $"status {r.Status}: {string.Join("; ", r.Reasons)}");
    True(r.LinkCrossingsAfter == 0, $"{r.LinkCrossingsAfter} crossings remain");
    Near(cov.Overlap, 0, 0.5, "area still covered twice"); Near(cov.Gap, 0, 0.5, "area left uncovered");
    True(r.OpenEnded == open, $"open ended: {r.OpenEnded}");
  });
Test("v0.2, curve 2 (R12 fill, the valley ends in the toe-drain notch): the last link alone cannot take the move; spread from the hinge it is 3.5 %", () =>
{
  var (last, cl) = Bench("btc-bench-v02-curve2.json");
  True(last.LinkCrossingsAfter == 0, $"{last.LinkCrossingsAfter} crossings remain");
  Near(cl.Overlap, 0, 0.5, "area still covered twice"); Near(cl.Gap, 0, 0.5, "area left uncovered");
  True(last.Status == SeamStatus.DesignConflict, "with only the last link taking the move this should be refused (a notch link moved 0.49 m)");
  var (spread, cs) = Bench("btc-bench-v02-curve2.json", spread: true);
  True(spread.Status == SeamStatus.Ok, $"spread: {spread.Status} {string.Join("; ", spread.Reasons)}");
  True(spread.MaxSlopeChange < 0.05, $"slope change {spread.MaxSlopeChange:0.000}");
  Near(cs.Overlap, 0, 0.5, "area still covered twice"); Near(cs.Gap, 0, 0.5, "area left uncovered");
});

// ---------------------------------------------------------------------------------------------------- section changes

// A change of section in or next to a bend is the engineer's design (a drain size, a lane width, a region with another
// assembly). It is solved as it stands, each side of the bend on its own sections, and reported for highlighting.
Console.WriteLine("section changes");
(double Before, double Overlap, double Gap) CoverageNote(SampledBaseline bl, SectionSet sec, SeamResult r, Side side)
{
  OverlapCells.Clear();
  var cov = Coverage(bl, sec, r, side);
  foreach (var c in OverlapCells.Take(12)) Note($"  overlap cell {c.X:0.0},{c.Y:0.0}: {c.Feet}");
  Note("  seam: " + string.Join(" ", r.Seam.Select(q => $"{q.Kind}({q.StationA:0.0}/{q.OffsetA:0.00}|{q.StationB:0.0}/{q.OffsetB:0.00})")));
  Note($"doubly covered before {cov.Before:0.0} m2, after {cov.Overlap:0.00} m2, uncovered after {cov.Gap:0.00} m2; changes: " +
    string.Join("; ", r.SectionChanges.Select(c => $"{c.From:0.##}-{c.To:0.##}{(c.InBend ? " (in bend)" : "")} {c.What}")));
  return cov;
}
Test("hinge moves at the PI (a wider drain after the angle point): solved, each side clipped on its own section, change reported", () =>
{
  var bl = AnglePoint(40, true);
  var sec = MakeSections(bl, Side.Left, Every(130, 190, 0.5), s => 10 - 0.01 * (s - 130), (_, _) => 6, hingeAt: s => s < 160 - 1e-9 ? 3.0 : 3.3);
  var r = SeamSolver.Solve(bl, sec, (_, _) => 6, 159, 161);
  Healthy(r);
  var (_, overlap, gap) = CoverageNote(bl, sec, r, Side.Left);
  Near(overlap, 0, 0.5, "area still covered twice"); Near(gap, 0, 0.5, "area left uncovered");
  True(r.SectionChanges.Count == 1 && r.SectionChanges[0].InBend, "the change at the PI must be reported as in the bend");
  True(r.Warnings.Any(w => w.Contains("section changes")), "the change must be reported for highlighting");
});
Test("inside links start further out after the PI (drain edge 1.65 -> 1.8 m, as FL-02 at 384.17): solved and reported", () =>
{
  var bl = AnglePoint(9.8, true, 90);
  var sec = MakeSections(bl, Side.Left, Every(130, 250, 0.5), s => 25 - 0.01 * (s - 130), (_, _) => 27.5,
    hinge: 5.5, crossfall: 0.02, startAt: s => s < 190 - 1e-9 ? 1.65 : 1.8, hingeAt: s => s < 190 - 1e-9 ? 5.35 : 5.5);
  var r = SeamSolver.Solve(bl, sec, (_, _) => 27.5, 189, 191);
  Healthy(r);
  var (_, overlap, gap) = CoverageNote(bl, sec, r, Side.Left);
  Near(overlap, 0, 0.5, "area still covered twice"); Near(gap, 0, 0.5, "area left uncovered");
  True(r.SectionChanges.Any(c => c.InBend && c.What.Contains("start")), "the start-offset change must be reported");
});
Test("section changes at the apex of a curve: solved and reported", () =>
{
  var bl = Scs(12, 8, 6.73, 40, true);
  var sec = MakeSections(bl, Side.Left, Every(100, 202, 0.25), s => 14 - 0.01 * (s - 100), (_, _) => 0, hingeAt: s => s < 151.5 ? 3.0 : 3.4);
  var r = SeamSolver.Solve(bl, sec, (_, _) => 0, 139, 164);
  Healthy(r);
  var (_, overlap, gap) = CoverageNote(bl, sec, r, Side.Left);
  Near(overlap, 0, 0.5, "area still covered twice"); True(gap < 1.0, $"uncovered area {gap:0.0} m2");
  True(r.SectionChanges.Any(c => c.InBend), "the change must be reported");
});
Test("region boundary inside the clip range (two sections at one station): the step is kept, not blended", () =>
{
  var bl = AnglePoint(40, true);
  var profile = (Func<double, double>)(s => 10 - 0.01 * (s - 130));
  var a = MakeSections(bl, Side.Left, Every(130, 154, 1), profile, (_, _) => 6, hinge: 3.0).Sections;
  var b = MakeSections(bl, Side.Left, Every(154, 190, 1), profile, (_, _) => 6, hinge: 3.6).Sections;
  var sec = new SectionSet(a.Concat(b));
  True(sec.Sections.Count(x => Math.Abs(x.Station - 154) < 1e-9) == 2, "fixture: two sections at 154");
  Near(sec.Hinge(153.5), 3.0, 1e-9, "hinge just before the boundary (region A)"); Near(sec.Hinge(154.5), 3.6, 1e-9, "hinge just after it (region B)");
  var r = SeamSolver.Solve(bl, sec, (_, _) => 6, 159, 161);
  Healthy(r);
  var (_, overlap, gap) = CoverageNote(bl, sec, r, Side.Left);
  Near(overlap, 0, 0.5, "area still covered twice"); Near(gap, 0, 0.5, "area left uncovered");
  True(r.SectionChanges.Any(c => !c.InBend && Math.Abs(c.From - 154) < 1e-6), "the boundary change must be reported (outside the bend)");
});
Test("no section change: nothing reported, and the same clip as the undivided solver gave", () =>
{
  var bl = AnglePoint(47.6, true);
  var sec = MakeSections(bl, Side.Left, Every(130, 190, 0.25), s => 10 - 0.01 * (s - 130), (_, _) => 6);
  var r = SeamSolver.Solve(bl, sec, (_, _) => 6, 159, 161);
  Healthy(r);
  True(r.SectionChanges.Count == 0 && !r.Warnings.Any(w => w.Contains("section changes")), "nothing should be reported");
});

// ---------------------------------------------------------------------------------------------------- stability

Console.WriteLine("stability");
Test("halving the sampling and the march step moves no clip by more than 5 mm", () =>
{
  SeamResult Run(double ds, double step)
  {
    var bl = Scs(12, 8, 6.73, 40, true, ds);
    var sec = MakeSections(bl, Side.Left, Every(100, 202, 1), s => 14 - 0.03 * (s - 100), (_, _) => 0);
    return SeamSolver.Solve(bl, sec, (_, _) => 0, 139, 164, new SolveOptions { Step = step });
  }
  var a = Run(0.05, 0.25); var b = Run(0.025, 0.125);
  var worst = a.Stations.Where(x => x.ClipOffset.HasValue).Max(x => Math.Abs(x.ClipOffset!.Value - ClipAt(b, x.Station)));
  Near(worst, 0, 0.005, "largest clip-offset change");
});
Test("same inputs, same answer", () =>
{
  var bl = Scs(12, 8, 6.73, 40, true);
  var sec = MakeSections(bl, Side.Left, Every(100, 202, 1), _ => 14, (_, _) => 0);
  var a = SeamSolver.Solve(bl, sec, (_, _) => 0, 139, 164); var b = SeamSolver.Solve(bl, sec, (_, _) => 0, 139, 164);
  True(a.Seam.Count == b.Seam.Count && a.Seam.Zip(b.Seam).All(p => p.First.X == p.Second.X && p.First.Y == p.Second.Y), "seam differs between runs");
});

Test("snapshot round trip: save, load, solve - the same seam to the millimetre", () =>
{
  var bl = Scs(12, 8, 6.73, 40, true);
  var sec = MakeSections(bl, Side.Left, Every(100, 202, 1), s => 14 - 0.01 * (s - 100), (_, _) => 0.5);
  var direct = SeamSolver.Solve(bl, sec, (_, _) => 0.5, 139, 164);
  var (c0, _) = bl.At(bl.Start);
  var snap = new Snapshot
  {
    BendFrom = 139, BendTo = 164,
    Samples = new() { S = bl.S, X = bl.X, Y = bl.Y, Dir = bl.Th },
    Sections = sec.Sections.Select(x => new Snapshot.SectionDto { Station = x.Station, Z0 = x.Z0, Template = x.Template.Select(p => new[] { p.Off, p.Dz }).ToList() }).ToList(),
    Ground = new() { X0 = 900, Y0 = 1950, Cell = 5, Nx = 40, Ny = 40, Z = Enumerable.Repeat<double?>(0.5, 1600).ToArray() },
  };
  var path = Path.Combine(Path.GetTempPath(), "bowtie-snapshot-test.json");
  snap.Save(path);
  var again = Snapshot.Load(path);
  var solved = SeamSolver.Solve(again.ToBaseline(), again.ToSections(), again.ToGround(), again.BendFrom, again.BendTo);
  True(solved.Status == SeamStatus.Ok, $"status {solved.Status}");
  Near(solved.Hit!.Value.X, direct.Hit!.Value.X, 0.001, "ground point x"); Near(solved.Hit!.Value.Y, direct.Hit!.Value.Y, 0.001, "ground point y");
  Near(ClipAt(solved, 144), ClipAt(direct, 144), 0.001, "clip offset");
  True(Snapshot.ToJson(ResultReport.Of(solved)).Contains("\"status\": \"Ok\""), "report");
});


// ---------------------------------------------------------------------------------------------------- refresh
Test("refresh: polyline deviation - same shape with other vertices is 0; shift, level, tail and interior bulge are measured", () =>
{
  var a = new List<P3> { new(0, 0, 10), new(10, 0, 11) };
  var a2 = new List<P3> { new(0, 0, 10), new(2.5, 0, 10.25), new(7, 0, 10.7), new(10, 0, 11) };
  var (p0, l0) = Refresh.Deviation(a, a2);
  Near(p0, 0, 1e-9, "same shape, plan"); Near(l0, 0, 1e-9, "same shape, level");
  var shifted = a.Select(q => new P3(q.X, q.Y + 0.3, q.Z)).ToList();
  Near(Refresh.Deviation(a, shifted).Plan, 0.3, 1e-9, "lateral shift");
  var raised = a.Select(q => new P3(q.X, q.Y, q.Z + 0.05)).ToList();
  var (pr, lr) = Refresh.Deviation(a, raised);
  Near(pr, 0, 1e-9, "raised, plan"); Near(lr, 0.05, 1e-9, "raised, level");
  var longer = new List<P3> { new(0, 0, 10), new(12, 0, 11.2) };
  Near(Refresh.Deviation(a, longer).Plan, 2, 1e-9, "a longer line: its tail counts");
  var bulge = new List<P3> { new(0, 0, 10), new(5, 0.4, 10.5), new(10, 0, 11) };
  Near(Refresh.Deviation(a, bulge).Plan, 0.4, 1e-9, "a vertex of the other line off this one");
  Near(Refresh.Deviation(bulge, a).Plan, 0.4, 1e-9, "symmetric");
});

Test("refresh: section compare - same design agrees, other slope / lane width / reach are found", () =>
{
  SectionSample S(double lane, double slope, double reach, double start = 1.05) => new SectionSample
  {
    Station = 100, Z0 = 20,
    Template = { (start, 0), (lane, 0), (reach, (reach - lane) * slope) },
  };
  var stock = S(4.75, 0.5, 9.0);
  var d0 = Refresh.Compare(stock, S(4.75, 0.5, 9.0));
  True(d0.Within(0.005, 0.02), $"identical: level {d0.MaxLevel}, reach {d0.ReachDiff}");
  var clipWithExtraVertex = new SectionSample { Station = 100, Z0 = 20, Template = { (1.05, 0), (3.0, 0), (4.75, 0), (6.0, 0.625), (9.0, 2.125) } };
  True(Refresh.Compare(stock, clipWithExtraVertex).Within(0.005, 0.02), "extra vertices on the same surface agree");
  var flatter = Refresh.Compare(stock, S(4.75, 0.45, 9.0));
  Near(flatter.MaxLevel, 4.25 * 0.05, 1e-9, "1:2 against 1:2.22 at the reach"); True(!flatter.Within(0.01, 0.05), "flagged");
  var wider = Refresh.Compare(stock, S(5.0, 0.5, 9.0));
  Near(wider.MaxLevel, 0.125, 1e-9, "lane 0.25 m wider: slope starts later"); True(!wider.Within(0.01, 0.05), "flagged");
  var shorter = Refresh.Compare(stock, S(4.75, 0.5, 8.0));
  Near(shorter.ReachDiff, 1.0, 1e-9, "reach"); True(!shorter.Within(0.01, 0.05), "flagged");
});

Test("refresh: the same design solves to the same valley; a flatter cut slope moves it", () =>
{
  var bl = AnglePoint(30, true);
  SeamResult Solve(double slope) =>
    SeamSolver.Solve(bl, MakeSections(bl, Side.Left, Every(130, 190, 1), s => 10 + 0.01 * (s - 160), (_, _) => 12, slope: slope), (_, _) => 12, 159, 161);
  List<P3> Line(SeamResult r) => r.Seam.Select(q => new P3(q.X, q.Y, double.IsNaN(q.Z) ? r.ApexZ ?? 0 : q.Z)).ToList();
  var first = Solve(0.5); var again = Solve(0.5);
  Healthy(first);
  var (p, l) = Refresh.Deviation(Line(first), Line(again));
  Near(p, 0, 1e-9, "same design, plan"); Near(l, 0, 1e-9, "same design, level");
  Near(first.MeetA!.Value, again.MeetA!.Value, 1e-9, "same meet station");
  var flatter = Solve(1.0 / 3);
  Healthy(flatter);
  var (pf, _) = Refresh.Deviation(Line(first), Line(flatter));
  True(pf > 0.5, $"a 1:3 cut reaches further than 1:2: the valley moves ({pf:0.###} m)");
  True(flatter.MeetA!.Value < first.MeetA!.Value - 0.2, $"incoming meet station moves back ({first.MeetA:0.###} -> {flatter.MeetA:0.###})");
});

Console.WriteLine($"\n{passed} passed, {failed} failed");
return failed;
