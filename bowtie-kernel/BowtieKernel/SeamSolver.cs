namespace Utnm.BowtieKernel;

public sealed class SolveOptions
{
  /// <summary>March step along the axis, metres.</summary>
  public double Step = 0.25;
  /// <summary>How far short of its centre of curvature a converging (arc) section is stopped.</summary>
  public double CapInset = 0.05;
  /// <summary>The seam is carried this far past the point where it meets the ground, so the meet sections still cross it.</summary>
  public double Overshoot = 0.10;
  /// <summary>The valley's own region is cut a few metres wider than the meet stations. A section in that margin never reaches
  /// the valley, and Civil 3D logs "Target object not found" for every mapped target a section cannot find. So the line is
  /// carried on, straight and level, far enough for the sections up to this many metres outside the meet stations to cross
  /// it. They cross it beyond their own daylight, so nothing is clipped by it. 0 = only Overshoot.</summary>
  public double OvershootReach = 4.0;
  public double OvershootMax = 30.0;
  /// <summary>A seam point counts only where at least one side is on a link steeper than this.</summary>
  public double MinSlope = 0.1;
  public double SimplifyTolerance = 0.005;
  /// <summary>Largest difference in first-point / hinge offset between the two ends of the bend before it is a section change.</summary>
  public double TemplateTolerance = 0.05;
  /// <summary>Spacing of the dense stations used to build the cap and to check between applied stations.</summary>
  public double DenseSpacing = 0.25;
  /// <summary>Where a cut slope from one side and a fill slope from the other cover the same ground there is no level at which
  /// they meet; the sides are then divided in plan and the step between them is reported. Above this step the bend is a design conflict.</summary>
  public double MaxLevelStep = 0.30;
  /// <summary>True when the clip subassembly also takes its LEVEL from the valley line (elevation target). The two sides then
  /// end on the same XYZ even where they do not meet in level, so a level step is no longer a conflict: what is limited
  /// instead is how far any one link is moved off its own slope (MaxLevelAdjust).</summary>
  public bool LevelFromTarget = false;
  public double MaxLevelAdjust = 0.30;
  /// <summary>Length over which the valley level runs out from the mean to the ground where the valley ends on one side's daylight.</summary>
  public double EndTaper = 1.0;
  /// <summary>Geometric tolerance for "the same crossing".</summary>
  public double CrossingTolerance = 0.02;
}

public enum SeamStatus { Ok, NoBend, NoBowtie, Unsupported, NoSeam, NeverMeetsGround, DesignConflict }

public sealed record SeamPoint(double X, double Y, double Z, double T, double U, double StationA, double OffsetA, double StationB, double OffsetB, string Kind);

public sealed class StationClip
{
  public double Station;
  /// <summary>natural | seam | apex | conflict | cap | meet | corner</summary>
  public string Role = "natural";
  public double Reach;
  public double? FocalOffset;
  public double? ClipOffset;
  public double? ClipX, ClipY, ClipZ;
  /// <summary>Every crossing of this section with the clip lines inside its natural reach.</summary>
  public List<double> Crossings = new();
  /// <summary>Seam stations: own design level minus the other side's design level at the clip point.</summary>
  public double? Closure;
  /// <summary>Level of the valley line (or apex) where this section stops, and how far that is from the level the section
  /// reaches on its own slope. With an elevation target the link is moved by LevelAdjust.</summary>
  public double? TargetZ, LevelAdjust;
  /// <summary>Slope of the clipped link as designed, and as it becomes when it is taken to TargetZ.</summary>
  public double? OwnSlope, AdjustedSlope;
  /// <summary>False when the section runs past its own centre of curvature before a clip line (or its daylight) stops it.</summary>
  public bool FirstCrossingIsClip = true;
}

public sealed class SeamResult
{
  public SeamStatus Status = SeamStatus.Ok;
  public readonly List<string> Reasons = new();
  public readonly List<string> Warnings = new();
  public Side Side;
  public string BendType = "";
  public double TurnDegrees;
  public double ApexStation;
  public P2 Apex, Axis;
  public double? Radius;
  public List<SeamPoint> SeamRaw = new();
  public List<SeamPoint> Seam = new();
  public List<P2> Cap = new();
  public (double X, double Y, double Z)? Hit;
  public double? MeetA, MeetB, MeetOffsetA, MeetOffsetB;
  public List<StationClip> Stations = new();
  public double? ClipFrom, ClipTo;
  public double? CapFrom, CapTo;
  /// <summary>Range of levels at which the converging sections arrive at the apex (grade x arc length): the size of the singular patch.</summary>
  public double ApexSpread;
  /// <summary>Mean level at which the converging sections arrive at the apex: the level to give the apex point of a feature line.</summary>
  public double? ApexZ;
  public double MaxLevelAdjust;
  /// <summary>Level change applied at a valley end that stops on one side's daylight (run-out from the mean to that side's level).</summary>
  public double EndRunOut;
  public int LinkCrossingsBefore, LinkCrossingsAfter;
  /// <summary>Largest level difference between the two sides where a section stops on the true seam. Should be millimetres.</summary>
  public double MaxClosure;
  /// <summary>Largest level difference where a section stops on the first leg of the seam, between the apex and the first point
  /// the two sides agree: lane level at an angle point with a grade break, the last metre before the centre on a graded curve.</summary>
  public double ApexMismatch;
  public double MaxLean;
  /// <summary>ground: the seam runs out to the point where both sides daylight together. separated: the overlap ends first
  /// (the sides daylight apart, as they do where one is in cut and the other in fill).</summary>
  public string SeamEnd = "";
  /// <summary>Largest step in level across a part of the seam where one side is a cut slope and the other a fill slope.</summary>
  public double LevelStep;
  /// <summary>Stations either side of the apex where the inside daylight changes between cut and fill, if it does.</summary>
  public List<double> CutFillChanges = new();
  /// <summary>Length of the level run-on past the valley's end (see SolveOptions.OvershootReach).</summary>
  public double OvershootLength;
}

