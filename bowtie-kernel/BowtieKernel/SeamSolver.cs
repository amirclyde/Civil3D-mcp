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
  /// <summary>A steep link shorter than this (a drain wall, a kerb face, a step) is not a slope the two sides meet on: it is
  /// treated like the flat link it sits in. The daylight link at the end of a section is exempt.</summary>
  public double MinSlopeRun = 0.5;
  /// <summary>A level meeting of the two sides counts only where at least one of them is within this distance of its own
  /// daylight (beyond it the sections are extended only to find where the valley meets the ground).</summary>
  public double ExactBeyondReach = 0.5;
  /// <summary>A section whose last point is further than this from the ground, in level, does not end on the ground.</summary>
  public double EndOnGroundTolerance = 0.10;
  /// <summary>Accept clipped sections that never come near the daylight surface given (walls, fixed-width sections whose
  /// region still has a surface target). Off by default: that usually means the wrong surface.</summary>
  public bool AcceptOffSurfaceEnds = false;
  public double SimplifyTolerance = 0.005;
  /// <summary>Largest difference in first-point / hinge offset between the two ends of the bend before it is a section change.</summary>
  public double TemplateTolerance = 0.05;
  /// <summary>Refuse a bend whose section changes inside it (the first version's behaviour). Off by default: a change of
  /// section is the design as given, solved with each side on its own sections and reported in SectionChanges.</summary>
  public bool RefuseSectionChange = false;
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
  /// <summary>With AdjustFrom.FromHinge: an optional limit on the extra grade (0.10 = 10 %) beyond which the bend is a
  /// design conflict. Null (default): no limit - links made steeper than designed are flagged instead (SlopeFlags).</summary>
  public double? MaxSlopeChange = null;
  /// <summary>How the clip subassembly takes a link to the valley level: LastLink (UTNM_LaneDaylightClip v0.3 and
  /// UTNMBenchClip v0.3: only the clipped link changes slope) or FromHinge (every link from the hinge to the clip gets the
  /// same extra grade, so the move is spread over the whole daylight side). Decides the adjusted slopes reported.</summary>
  public AdjustFrom AdjustFrom = AdjustFrom.LastLink;
  /// <summary>Default NoSteeper (Amir, 23 Sep 2026: the designed ratio is the steepest allowed); Mean as the option.</summary>
  public LevelRule LevelRule = LevelRule.NoSteeper;
  /// <summary>A slope counts as steeper than designed when its grade exceeds the designed one by more than this (0.001 =
  /// 0.1 %, the modelling precision).</summary>
  public double SteeperTolerance = 0.001;
  /// <summary>Length over which the valley level runs out from the mean to the ground where the valley ends on one side's daylight.</summary>
  public double EndTaper = 1.0;
  /// <summary>Cell size of the ground grid the ground function interpolates (for the end-on-ground tolerance); 0 = exact ground.</summary>
  public double GroundCell = 0;
  /// <summary>Geometric tolerance for "the same crossing".</summary>
  public double CrossingTolerance = 0.02;
}

/// <summary>Unresolved: a clip was computed but it does not do the job (link crossings remain, or a section would run past
/// its own centre of curvature before anything stops it). Nothing should be written from such a result.</summary>
public enum SeamStatus { Ok, NoBend, NoBowtie, Unsupported, NoSeam, NeverMeetsGround, DesignConflict, Unresolved }
public enum AdjustFrom { LastLink, FromHinge }
/// <summary>The one level both sides take on the valley line where they do not meet in level. Mean: halfway (the rule of
/// 20 Sep 2026). NoSteeper (default from 23 Sep 2026): a level that only ever flattens the slopes (a side in cut is only lowered, a side in fill only
/// raised), so no batter comes out steeper than the engineer's ratio; the mean where no such level exists.</summary>
public enum LevelRule { Mean, NoSteeper }

/// <summary>Two neighbouring sections of a different kind (see SectionSet.SameKind). InBend: between the ends of the bend.</summary>
public sealed record SectionChange(double From, double To, string What, bool InBend);