/// <summary>
/// The valley (seam) of one bend: the line where the design surface swept by the sections before the apex and the one
/// swept by the sections after it have the same elevation. Every inside section stops where it meets the seam; sections
/// on a constant-radius arc converge on the arc centre before they can reach it and are stopped just short of it (the cap).
/// An angle point is the same construction with the apex at the PI and no cap.
/// </summary>
public static class SeamSolver
{
  public static SeamResult Solve(SampledBaseline bl, SectionSet sections, Func<double, double, double?>? ground,
    double bendFrom, double bendTo, SolveOptions? options = null, double? searchFrom = null, double? searchTo = null)
  {
    var opt = options ?? new SolveOptions();
    var r = new SeamResult();
    bendFrom = Math.Max(bendFrom, bl.Start); bendTo = Math.Min(bendTo, bl.End);

    // ---------------------------------------------------------------- the bend
    {
      bool pos = false, neg = false;
      for (var s = bendFrom; s <= bendTo; s += 0.25) { var k = bl.Kappa(s); pos |= k > 1e-4; neg |= k < -1e-4; }
      foreach (var c in bl.Corners.Where(c => c.Station >= bendFrom - 1e-6 && c.Station <= bendTo + 1e-6)) { pos |= c.Jump > 0; neg |= c.Jump < 0; }
      if (pos && neg) { r.Status = SeamStatus.Unsupported; r.Reasons.Add("The baseline turns both ways inside the range (a reverse curve): solve one bend per side (BendFinder splits them)."); return r; }
    }
    var turn = bl.Turn(bendFrom, bendTo);
    r.TurnDegrees = turn * 180 / Math.PI;
    if (Math.Abs(turn) < Math.PI / 180) { r.Status = SeamStatus.NoBend; r.Reasons.Add($"The baseline turns only {r.TurnDegrees:0.##} deg between {bendFrom:0.###} and {bendTo:0.###}."); return r; }
    var side = turn > 0 ? Side.Left : Side.Right;
    var sgn = turn > 0 ? 1.0 : -1.0;
    r.Side = side;

    var reachMax = sections.MaxReach + sections.Extension;
    var lo = Math.Max(bl.Start, searchFrom ?? bendFrom - 3 * reachMax);
    var hi = Math.Min(bl.End, searchTo ?? bendTo + 3 * reachMax);

    // reverse curvature inside the bend range is two bends, not one
    for (var s = bendFrom; s <= bendTo; s += 0.25)
      if (sgn * bl.Kappa(s) < -1e-4) { r.Status = SeamStatus.Unsupported; r.Reasons.Add($"The baseline turns the other way at {s:0.##}: split the range into one bend per side."); return r; }
    var corners = bl.Corners.Where(c => c.Station >= bendFrom - 1e-6 && c.Station <= bendTo + 1e-6).ToList();
    if (corners.Any(c => sgn * c.Jump < 0)) { r.Status = SeamStatus.Unsupported; r.Reasons.Add("An angle point inside the range turns the other way: split the range into one bend per side."); return r; }

    var biggest = corners.OrderByDescending(c => Math.Abs(c.Jump)).FirstOrDefault();
    double sApex; P2 apex, axis;
    if (corners.Count > 0 && Math.Abs(biggest.Jump) >= 0.5 * Math.Abs(turn))
    {
      r.BendType = "angle_point";
      sApex = biggest.Station;
      var (p, thA) = bl.At(sApex, before: true);
      var (_, thB) = bl.At(sApex, before: false);
      apex = p;
      axis = (SampledBaseline.InsideNormal(thA, side) + SampledBaseline.InsideNormal(thB, side)).Unit();
      if (Math.Abs(biggest.Jump) < 0.9 * Math.Abs(turn))
        r.Warnings.Add($"The angle point at {sApex:0.###} carries only {Math.Abs(biggest.Jump / turn):P0} of the turn; the rest of the range also bends.");
    }
    else
    {
      if (corners.Sum(c => Math.Abs(c.Jump)) > 0.2 * Math.Abs(turn))
      { r.Status = SeamStatus.Unsupported; r.Reasons.Add("The bend is a string of small angle points (a tessellated curve): rebuild the baseline with a true arc, or treat each angle point on its own."); return r; }
      r.BendType = "curve";
      // apex station: on the tightest part of the curve, as near the half-turn station as that part allows
      var st = new List<double>(); for (var s = bendFrom; s <= bendTo + 1e-9; s += 0.05) st.Add(Math.Min(s, bendTo));
      var kap = st.Select(s => sgn * bl.Kappa(s)).ToList();
      var kMax = kap.Max();
      if (kMax < 1e-5) { r.Status = SeamStatus.NoBend; r.Reasons.Add("No curvature found in the range."); return r; }
      var half = 0.5 * sgn * bl.SmoothTurn(bendFrom, bendTo);
      double a0 = bendFrom, b0 = bendTo;
      for (var i = 0; i < 50; i++) { var mid = 0.5 * (a0 + b0); if (sgn * bl.SmoothTurn(bendFrom, mid) < half) a0 = mid; else b0 = mid; }
      var sHalf = 0.5 * (a0 + b0);
      sApex = sgn * bl.Kappa(sHalf) >= 0.98 * kMax ? sHalf : st.Where((_, i) => kap[i] >= 0.98 * kMax).OrderBy(s => Math.Abs(s - sHalf)).First();
      var k = sgn * bl.Kappa(sApex);
      var (p, th) = bl.At(sApex);
      axis = SampledBaseline.InsideNormal(th, side);
      apex = p + axis * (1.0 / k);
      r.Radius = 1.0 / k;
      if (1.0 / k <= sections.StartOffset(sApex))
      { r.Status = SeamStatus.Unsupported; r.Reasons.Add($"The curve radius ({1.0 / k:0.##} m) is inside the road itself (the inside links start at {sections.StartOffset(sApex):0.##} m): the carriageway overlaps, which a daylight clip cannot fix."); return r; }
    }
    r.ApexStation = sApex; r.Apex = apex; r.Axis = axis;
    var m = axis.Left();

    // ---------------------------------------------------------------- the two ends must carry the same kind of section
    var secA = sections.Sections.LastOrDefault(s => s.Station <= bendFrom + 1e-6) ?? sections.Sections[0];
    var secB = sections.Sections.FirstOrDefault(s => s.Station >= bendTo - 1e-6) ?? sections.Sections[^1];
    if (Math.Abs(secA.StartOffset - secB.StartOffset) > opt.TemplateTolerance)
      r.Reasons.Add($"the inside links start at different offsets either side of the bend ({secA.StartOffset:0.###} / {secB.StartOffset:0.###} m)");
    if (secA.HingeOffset.HasValue && secB.HingeOffset.HasValue && Math.Abs(secA.HingeOffset.Value - secB.HingeOffset.Value) > opt.TemplateTolerance)
      r.Reasons.Add($"the hinges are at different offsets either side of the bend ({secA.HingeOffset:0.###} / {secB.HingeOffset:0.###} m)");
    static int CutFill(SectionSample s) { var h = s.HingeOffset; if (!h.HasValue) return 0; var z = s.Dz(h.Value, 0) ?? 0; return Math.Sign(s.Template[^1].Dz - z); }
    {
      var inRange = sections.Sections.Where(x => x.Station >= lo && x.Station <= hi).ToList();
      for (var i = 1; i < inRange.Count; i++)
        if (CutFill(inRange[i - 1]) * CutFill(inRange[i]) < 0) r.CutFillChanges.Add(0.5 * (inRange[i - 1].Station + inRange[i].Station));
    }
    if (r.Reasons.Count > 0) { r.Status = SeamStatus.Unsupported; r.Reasons.Insert(0, "A section change inside the bend is a design question, not a plain bowtie:"); return r; }
    if (sections.Sections.Any(s => s.Clipped && s.Station >= lo && s.Station <= hi))
    { r.Status = SeamStatus.Unsupported; r.Reasons.Add("Some sections in range are clipped: the seam must be computed from unclipped sections."); return r; }

    // ---------------------------------------------------------------- is there a bowtie at all?
    {
      var segs = new List<(P2, P2)>();
      for (var s = lo; s <= hi + 1e-9; s += opt.DenseSpacing)
      {
        if (bl.Corners.Any(c => Math.Abs(c.Station - s) < 1e-6)) continue;
        var (c, th) = bl.At(s);
        segs.Add((c, c + SampledBaseline.InsideNormal(th, side) * sections.Reach(s)));
      }
      if (Geometry.CountCrossings(segs, opt.CrossingTolerance) == 0)
      { r.Status = SeamStatus.NoBowtie; r.Reasons.Add("No inside section crosses another at its natural daylight: there is nothing to clip."); return r; }
    }

    // ---------------------------------------------------------------- the two design surfaces
    (double S, double O, double Z)? Eval(P2 p, bool entry)
    {
      var feet = entry ? bl.Feet(p, lo, sApex, side, reachMax) : bl.Feet(p, sApex, hi, side, reachMax);
      if (feet.Count == 0) return null;
      var f = feet.OrderBy(x => Math.Abs(x.S - sApex)).First();
      var z = sections.Z(f.S, f.Offset);
      return z.HasValue ? (f.S, f.Offset, z.Value) : null;
    }

    // ---------------------------------------------------------------- march: candidate seam points across the axis, per step
    // level : zA = zB, at least one side on its daylight slope, and not a cut slope against a fill slope
    // plan  : a cut slope on one side and a fill slope on the other cover the same ground. They never reach the same level,
    //         so the sides are divided where they are equally far from the baseline, and the step between them is reported
    // lane  : both sides still inside the hinge; only used when the bend has no other seam (a cut-to-fill change at the apex)
    const double coverTol = 0.05;
    bool Covered((double S, double O, double Z) e) => e.O <= sections.Reach(e.S) + coverTol;
    var tCap = 2 * reachMax + 2;
    var candidates = new List<(double T, List<(double U, string Kind)> Roots)>();
    var laneCandidates = new List<(double T, List<(double U, string Kind)> Roots)>();
    var empty = 0;
    for (var t = opt.Step; t <= tCap; t += opt.Step)
    {
      var c = apex + axis * t;
      // search width across the axis: on a curve the offsets at the apex are already a radius long, so a grade leans the seam from the start
      var U = 0.2 * (t + (r.Radius ?? 0.0)) + 0.3;
      const int n = 400;
      var ev = new ((double S, double O, double Z)? A, (double S, double O, double Z)? B)[n + 1];
      for (var i = 0; i <= n; i++) { var q = c + m * (-U + 2 * U * i / n); ev[i] = (Eval(q, true), Eval(q, false)); }
      var any = ev.Any(e => e.A.HasValue && e.B.HasValue);

      List<double> Roots(Func<(double S, double O, double Z), (double S, double O, double Z), double> f)
      {
        var found = new List<double>();
        double? F(double u) { var q = c + m * u; var ea = Eval(q, true); var eb = Eval(q, false); return ea.HasValue && eb.HasValue ? f(ea.Value, eb.Value) : null; }
        for (var i = 1; i <= n; i++)
        {
          if (!ev[i - 1].A.HasValue || !ev[i - 1].B.HasValue || !ev[i].A.HasValue || !ev[i].B.HasValue) continue;
          var f0 = f(ev[i - 1].A!.Value, ev[i - 1].B!.Value); var f1 = f(ev[i].A!.Value, ev[i].B!.Value);
          if (!(f0 == 0 || f0 * f1 < 0)) continue;
          double lo2 = -U + 2 * U * (i - 1) / n, hi2 = -U + 2 * U * i / n, fl = f0;
          for (var k = 0; k < 50; k++)
          {
            var mid = 0.5 * (lo2 + hi2); var fm = F(mid);
            if (!fm.HasValue) break;
            if (fl * fm.Value <= 0) hi2 = mid; else { lo2 = mid; fl = fm.Value; }
          }
          found.Add(0.5 * (lo2 + hi2));
        }
        return found;
      }
      (bool Ok, bool SlopedA, bool SlopedB, bool Opposite, bool Cover) Classify(double u)
      {
        var q = c + m * u; var ea = Eval(q, true); var eb = Eval(q, false);
        if (!ea.HasValue || !eb.HasValue || ea.Value.O < sections.StartOffset(ea.Value.S) || eb.Value.O < sections.StartOffset(eb.Value.S)) return default;
        var s1 = sections.Slope(ea.Value.S, ea.Value.O); var s2 = sections.Slope(eb.Value.S, eb.Value.O);
        if (!s1.HasValue || !s2.HasValue) return default;
        var a1 = Math.Abs(s1.Value) > opt.MinSlope; var a2 = Math.Abs(s2.Value) > opt.MinSlope;
        return (true, a1, a2, a1 && a2 && s1.Value * s2.Value < 0, Covered(ea.Value) && Covered(eb.Value));
      }

      var roots = new List<(double U, string Kind)>(); var lane = new List<(double U, string Kind)>();
      foreach (var u in Roots((ea, eb) => ea.Z - eb.Z))
      {
        var k = Classify(u);
        if (!k.Ok || k.Opposite || !(k.SlopedA || k.SlopedB)) continue;
        if (k.SlopedA && k.SlopedB) roots.Add((u, "exact"));
        else if (k.Cover) roots.Add((u, "hinge"));
      }
      // Same-way slopes can also fail to meet: next to a curve's centre the ground covered by both sides is a thin wedge,
      // and on a steep or uneven grade one side is above the other right across it. Then, too, the sides are divided in plan.
      var levelRootHere = roots.Count > 0;
      foreach (var u in Roots((ea, eb) => ea.O - eb.O))
      {
        var k = Classify(u);
        if (!k.Ok || !k.Cover) continue;
        if (k.Opposite) roots.Add((u, "plan"));
        else if (!k.SlopedA && !k.SlopedB) lane.Add((u, "lane"));
        else if (!levelRootHere) lane.Add((u, "plan"));
      }
      if (lane.Count > 0) laneCandidates.Add((t, lane));
      if (roots.Count > 0) { candidates.Add((t, roots)); empty = 0; }
      else if (!any && candidates.Count + laneCandidates.Count > 0 && ++empty * opt.Step >= 3.0) break;
    }

    // track the branch from the outer end inward (it is well conditioned out there)
    List<(double T, double U, string Kind)> Track(List<(double T, List<(double U, string Kind)> Roots)> cands)
    {
      var found = new List<(double T, double U, string Kind)>();
      (double T, double U)? prev = null;
      for (var i = cands.Count - 1; i >= 0; i--)
      {
        var (t, roots) = cands[i];
        (double U, string Kind) pick;
        if (prev == null) pick = roots.OrderBy(x => Math.Abs(x.U)).First();
        else
        {
          var pu = prev.Value.U;
          pick = roots.OrderBy(x => Math.Abs(x.U - pu)).First();
          if (Math.Abs(pick.U - pu) > 0.05 + 0.3 * (prev.Value.T - t)) continue;
        }
        found.Add((t, pick.U, pick.Kind)); prev = (t, pick.U);
      }
      found.Reverse();
      return found;
    }
    var path = Track(candidates);
    if (path.Count(x => x.Kind == "exact") < 2)
    {
      // No seam on matching slopes. That is what a change between cut and fill at the apex looks like: a cut slope and a fill
      // slope part company instead of meeting, so only the lanes overlap (plus, on a grade, a sliver where the slopes pass
      // each other). The seam is then the plan line between the two sides, for as long as they cover the same ground.
      var merged = laneCandidates.Select(x => (x.T, Roots: x.Roots.ToList())).ToList();
      foreach (var (t, roots) in candidates)
      {
        var plan = roots.Where(x => x.Kind == "plan").ToList();
        if (plan.Count == 0) continue;
        var at = merged.FindIndex(x => Math.Abs(x.T - t) < 1e-9);
        if (at >= 0) merged[at].Roots.AddRange(plan); else merged.Add((t, plan));
      }
      path = Track(merged.OrderBy(x => x.T).ToList());
      if (path.Count == 0 && !(r.BendType == "curve" && r.CutFillChanges.Count > 0))
      { r.Status = SeamStatus.NoSeam; r.Reasons.Add("The two sides never meet on a sloped link, and they do not overlap at lane level either: check that the sections carry inside links reaching past the hinge."); return r; }
      // a curve whose daylight changes between cut and fill at the apex: the two sides never cover the same ground, and the
      // only overlap is among the sections converging on the centre, which the cap deals with
      if (path.Count == 0) r.Warnings.Add("The inside daylight changes between cut and fill at the apex, so the two sides never cover the same ground: there is no seam, only the cap at the centre of the curve.");
    }
    r.MaxLean = path.Count > 0 ? path.Max(x => Math.Abs(x.U)) : 0.0;

    SeamPoint Make(double t, double u, string kind)
    {
      var q = apex + axis * t + m * u;
      var a = Eval(q, true); var b = Eval(q, false);
      var z = a?.Z ?? b?.Z ?? double.NaN;
      if (kind == "plan" && a.HasValue && b.HasValue) r.LevelStep = Math.Max(r.LevelStep, Math.Abs(a.Value.Z - b.Value.Z));
      if (a.HasValue && b.HasValue) z = 0.5 * (a.Value.Z + b.Value.Z);   // the agreed level: the mean of the two sides (they are equal on a true seam)
      return new SeamPoint(q.X, q.Y, z, t, u, a?.S ?? double.NaN, a?.O ?? double.NaN, b?.S ?? double.NaN, b?.O ?? double.NaN, kind);
    }
    r.SeamRaw.Add(new SeamPoint(apex.X, apex.Y, double.NaN, 0, 0, sApex, double.NaN, sApex, double.NaN, r.BendType == "curve" ? "curve_centre" : "pi"));
    foreach (var (t, u, kind) in path) r.SeamRaw.Add(Make(t, u, kind));

    // ---------------------------------------------------------------- where the seam ends
    // Walk out along it. It ends where it meets the ground (both sides daylight together there), or, failing that, where the
    // two sides stop covering the same ground (they have daylighted apart). Running out of seam while they still overlap blocks.
    var used = r.SeamRaw;
    if (path.Count == 0) { used = new List<SeamPoint>(); r.SeamEnd = "none"; }
    else
    {
      double? Diff(P2 q)
      {
        if (ground == null) return null;
        var a = Eval(q, true);
        // only on the daylight slope: the lanes can sit right at ground level where the daylight changes between cut and fill
        if (!a.HasValue || a.Value.O < sections.Hinge(a.Value.S)) return null;
        var g = ground(q.X, q.Y);
        return g.HasValue ? a.Value.Z - g.Value : null;
      }
      bool Overlap(P2 q) { var a = Eval(q, true); var b = Eval(q, false); return a.HasValue && b.HasValue && Covered(a.Value) && Covered(b.Value); }
      // a change of sign in (design - ground) only counts on the slopes: on a lane-only seam the lanes simply sit near the ground
      var slopeSeam = path.Any(x => x.Kind == "exact");
      double? prevD = null; P2 prevQ = default; var cut = -1;
      P2? lastOverlap = null; var lastOverlapIndex = -1; var overlapAtEnd = false;
      for (var i = 1; i < r.SeamRaw.Count && !r.Hit.HasValue; i++)
      {
        var a = new P2(r.SeamRaw[i - 1].X, r.SeamRaw[i - 1].Y); var b = new P2(r.SeamRaw[i].X, r.SeamRaw[i].Y);
        var len = (b - a).Length; var n = Math.Max(1, (int)Math.Ceiling(len / 0.05));
        for (var k = i == 1 ? 0 : 1; k <= n; k++)
        {
          var q = a + (b - a) * ((double)k / n);
          overlapAtEnd = Overlap(q);
          if (overlapAtEnd) { lastOverlap = q; lastOverlapIndex = i; }
          var d = slopeSeam ? Diff(q) : null;
          if (d.HasValue && prevD.HasValue && prevD.Value != 0 && prevD.Value * d.Value <= 0)
          {
            P2 l = prevQ, h = q; var dl = prevD.Value;
            for (var it = 0; it < 40; it++)
            {
              var mid = (l + h) * 0.5; var dm = Diff(mid);
              if (!dm.HasValue) break;
              if (dl * dm.Value <= 0) h = mid; else { l = mid; dl = dm.Value; }
            }
            var x = (l + h) * 0.5;
            r.Hit = (x.X, x.Y, Eval(x, true)?.Z ?? double.NaN);
            cut = i; r.SeamEnd = "ground";
            break;
          }
          if (d.HasValue) { prevD = d; prevQ = q; }
        }
      }
      P2? end = r.Hit.HasValue ? new P2(r.Hit.Value.X, r.Hit.Value.Y) : null;
      // ending by separation is only believed where there is a reason for it: a change between cut and fill in range, a
      // lane-only seam, or no ground to test against. Otherwise a seam that misses the ground means the wrong surface or short templates.
      var maySeparate = !slopeSeam || ground == null || r.CutFillChanges.Count > 0;
      if (end == null && path[^1].Kind is "plan" or "lane" && lastOverlap.HasValue)
      {
        // these points exist only where the sides overlap, so the overlap ends within one step beyond the last of them
        var lastP = new P2(r.SeamRaw[^1].X, r.SeamRaw[^1].Y);
        var dirP = r.SeamRaw.Count > 2 ? (lastP - new P2(r.SeamRaw[^2].X, r.SeamRaw[^2].Y)).Unit() : axis;
        double inside = 0, outside = opt.Step;
        if (!Overlap(lastP + dirP * outside))
          for (var it = 0; it < 30; it++) { var mid = 0.5 * (inside + outside); if (Overlap(lastP + dirP * mid)) inside = mid; else outside = mid; }
        end = lastP + dirP * inside; cut = r.SeamRaw.Count; r.SeamEnd = "separated";
      }
      if (end == null && maySeparate && lastOverlap.HasValue && (!overlapAtEnd || !slopeSeam)) { end = lastOverlap; cut = lastOverlapIndex; r.SeamEnd = "separated"; }
      if (end == null)
      {
        if (ground == null && lastOverlap.HasValue) r.Warnings.Add("No ground surface given and the sides still overlap at the end of the computed seam: the seam end was not found.");
        else { r.Status = SeamStatus.NeverMeetsGround; r.Reasons.Add("The seam runs out while the two sides still cover the same ground, without reaching the ground surface: the section templates stop short, or the two sides never daylight here."); }
      }
      else
      {
        var e = end.Value;
        var ea = Eval(e, true); var eb = Eval(e, false);
        r.MeetA = ea?.S; r.MeetOffsetA = ea?.O; r.MeetB = eb?.S; r.MeetOffsetB = eb?.O;
        used = r.SeamRaw.Take(cut).ToList();
        var last = used[^1];
        var dir = new P2(e.X - last.X, e.Y - last.Y);
        dir = dir.Length > 1e-6 ? dir.Unit() : axis;
        var ez = ea?.Z ?? double.NaN;
        if (r.SeamEnd == "separated" && ea.HasValue && eb.HasValue)
        {
          // The valley ends because one side has reached the ground while the other is still on its slope. That side's
          // sections just beyond the end daylight on their own, at ground level, right beside the valley end; so the valley
          // runs out to that level. The mean is kept along the valley and taken to the run-out level over the last stretch.
          var mean = 0.5 * (ea.Value.Z + eb.Value.Z);
          var dayA = ea.Value.O >= sections.Reach(ea.Value.S) - 0.10; var dayB = eb.Value.O >= sections.Reach(eb.Value.S) - 0.10;
          ez = dayA == dayB ? mean : dayA ? ea.Value.Z : eb.Value.Z;
          var shift = ez - mean;
          if (Math.Abs(shift) > 1e-4)
          {
            r.EndRunOut = shift;
            var lengthOut = 0.0; var at = e;
            for (var i = used.Count - 1; i >= 1 && lengthOut < opt.EndTaper; i--)
            {
              var q = new P2(used[i].X, used[i].Y); lengthOut += (at - q).Length; at = q;
              if (lengthOut >= opt.EndTaper || double.IsNaN(used[i].Z)) break;
              used[i] = used[i] with { Z = used[i].Z + shift * (1 - lengthOut / opt.EndTaper) };
            }
          }
        }
        used.Add(new SeamPoint(e.X, e.Y, ez, double.NaN, double.NaN, r.MeetA ?? double.NaN, r.MeetOffsetA ?? double.NaN, r.MeetB ?? double.NaN, r.MeetOffsetB ?? double.NaN, r.SeamEnd == "ground" ? "ground" : "end"));
        var over = opt.Overshoot;
        if (opt.OvershootReach > 0)
        {
          void Reach(double st)
          {
            if (st < bl.Start || st > bl.End || bl.Corners.Any(c => Math.Abs(c.Station - st) < 1e-6)) return;
            var (c0, th0) = bl.At(st); var n0 = SampledBaseline.InsideNormal(th0, side);
            var den = dir.Cross(n0);
            if (Math.Abs(den) < 1e-9) return;
            var w = c0 - e;
            var t = w.Cross(n0) / den;          // along the line from its end
            var o = w.Cross(dir) / den;         // along the section from the baseline
            if (t > 0 && o > 0) over = Math.Max(over, Math.Min(opt.OvershootMax, t + opt.Overshoot));
          }
          for (var d = 0.5; d <= opt.OvershootReach + 1e-9; d += 0.5)
          {
            if (r.MeetA.HasValue) Reach(r.MeetA.Value - d);
            if (r.MeetB.HasValue) Reach(r.MeetB.Value + d);
          }
        }
        r.OvershootLength = over;
        used.Add(new SeamPoint(e.X + dir.X * over, e.Y + dir.Y * over, ez, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, "overshoot"));
      }
    }
    r.Seam = Simplify(used, opt.SimplifyTolerance);
    List<P2> seamLine = r.Seam.Select(q => new P2(q.X, q.Y)).ToList();
    // the apex leg: every segment before the first point where both sides are on their daylight slopes
    var firstFull = r.Seam.FindIndex(q => q.Kind is "exact" or "plan" or "ground");
    if (firstFull < 0) firstFull = r.Seam.Count;

    // ---------------------------------------------------------------- per-station clip: seam, cap or natural daylight
    // the tightest curvature within 0.1 m: at a tangent-to-arc joint the sampled curvature ramps over one window, and the
    // conservative value keeps every converging section on the cap instead of letting it slip past the centre
    double? Focal(double s)
    {
      var k = Math.Max(sgn * bl.Kappa(s, 0.05), Math.Max(sgn * bl.Kappa(s - 0.1, 0.05), sgn * bl.Kappa(s + 0.1, 0.05)));
      return k > 1e-6 ? 1.0 / k : null;
    }
    bool AtCorner(double s) => bl.Corners.Any(c => Math.Abs(c.Station - s) < 1e-6);
    (P2 C, P2 N) Frame(double s) { var (c, th) = bl.At(s); return (c, SampledBaseline.InsideNormal(th, side)); }

    double? SeamCrossing(double s, double limit) => SeamCrossingAt(s, limit)?.Offset;
    (double Offset, int Segment)? SeamCrossingAt(double s, double limit)
    {
      var (c, nrm) = Frame(s);
      var hits = Geometry.RayPolylineSegments(c, nrm, limit, seamLine);
      return hits.Count > 0 ? hits[0] : null;
    }

    // the cap: the run of stations around the apex whose section reaches its centre of curvature before it reaches the seam
    bool IsCap(double s)
    {
      if (AtCorner(s)) return false;
      var f = Focal(s);
      return f.HasValue && sections.Reach(s) > f.Value - opt.CapInset && SeamCrossing(s, f.Value - opt.CapInset) == null;
    }
    if (r.BendType == "curve")
    {
      var runs = new List<(double From, double To)>();
      double? start = null; var prevS = lo;
      for (var s = lo; s <= hi + 1e-9; s += opt.DenseSpacing)
      {
        var cap = IsCap(s);
        if (cap && !start.HasValue) start = s;
        if (!cap && start.HasValue) { runs.Add((start.Value, prevS)); start = null; }
        prevS = s;
      }
      if (start.HasValue) runs.Add((start.Value, prevS));
      if (runs.Count > 1) r.Warnings.Add($"The converging sections form {runs.Count} separate groups; only the one nearest the apex is capped.");
      if (runs.Count > 0)
      {
        var (from, to) = runs.OrderBy(x => sApex < x.From ? x.From - sApex : sApex > x.To ? sApex - x.To : 0).First();
        // refine both ends to the station where the section meets the seam exactly at its cap offset
        double Refine(double inside, double outside)
        {
          for (var i = 0; i < 25; i++) { var mid = 0.5 * (inside + outside); if (IsCap(mid)) inside = mid; else outside = mid; }
          return inside;
        }
        from = Refine(from, Math.Max(lo, from - opt.DenseSpacing));
        to = Refine(to, Math.Min(hi, to + opt.DenseSpacing));
        // cap vertices: evenly spaced, plus every applied station in the run so its own section meets the cap exactly
        var n = Math.Max(1, (int)Math.Ceiling((to - from) / opt.DenseSpacing));
        var capStations = Enumerable.Range(0, n + 1).Select(i => from + (to - from) * i / n)
          .Concat(sections.Sections.Select(x => x.Station).Where(x => x > from && x < to && !AtCorner(x)))
          .OrderBy(x => x).ToList();
        var focal = new List<(double S, double F)>();
        foreach (var s in capStations)
        {
          var f = Focal(s); if (!f.HasValue) continue;
          if (focal.Count > 0 && s - focal[^1].S < 1e-6) continue;
          var (c, nrm) = Frame(s);
          r.Cap.Add(c + nrm * (f.Value - opt.CapInset));
          focal.Add((s, f.Value));
        }
        // the converging part proper (the constant-radius arc): the same offset, so any level range is grade along the arc
        var fMin = focal.Min(x => x.F);
        var zs = focal.Where(x => x.F <= 1.02 * fMin).Select(x => sections.Z(x.S, fMin)).Where(z => z.HasValue).Select(z => z!.Value).ToList();
        if (zs.Count > 0) r.ApexZ = zs.Average();
        // The clip line for these sections is a short straight bar through the apex, square to the axis. Every arc section
        // passes through the apex, so they all stop on the bar at that one point: no gap is left around it. The bar is only
        // as long as the converging sections need (spiral sections next to the arc pass a little to one side of the apex).
        r.Cap.Clear();
        var half = 0.05;
        foreach (var (st, _) in focal)
        {
          var (c, nrm) = Frame(st);
          var den = nrm.Cross(m);
          if (Math.Abs(den) < 1e-9) continue;
          var v = (apex - c).Cross(nrm) / den;      // where this section meets the bar line, measured along the bar from the apex
          half = Math.Max(half, Math.Abs(v) + 0.02);
        }
        r.Cap.Add(apex - m * half); r.Cap.Add(apex + m * half);
        r.CapFrom = from; r.CapTo = to;
        if (zs.Count > 0) r.ApexSpread = zs.Max() - zs.Min();
      }
    }

    // the level of the valley line at a point on it: between its vertices, as a feature line would give it
    double? SeamLevelAt(P2 q)
    {
      double? best = null; var bestD = double.MaxValue;
      for (var i = 0; i + 1 < r.Seam.Count; i++)
      {
        var a = new P2(r.Seam[i].X, r.Seam[i].Y); var e = new P2(r.Seam[i + 1].X, r.Seam[i + 1].Y) - a;
        var len2 = e.Dot(e); if (len2 < 1e-12) continue;
        var v = Math.Clamp((q - a).Dot(e) / len2, 0, 1);
        var d = (q - (a + e * v)).Length;
        double Zof(int k) => double.IsNaN(r.Seam[k].Z) ? (r.ApexZ ?? sections.Z(sApex, 0) ?? double.NaN) : r.Seam[k].Z;
        if (d < bestD && !double.IsNaN(Zof(i)) && !double.IsNaN(Zof(i + 1))) { bestD = d; best = Zof(i) + (Zof(i + 1) - Zof(i)) * v; }
      }
      return best;
    }

    var stations = sections.Sections.Select(s => s.Station).Where(s => s >= lo - 1e-9 && s <= hi + 1e-9).ToList();
    if (r.MeetA.HasValue) stations.Add(r.MeetA.Value);
    if (r.MeetB.HasValue) stations.Add(r.MeetB.Value);
    stations = stations.OrderBy(s => s).ToList();
    stations = stations.Where((s, i) => i == 0 || s - stations[i - 1] > 1e-4).ToList();

    foreach (var s in stations)
    {
      var sc = new StationClip { Station = s, Reach = sections.Reach(s), FocalOffset = Focal(s) };
      if (AtCorner(s)) { sc.Role = "corner"; r.Stations.Add(sc); continue; }
      var (c, nrm) = Frame(s);
      var all = Geometry.RayPolyline(c, nrm, sc.Reach + 1e-6, seamLine).Select(o => (O: o, Seam: true))
        .Concat(Geometry.RayPolyline(c, nrm, sc.Reach + 1e-6, r.Cap).Select(o => (O: o, Seam: false)))
        .OrderBy(x => x.O).ToList();
      foreach (var x in all) if (sc.Crossings.Count == 0 || x.O - sc.Crossings[^1] > opt.CrossingTolerance) sc.Crossings.Add(x.O);

      // A section stops at the first clip line it meets, seam or apex bar: that is what the subassembly does, so that is
      // what is modelled. Whether the result is sound is judged afterwards (no crossings left, no section past its own
      // centre of curvature, and in the tests: every piece of ground covered once).
      var seamHit = SeamCrossingAt(s, sc.Reach + 1e-6);
      var barHits = Geometry.RayPolyline(c, nrm, sc.Reach + 1e-6, r.Cap);
      var barAt = barHits.Count > 0 ? barHits[0] : (double?)null;
      if (barAt.HasValue && (!seamHit.HasValue || barAt.Value <= seamHit.Value.Offset))
      {
        if (barAt.Value < sc.Reach - opt.CrossingTolerance) { sc.Role = "cap"; sc.ClipOffset = barAt; }
      }
      else if (seamHit.HasValue && seamHit.Value.Offset >= sc.Reach - opt.CrossingTolerance) sc.Role = "meet";
      else if (seamHit.HasValue)
      {
        // the first leg of the seam joins the apex to the first point where the two sides really agree: a section stopped
        // there is inside the apex patch (lane level at an angle point, the last metre before the centre on a graded curve)
        var seg = seamHit.Value.Segment;
        // the closing points ("end", "ground", "overshoot") belong to whatever kind of seam led up to them
        string KindAt(int i) { while (i > 0 && r.Seam[i].Kind is "end" or "ground" or "overshoot") i--; return r.Seam[i].Kind; }
        sc.Role = seg < firstFull ? "apex" : KindAt(seg) == "plan" || KindAt(Math.Min(seg + 1, r.Seam.Count - 1)) == "plan" ? "conflict" : "seam";
        sc.ClipOffset = seamHit.Value.Offset;
      }
      var focalNow = sc.FocalOffset;
      var stopsAt = sc.ClipOffset ?? sc.Reach;
      if (focalNow.HasValue && stopsAt > focalNow.Value + opt.CrossingTolerance)
      {
        sc.FirstCrossingIsClip = false;
        r.Warnings.Add($"Station {s:0.###} runs {stopsAt - focalNow.Value:0.###} m past its own centre of curvature before anything stops it: it folds back over its neighbours.");
      }

      if (sc.ClipOffset.HasValue)
      {
        var q = c + nrm * sc.ClipOffset.Value;
        sc.ClipX = q.X; sc.ClipY = q.Y; sc.ClipZ = sections.Z(s, sc.ClipOffset.Value);
        sc.TargetZ = sc.Role == "cap" ? r.ApexZ : SeamLevelAt(q);
        if (sc.TargetZ.HasValue && sc.ClipZ.HasValue)
        {
          sc.LevelAdjust = sc.TargetZ.Value - sc.ClipZ.Value;
          var from = sections.LastVertexBefore(s, sc.ClipOffset.Value);
          var zFrom = sections.Z(s, from);
          if (zFrom.HasValue && sc.ClipOffset.Value - from > 0.05)
          {
            sc.OwnSlope = (sc.ClipZ.Value - zFrom.Value) / (sc.ClipOffset.Value - from);
            sc.AdjustedSlope = (sc.TargetZ.Value - zFrom.Value) / (sc.ClipOffset.Value - from);
          }
        }
        if (sc.Role is "seam" or "apex" or "conflict")
        {
          var other = Eval(q, entry: s > sApex);
          if (other.HasValue && sc.ClipZ.HasValue) sc.Closure = sc.ClipZ.Value - other.Value.Z;
        }
      }
      r.Stations.Add(sc);
    }

    var clipped = r.Stations.Where(x => x.ClipOffset.HasValue).ToList();
    if (clipped.Count > 0) { r.ClipFrom = clipped.Min(x => x.Station); r.ClipTo = clipped.Max(x => x.Station); }
    r.MaxClosure = r.Stations.Where(x => x.Role == "seam" && x.Closure.HasValue).Select(x => Math.Abs(x.Closure!.Value)).DefaultIfEmpty(0).Max();
    r.ApexMismatch = r.Stations.Where(x => x.Role == "apex" && x.Closure.HasValue).Select(x => Math.Abs(x.Closure!.Value)).DefaultIfEmpty(0).Max();
    r.LevelStep = Math.Max(r.LevelStep, r.Stations.Where(x => x.Role == "conflict" && x.Closure.HasValue).Select(x => Math.Abs(x.Closure!.Value)).DefaultIfEmpty(0).Max());
    r.MaxLevelAdjust = r.Stations.Where(x => x.LevelAdjust.HasValue).Select(x => Math.Abs(x.LevelAdjust!.Value)).DefaultIfEmpty(0).Max();
    if (opt.LevelFromTarget)
    {
      if (r.MaxLevelAdjust > opt.MaxLevelAdjust && r.Status == SeamStatus.Ok)
      {
        r.Status = SeamStatus.DesignConflict;
        r.Reasons.Add($"To bring both sides of the bend to one level, a link would have to be moved {r.MaxLevelAdjust:0.##} m off its own slope (limit {opt.MaxLevelAdjust:0.##} m). The two sides are too far apart in level where they cover the same ground: it needs a steeper slope, a wall, a wider bend or a gentler grade; or pass a larger maxLevelAdjust to accept it.");
      }
      else if (r.MaxLevelAdjust > 0.02)
        r.Warnings.Add($"The sides do not meet in level everywhere; links are taken to the mean level on the valley line, up to {r.MaxLevelAdjust:0.##} m off their own slope (see adjustedSlope per station).");
    }
    else if (r.LevelStep > opt.MaxLevelStep && r.Status == SeamStatus.Ok)
    {
      r.Status = SeamStatus.DesignConflict;
      r.Reasons.Add($"Where the two sides of the bend cover the same ground they are {r.LevelStep:0.##} m apart in level and never meet (a cut slope beside a fill slope, or a steep or uneven grade through a tight bend). A clip line can only divide them in plan, which leaves that step along it. It needs a steeper slope, a wall, a wider bend or a gentler grade; or pass a larger maxLevelStep to accept the step.");
    }
    else if (r.LevelStep > 0.02) r.Warnings.Add($"Along part of the seam the two sides do not meet in level; they are divided in plan and are {r.LevelStep:0.##} m apart there.");
    if (!opt.LevelFromTarget)
    if (Math.Max(r.ApexMismatch, r.ApexSpread) > 0.10)
      r.Warnings.Add($"Next to the apex the sections arrive at levels up to {Math.Max(r.ApexMismatch, r.ApexSpread):0.##} m apart (a grade along the bend, or a cut slope beside a fill slope): the small patch there will not be a clean surface and wants a look.");
    // ---------------------------------------------------------------- do the links still cross in plan?
    var live = r.Stations.Where(x => x.Role != "corner").ToList();
    r.LinkCrossingsBefore = Geometry.CountCrossings(live.Select(x => { var (c, nrm) = Frame(x.Station); return (c, c + nrm * x.Reach); }).ToList(), opt.CrossingTolerance);
    r.LinkCrossingsAfter = Geometry.CountCrossings(live.Select(x => { var (c, nrm) = Frame(x.Station); return (c, c + nrm * (x.ClipOffset ?? x.Reach)); }).ToList(), opt.CrossingTolerance);
    return r;
  }

  private static List<SeamPoint> Simplify(List<SeamPoint> pts, double tol)
  {
    if (pts.Count < 3) return pts.ToList();
    var keep = new bool[pts.Count];
    keep[0] = keep[^1] = true;
    for (var i = 0; i < pts.Count; i++) if (pts[i].Kind is "ground" or "end" or "pi" or "curve_centre") keep[i] = true;
    for (var i = 1; i < pts.Count; i++) if (pts[i].Kind != pts[i - 1].Kind) { keep[i] = true; keep[i - 1] = true; }
    var full = pts.FindIndex(q => q.Kind == "exact");
    if (full > 0) keep[full] = true;      // the first point with both sides on the slope ends the apex leg
    if (pts.Count > 1) keep[1] = true;
    void Rec(int i, int j)
    {
      if (j <= i + 1) return;
      var a = new P2(pts[i].X, pts[i].Y); var e = new P2(pts[j].X, pts[j].Y) - a; var len = e.Length;
      var best = -1.0; var idx = -1;
      for (var k = i + 1; k < j; k++)
      {
        var v = new P2(pts[k].X, pts[k].Y) - a;
        var d = len < 1e-12 ? v.Length : Math.Abs(e.Cross(v)) / len;
        // levels count too: between kept vertices the valley line is straight in level, so a bend in level needs a vertex
        if (len > 1e-12 && !double.IsNaN(pts[i].Z) && !double.IsNaN(pts[j].Z) && !double.IsNaN(pts[k].Z))
          d = Math.Max(d, Math.Abs(pts[k].Z - (pts[i].Z + (pts[j].Z - pts[i].Z) * Math.Clamp(v.Dot(e) / (len * len), 0, 1))));
        if (d > best) { best = d; idx = k; }
      }
      if (best > tol) { keep[idx] = true; Rec(i, idx); Rec(idx, j); }
    }
    var anchors = Enumerable.Range(0, pts.Count).Where(i => keep[i]).ToList();
    for (var a = 0; a + 1 < anchors.Count; a++) Rec(anchors[a], anchors[a + 1]);
    return pts.Where((_, k) => keep[k]).ToList();
  }
}