/// <summary>A link that spreading the level move would take beyond its design: kind "steeper" (a slope steeper than its
/// designed ratio) or "fall_reversed" (a bench or lane whose cross-fall changes direction). Grades as rise/run, signed
/// towards the inside; offsets from the baseline.</summary>
public sealed record SlopeFlag(double Station, string Kind, double FromOffset, double ToOffset, double DesignGrade, double NewGrade);

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
  /// <summary>The extra grade every link from the hinge to the clip would get if the move were spread over them.</summary>
  public double? SpreadGrade;
  /// <summary>With the move spread from the hinge: the links that would come out steeper than designed (the designed ratio
  /// is the steepest allowed - these need remedial or extra slope protection from the engineer), and benches whose
  /// designed cross-fall would be reversed (water sent away from the berm drain).</summary>
  public List<SlopeFlag> SlopeFlags = new();
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
  /// <summary>Largest change in the slope of a clipped link when it is taken to the valley level (gradient).</summary>
  public double MaxSlopeChange;
  /// <summary>Largest extra grade if the level moves were spread from the hinge (AdjustFrom.FromHinge).</summary>
  public double MaxSpreadGrade;
  /// <summary>Every link that spreading would take beyond its design (see StationClip.SlopeFlags), for highlighting.</summary>
  public List<SlopeFlag> SlopeFlags = new();
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
  /// <summary>Where the design changes section within the range read: to be highlighted, not silently smoothed over.</summary>
  public List<SectionChange> SectionChanges = new();
  /// <summary>True when sections in range do not end on the ground: the valley then ends where the two sides stop covering
  /// the same ground.</summary>
  public bool OpenEnded;
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

    // ---------------------------------------------------------------- where the design changes section
    // A change of section in or near the bend (a drain size, a lane width, a region with another assembly) is the
    // engineer's design: it is solved as it stands, each side of the bend on its own sections, and reported so it can be
    // highlighted. Only with RefuseSectionChange is it refused, as the first version did.
    foreach (var (a, b, what) in sections.Changes(lo, hi, opt.TemplateTolerance))
      r.SectionChanges.Add(new SectionChange(a, b, what, a <= bendTo + 1e-6 && b >= bendFrom - 1e-6));
    if (opt.RefuseSectionChange && r.SectionChanges.Any(c => c.InBend))
    {
      r.Status = SeamStatus.Unsupported;
      r.Reasons.Add("A section change inside the bend is a design question, not a plain bowtie:");
      r.Reasons.AddRange(r.SectionChanges.Where(c => c.InBend).Select(c => $"between {c.From:0.###} and {c.To:0.###}: {c.What}"));
      return r;
    }
    static int CutFill(SectionSample s) { var h = s.HingeOffset; if (!h.HasValue) return 0; var z = s.Dz(h.Value, 0) ?? 0; return Math.Sign(s.Template[^1].Dz - z); }
    {
      var inRange = sections.Sections.Where(x => x.Station >= lo && x.Station <= hi).ToList();
      for (var i = 1; i < inRange.Count; i++)
        if (CutFill(inRange[i - 1]) * CutFill(inRange[i]) < 0) r.CutFillChanges.Add(0.5 * (inRange[i - 1].Station + inRange[i].Station));
    }

    // ---------------------------------------------------------------- each side of the bend on its own sections
    // The surface before the apex is swept by the incoming sections and the one after it by the outgoing sections. Where
    // the section is the same kind on both sides the two sets share their neighbours across the apex (a smooth surface);
    // where it changes at the bend neither side borrows the other's section. At an angle point the section Civil 3D draws
    // at the PI points along the outgoing leg, so it belongs to the outgoing side (the first of two at the PI, where a
    // region ends there, belongs to the incoming side).
    SectionSet entrySet, exitSet;
    {
      var all = sections.Sections;
      var atApex = Enumerable.Range(0, all.Count).Where(i => Math.Abs(all[i].Station - sApex) <= 1e-6).ToList();
      var inEntry = new HashSet<int>(Enumerable.Range(0, all.Count).Where(i => all[i].Station < sApex - 1e-6));
      var inExit = new HashSet<int>(Enumerable.Range(0, all.Count).Where(i => all[i].Station > sApex + 1e-6));
      if (r.BendType == "curve") { inEntry.UnionWith(atApex); inExit.UnionWith(atApex); }
      else if (atApex.Count > 1) { inEntry.UnionWith(atApex.Take(atApex.Count - 1)); inExit.UnionWith(atApex.Skip(1)); }
      else inExit.UnionWith(atApex);
      if (inEntry.Count > 0 && inExit.Count > 0)
      {
        var lastIn = inEntry.Max(); var firstOut = inExit.Min();
        if (SectionSet.SameKind(all[lastIn], all[firstOut], opt.TemplateTolerance)) { inEntry.Add(firstOut); inExit.Add(lastIn); }
      }
      entrySet = inEntry.Count > 0 ? sections.Subset((_, i) => inEntry.Contains(i)) : sections;
      exitSet = inExit.Count > 0 ? sections.Subset((_, i) => inExit.Contains(i)) : sections;
      if (r.SectionChanges.Any(c => c.InBend))
        r.Warnings.Add("The section changes inside the bend (" + string.Join("; ", r.SectionChanges.Where(c => c.InBend).Select(c => $"{c.From:0.###}-{c.To:0.###}: {c.What}")) +
          "). That is the design as given: each side is clipped on its own sections, and the change is reported for highlighting.");
    }
    SectionSet For(double s) => s < sApex - 1e-9 ? entrySet : exitSet;
    SectionSet Of(bool entry) => entry ? entrySet : exitSet;

    // ---------------------------------------------------------------- do the sections end on the ground?
    // A daylight link ends on the ground. Other sections do not: a fixed-width section or a wall stops in the air, and a
    // benched part can meet the ground on the way out and carry on with a terminal bench and drain whatever the ground does.
    // Such a section is "open ended": it is not extended past its last point, a crossing of design and ground along it is
    // not its daylight, and the valley ends where the two sides stop covering the same ground rather than at the ground.
    var neverOnGround = new List<double>();
    if (ground != null)
    {
      var metOnTheWay = new List<double>(); var never = neverOnGround;
      foreach (var sec in sections.Sections.Where(x => x.Station >= lo - 1e-9 && x.Station <= hi + 1e-9))
      {
        if (bl.Corners.Any(c => Math.Abs(c.Station - sec.Station) < 1e-6)) continue;
        var (c0, th0) = bl.At(sec.Station); var n0 = SampledBaseline.InsideNormal(th0, side);
        double? Gap(double off) { var q = c0 + n0 * off; var g = ground(q.X, q.Y); var dz = sec.Dz(off, 0); return g.HasValue && dz.HasValue ? sec.Z0 + dz.Value - g.Value : null; }
        // measured on the real surface when the plugin gave it; from the grid otherwise, with room for the grid's own error
        // on sloping ground (bilinear over a cell misses the TIN by up to about slope x cell / 2)
        double tol = opt.EndOnGroundTolerance, atEndValue;
        if (sec.EndGap.HasValue) atEndValue = sec.EndGap.Value;
        else
        {
          var atEnd = Gap(sec.Reach);
          if (!atEnd.HasValue) continue;
          atEndValue = atEnd.Value;
          var q = c0 + n0 * sec.Reach;
          var gx0 = ground(q.X - 0.5, q.Y); var gx1 = ground(q.X + 0.5, q.Y); var gy0 = ground(q.X, q.Y - 0.5); var gy1 = ground(q.X, q.Y + 0.5);
          if (gx0.HasValue && gx1.HasValue && gy0.HasValue && gy1.HasValue)
            tol += 0.5 * Math.Sqrt(Math.Pow(gx1.Value - gx0.Value, 2) + Math.Pow(gy1.Value - gy0.Value, 2)) * opt.GroundCell;
        }
        if (Math.Abs(atEndValue) <= tol) continue;
        sec.Extend = false;
        double? first = null; var met = false;
        for (var o = sec.HingeOffset ?? sec.StartOffset; o <= sec.Reach && !met; o += 0.1)
        { var d = Gap(o); if (!d.HasValue) continue; first ??= d; met = first.Value * d.Value <= 0 || Math.Abs(d.Value) <= opt.EndOnGroundTolerance; }
        (met ? metOnTheWay : never).Add(sec.Station);
      }
      r.OpenEnded = metOnTheWay.Count + never.Count > 0;
      if (metOnTheWay.Count > 0)
        r.Warnings.Add($"{metOnTheWay.Count} inside section(s) between {metOnTheWay.Min():0.##} and {metOnTheWay.Max():0.##} meet the ground on the way out and carry on (a terminal bench or drain): they are taken as they end, and the valley runs out to where the two sides stop covering the same ground.");
      if (never.Count > 0)
        r.Warnings.Add($"{never.Count} inside section(s) between {never.Min():0.##} and {never.Max():0.##} end off the daylight surface given (a fixed-width section or a wall, or a different target surface): they are taken as they end, and the valley runs out to where the two sides stop covering the same ground. Check the surface if they should daylight.");
    }
    if (sections.Sections.Any(s => s.Clipped && s.Station >= lo && s.Station <= hi))
    { r.Status = SeamStatus.Unsupported; r.Reasons.Add("Some sections in range are clipped: the seam must be computed from unclipped sections."); return r; }

    // ---------------------------------------------------------------- is there a bowtie at all?
    {
      var segs = new List<(P2, P2)>();
      for (var s = lo; s <= hi + 1e-9; s += opt.DenseSpacing)
      {
        if (bl.Corners.Any(c => Math.Abs(c.Station - s) < 1e-6)) continue;
        var (c, th) = bl.At(s);
        segs.Add((c, c + SampledBaseline.InsideNormal(th, side) * For(s).Reach(s)));
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
      var z = Of(entry).Z(f.S, f.Offset);
      return z.HasValue ? (f.S, f.Offset, z.Value) : null;
    }

    // ---------------------------------------------------------------- march: candidate seam points across the axis, per step
    // level : zA = zB, at least one side on its daylight slope, and not a cut slope against a fill slope
    // plan  : a cut slope on one side and a fill slope on the other cover the same ground. They never reach the same level,
    //         so the sides are divided where they are equally far from the baseline, and the step between them is reported
    // lane  : both sides still inside the hinge; only used when the bend has no other seam (a cut-to-fill change at the apex)
    const double coverTol = 0.05;
    bool Covered((double S, double O, double Z) e, bool entry) => e.O <= Of(entry).Reach(e.S) + coverTol;
    var tCap = 2 * reachMax + 2;
    var candidates = new List<(double T, List<(double U, string Kind)> Roots)>();
    var laneCandidates = new List<(double T, List<(double U, string Kind)> Roots)>();
    var benchCandidates = new List<(double T, List<(double U, string Kind)> Roots)>();
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
      (bool Ok, bool SlopedA, bool SlopedB, bool Opposite, bool Cover, bool Beyond, bool Near) Classify(double u)
      {
        var q = c + m * u; var ea = Eval(q, true); var eb = Eval(q, false);
        if (!ea.HasValue || !eb.HasValue || ea.Value.O < entrySet.StartOffset(ea.Value.S) || eb.Value.O < exitSet.StartOffset(eb.Value.S)) return default;
        var s1 = entrySet.Slope(ea.Value.S, ea.Value.O); var s2 = exitSet.Slope(eb.Value.S, eb.Value.O);
        if (!s1.HasValue || !s2.HasValue) return default;
        var minRun = opt.MinSlopeRun;
        var a1 = Math.Abs(s1.Value) > opt.MinSlope && entrySet.LinkRun(ea.Value.S, ea.Value.O) >= minRun;
        var a2 = Math.Abs(s2.Value) > opt.MinSlope && exitSet.LinkRun(eb.Value.S, eb.Value.O) >= minRun;
        var beyond = ea.Value.O > entrySet.Hinge(ea.Value.S) + 0.05 && eb.Value.O > exitSet.Hinge(eb.Value.S) + 0.05;
        var near = Math.Min(ea.Value.O - entrySet.Reach(ea.Value.S), eb.Value.O - exitSet.Reach(eb.Value.S)) <= opt.ExactBeyondReach;
        return (true, a1, a2, a1 && a2 && s1.Value * s2.Value < 0, Covered(ea.Value, true) && Covered(eb.Value, false), beyond, near);
      }

      var roots = new List<(double U, string Kind)>(); var lane = new List<(double U, string Kind)>(); var bench = new List<(double U, string Kind)>();
      foreach (var u in Roots((ea, eb) => ea.Z - eb.Z))
      {
        var k = Classify(u);
        if (!k.Ok || k.Opposite || !(k.SlopedA || k.SlopedB)) continue;
        // a meeting well beyond both daylights is only the extended slopes crossing: no ground is shared there
        if (!k.Near) continue;
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
        else if (!k.SlopedA && !k.SlopedB && k.Beyond) bench.Add((u, "plan"));      // both on flat links past the hinge: benches
        else if (!k.SlopedA && !k.SlopedB) lane.Add((u, "lane"));
        else if (!levelRootHere) lane.Add((u, "plan"));
      }
      if (lane.Count > 0) laneCandidates.Add((t, lane));
      if (bench.Count > 0) benchCandidates.Add((t, bench));
      if (roots.Count > 0) { candidates.Add((t, roots)); empty = 0; }
      else if (bench.Count > 0) empty = 0;
      else if (!any && candidates.Count + laneCandidates.Count > 0 && ++empty * opt.Step >= 3.0) break;
    }

    // track the branch from the outer end inward (it is well conditioned out there)
    List<(double T, double U, string Kind)> Track(List<(double T, List<(double U, string Kind)> Roots)> cands, bool preferLevel = false)
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
          var gate = 0.05 + 0.3 * (prev.Value.T - t);
          pick = roots.OrderBy(x => Math.Abs(x.U - pu)).First();
          // with bench division points in the list: a true meeting of the two sides wins wherever there is one within reach
          if (preferLevel && pick.Kind == "plan")
          {
            var level = roots.Where(x => x.Kind != "plan" && Math.Abs(x.U - pu) <= gate).OrderBy(x => Math.Abs(x.U - pu)).ToList();
            if (level.Count > 0) pick = level[0];
          }
          if (Math.Abs(pick.U - pu) > gate) continue;
        }
        found.Add((t, pick.U, pick.Kind)); prev = (t, pick.U);
      }
      found.Reverse();
      return found;
    }
    var path = Track(candidates);
    if (path.Count(x => x.Kind == "exact") >= 2 && benchCandidates.Count > 0)
    {
      // Benches beyond the first slope: where both sides are on a flat link they meet in level nowhere useful (two benches
      // 0.1 m apart in level at 4 % would put the line 2.5 m to one side), so there the sides are divided in plan, as a cut
      // slope against a fill slope is, and the line takes the mean level. Wherever the two sides do meet in level within
      // reach of the line, that meeting wins.
      var tFirst = path.First(x => x.Kind == "exact").T;
      var merged = candidates.Select(x => (x.T, Roots: x.Roots.ToList())).ToList();
      var added = false;
      foreach (var (t, b) in benchCandidates)
      {
        if (t <= tFirst) continue;
        var at = merged.FindIndex(x => Math.Abs(x.T - t) < 1e-9);
        if (at >= 0) merged[at].Roots.AddRange(b); else merged.Add((t, b.ToList()));
        added = true;
      }
      if (added) path = Track(merged.OrderBy(x => x.T).ToList(), preferLevel: true);
    }
    if (path.Count(x => x.Kind == "exact") < 2)
    {
      // No seam on matching slopes. That is what a change between cut and fill at the apex looks like: a cut slope and a fill
      // slope part company instead of meeting, so only the lanes overlap (plus, on a grade, a sliver where the slopes pass
      // each other). The seam is then the plan line between the two sides, for as long as they cover the same ground.
      var merged = laneCandidates.Select(x => (x.T, Roots: x.Roots.ToList())).ToList();
      // benches count as lane here: both sides flat, divided in plan
      foreach (var (t, b) in benchCandidates)
      {
        var at = merged.FindIndex(x => Math.Abs(x.T - t) < 1e-9);
        if (at >= 0) merged[at].Roots.AddRange(b); else merged.Add((t, b.ToList()));
      }
      merged = merged.OrderBy(x => x.T).ToList();
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

    // the one level both sides take at a valley point: the mean of the two (Amir, 20 Sep 2026), or with NoSteeper the level
    // closest to the mean that only flattens each side; equal on a true seam
    // +1: the side rises from its hinge to this point (cut: lowering it flattens it), -1: it falls (fill), 0: level
    int Rise(SectionSet set, double st, double off)
    {
      var h = set.Hinge(st);
      if (off <= h + 0.05) return 0;
      var zo = set.Dz(st, off); var zh = set.Dz(st, h);
      return zo.HasValue && zh.HasValue && Math.Abs(zo.Value - zh.Value) > 0.01 ? Math.Sign(zo.Value - zh.Value) : 0;
    }
    double NoSteeperLevel(IEnumerable<(double Z, int Rise)> sides)
    {
      double lo = double.NegativeInfinity, hi = double.PositiveInfinity; var zs = new List<double>();
      foreach (var (z, rise) in sides) { zs.Add(z); if (rise > 0) hi = Math.Min(hi, z); else if (rise < 0) lo = Math.Max(lo, z); }
      var mean = zs.Average();
      if (lo > hi + 1e-9) return mean;                       // no level flattens every side: the mean, and the flags say where
      return Math.Clamp(mean, lo, hi);
    }
    double AgreedLevel((double S, double O, double Z) ea, (double S, double O, double Z) eb) =>
      opt.LevelRule == LevelRule.Mean ? 0.5 * (ea.Z + eb.Z)
        : NoSteeperLevel(new[] { (ea.Z, Rise(entrySet, ea.S, ea.O)), (eb.Z, Rise(exitSet, eb.S, eb.O)) });
    SeamPoint Make(double t, double u, string kind)
    {
      var q = apex + axis * t + m * u;
      var a = Eval(q, true); var b = Eval(q, false);
      var z = a?.Z ?? b?.Z ?? double.NaN;
      if (kind == "plan" && a.HasValue && b.HasValue) r.LevelStep = Math.Max(r.LevelStep, Math.Abs(a.Value.Z - b.Value.Z));
      if (a.HasValue && b.HasValue) z = AgreedLevel(a.Value, b.Value);   // equal on a true seam; where they differ, see LevelRule
      return new SeamPoint(q.X, q.Y, z, t, u, a?.S ?? double.NaN, a?.O ?? double.NaN, b?.S ?? double.NaN, b?.O ?? double.NaN, kind);
    }
    r.SeamRaw.Add(new SeamPoint(apex.X, apex.Y, double.NaN, 0, 0, sApex, double.NaN, sApex, double.NaN, r.BendType == "curve" ? "curve_centre" : "pi"));
    {
      // Between march steps the line is straight in plan, but its level need not be: a bench edge or a drain crossed by the
      // valley bends it. Points are added along the straight piece until the level between them is straight to 10 mm.
      void Fill(SeamPoint a, SeamPoint b, int depth)
      {
        // only along one run of the valley (never across the apex leg or from one kind of meeting to another), so a level
        // point never changes where the valley proper starts or what kind of meeting a section stops on
        if (depth > 6 || a.Kind != b.Kind || a.Kind is not ("exact" or "plan") || double.IsNaN(a.Z) || double.IsNaN(b.Z) || b.T - a.T < 0.02) return;
        var kind = a.Kind;
        var mid = Make(0.5 * (a.T + b.T), 0.5 * (a.U + b.U), kind);
        if (double.IsNaN(mid.Z)) return;
        var bent = Math.Abs(mid.Z - 0.5 * (a.Z + b.Z)) > 0.01;
        if (!bent && depth >= 2) return;                  // two levels are always looked at: a narrow drain can hide between the ends
        Fill(a, mid, depth + 1);
        if (bent) r.SeamRaw.Add(mid);
        Fill(mid, b, depth + 1);
      }
      SeamPoint? before = null;
      foreach (var (t, u, kind) in path)
      {
        var now = Make(t, u, kind);
        if (before != null) Fill(before, now, 0);
        r.SeamRaw.Add(now); before = now;
      }
    }

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
        // an open-ended section can cross the ground on its way out to a terminal bench: that is not where it ends
        if (a.HasValue && !entrySet.Extends(a.Value.S)) return null;
        // only on the daylight slope: the lanes can sit right at ground level where the daylight changes between cut and fill
        if (!a.HasValue || a.Value.O < entrySet.Hinge(a.Value.S)) return null;
        var g = ground(q.X, q.Y);
        return g.HasValue ? a.Value.Z - g.Value : null;
      }
      bool Overlap(P2 q) { var a = Eval(q, true); var b = Eval(q, false); return a.HasValue && b.HasValue && Covered(a.Value, true) && Covered(b.Value, false); }
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
      var maySeparate = !slopeSeam || ground == null || r.CutFillChanges.Count > 0 || r.OpenEnded;
      // these points exist only where the sides overlap (or, with open-ended sections, the valley simply stops at their
      // last points): the overlap ends within one step beyond the last of them
      if (end == null && lastOverlap.HasValue && (path[^1].Kind is "plan" or "lane" || (r.OpenEnded && overlapAtEnd)))
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
          var dayA = ea.Value.O >= entrySet.Reach(ea.Value.S) - 0.10; var dayB = eb.Value.O >= exitSet.Reach(eb.Value.S) - 0.10;
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
        // The run-on is there for sections that reach past the end of the valley only BEYOND their own daylight. It must
        // never stop a section before that: every section in range whose ray crosses it inside its own reach shortens it
        // to just before that crossing. A valley that has no direction of its own (it ends where it starts) gets none.
        var degenerate = (e - new P2(last.X, last.Y)).Length < 1e-6;
        if (degenerate) over = 0;
        foreach (var sec in sections.Sections)
        {
          var st = sec.Station;
          if (over <= 0 || st < lo - 1e-9 || st > hi + 1e-9 || bl.Corners.Any(c => Math.Abs(c.Station - st) < 1e-6)) continue;
          var (c0, th0) = bl.At(st); var n0 = SampledBaseline.InsideNormal(th0, side);
          var den = dir.Cross(n0);
          if (Math.Abs(den) < 1e-9) continue;
          var w = c0 - e;
          var t = w.Cross(n0) / den; var o = w.Cross(dir) / den;
          if (t > 1e-6 && t < over && o > 0 && o < For(st).Reach(st) - opt.CrossingTolerance) over = Math.Max(0, t - 0.05);
        }
        r.OvershootLength = over;
        if (over > 1e-3)
          used.Add(new SeamPoint(e.X + dir.X * over, e.Y + dir.Y * over, ez, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, "overshoot"));
      }
    }
    r.Seam = Simplify(used, opt.SimplifyTolerance);
    // a valley that collapses to a point (it ends where it starts, as next to a curve centre whose sections barely overlap)
    // is no line at all: the apex bar alone does the work there
    if (r.Seam.Count >= 2)
    {
      var len = 0.0;
      for (var i = 1; i < r.Seam.Count; i++) len += new P2(r.Seam[i].X - r.Seam[i - 1].X, r.Seam[i].Y - r.Seam[i - 1].Y).Length;
      if (len < 0.05) { r.Warnings.Add("The valley line has no length (the two sides stop overlapping right at the apex): only the apex bar is written."); r.Seam = new List<SeamPoint>(); }
    }
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
      return f.HasValue && For(s).Reach(s) > f.Value - opt.CapInset && SeamCrossing(s, f.Value - opt.CapInset) == null;
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
        var conv = focal.Where(x => x.F <= 1.02 * fMin).Select(x => (Z: For(x.S).Z(x.S, fMin), Rise: Rise(For(x.S), x.S, fMin))).Where(x => x.Z.HasValue).Select(x => (Z: x.Z!.Value, x.Rise)).ToList();
        var zs = conv.Select(x => x.Z).ToList();
        if (zs.Count > 0) r.ApexZ = opt.LevelRule == LevelRule.Mean ? zs.Average() : NoSteeperLevel(conv);
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
      var sc = new StationClip { Station = s, Reach = For(s).Reach(s), FocalOffset = Focal(s) };
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
        sc.ClipX = q.X; sc.ClipY = q.Y; sc.ClipZ = For(s).Z(s, sc.ClipOffset.Value);
        sc.TargetZ = sc.Role == "cap" ? r.ApexZ : SeamLevelAt(q);
        if (sc.TargetZ.HasValue && sc.ClipZ.HasValue)
        {
          sc.LevelAdjust = sc.TargetZ.Value - sc.ClipZ.Value;
          var set = For(s);
          var from = set.LastVertexBefore(s, sc.ClipOffset.Value);
          var zFrom = set.Z(s, from);
          if (zFrom.HasValue && sc.ClipOffset.Value - from > 0.01)
          {
            sc.OwnSlope = (sc.ClipZ.Value - zFrom.Value) / (sc.ClipOffset.Value - from);
            sc.AdjustedSlope = (sc.TargetZ.Value - zFrom.Value) / (sc.ClipOffset.Value - from);
          }
          // spread from the hinge: every link from there to the clip gets the same extra grade
          var h = set.Hinge(s);
          var runH = sc.ClipOffset.Value > h + 0.05 ? sc.ClipOffset.Value - h : sc.ClipOffset.Value - set.StartOffset(s);
          if (runH > 0.01) sc.SpreadGrade = sc.LevelAdjust.Value / runH;
          if (opt.AdjustFrom == AdjustFrom.FromHinge && sc.OwnSlope.HasValue && sc.SpreadGrade.HasValue) sc.AdjustedSlope = sc.OwnSlope + sc.SpreadGrade;
          // Spread from the hinge, every link between the hinge and the clip gets the same extra grade. The engineer's
          // ratio is the steepest a slope may be: a link that would come out steeper is flagged (it needs remedial or extra
          // protection), as is a bench whose cross-fall would change direction.
          if (sc.SpreadGrade.HasValue && Math.Abs(sc.SpreadGrade.Value) > 1e-6)
          {
            var g = sc.SpreadGrade.Value;
            var start = sc.ClipOffset.Value > h + 0.05 ? h : set.StartOffset(s);
            var tpl = set.SampleAt(s).Template;
            for (var i = 1; i < tpl.Count; i++)
            {
              var a0 = Math.Max(tpl[i - 1].Off, start); var a1 = Math.Min(tpl[i].Off, sc.ClipOffset.Value);
              var run = tpl[i].Off - tpl[i - 1].Off;
              if (a1 - a0 < 0.01 || run < 1e-6) continue;
              var grade = (tpl[i].Dz - tpl[i - 1].Dz) / run;
              var slopeLink = Math.Abs(grade) > opt.MinSlope && run >= opt.MinSlopeRun;
              if (slopeLink && Math.Abs(grade + g) > Math.Abs(grade) + opt.SteeperTolerance)
                sc.SlopeFlags.Add(new SlopeFlag(s, "steeper", a0, a1, grade, grade + g));
              else if (!slopeLink && Math.Abs(grade) > 1e-3 && Math.Sign(grade + g) != Math.Sign(grade))
                sc.SlopeFlags.Add(new SlopeFlag(s, "fall_reversed", a0, a1, grade, grade + g));
            }
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
    if (!opt.AcceptOffSurfaceEnds && r.Status == SeamStatus.Ok && clipped.Count > 0)
    {
      var off = neverOnGround.Where(st => st >= r.ClipFrom!.Value - 1e-6 && st <= r.ClipTo!.Value + 1e-6).ToList();
      if (off.Count > 0)
      {
        r.Status = SeamStatus.NeverMeetsGround;
        r.Reasons.Add($"{off.Count} section(s) to be clipped ({off.Min():0.##}-{off.Max():0.##}) never come near the daylight surface given: the wrong surface, or sections that do not daylight here (walls, fixed-width sections). Check the surface, or accept it with acceptOffSurfaceEnds.");
      }
    }
    r.MaxClosure = r.Stations.Where(x => x.Role == "seam" && x.Closure.HasValue).Select(x => Math.Abs(x.Closure!.Value)).DefaultIfEmpty(0).Max();
    r.ApexMismatch = r.Stations.Where(x => x.Role == "apex" && x.Closure.HasValue).Select(x => Math.Abs(x.Closure!.Value)).DefaultIfEmpty(0).Max();
    r.LevelStep = Math.Max(r.LevelStep, r.Stations.Where(x => x.Role == "conflict" && x.Closure.HasValue).Select(x => Math.Abs(x.Closure!.Value)).DefaultIfEmpty(0).Max());
    r.MaxLevelAdjust = r.Stations.Where(x => x.LevelAdjust.HasValue).Select(x => Math.Abs(x.LevelAdjust!.Value)).DefaultIfEmpty(0).Max();
    if (opt.LevelFromTarget)
    {
      var slopeWorst = r.Stations.Where(x => x.AdjustedSlope.HasValue && x.OwnSlope.HasValue).Select(x => (x.Station, D: Math.Abs(x.AdjustedSlope!.Value - x.OwnSlope!.Value))).OrderByDescending(x => x.D).FirstOrDefault();
      r.MaxSlopeChange = slopeWorst.D;
      r.MaxSpreadGrade = r.Stations.Where(x => x.SpreadGrade.HasValue).Select(x => Math.Abs(x.SpreadGrade!.Value)).DefaultIfEmpty(0).Max();
      if (opt.AdjustFrom == AdjustFrom.FromHinge)
      {
        r.SlopeFlags = r.Stations.SelectMany(x => x.SlopeFlags).ToList();
        string Ratio(double gr) => Math.Abs(gr) < 1e-9 ? "level" : $"1:{1 / Math.Abs(gr):0.##}";
        var steep = r.SlopeFlags.Where(f => f.Kind == "steeper").ToList();
        if (steep.Count > 0)
        {
          var worst = steep.OrderByDescending(f => Math.Abs(f.NewGrade) - Math.Abs(f.DesignGrade)).First();
          r.Warnings.Add($"Spreading the level move makes the slope steeper than designed at {steep.Select(f => f.Station).Distinct().Count()} section(s) between {steep.Min(f => f.Station):0.##} and {steep.Max(f => f.Station):0.##} " +
            $"(worst at {worst.Station:0.##}: {Ratio(worst.DesignGrade)} designed, {Ratio(worst.NewGrade)} built). The designed ratio is the steepest allowed: the engineer needs to add remedial or extra slope protection there.");
        }
        var reversed = r.SlopeFlags.Where(f => f.Kind == "fall_reversed").ToList();
        if (reversed.Count > 0)
          r.Warnings.Add($"Spreading the level move reverses the cross-fall of a bench or lane at {reversed.Select(f => f.Station).Distinct().Count()} section(s) between {reversed.Min(f => f.Station):0.##} and {reversed.Max(f => f.Station):0.##} (water would run away from its drain): for the engineer to look at.");
      }
      if (slopeWorst.D > 0.10 && opt.AdjustFrom == AdjustFrom.LastLink)
        r.Warnings.Add($"Taking the clipped link at {slopeWorst.Station:0.##} to the valley level changes its slope by {slopeWorst.D * 100:0}% (a short link: a bench or drain wall); spread from the hinge it would be {r.MaxSpreadGrade * 100:0.#}% at most. Highlight it for a look.");
      if (opt.AdjustFrom == AdjustFrom.FromHinge && opt.MaxSlopeChange.HasValue && slopeWorst.D > opt.MaxSlopeChange.Value && r.Status == SeamStatus.Ok)
      {
        r.Status = SeamStatus.DesignConflict;
        r.Reasons.Add($"To bring both sides of the bend to one level, the clipped link at {slopeWorst.Station:0.##} would change slope by {slopeWorst.D * 100:0}% (limit {opt.MaxSlopeChange * 100:0}%): the two sides are too far apart in level for the links they end on. It needs a steeper slope, a wall, a wider bend or a gentler grade; or pass a larger maxSlopeChange to accept it.");
      }
      else if (opt.AdjustFrom == AdjustFrom.LastLink && r.MaxLevelAdjust > opt.MaxLevelAdjust && r.Status == SeamStatus.Ok)
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
    // a result is only Ok when it does the job: never with crossings left or with a section folding past its own centre
    if (r.Status == SeamStatus.Ok)
    {
      var folds = r.Stations.Where(x => !x.FirstCrossingIsClip).Select(x => x.Station).ToList();
      if (r.LinkCrossingsAfter > 0 || folds.Count > 0)
      {
        r.Status = SeamStatus.Unresolved;
        if (r.LinkCrossingsAfter > 0) r.Reasons.Add($"{r.LinkCrossingsAfter} link crossing(s) would remain after the clip ({r.LinkCrossingsBefore} before).");
        if (folds.Count > 0) r.Reasons.Add($"{folds.Count} section(s) would run past their own centre of curvature before anything stops them ({string.Join(", ", folds.Take(6).Select(x => x.ToString("0.##")))}).");
      }
    }
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