public static class Geometry
{
  /// <summary>Offsets along the ray origin + dir * o (0 &lt; o &lt;= length) at which it crosses the polyline, ascending.</summary>
  public static List<double> RayPolyline(P2 origin, P2 dir, double length, IReadOnlyList<P2> line)
  {
    var hits = new List<double>();
    for (var i = 0; i + 1 < line.Count; i++)
    {
      var a = line[i]; var e = line[i + 1] - a;
      var den = dir.Cross(e);
      if (Math.Abs(den) < 1e-12) continue;
      var w = a - origin;
      var o = w.Cross(e) / den;
      var v = w.Cross(dir) / den;
      if (o > 1e-9 && o <= length && v >= -1e-9 && v <= 1 + 1e-9) hits.Add(o);
    }
    hits.Sort();
    return hits;
  }

  public static List<(double Offset, int Segment)> RayPolylineSegments(P2 origin, P2 dir, double length, IReadOnlyList<P2> line)
  {
    var hits = new List<(double, int)>();
    for (var i = 0; i + 1 < line.Count; i++)
    {
      var a = line[i]; var e = line[i + 1] - a;
      var den = dir.Cross(e);
      if (Math.Abs(den) < 1e-12) continue;
      var w = a - origin;
      var o = w.Cross(e) / den;
      var v = w.Cross(dir) / den;
      if (o > 1e-9 && o <= length && v >= -1e-9 && v <= 1 + 1e-9) hits.Add((o, i));
    }
    hits.Sort((x, y) => x.Item1.CompareTo(y.Item1));
    return hits;
  }

  /// <summary>Pairs of segments that cross properly, i.e. more than 'tol' from every segment end.</summary>
  public static int CountCrossings(IReadOnlyList<(P2 A, P2 B)> segs, double tol)
  {
    var count = 0;
    for (var i = 0; i < segs.Count; i++)
      for (var j = i + 1; j < segs.Count; j++)
      {
        var (a, b) = segs[i]; var (c, d) = segs[j];
        var e = b - a; var f = d - c;
        var den = e.Cross(f);
        if (Math.Abs(den) < 1e-12) continue;
        var w = c - a;
        var t = w.Cross(f) / den; var u = w.Cross(e) / den;
        var le = e.Length; var lf = f.Length;
        if (t * le > tol && (1 - t) * le > tol && u * lf > tol && (1 - u) * lf > tol) count++;
      }
    return count;
  }
}
