using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

/// <summary>
/// Bowtie fixing, part 2 (Civil 3D 2026 API, no reflection):
///
///   bowtieValley  - builds the valley line of one bend: where the design surfaces of the incoming and outgoing legs
///                   meet on the inside of the bend. Each leg's surface = baseline elevation along the leg (its grade)
///                   plus the inside section template (Top links) read from a built, unclipped section next to the bend,
///                   extended a few metres past its daylight. The valley is marched outward along the inside bisector
///                   from the PI (or from the curve centre on a curved bend): on slopes it is the exact z1 = z2 line,
///                   across flat-on-flat stretches (lanes, benches) the neighbouring exact points are joined. Optional:
///                   the point where the valley meets the daylight surface (both legs daylight there) and an extra
///                   corridor station on each leg at that point, so the Daylight feature line gets an exact corner.
///                   Creates a siteless alignment to use as the clip (offset) target of UTNM_LaneDaylightClip.
///   checkBowties  - read-only verification of the BUILT corridor: plan crossings between links of different stations
///                   (per side, optionally only links reaching beyond minOffset) and the built feature line of one code
///                   (applied stations only, straight chords): self-crossings and backward steps. Unlike
///                   bowtie_predict it does not interpolate across stations that lack the code.
/// </summary>
public static partial class CorridorBowtieCommands
{
  // =========================================================================
  // bowtieValley
  // =========================================================================

  private sealed class LegModel
  {
    public double Station;
    public double Cx, Cy, Cz;
    public double Dx, Dy;
    public double Nx, Ny;
    public double Grade;
    public double GradeFrom;
    public readonly List<(double Off, double Dz)> Template = new();
    public bool Clipped;
    public int LinksUsed;

    public (double S, double O) SO(double x, double y)
    {
      var vx = x - Cx;
      var vy = y - Cy;
      return (vx * Dx + vy * Dy, vx * Nx + vy * Ny);
    }

    public double Start => Template.Count > 0 ? Template[0].Off : 0.0;
    public double End => Template.Count > 0 ? Template[^1].Off : 0.0;

    public double? Dz(double off)
    {
      if (Template.Count == 0) return null;
      if (off <= Template[0].Off) return Template[0].Dz;
      for (var i = 1; i < Template.Count; i++)
      {
        var (o0, z0) = Template[i - 1];
        var (o1, z1) = Template[i];
        if (off <= o1)
          return o1 - o0 > 1e-9 ? z0 + (z1 - z0) * (off - o0) / (o1 - o0) : z1;
      }
      return null;
    }

    public double? SlopeAt(double off)
    {
      for (var i = 1; i < Template.Count; i++)
      {
        var (o0, z0) = Template[i - 1];
        var (o1, z1) = Template[i];
        if (off <= o1 && o1 - o0 > 1e-9) return (z1 - z0) / (o1 - o0);
      }
      return null;
    }

    public double? Z(double x, double y)
    {
      var (s, o) = SO(x, y);
      var dz = Dz(o);
      return dz.HasValue ? Cz + Grade * s + dz.Value : null;
    }
  }

  private sealed class ValleyGeometry
  {
    public string Side = "";
    public double SA, SB;
    public LegModel LegA = null!, LegB = null!;
    public double Deflection, PiX, PiY, PiStation, Bx, By, Mx, My;
    public string BendType = "angle_point";
    public double TurnStart, TurnEnd, StartX, StartY, TStart;
    public List<(double T, double U)> Path = new();
    public List<(double X, double Y, double? Z, double T, double U, string Kind)> Poly = new();
    public double MaxSideways, LevelStep;
    public readonly List<string> MismatchReasons = new();
    public string? SurfaceUsed;
    public (double X, double Y, double Z)? Hit;
    public double? MeetA, MeetB, MeetOffA, MeetOffB;
    public Dictionary<string, object?>? Lean;
    public readonly List<string> Warnings = new();
  }

  private sealed class ValleyVerdict
  {
    public readonly List<string> Blocking = new();
    public bool Straddles = true;
    public bool? InsideRegion;
    public double[]? SuggestedRange;
    public readonly List<Dictionary<string, object?>> Crossings = new();
    public readonly List<string> Warnings = new();
  }

  public static Task<object?> BowtieValleyAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var side = (PluginRuntime.GetOptionalString(parameters, "side") ?? "").Trim().ToLowerInvariant();
    var startStation = PluginRuntime.GetRequiredDouble(parameters, "startStation");
    var endStation = PluginRuntime.GetRequiredDouble(parameters, "endStation");
    var templateBefore = PluginRuntime.GetOptionalDouble(parameters, "templateStationBefore");
    var templateAfter = PluginRuntime.GetOptionalDouble(parameters, "templateStationAfter");
    var valleyName = PluginRuntime.GetOptionalString(parameters, "valleyName");
    var linkCode = PluginRuntime.GetOptionalString(parameters, "linkCode") ?? "Top";
    var extension = PluginRuntime.GetOptionalDouble(parameters, "extension") ?? 3.0;
    var step = PluginRuntime.GetOptionalDouble(parameters, "step") ?? 0.1;
    var surfaceName = PluginRuntime.GetOptionalString(parameters, "surfaceName");
    var addStations = PluginRuntime.GetOptionalBool(parameters, "addStations") ?? true;
    var createAlignment = PluginRuntime.GetOptionalBool(parameters, "createAlignment") ?? true;
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    var style = PluginRuntime.GetOptionalString(parameters, "style");
    var layer = PluginRuntime.GetOptionalString(parameters, "layer");
    var allowMismatch = PluginRuntime.GetOptionalBool(parameters, "allowMismatch") ?? false;

    if (side is not ("left" or "right"))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "side must be left or right: the inside of the bend (bowtie_predict reports it).");
    if (!(endStation > startStation))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "endStation must be greater than startStation (pass the bowtie range from bowtie_predict).");
    if (extension < 0 || step <= 0 || step > 1)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "extension must be >= 0 and step in (0, 1].");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, dryRun ? OpenMode.ForRead : OpenMode.ForWrite);
      var baseline = GetBaseline(corridor, baselineIndex);
      var stations = AppliedStationsOrThrow(corridor, baseline);

      // ---- template stations: the last applied stations at/before the bowtie and the first at/after it (the nearest pair
      // first, then up to two more on each side if the nearest one gives no usable valley)
      var candA = templateBefore.HasValue
        ? new List<double> { templateBefore.Value }
        : stations.Where(s => s <= startStation + StationTolerance).OrderByDescending(s => s).Take(3).ToList();
      var candB = templateAfter.HasValue
        ? new List<double> { templateAfter.Value }
        : stations.Where(s => s >= endStation - StationTolerance).OrderBy(s => s).Take(3).ToList();
      if (candA.Count == 0 || candB.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"No applied station before {startStation:0.###} or after {endStation:0.###} to read the leg sections from.");

      var search = SearchValley(civilDoc, transaction, baseline, stations, side, candA, candB, linkCode, extension, step, surfaceName,
        geometry => RegionAtStation(baseline, geometry.PiStation));
      var g = search.G;
      var region = search.Region;
      var verdict = search.Verdict;
      var warnings = new List<string>(g.Warnings);
      warnings.AddRange(verdict.Warnings);
      warnings.AddRange(search.Notes);

      if (verdict.Blocking.Count > 0)
      {
        var why = string.Join("; ", verdict.Blocking);
        if (!dryRun && !allowMismatch)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_STATE",
            $"Refused, nothing was changed: {why}. " +
            (verdict.SuggestedRange != null ? $"Isolate the bend over {verdict.SuggestedRange[0]:0.###}-{verdict.SuggestedRange[1]:0.###} and run it again. " : "") +
            (!verdict.Straddles || g.MismatchReasons.Count > 0
              ? "A section change or grade break at the bend is a design question, not a plain bowtie: move it off the bend or design a transition. "
              : "") +
            "Run with dryRun to inspect it; allowMismatch true builds it anyway.");
        warnings.Add($"{(dryRun ? "A real run would be refused" : "Built anyway (allowMismatch)")}: {why}.");
      }
      if (!verdict.Straddles && addStations)
      {
        addStations = false;
        if (!dryRun) warnings.Add("No corridor stations were added: the meet stations do not straddle the bend.");
      }

      var sideLetter = side[0].ToString().ToUpperInvariant();
      var name = string.IsNullOrWhiteSpace(valleyName)
        ? $"{corridor.Name} {region?.Name ?? "BT"} Valley {sideLetter}"
        : valleyName!.Trim();
      var nameInUse = AlignmentNameInUse(civilDoc, transaction, name);
      if (nameInUse && createAlignment)
      {
        if (!dryRun)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"An alignment named '{name}' already exists. Pass another valleyName, or delete it first. Nothing was changed.");
        warnings.Add($"An alignment named '{name}' already exists: a real run needs another valleyName.");
      }

      // ---- write: stations, then the alignment (its description records the added stations for bowtie_refresh)
      Dictionary<string, object?>? alignmentInfo = null;
      var stationsAdded = new List<Dictionary<string, object?>>();
      var addedList = new List<double>();
      if (!dryRun && addStations && g.MeetA.HasValue && g.MeetB.HasValue)
      {
        foreach (var (st, leg) in new[] { (g.MeetA.Value, "incoming"), (g.MeetB.Value, "outgoing") })
        {
          var row = AddMeetStation(baseline, stations, st, leg, Array.Empty<double>());
          stationsAdded.Add(row);
          if (row["added"] is true) addedList.Add(st);
        }
      }
      if (!dryRun && createAlignment)
      {
        var alignmentId = CreateValleyAlignment(civilDoc, database, transaction, name, g.Poly, style, layer);
        var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, alignmentId, OpenMode.ForWrite);
        try { alignment.Description = ValleyDescription(corridor.Name, baselineIndex, g, addedList); }
        catch (Exception ex) { warnings.Add($"The alignment description (used by bowtie_refresh) could not be set: {ex.Message}"); }
        alignmentInfo = new Dictionary<string, object?>
        {
          ["name"] = alignment.Name,
          ["handle"] = CivilObjectUtils.GetHandle(alignment),
          ["length"] = Math.Round(alignment.Length, 4),
        };
      }

      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridor.Name,
        ["baselineIndex"] = baselineIndex,
        ["side"] = side,
        ["dryRun"] = dryRun,
        ["bend"] = BendInfo(g),
        ["legs"] = new[] { LegInfo(g.LegA, "incoming"), LegInfo(g.LegB, "outgoing") },
        ["templateSearch"] = search.Tried,
        ["valley"] = ValleyInfo(g, name),
        ["meetsDaylight"] = MeetInfo(g),
        ["checks"] = ChecksInfo(g, verdict, region),
        ["alignment"] = alignmentInfo,
        ["stationsAdded"] = stationsAdded,
        ["rebuilt"] = false,
        ["nextStep"] = alignmentInfo == null ? null :
          $"Give the region an assembly with UTNM_LaneDaylightClip on the {side} side, map its ClipTarget to alignment '{name}' (target_mapping_set, targetType alignment) and rebuild; then run bowtie_check.",
        ["warnings"] = warnings,
      };
    });
  }

  private static double[] AppliedStationsOrThrow(Corridor corridor, Baseline baseline)
  {
    double[] stations;
    try { stations = baseline.SortedStations() ?? Array.Empty<double>(); }
    catch (Exception ex) { throw new JsonRpcDispatchException("CIVIL3D.INVALID_STATE", $"Corridor '{corridor.Name}' has no applied stations ({ex.Message}) - rebuild it first."); }
    if (stations.Length < 4)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_STATE", $"Corridor '{corridor.Name}' has too few applied stations - rebuild it first.");
    return stations;
  }

  /// <summary>Valley line of one bend from the unclipped sections at sA (incoming leg) and sB (outgoing leg). Read-only.</summary>
  private static ValleyGeometry ComputeValley(CivilDocument civilDoc, Transaction transaction, Baseline baseline, double[] stations,
    string side, double sA, double sB, string linkCode, double extension, double step, string? surfaceName)
  {
    var g = new ValleyGeometry { Side = side, SA = sA, SB = sB };
    var warnings = g.Warnings;
    var sign = side == "left" ? -1.0 : 1.0;
    var legA = g.LegA = BuildLeg(baseline, stations, sA, sign, linkCode, extension, before: true, warnings);
    var legB = g.LegB = BuildLeg(baseline, stations, sB, sign, linkCode, extension, before: false, warnings);

    // ---- bend geometry
    var cross = legA.Dx * legB.Dy - legA.Dy * legB.Dx;
    var deflection = Math.Asin(Math.Clamp(cross, -1.0, 1.0)) * 180.0 / Math.PI;
    var dot = legA.Dx * legB.Dx + legA.Dy * legB.Dy;
    if (dot < 0) deflection = Math.Sign(cross) * 180.0 - deflection;
    if (Math.Abs(cross) < 1e-4)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"The legs at {sA:0.###} and {sB:0.###} are parallel (deflection {deflection:0.###} deg); there is no bend to build a valley for.");
    var insideSide = cross > 0 ? "left" : "right";
    if (insideSide != side)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"The {side} side is the outside of this bend (it turns {insideSide}); the bowtie and the valley are on the {insideSide} side.");

    var t0 = ((legB.Cx - legA.Cx) * legB.Dy - (legB.Cy - legA.Cy) * legB.Dx) / cross;
    var piX = legA.Cx + legA.Dx * t0;
    var piY = legA.Cy + legA.Dy * t0;
    var piStation = sA + t0;
    var bx = legA.Nx + legB.Nx;
    var by = legA.Ny + legB.Ny;
    var bl = Math.Sqrt(bx * bx + by * by);
    bx /= bl; by /= bl;
    var mx = -by;
    var my = bx;
    g.Deflection = deflection; g.PiX = piX; g.PiY = piY; g.PiStation = piStation;
    g.Bx = bx; g.By = by; g.Mx = mx; g.My = my;

    // ---- angle point or curve: where does the direction change?
    var (turnStart, turnEnd) = TurnZone(baseline, sA, sB);
    g.TurnStart = turnStart; g.TurnEnd = turnEnd;
    g.BendType = turnEnd - turnStart > 0.5 ? "curve" : "angle_point";
    double startX = piX, startY = piY, tStart = 0.0;
    if (g.BendType == "curve")
    {
      var ca = SafeLeg(baseline, turnStart, sign);
      var cb = SafeLeg(baseline, turnEnd, sign);
      if (ca != null && cb != null)
      {
        var den = ca.Value.Nx * cb.Value.Ny - ca.Value.Ny * cb.Value.Nx;
        if (Math.Abs(den) > 1e-6)
        {
          var u = ((cb.Value.X - ca.Value.X) * cb.Value.Ny - (cb.Value.Y - ca.Value.Y) * cb.Value.Nx) / den;
          startX = ca.Value.X + ca.Value.Nx * u;
          startY = ca.Value.Y + ca.Value.Ny * u;
          tStart = (startX - piX) * bx + (startY - piY) * by;
        }
      }
      warnings.Add($"The bend is curved between {turnStart:0.###} and {turnEnd:0.###}: the valley starts at the curve centre ({startX:0.###}, {startY:0.###}), where the curve's sections converge. Curved bends are not live-tested yet: check the sections.");
    }
    g.StartX = startX; g.StartY = startY; g.TStart = tStart;

    // ---- march the valley
    var cosA = Math.Max(1e-6, bx * legA.Nx + by * legA.Ny);
    var tMax = Math.Max(legA.End, legB.End) / cosA + 1.0;
    var candidates = new List<(double T, List<double> Roots)>();
    for (var t = Math.Max(step, tStart + step); t <= tMax; t += step)
    {
      var px = piX + bx * t;
      var py = piY + by * t;
      var U = 0.2 * t + 0.3;
      double? F(double u)
      {
        var qx = px + mx * u; var qy = py + my * u;
        var z1 = legA.Z(qx, qy); var z2 = legB.Z(qx, qy);
        return z1.HasValue && z2.HasValue ? z1.Value - z2.Value : null;
      }
      var roots = new List<double>();
      const int n = 200;
      double? prevF = null;
      double prevU = 0;
      for (var i = 0; i <= n; i++)
      {
        var u = -U + 2 * U * i / n;
        var f = F(u);
        if (prevF.HasValue && f.HasValue && (prevF.Value == 0 || prevF.Value * f.Value < 0))
        {
          double lo = prevU, hi = u, flo = prevF.Value;
          for (var k = 0; k < 50; k++)
          {
            var mid = 0.5 * (lo + hi);
            var fm = F(mid);
            if (!fm.HasValue) break;
            if (flo * fm.Value <= 0) hi = mid; else { lo = mid; flo = fm.Value; }
          }
          var r = 0.5 * (lo + hi);
          var qx = px + mx * r; var qy = py + my * r;
          var oA = legA.SO(qx, qy).O; var oB = legB.SO(qx, qy).O;
          if (oA >= legA.Start && oB >= legB.Start)
          {
            var s1 = legA.SlopeAt(oA); var s2 = legB.SlopeAt(oB);
            if (s1.HasValue && s2.HasValue && (Math.Abs(s1.Value) > 0.1 || Math.Abs(s2.Value) > 0.1)) roots.Add(r);
          }
        }
        prevF = f; prevU = u;
      }
      if (roots.Count > 0) candidates.Add((t, roots));
    }

    // track the branch from the outer end inward (well conditioned there)
    var path = new List<(double T, double U)>();
    (double T, double U)? prev = null;
    for (var i = candidates.Count - 1; i >= 0; i--)
    {
      var (t, roots) = candidates[i];
      double u;
      if (prev == null) u = roots.OrderBy(Math.Abs).First();
      else
      {
        var pu = prev.Value.U;
        u = roots.OrderBy(r => Math.Abs(r - pu)).First();
        if (Math.Abs(u - pu) > 0.05 + 0.3 * (prev.Value.T - t)) continue;
      }
      path.Add((t, u));
      prev = (t, u);
    }
    path.Reverse();
    if (path.Count < 2)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_STATE",
        $"No valley found between the legs at {sA:0.###} and {sB:0.###}: the inside templates never meet on a sloped link. Check that both sections have {linkCode}-coded links on the {side} side reaching past the hinge.");
    g.Path = path;

    var raw = new List<(double X, double Y, double? Z, double T, double U, string Kind)>
    {
      (startX, startY, null, tStart, 0.0, g.BendType == "curve" ? "curve_centre" : "pi"),
    };
    foreach (var (t, u) in path)
    {
      var qx = piX + bx * t + mx * u; var qy = piY + by * t + my * u;
      raw.Add((qx, qy, legA.Z(qx, qy), t, u, "exact"));
    }
    g.Poly = SimplifyPolyline(raw, 0.005);

    // ---- do the two legs carry the same section? (a region boundary with a different drain / lane level at the
    // bend puts the valley far off the bisector and the construction is no longer reliable)
    var sPiA = legA.SO(piX, piY).S;
    var sPiB = legB.SO(piX, piY).S;
    var levelA = legA.Cz + legA.Grade * sPiA + legA.Template[0].Dz;
    var levelB = legB.Cz + legB.Grade * sPiB + legB.Template[0].Dz;
    g.LevelStep = levelA - levelB;
    var hingeA = HingeOffset(legA);
    var hingeB = HingeOffset(legB);
    if (Math.Abs(g.LevelStep) > 0.05) g.MismatchReasons.Add($"the inside edge levels differ by {g.LevelStep:0.###} m at the PI");
    if (Math.Abs(legA.Start - legB.Start) > 0.05) g.MismatchReasons.Add($"the inside links start at different offsets ({legA.Start:0.###} / {legB.Start:0.###} m)");
    if (hingeA.HasValue && hingeB.HasValue && Math.Abs(hingeA.Value - hingeB.Value) > 0.05) g.MismatchReasons.Add($"the hinges are at different offsets ({hingeA:0.###} / {hingeB:0.###} m)");
    g.MaxSideways = path.Max(p => Math.Abs(p.U));
    var firstExact = path[0];
    if (Math.Abs(firstExact.U) > 0.25 * firstExact.T + 0.3)
      g.MismatchReasons.Add($"the valley runs {Math.Abs(firstExact.U):0.##} m off the bisector {firstExact.T:0.#} m out from the PI");

    // ---- where the valley meets the daylight surface
    CivilSurface? surface;
    string? surfaceUsed;
    if (!string.IsNullOrWhiteSpace(surfaceName)) (surface, surfaceUsed) = FindSurface(civilDoc, transaction, surfaceName!);
    else (surface, surfaceUsed) = SurfaceFromTargets(baseline, transaction, piStation);
    g.SurfaceUsed = surfaceUsed;
    if (surface == null)
    {
      warnings.Add("No daylight surface (pass surfaceName, or map a surface target in the region): the point where the valley meets the ground was not computed.");
    }
    else
    {
      var hit = ValleyMeetsSurface(g.Poly, legA, surface);
      if (hit == null) warnings.Add($"The valley does not reach surface '{surfaceUsed}' within the section templates (+{extension:0.##} m): widen extension, or check the templates.");
      else
      {
        var (hx, hy, _) = hit.Value;
        g.Hit = hit;
        g.MeetA = sA + legA.SO(hx, hy).S;
        g.MeetB = sB + legB.SO(hx, hy).S;
        g.MeetOffA = legA.SO(hx, hy).O;
        g.MeetOffB = legB.SO(hx, hy).O;
      }
    }

    // ---- expected lean off the bisector at the meet (plane model, angle point): u = t sin(D/2)(gA+gB) / (2 k sin(D/2) - cos(D/2)(gB-gA)),
    // k = the daylight slope there. The grade-break term dominating means the valley swings with small changes: a design question.
    var kAtMeet = g.MeetOffA.HasValue ? legA.SlopeAt(g.MeetOffA.Value) : null;
    if (g.Hit.HasValue && g.BendType == "angle_point" && kAtMeet.HasValue && Math.Abs(kAtMeet.Value) >= 0.05)
    {
      var (hx, hy, _) = g.Hit.Value;
      var tM = (hx - piX) * bx + (hy - piY) * by;
      var uM = (hx - piX) * mx + (hy - piY) * my;
      var k = kAtMeet.Value;
      var half = Math.Abs(deflection) * Math.PI / 360.0;
      var gradeBreak = legB.Grade - legA.Grade;
      var slopeTerm = 2 * k * Math.Sin(half);
      var breakTerm = Math.Cos(half) * gradeBreak;
      var den = slopeTerm - breakTerm;
      double? expected = Math.Abs(den) > 1e-9 ? Math.Abs(tM * Math.Sin(half) * (legA.Grade + legB.Grade) / den) : null;
      var ratio = Math.Abs(slopeTerm) > 1e-9 ? Math.Abs(breakTerm) / Math.Abs(slopeTerm) : double.PositiveInfinity;
      g.Lean = new Dictionary<string, object?>
      {
        ["distanceFromPi"] = Math.Round(tM, 3),
        ["actual"] = Math.Round(Math.Abs(uM), 3),
        ["expected"] = expected.HasValue ? Math.Round(expected.Value, 3) : null,
        ["daylightSlope"] = Math.Round(k, 4),
        ["gradeIncoming"] = Math.Round(legA.Grade, 5),
        ["gradeOutgoing"] = Math.Round(legB.Grade, 5),
        ["gradeBreakRatio"] = double.IsInfinity(ratio) ? null : Math.Round(ratio, 3),
      };
      if (ratio >= 0.5)
        warnings.Add($"The grade break at the bend ({gradeBreak * 100:0.##} %) is large against the bend ({Math.Abs(deflection):0.#} deg) on this slope " +
                     $"(ratio {(double.IsInfinity(ratio) ? "inf" : ratio.ToString("0.##"))}, above 0.5): the valley swings far with small changes. Treat it as a design question.");
      else if (expected.HasValue && Math.Abs(Math.Abs(uM) - expected.Value) > 0.3 + 0.5 * expected.Value)
        warnings.Add($"The valley meets the ground {Math.Abs(uM):0.##} m off the bisector; grades and slope predict about {expected.Value:0.##} m. Check the templates.");
    }
    return g;
  }

  private sealed class ValleySearch
  {
    public ValleyGeometry G = null!;
    public ValleyVerdict Verdict = null!;
    public BaselineRegion? Region;
    public readonly List<Dictionary<string, object?>> Tried = new();
    public readonly List<string> Notes = new();
  }

  /// <summary>Valley from the nearest pair of template stations; when that pair gives no usable valley (an error, or a blocking
  /// check), the next pairs outward (by total steps) until one passes. Returns the passing pair, else the nearest one that computed.</summary>
  private static ValleySearch SearchValley(CivilDocument civilDoc, Transaction transaction, Baseline baseline, double[] stations, string side,
    List<double> candA, List<double> candB, string linkCode, double extension, double step, string? surfaceName,
    Func<ValleyGeometry, BaselineRegion?> regionOf)
  {
    var result = new ValleySearch();
    var pairs = new List<(int I, int J)>();
    for (var i = 0; i < candA.Count; i++)
      for (var j = 0; j < candB.Count; j++) pairs.Add((i, j));
    pairs = pairs.OrderBy(p => p.I + p.J).ThenBy(p => p.I).ToList();
    (ValleyGeometry G, ValleyVerdict V, BaselineRegion? R)? first = null;
    JsonRpcDispatchException? firstError = null;
    foreach (var (i, j) in pairs)
    {
      var row = new Dictionary<string, object?> { ["incoming"] = Math.Round(candA[i], 4), ["outgoing"] = Math.Round(candB[j], 4) };
      result.Tried.Add(row);
      ValleyGeometry g;
      try { g = ComputeValley(civilDoc, transaction, baseline, stations, side, candA[i], candB[j], linkCode, extension, step, surfaceName); }
      catch (JsonRpcDispatchException ex)
      {
        row["result"] = ex.Message;
        firstError ??= ex;
        // wrong side / parallel legs do not depend on the template stations: stop searching
        if (ex.Message.Contains("outside of this bend") || ex.Message.Contains("are parallel")) throw;
        continue;
      }
      var region = regionOf(g);
      var verdict = CheckValley(g, baseline, stations, region);
      first ??= (g, verdict, region);
      if (verdict.Blocking.Count == 0)
      {
        row["result"] = "ok";
        result.G = g; result.Verdict = verdict; result.Region = region;
        if (i != 0 || j != 0)
          result.Notes.Add($"The nearest sections ({candA[0]:0.###} / {candB[0]:0.###}) gave no usable valley; used {candA[i]:0.###} / {candB[j]:0.###}. " +
                           "Check the result (templateSearch lists why the nearer pairs failed).");
        return result;
      }
      row["result"] = string.Join("; ", verdict.Blocking);
    }
    if (first == null) throw firstError ?? new JsonRpcDispatchException("CIVIL3D.INVALID_STATE", "No template stations to build the valley from.");
    result.G = first.Value.G; result.Verdict = first.Value.V; result.Region = first.Value.R;
    return result;
  }

  /// <summary>Checks that decide whether the valley may be built: same section, meet stations straddle the bend and lie inside the
  /// bend's region; plus a warning for sections that cross the valley more than once.</summary>
  private static ValleyVerdict CheckValley(ValleyGeometry g, Baseline baseline, double[] stations, BaselineRegion? region)
  {
    var v = new ValleyVerdict();
    if (g.MismatchReasons.Count > 0)
      v.Blocking.Add($"the two legs do not carry the same inside section ({string.Join("; ", g.MismatchReasons)})");
    if (region == null)
    {
      v.InsideRegion = false;
      v.Blocking.Add($"the bend ({g.PiStation:0.###}) is on a region boundary: isolate it into one region first");
    }
    if (g.SurfaceUsed != null && !g.Hit.HasValue)
      v.Blocking.Add($"the valley does not reach surface '{g.SurfaceUsed}' within the two leg sections (+ extension): the sections do not describe the same design surface near the ground (e.g. one benched, one not)");
    if (g.MeetA.HasValue && g.MeetB.HasValue)
    {
      var a = g.MeetA.Value;
      var b = g.MeetB.Value;
      var latestA = g.BendType == "curve" ? g.TurnEnd : g.PiStation;
      var earliestB = g.BendType == "curve" ? g.TurnStart : g.PiStation;
      if (a > latestA + 0.01 || b < earliestB - 0.01 || a > b)
      {
        v.Straddles = false;
        v.Blocking.Add($"the valley meets the ground at stations {a:0.###} (incoming) / {b:0.###} (outgoing), which do not straddle the bend at {g.PiStation:0.###}: it runs to the wrong side");
      }
      if (region != null && v.Straddles) v.InsideRegion = true;
      if (region != null && v.Straddles && (a <= region.StartStation + RegionMargin || b >= region.EndStation - RegionMargin))
      {
        v.InsideRegion = false;
        v.SuggestedRange = new[] { Math.Floor(Math.Min(a - 1.0, region.StartStation)), Math.Ceiling(Math.Max(b + 1.0, region.EndStation)) };
        v.Blocking.Add($"the meet stations {a:0.###} / {b:0.###} are not inside region '{region.Name}' ({region.StartStation:0.###}-{region.EndStation:0.###}), so part of the loop would stay unclipped");
      }
    }
    else if (g.SurfaceUsed == null) v.Warnings.Add("No daylight surface: the straddle and region checks were skipped.");

    // ---- sections that cross the valley more than once (the clip stops at the first crossing), or reach it before the meet
    var sign = g.Side == "left" ? -1.0 : 1.0;
    var o0 = Math.Min(g.LegA.Start, g.LegB.Start);
    var o1 = Math.Max(g.LegA.End, g.LegB.End);
    var test = new List<(double S, double? MeetOff, string? Leg)>();
    var lo = region?.StartStation ?? Math.Min(g.SA, g.SB);
    var hi = region?.EndStation ?? Math.Max(g.SA, g.SB);
    foreach (var s in stations)
      if (s >= lo - 1e-6 && s <= hi + 1e-6 && Math.Abs(s - g.PiStation) > 0.05) test.Add((s, null, null));
    if (g.MeetA.HasValue && v.Straddles) { test.RemoveAll(x => Math.Abs(x.S - g.MeetA.Value) < 0.01); test.Add((g.MeetA.Value, g.MeetOffA, "incoming meet")); }
    if (g.MeetB.HasValue && v.Straddles) { test.RemoveAll(x => Math.Abs(x.S - g.MeetB.Value) < 0.01); test.Add((g.MeetB.Value, g.MeetOffB, "outgoing meet")); }
    foreach (var (s, meetOff, leg) in test.OrderBy(x => x.S))
    {
      Point3d p0, p1;
      try
      {
        p0 = baseline.StationOffsetElevationToXYZ(new Point3d(s, sign * o0, 0.0));
        p1 = baseline.StationOffsetElevationToXYZ(new Point3d(s, sign * o1, 0.0));
      }
      catch { continue; }
      var offs = SectionValleyCrossings(p0.X, p0.Y, p1.X, p1.Y, g.Poly).Select(l => o0 + l * (o1 - o0)).ToList();
      var multi = offs.Count > 1;
      var early = meetOff.HasValue && offs.Count > 0 && offs[0] < meetOff.Value - 0.05;
      if (!multi && !early) continue;
      v.Crossings.Add(new Dictionary<string, object?>
      {
        ["station"] = Math.Round(s, 4),
        ["section"] = leg ?? "applied",
        ["crossingOffsets"] = offs.Select(o => Math.Round(o, 3)).ToList(),
        ["meetOffset"] = meetOff.HasValue ? Math.Round(meetOff.Value, 3) : null,
      });
    }
    if (v.Crossings.Count > 0)
    {
      var list = string.Join(", ", v.Crossings.Take(8).Select(c => $"{c["station"]}{(c["section"] is "applied" ? "" : $" ({c["section"]})")}"));
      v.Warnings.Add($"{v.Crossings.Count} section(s) cross the valley more than once or reach it before the meet point ({list}{(v.Crossings.Count > 8 ? ", ..." : "")}): " +
                     "the clip stops at the first crossing, so the daylight there ends early. Check those sections after the rebuild (checks.multipleCrossings has the offsets).");
    }
    return v;
  }

  /// <summary>Parameters (0..1 along p0-p1) where the section line crosses the valley polyline, sorted and de-duplicated.</summary>
  private static List<double> SectionValleyCrossings(double ax, double ay, double bx, double by,
    List<(double X, double Y, double? Z, double T, double U, string Kind)> poly)
  {
    var hits = new List<double>();
    var rx = bx - ax; var ry = by - ay;
    var len = Math.Sqrt(rx * rx + ry * ry);
    if (len < 1e-9) return hits;
    for (var i = 1; i < poly.Count; i++)
    {
      var cx = poly[i - 1].X; var cy = poly[i - 1].Y;
      var sx = poly[i].X - cx; var sy = poly[i].Y - cy;
      var den = rx * sy - ry * sx;
      if (Math.Abs(den) < 1e-12) continue;
      var qx = cx - ax; var qy = cy - ay;
      var t = (qx * sy - qy * sx) / den;
      var u = (qx * ry - qy * rx) / den;
      if (t < -1e-9 || t > 1 + 1e-9 || u < -1e-9 || u > 1 + 1e-9) continue;
      hits.Add(t);
    }
    hits.Sort();
    var result = new List<double>();
    foreach (var h in hits)
      if (result.Count == 0 || (h - result[^1]) * len > 0.02) result.Add(h);
    return result;
  }

  private static Dictionary<string, object?> AddMeetStation(Baseline baseline, double[] stations, double st, string leg, double[] ignore)
  {
    var region = RegionAtStation(baseline, st);
    if (region == null || st <= region.StartStation + RegionMargin || st >= region.EndStation - RegionMargin)
      return new Dictionary<string, object?> { ["station"] = Math.Round(st, 4), ["leg"] = leg, ["added"] = false, ["reason"] = "on a region boundary or outside the regions" };
    if (stations.Any(s => Math.Abs(s - st) < 0.01 && !ignore.Any(x => Math.Abs(x - s) < 0.001)))
      return new Dictionary<string, object?> { ["station"] = Math.Round(st, 4), ["leg"] = leg, ["added"] = false, ["reason"] = "an applied station is already there" };
    try
    {
      region.AddStation(st, "Bowtie valley meets daylight");
      return new Dictionary<string, object?> { ["station"] = Math.Round(st, 4), ["leg"] = leg, ["region"] = region.Name, ["added"] = true };
    }
    catch (Exception ex)
    {
      return new Dictionary<string, object?> { ["station"] = Math.Round(st, 4), ["leg"] = leg, ["added"] = false, ["reason"] = $"{ex.GetType().Name}: {ex.Message}" };
    }
  }

  private static bool AlignmentNameInUse(CivilDocument civilDoc, Transaction transaction, string name, ObjectId? except = null)
  {
    foreach (ObjectId aid in civilDoc.GetAlignmentIds())
    {
      if (except.HasValue && aid == except.Value) continue;
      var existing = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, aid, OpenMode.ForRead);
      if (string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
    }
    return false;
  }

  private const string ValleyDescriptionTag = "bowtie-valley v1";

  private static string ValleyDescription(string corridorName, int baselineIndex, ValleyGeometry g, IEnumerable<double> addedStations)
  {
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    var added = string.Join("|", addedStations.Select(s => s.ToString("0.####", inv)));
    return $"{ValleyDescriptionTag}; corridor={corridorName.Replace(";", ",")}; baseline={baselineIndex}; side={g.Side}; " +
           $"pi={g.PiStation.ToString("0.####", inv)}; templates={g.SA.ToString("0.####", inv)}|{g.SB.ToString("0.####", inv)}; stations={added}";
  }

  private static Dictionary<string, string> ParseValleyDescription(string? description)
  {
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (string.IsNullOrWhiteSpace(description) || !description.StartsWith("bowtie-valley", StringComparison.OrdinalIgnoreCase)) return map;
    foreach (var part in description.Split(';'))
    {
      var eq = part.IndexOf('=');
      if (eq <= 0) continue;
      map[part[..eq].Trim()] = part[(eq + 1)..].Trim();
    }
    return map;
  }

  private static List<double> ParseStationList(string? text)
  {
    var list = new List<double>();
    if (string.IsNullOrWhiteSpace(text)) return list;
    foreach (var s in text.Split('|'))
      if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) list.Add(v);
    return list;
  }

  private static Dictionary<string, object?> BendInfo(ValleyGeometry g) => new()
  {
    ["type"] = g.BendType,
    ["piX"] = g.PiX, ["piY"] = g.PiY,
    ["piStation"] = Math.Round(g.PiStation, 4),
    ["deflectionDeg"] = Math.Round(g.Deflection, 4),
    ["turnZone"] = new[] { Math.Round(g.TurnStart, 3), Math.Round(g.TurnEnd, 3) },
  };

  private static Dictionary<string, object?> ValleyInfo(ValleyGeometry g, string name) => new()
  {
    ["name"] = name,
    ["start"] = g.BendType == "curve" ? "curve_centre" : "pi",
    ["pointCount"] = g.Poly.Count,
    ["exactSamples"] = g.Path.Count,
    ["maxSidewaysFromBisector"] = Math.Round(g.MaxSideways, 4),
    ["lean"] = g.Lean,
    ["templatesMatch"] = g.MismatchReasons.Count == 0,
    ["levelStepAtPi"] = Math.Round(g.LevelStep, 4),
    ["length"] = Math.Round(PolylineLength(g.Poly), 4),
    ["points"] = g.Poly.Select(p => new Dictionary<string, object?>
    {
      ["x"] = Math.Round(p.X, 4), ["y"] = Math.Round(p.Y, 4),
      ["z"] = p.Z.HasValue ? Math.Round(p.Z.Value, 4) : null,
      ["t"] = Math.Round(p.T, 3), ["u"] = Math.Round(p.U, 4), ["kind"] = p.Kind,
    }).ToList(),
  };

  private static Dictionary<string, object?>? MeetInfo(ValleyGeometry g)
  {
    if (!g.Hit.HasValue || !g.MeetA.HasValue || !g.MeetB.HasValue) return null;
    var (hx, hy, hz) = g.Hit.Value;
    return new Dictionary<string, object?>
    {
      ["x"] = hx, ["y"] = hy, ["z"] = hz,
      ["surfaceName"] = g.SurfaceUsed,
      ["stationIncoming"] = Math.Round(g.MeetA.Value, 4),
      ["stationOutgoing"] = Math.Round(g.MeetB.Value, 4),
      ["offsetIncoming"] = g.MeetOffA.HasValue ? Math.Round(g.MeetOffA.Value, 4) : null,
      ["offsetOutgoing"] = g.MeetOffB.HasValue ? Math.Round(g.MeetOffB.Value, 4) : null,
    };
  }

  private static Dictionary<string, object?> ChecksInfo(ValleyGeometry g, ValleyVerdict v, BaselineRegion? region) => new()
  {
    ["ok"] = v.Blocking.Count == 0,
    ["templatesMatch"] = g.MismatchReasons.Count == 0,
    ["straddlesBend"] = v.Straddles,
    ["meetsInsideRegion"] = v.InsideRegion,
    ["region"] = region == null ? null : new Dictionary<string, object?>
    {
      ["name"] = region.Name,
      ["startStation"] = Math.Round(region.StartStation, 4),
      ["endStation"] = Math.Round(region.EndStation, 4),
    },
    ["suggestedRegionRange"] = v.SuggestedRange,
    ["multipleCrossings"] = v.Crossings,
    ["blocking"] = v.Blocking,
  };

  private static LegModel BuildLeg(Baseline baseline, double[] stations, double s, double sign, string linkCode, double extension, bool before, List<string> warnings)
  {
    var leg = new LegModel { Station = s };
    AppliedAssembly applied;
    try { applied = baseline.GetAppliedAssemblyAtStation(s); }
    catch (Exception ex) { throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"No applied assembly at station {s:0.###}: {ex.Message}"); }

    leg.Cz = BaselineElevation(applied) ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_STATE", $"The section at {s:0.###} has no points.");
    var c = baseline.StationOffsetElevationToXYZ(new Point3d(s, 0.0, 0.0));
    if (Math.Abs(c.X) < 1e-9 && Math.Abs(c.Y) < 1e-9)
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"The baseline has no point at station {s:0.###}.");
    leg.Cx = c.X; leg.Cy = c.Y;
    var dir = DirectionAt(baseline, s, before);
    leg.Dx = dir.X; leg.Dy = dir.Y;
    // inside normal: left = (-dy, dx), right = (dy, -dx)
    if (sign < 0) { leg.Nx = -leg.Dy; leg.Ny = leg.Dx; }
    else { leg.Nx = leg.Dy; leg.Ny = -leg.Dx; }

    // grade from the neighbouring applied station on the same leg
    var neighbour = before
      ? stations.Where(x => x < s - 0.05).DefaultIfEmpty(double.NaN).Max()
      : stations.Where(x => x > s + 0.05).DefaultIfEmpty(double.NaN).Min();
    if (!double.IsNaN(neighbour))
    {
      try
      {
        var z2 = BaselineElevation(baseline.GetAppliedAssemblyAtStation(neighbour));
        if (z2.HasValue) { leg.Grade = (leg.Cz - z2.Value) / (s - neighbour); leg.GradeFrom = neighbour; }
      }
      catch { }
    }
    else warnings.Add($"No second applied station next to {s:0.###}: the leg grade is taken as 0.");

    // inside chain of linkCode links, oriented inner -> outer
    var segs = new List<((double O, double Z) A, (double O, double Z) B)>();
    foreach (CalculatedLink link in applied.Links)
    {
      if (!HasCode(link.CorridorCodes, linkCode)) continue;
      var pts = link.CalculatedPoints.Cast<CalculatedPoint>().Select(p => p.StationOffsetElevationToBaseline).ToList();
      if (pts.Count < 2) continue;
      var a = (O: sign * pts[0].Y, Z: pts[0].Z);
      var b = (O: sign * pts[^1].Y, Z: pts[^1].Z);
      if (a.O < -1e-6 || b.O < -1e-6) continue;
      if (Math.Abs(a.O - b.O) < 1e-9 && Math.Abs(a.Z - b.Z) < 1e-9) continue;
      segs.Add(a.O <= b.O ? (a, b) : (b, a));
    }
    foreach (CalculatedPoint p in applied.Points)
    {
      if (sign * p.StationOffsetElevationToBaseline.Y > 1e-6 && HasCode(p.CorridorCodes, "Valley")) leg.Clipped = true;
    }
    if (segs.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_STATE", $"The section at {s:0.###} has no {linkCode}-coded links on the {(sign < 0 ? "left" : "right")} side.");
    if (leg.Clipped)
      warnings.Add($"The section at {s:0.###} is already clipped (has a Valley point): its template stops at the clip. Pass templateStationBefore / templateStationAfter outside the clip zone.");

    segs.Sort((x, y) => x.A.O.CompareTo(y.A.O));
    var used = new bool[segs.Count];
    var chain = new List<(double O, double Z)>();
    used[0] = true;
    chain.Add(segs[0].A);
    chain.Add(segs[0].B);
    leg.LinksUsed = 1;
    while (true)
    {
      var end = chain[^1];
      var next = -1;
      for (var i = 0; i < segs.Count; i++)
      {
        if (used[i]) continue;
        if (Math.Abs(segs[i].A.O - end.O) < 1e-4 && Math.Abs(segs[i].A.Z - end.Z) < 1e-4) { next = i; break; }
      }
      if (next < 0)
      {
        for (var i = 0; i < segs.Count; i++)
        {
          if (used[i] || segs[i].A.O < end.O - 1e-6) continue;
          if (next < 0 || segs[i].A.O < segs[next].A.O) next = i;
        }
      }
      if (next < 0) break;
      used[next] = true;
      leg.LinksUsed++;
      if (Math.Abs(segs[next].A.O - end.O) > 1e-4 || Math.Abs(segs[next].A.Z - end.Z) > 1e-4) chain.Add(segs[next].A);
      chain.Add(segs[next].B);
    }
    foreach (var p in chain)
    {
      if (leg.Template.Count > 0 && p.O < leg.Template[^1].Off - 1e-6) continue;
      if (leg.Template.Count > 0 && Math.Abs(p.O - leg.Template[^1].Off) < 1e-9) { leg.Template[^1] = (p.O, p.Z); continue; }
      leg.Template.Add((p.O, p.Z));
    }
    if (leg.Template.Count >= 2 && extension > 0)
    {
      var (o1, z1) = leg.Template[^1];
      var (o0, z0) = leg.Template[^2];
      var slope = o1 - o0 > 1e-9 ? (z1 - z0) / (o1 - o0) : 0.0;
      leg.Template.Add((o1 + extension, z1 + slope * extension));
    }
    return leg;
  }

  private static double? BaselineElevation(AppliedAssembly applied)
  {
    foreach (CalculatedPoint p in applied.Points)
    {
      try { return p.XYZ.Z - p.StationOffsetElevationToBaseline.Z; } catch { }
    }
    return null;
  }

  /// <summary>Unit direction of travel at a station; at an angle point the leg's own side (before / after).</summary>
  private static Vector2d DirectionAt(Baseline baseline, double s, bool before)
  {
    const double h = 0.05;
    try
    {
      var p0 = baseline.StationOffsetElevationToXYZ(new Point3d(before ? s - h : s, 0.0, 0.0));
      var p1 = baseline.StationOffsetElevationToXYZ(new Point3d(before ? s : s + h, 0.0, 0.0));
      var v = new Vector2d(p1.X - p0.X, p1.Y - p0.Y);
      if (v.Length > 1e-9 && !(Math.Abs(p0.X) < 1e-9 && Math.Abs(p0.Y) < 1e-9)) return v.GetNormal();
    }
    catch { }
    var d = baseline.GetDirectionAtStation(s);
    var w = new Vector2d(d.X, d.Y);
    if (w.Length < 1e-12) throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"No baseline direction at station {s:0.###}.");
    return w.GetNormal();
  }

  private static (double X, double Y, double Nx, double Ny)? SafeLeg(Baseline baseline, double s, double sign)
  {
    try
    {
      var c = baseline.StationOffsetElevationToXYZ(new Point3d(s, 0.0, 0.0));
      var d = baseline.GetDirectionAtStation(s);
      var v = new Vector2d(d.X, d.Y);
      if (v.Length < 1e-12) return null;
      v = v.GetNormal();
      return sign < 0 ? (c.X, c.Y, -v.Y, v.X) : (c.X, c.Y, v.Y, -v.X);
    }
    catch { return null; }
  }

  /// <summary>Stations between which 5 % .. 95 % of the direction change between a and b happens.</summary>
  private static (double Start, double End) TurnZone(Baseline baseline, double a, double b)
  {
    var (s, e, _) = TurnZoneWithAngle(baseline, a, b);
    return (s, e);
  }

  /// <summary>As TurnZone, plus the total direction change (radians, absolute sum) between a and b.</summary>
  private static (double Start, double End, double Angle) TurnZoneWithAngle(Baseline baseline, double a, double b)
  {
    var n = Math.Clamp((int)Math.Ceiling((b - a) / 0.05), 2, 4000);
    var angles = new List<(double S, double A)>();
    double? prev = null;
    double acc = 0;
    for (var i = 0; i <= n; i++)
    {
      var s = a + (b - a) * i / n;
      double ang;
      try
      {
        var d = baseline.GetDirectionAtStation(s);
        ang = Math.Atan2(d.Y, d.X);
      }
      catch { continue; }
      if (prev.HasValue)
      {
        var da = ang - prev.Value;
        while (da > Math.PI) da -= 2 * Math.PI;
        while (da < -Math.PI) da += 2 * Math.PI;
        acc += Math.Abs(da);
      }
      prev = ang;
      angles.Add((s, acc));
    }
    if (angles.Count < 2 || acc < 1e-9) return (a, a, 0.0);
    var s5 = angles.First(x => x.A >= 0.05 * acc).S;
    var s95 = angles.First(x => x.A >= 0.95 * acc).S;
    return (s5, s95, acc);
  }

  private static List<(double X, double Y, double? Z, double T, double U, string Kind)> SimplifyPolyline(
    List<(double X, double Y, double? Z, double T, double U, string Kind)> pts, double tol)
  {
    if (pts.Count < 3) return pts;
    var keep = new bool[pts.Count];
    keep[0] = keep[^1] = true;
    void Rec(int i, int j)
    {
      if (j <= i + 1) return;
      var ax = pts[i].X; var ay = pts[i].Y;
      var ex = pts[j].X - ax; var ey = pts[j].Y - ay;
      var len = Math.Sqrt(ex * ex + ey * ey);
      var best = -1.0; var idx = -1;
      for (var k = i + 1; k < j; k++)
      {
        var d = len < 1e-12 ? Math.Sqrt(Math.Pow(pts[k].X - ax, 2) + Math.Pow(pts[k].Y - ay, 2))
                            : Math.Abs(ex * (pts[k].Y - ay) - ey * (pts[k].X - ax)) / len;
        if (d > best) { best = d; idx = k; }
      }
      if (best > tol) { keep[idx] = true; Rec(i, idx); Rec(idx, j); }
    }
    Rec(0, pts.Count - 1);
    return pts.Where((_, k) => keep[k]).ToList();
  }

  private static double PolylineLength(List<(double X, double Y, double? Z, double T, double U, string Kind)> pts)
  {
    double sum = 0;
    for (var i = 1; i < pts.Count; i++) sum += Math.Sqrt(Math.Pow(pts[i].X - pts[i - 1].X, 2) + Math.Pow(pts[i].Y - pts[i - 1].Y, 2));
    return sum;
  }

  private static (CivilSurface?, string?) FindSurface(CivilDocument civilDoc, Transaction transaction, string name)
  {
    foreach (ObjectId id in civilDoc.GetSurfaceIds())
    {
      if (transaction.GetObject(id, OpenMode.ForRead) is CivilSurface s && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
        return (s, s.Name);
    }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Surface '{name}' was not found.");
  }

  private static (CivilSurface?, string?) SurfaceFromTargets(Baseline baseline, Transaction transaction, double station)
  {
    var regions = new List<BaselineRegion>();
    var at = RegionAtStation(baseline, station);
    if (at != null) regions.Add(at);
    for (var i = 0; i < baseline.BaselineRegions.Count; i++) regions.Add(baseline.BaselineRegions[i]);
    foreach (var region in regions)
    {
      try
      {
        var infos = region.GetTargets();
        for (var i = 0; i < infos.Count; i++)
        {
          foreach (ObjectId id in infos[i].TargetIds)
          {
            if (!id.IsNull && transaction.GetObject(id, OpenMode.ForRead) is CivilSurface s) return (s, s.Name);
          }
        }
      }
      catch { }
    }
    return (null, null);
  }

  /// <summary>First point along the valley (after its start) where the design surface crosses the ground.</summary>
  private static (double X, double Y, double Z)? ValleyMeetsSurface(
    List<(double X, double Y, double? Z, double T, double U, string Kind)> poly, LegModel leg, CivilSurface surface)
  {
    double? Diff(double x, double y)
    {
      var zd = leg.Z(x, y);
      if (!zd.HasValue) return null;
      try { return zd.Value - surface.FindElevationAtXY(x, y); } catch { return null; }
    }
    double? prevD = null;
    double px = 0, py = 0;
    for (var i = 1; i < poly.Count; i++)
    {
      var (ax, ay) = (poly[i - 1].X, poly[i - 1].Y);
      var (ex, ey) = (poly[i].X - ax, poly[i].Y - ay);
      var len = Math.Sqrt(ex * ex + ey * ey);
      var n = Math.Max(1, (int)Math.Ceiling(len / 0.05));
      for (var k = (i == 1 ? 0 : 1); k <= n; k++)
      {
        var x = ax + ex * k / n; var y = ay + ey * k / n;
        if (leg.SO(x, y).O < leg.Start) { prevD = null; continue; }
        var d = Diff(x, y);
        if (d.HasValue && prevD.HasValue && prevD.Value * d.Value <= 0 && prevD.Value != 0)
        {
          double lx = px, ly = py, hx = x, hy = y, lo = prevD.Value;
          for (var it = 0; it < 40; it++)
          {
            var mxp = 0.5 * (lx + hx); var myp = 0.5 * (ly + hy);
            var dm = Diff(mxp, myp);
            if (!dm.HasValue) break;
            if (lo * dm.Value <= 0) { hx = mxp; hy = myp; } else { lx = mxp; ly = myp; lo = dm.Value; }
          }
          var rx = 0.5 * (lx + hx); var ry = 0.5 * (ly + hy);
          return (rx, ry, leg.Z(rx, ry) ?? 0.0);
        }
        if (d.HasValue) { prevD = d; px = x; py = y; }
      }
    }
    return null;
  }

  private static ObjectId CreateValleyAlignment(CivilDocument civilDoc, Database database, Transaction transaction, string name,
    List<(double X, double Y, double? Z, double T, double U, string Kind)> poly, string? style, string? layer)
  {
    var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
    var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
    using var polyline = new Polyline();
    for (var i = 0; i < poly.Count; i++) polyline.AddVertexAt(i, new Point2d(poly[i].X, poly[i].Y), 0, 0, 0);
    var polylineId = modelSpace.AppendEntity(polyline);
    transaction.AddNewlyCreatedDBObject(polyline, true);

    var options = new PolylineOptions { AddCurvesBetweenTangents = false, EraseExistingEntities = true, PlineId = polylineId };
    var layerId = LookupUtils.GetLayerId(database, transaction, layer);
    var styleId = LookupUtils.GetAlignmentStyleId(civilDoc, transaction, style);
    var labelSetId = NoLabelsAlignmentLabelSet(civilDoc, transaction);
    return Alignment.Create(civilDoc, options, name, ObjectId.Null, layerId, styleId, labelSetId);
  }

  private static ObjectId NoLabelsAlignmentLabelSet(CivilDocument civilDoc, Transaction transaction) =>
    LookupUtils.GetStyleIdPreferring(civilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles, transaction, null, new[] { "No Labels", "_No Labels" });

  /// <summary>Offset where the template first turns from flat (|slope| &lt;= 0.1) to sloped.</summary>
  private static double? HingeOffset(LegModel leg)
  {
    for (var i = 1; i < leg.Template.Count; i++)
    {
      var (o0, z0) = leg.Template[i - 1];
      var (o1, z1) = leg.Template[i];
      if (o1 - o0 > 1e-9 && Math.Abs((z1 - z0) / (o1 - o0)) > 0.1) return o0;
    }
    return null;
  }

  private static Dictionary<string, object?> LegInfo(LegModel leg, string which) => new()
  {
    ["leg"] = which,
    ["templateStation"] = leg.Station,
    ["baselineElevation"] = Math.Round(leg.Cz, 4),
    ["grade"] = Math.Round(leg.Grade, 6),
    ["gradeFromStation"] = leg.GradeFrom,
    ["linksUsed"] = leg.LinksUsed,
    ["alreadyClipped"] = leg.Clipped,
    ["template"] = leg.Template.Select(t => new[] { Math.Round(t.Off, 4), Math.Round(t.Dz, 4) }).ToList(),
  };

  // =========================================================================
  // bowtieRefresh: recompute every valley a corridor's regions clip to (ClipTarget -> alignment) after a design change,
  // update the alignment in place (so the target mappings stay) and move the meet stations.
  // =========================================================================

  public static Task<object?> BowtieRefreshAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var regionName = PluginRuntime.GetOptionalString(parameters, "regionName");
    var linkCode = PluginRuntime.GetOptionalString(parameters, "linkCode") ?? "Top";
    var extension = PluginRuntime.GetOptionalDouble(parameters, "extension") ?? 3.0;
    var step = PluginRuntime.GetOptionalDouble(parameters, "step") ?? 0.1;
    var surfaceName = PluginRuntime.GetOptionalString(parameters, "surfaceName");
    var tolerance = PluginRuntime.GetOptionalDouble(parameters, "tolerance") ?? 0.01;
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    var rebuild = PluginRuntime.GetOptionalBool(parameters, "rebuild") ?? true;
    var rebuildFirst = PluginRuntime.GetOptionalBool(parameters, "rebuildFirst") ?? true;
    var allowMismatch = PluginRuntime.GetOptionalBool(parameters, "allowMismatch") ?? false;
    if (extension < 0 || step <= 0 || step > 1 || tolerance <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "extension must be >= 0, step in (0, 1] and tolerance > 0.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, dryRun ? OpenMode.ForRead : OpenMode.ForWrite);
      var baseline = GetBaseline(corridor, baselineIndex);
      var warnings = new List<string>();
      bool? outOfDate = null;
      try { outOfDate = corridor.IsOutOfDate; } catch { }
      string? firstRebuildError = null;
      var rebuiltFirst = false;
      if (!dryRun && rebuildFirst)
      {
        firstRebuildError = TryRebuild(corridor);
        rebuiltFirst = firstRebuildError == null;
        if (firstRebuildError != null) warnings.Add($"The rebuild before the refresh failed ({firstRebuildError}): the valleys were computed from the sections as they were.");
      }
      else if (outOfDate == true)
        warnings.Add("The corridor is out of date: the valleys were computed from its last built sections. A real run rebuilds it first (rebuildFirst).");
      var stations = AppliedStationsOrThrow(corridor, baseline);

      var results = new List<Dictionary<string, object?>>();
      var seen = new HashSet<ObjectId>();
      var regionFound = regionName == null;
      var changed = false;
      var regions = baseline.BaselineRegions;
      for (var ri = 0; ri < regions.Count; ri++)
      {
        var region = regions[ri];
        if (regionName != null && !string.Equals(region.Name, regionName, StringComparison.OrdinalIgnoreCase)) continue;
        regionFound = true;
        SubassemblyTargetInfoCollection infos;
        try { infos = region.GetTargets(); } catch { continue; }
        for (var ti = 0; ti < infos.Count; ti++)
        {
          var info = infos[ti];
          if (!string.Equals(info.LogicalName, "ClipTarget", StringComparison.OrdinalIgnoreCase)) continue;
          ObjectIdCollection ids;
          try { ids = info.TargetIds; } catch { continue; }
          foreach (ObjectId id in ids)
          {
            if (id.IsNull || !seen.Add(id)) continue;
            if (transaction.GetObject(id, OpenMode.ForRead) is not Alignment valley) continue;
            var row = RefreshValley(civilDoc, transaction, corridor, baselineIndex, baseline, stations, region, ri, info.SubassemblyName, valley,
              linkCode, extension, step, surfaceName, tolerance, dryRun, allowMismatch);
            if (row["applied"] is true) changed = true;
            results.Add(row);
          }
        }
      }
      if (!regionFound)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Region '{regionName}' was not found on baseline '{baseline.Name}'.");
      if (results.Count == 0)
        warnings.Add("No region of this baseline has its ClipTarget mapped to an alignment: nothing to refresh.");

      string? rebuildError = null;
      if (!dryRun && rebuild && changed) rebuildError = TryRebuild(corridor);
      var counts = results.GroupBy(r => (string)r["status"]!).ToDictionary(x => x.Key, x => x.Count());
      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridor.Name,
        ["baselineIndex"] = baselineIndex,
        ["dryRun"] = dryRun,
        ["outOfDateBefore"] = outOfDate,
        ["rebuiltFirst"] = rebuiltFirst,
        ["valleys"] = results,
        ["summary"] = counts,
        ["rebuilt"] = !dryRun && rebuild && changed && rebuildError == null,
        ["rebuildError"] = rebuildError,
        ["nextStep"] = changed ? "Run bowtie_check on the corridor (or each refreshed range) to confirm it is still clean." : null,
        ["warnings"] = warnings,
      };
    });
  }

  private static Dictionary<string, object?> RefreshValley(CivilDocument civilDoc, Transaction transaction, Corridor corridor, int baselineIndex,
    Baseline baseline, double[] stations, BaselineRegion region, int regionIndex, string subassemblyName, Alignment valley,
    string linkCode, double extension, double step, string? surfaceName, double tolerance, bool dryRun, bool allowMismatch)
  {
    var row = new Dictionary<string, object?>
    {
      ["regionIndex"] = regionIndex,
      ["regionName"] = region.Name,
      ["subassemblyName"] = subassemblyName,
      ["valleyName"] = valley.Name,
      ["applied"] = false,
    };
    var notes = new List<string>();
    row["warnings"] = notes;
    Dictionary<string, object?> Refuse(string why)
    {
      row["status"] = "refused";
      row["reason"] = why;
      return row;
    }

    var desc = ParseValleyDescription(valley.Description);
    row["recorded"] = desc.Count > 0;

    // ---- the bend inside the region
    var (turnStart, turnEnd, turnAngle) = TurnZoneWithAngle(baseline, region.StartStation, region.EndStation);
    if (turnAngle < 0.2 * Math.PI / 180.0)
      return Refuse($"no bend inside region '{region.Name}' ({region.StartStation:0.###}-{region.EndStation:0.###}): the region no longer holds the bend it was clipped for");
    var piGuess = 0.5 * (turnStart + turnEnd);

    // ---- side: as recorded, else the inside of the turn
    string side;
    if (desc.TryGetValue("side", out var recordedSide) && recordedSide is "left" or "right") side = recordedSide;
    else
    {
      var dA = DirectionAt(baseline, Math.Max(region.StartStation, turnStart - 0.1), before: true);
      var dB = DirectionAt(baseline, Math.Min(region.EndStation, turnEnd + 0.1), before: false);
      side = dA.X * dB.Y - dA.Y * dB.X > 0 ? "left" : "right";
    }
    row["side"] = side;
    var sign = side == "left" ? -1.0 : 1.0;

    // ---- template stations: the ones the valley was built from (if recorded and still unclipped), then walking out from
    // the bend the first sections the clip does not touch; SearchValley tries further ones if the nearest give no usable valley
    var stationsBefore = stations.Where(s => s < Math.Min(piGuess, turnStart) - 0.05).OrderByDescending(s => s);
    var stationsAfter = stations.Where(s => s > Math.Max(piGuess, turnEnd) + 0.05).OrderBy(s => s);
    var candA = UnclippedStations(baseline, stationsBefore, sign, 3);
    var candB = UnclippedStations(baseline, stationsAfter, sign, 3);
    var recordedTemplates = desc.TryGetValue("templates", out var rt) ? ParseStationList(rt) : new List<double>();
    if (recordedTemplates.Count == 2)
    {
      var ra = stations.Where(s => Math.Abs(s - recordedTemplates[0]) < 0.001).Cast<double?>().FirstOrDefault();
      var rb = stations.Where(s => Math.Abs(s - recordedTemplates[1]) < 0.001).Cast<double?>().FirstOrDefault();
      if (ra.HasValue && ra.Value < piGuess && UnclippedStations(baseline, new[] { ra.Value }, sign, 1).Count == 1)
      { candA.RemoveAll(x => Math.Abs(x - ra.Value) < 0.001); candA.Insert(0, ra.Value); }
      if (rb.HasValue && rb.Value > piGuess && UnclippedStations(baseline, new[] { rb.Value }, sign, 1).Count == 1)
      { candB.RemoveAll(x => Math.Abs(x - rb.Value) < 0.001); candB.Insert(0, rb.Value); }
    }
    if (candA.Count == 0 || candB.Count == 0)
      return Refuse("no unclipped section found next to the bend to read the leg templates from");

    ValleyGeometry g;
    ValleyVerdict verdict;
    try
    {
      var search = SearchValley(civilDoc, transaction, baseline, stations, side, candA, candB, linkCode, extension, step, surfaceName, _ => region);
      g = search.G;
      verdict = search.Verdict;
      notes.AddRange(search.Notes);
      row["templateSearch"] = search.Tried;
    }
    catch (JsonRpcDispatchException ex) { return Refuse(ex.Message); }
    row["templateStations"] = new[] { Math.Round(g.SA, 4), Math.Round(g.SB, 4) };
    notes.AddRange(g.Warnings);
    notes.AddRange(verdict.Warnings);
    row["bend"] = BendInfo(g);
    row["checks"] = ChecksInfo(g, verdict, region);
    row["meetsDaylight"] = MeetInfo(g);
    row["lean"] = g.Lean;

    // ---- compare with what is there
    var shift = ValleyShift(valley, UsedPart(g));
    row["maxShift"] = shift.HasValue ? Math.Round(shift.Value, 4) : null;
    row["oldLength"] = Math.Round(valley.Length, 4);
    row["newLength"] = Math.Round(PolylineLength(g.Poly), 4);
    double[] added;
    try { added = region.AdditionalStations() ?? Array.Empty<double>(); } catch { added = Array.Empty<double>(); }
    List<double> oldMeets;
    if (desc.TryGetValue("stations", out var recorded))
      oldMeets = ParseStationList(recorded).Where(s => added.Any(a => Math.Abs(a - s) < 0.001)).ToList();
    else
    {
      // valleys from before the description was recorded: the region's added stations either side of the bend
      oldMeets = new List<double>();
      var before = added.Where(s => s < piGuess).DefaultIfEmpty(double.NaN).Max();
      var after = added.Where(s => s > piGuess).DefaultIfEmpty(double.NaN).Min();
      if (!double.IsNaN(before)) oldMeets.Add(before);
      if (!double.IsNaN(after)) oldMeets.Add(after);
      if (oldMeets.Count > 0) notes.Add($"No recorded meet stations (valley made before bowtie_refresh): took the region's added stations {string.Join(" / ", oldMeets.Select(s => s.ToString("0.###")))} as the old ones.");
    }
    var newMeets = new List<double>();
    if (verdict.Straddles && g.MeetA.HasValue && g.MeetB.HasValue) { newMeets.Add(g.MeetA.Value); newMeets.Add(g.MeetB.Value); }
    row["oldMeetStations"] = oldMeets.Select(s => Math.Round(s, 4)).ToList();
    row["newMeetStations"] = newMeets.Select(s => Math.Round(s, 4)).ToList();
    var stationsMove = oldMeets.Count != newMeets.Count || oldMeets.OrderBy(s => s).Zip(newMeets.OrderBy(s => s)).Any(p => Math.Abs(p.First - p.Second) > 0.001);
    var geometryMoves = !shift.HasValue || shift.Value > tolerance;

    if (verdict.Blocking.Count > 0 && !allowMismatch)
      return Refuse($"{string.Join("; ", verdict.Blocking)}. The valley was left as it is: the design change needs a look (dryRun shows the details; allowMismatch true refreshes it anyway)");
    if (verdict.Blocking.Count > 0) notes.Add($"Refreshed anyway (allowMismatch): {string.Join("; ", verdict.Blocking)}.");
    if (!geometryMoves && !stationsMove)
    {
      row["status"] = "unchanged";
      if (!dryRun && desc.Count == 0)
      {
        // record what bowtie_refresh needs next time (valleys made before the description was written)
        try
        {
          valley.UpgradeOpen();
          valley.Description = ValleyDescription(corridor.Name, baselineIndex, g, oldMeets);
          row["descriptionWritten"] = true;
        }
        catch (Exception ex) { notes.Add($"The alignment description could not be set: {ex.Message}"); }
      }
      return row;
    }
    row["status"] = dryRun ? "would_update" : "updated";
    row["geometryUpdated"] = geometryMoves;
    row["stationsMoved"] = stationsMove;
    if (dryRun) return row;

    // ---- apply: geometry in place (keeps the ClipTarget mapping), meet stations, description
    if (geometryMoves)
    {
      valley.UpgradeOpen();
      ReplaceValleyGeometry(valley, g.Poly);
    }
    var stationRows = new List<Dictionary<string, object?>>();
    var nowAdded = new List<double>();
    if (stationsMove)
    {
      foreach (var s in oldMeets)
      {
        try { region.DeleteStation(s); stationRows.Add(new Dictionary<string, object?> { ["station"] = Math.Round(s, 4), ["deleted"] = true }); }
        catch (Exception ex) { stationRows.Add(new Dictionary<string, object?> { ["station"] = Math.Round(s, 4), ["deleted"] = false, ["reason"] = $"{ex.GetType().Name}: {ex.Message}" }); }
      }
      var legs = new[] { "incoming", "outgoing" };
      for (var i = 0; i < newMeets.Count; i++)
      {
        var r = AddMeetStation(baseline, stations, newMeets[i], legs[Math.Min(i, 1)], oldMeets.ToArray());
        stationRows.Add(r);
        if (r["added"] is true) nowAdded.Add(newMeets[i]);
      }
    }
    else nowAdded.AddRange(oldMeets);
    row["stations"] = stationRows;
    try
    {
      if (!valley.IsWriteEnabled) valley.UpgradeOpen();
      valley.Description = ValleyDescription(corridor.Name, baselineIndex, g, nowAdded);
    }
    catch (Exception ex) { notes.Add($"The alignment description could not be updated: {ex.Message}"); }
    row["applied"] = true;
    return row;
  }

  /// <summary>The first <paramref name="count"/> candidate stations whose section has no Valley point on the inside side.</summary>
  private static List<double> UnclippedStations(Baseline baseline, IEnumerable<double> candidates, double sign, int count)
  {
    var list = new List<double>();
    var n = 0;
    foreach (var s in candidates)
    {
      if (list.Count >= count || ++n > 400) break;
      try
      {
        var applied = baseline.GetAppliedAssemblyAtStation(s);
        var clipped = false;
        var inside = false;
        foreach (CalculatedPoint p in applied.Points)
        {
          var off = sign * p.StationOffsetElevationToBaseline.Y;
          if (off <= 1e-6) continue;
          inside = true;
          if (HasCode(p.CorridorCodes, "Valley")) { clipped = true; break; }
        }
        if (inside && !clipped) list.Add(s);
      }
      catch { }
    }
    return list;
  }

  /// <summary>The valley up to 0.5 m past where it meets the ground: the part the sections can reach.</summary>
  private static List<(double X, double Y, double? Z, double T, double U, string Kind)> UsedPart(ValleyGeometry g)
  {
    if (!g.Hit.HasValue || g.Poly.Count < 2) return g.Poly;
    var (hx, hy, _) = g.Hit.Value;
    // arc length of the hit along the polyline
    double run = 0, best = double.MaxValue, hitAt = 0;
    for (var i = 1; i < g.Poly.Count; i++)
    {
      var ax = g.Poly[i - 1].X; var ay = g.Poly[i - 1].Y;
      var ex = g.Poly[i].X - ax; var ey = g.Poly[i].Y - ay;
      var len = Math.Sqrt(ex * ex + ey * ey);
      var t = len < 1e-12 ? 0.0 : Math.Clamp(((hx - ax) * ex + (hy - ay) * ey) / (len * len), 0.0, 1.0);
      var d = Math.Sqrt(Math.Pow(ax + ex * t - hx, 2) + Math.Pow(ay + ey * t - hy, 2));
      if (d < best) { best = d; hitAt = run + t * len; }
      run += len;
    }
    var limit = hitAt + 0.5;
    var result = new List<(double X, double Y, double? Z, double T, double U, string Kind)> { g.Poly[0] };
    run = 0;
    for (var i = 1; i < g.Poly.Count; i++)
    {
      var a = g.Poly[i - 1]; var b = g.Poly[i];
      var len = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
      if (run + len <= limit) { result.Add(b); run += len; continue; }
      var f = len < 1e-12 ? 0.0 : (limit - run) / len;
      result.Add((a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f, null, a.T + (b.T - a.T) * f, a.U + (b.U - a.U) * f, "cut"));
      break;
    }
    return result;
  }

  /// <summary>Largest distance between the existing valley alignment and the new polyline (both sampled at 0.1 m).</summary>
  private static double? ValleyShift(Alignment old, List<(double X, double Y, double? Z, double T, double U, string Kind)> poly)
  {
    try
    {
      var oldPts = new List<(double X, double Y)>();
      var s0 = old.StartingStation;
      var s1 = old.EndingStation;
      var n = Math.Clamp((int)Math.Ceiling((s1 - s0) / 0.1), 1, 5000);
      for (var i = 0; i <= n; i++)
      {
        double x = 0, y = 0;
        old.PointLocation(s0 + (s1 - s0) * i / n, 0.0, ref x, ref y);
        oldPts.Add((x, y));
      }
      var newPts = poly.Select(p => (p.X, p.Y)).ToList();
      var newSamples = new List<(double X, double Y)>();
      for (var i = 1; i < newPts.Count; i++)
      {
        var (ax, ay) = newPts[i - 1];
        var (bx, by) = newPts[i];
        var m = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)) / 0.1));
        for (var k = (i == 1 ? 0 : 1); k <= m; k++) newSamples.Add((ax + (bx - ax) * k / m, ay + (by - ay) * k / m));
      }
      // the new valley's used part against the old line, and the old line's start (the PI end) against the new one
      var max = 0.0;
      foreach (var p in newSamples) max = Math.Max(max, DistanceToPolyline(p, oldPts));
      max = Math.Max(max, DistanceToPolyline(oldPts[0], newPts));
      return max;
    }
    catch { return null; }
  }

  private static double DistanceToPolyline((double X, double Y) p, List<(double X, double Y)> pts)
  {
    var best = double.MaxValue;
    for (var i = 1; i < pts.Count; i++)
    {
      var (ax, ay) = pts[i - 1];
      var ex = pts[i].X - ax; var ey = pts[i].Y - ay;
      var l2 = ex * ex + ey * ey;
      var t = l2 < 1e-18 ? 0.0 : Math.Clamp(((p.X - ax) * ex + (p.Y - ay) * ey) / l2, 0.0, 1.0);
      var dx = ax + ex * t - p.X; var dy = ay + ey * t - p.Y;
      best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy));
    }
    if (pts.Count == 1) best = Math.Sqrt(Math.Pow(pts[0].X - p.X, 2) + Math.Pow(pts[0].Y - p.Y, 2));
    return best;
  }

  /// <summary>Replaces the alignment's entities by fixed lines through the polyline's vertices. The object (and every target mapping to it) stays.</summary>
  private static void ReplaceValleyGeometry(Alignment alignment, List<(double X, double Y, double? Z, double T, double U, string Kind)> poly)
  {
    if (poly.Count < 2) throw new JsonRpcDispatchException("CIVIL3D.INVALID_STATE", $"The new valley for '{alignment.Name}' has fewer than two points.");
    var entities = alignment.Entities;
    entities.Clear();
    var previous = -1;
    for (var i = 1; i < poly.Count; i++)
    {
      var a = new Point3d(poly[i - 1].X, poly[i - 1].Y, 0.0);
      var b = new Point3d(poly[i].X, poly[i].Y, 0.0);
      if (a.DistanceTo(b) < 1e-6) continue;
      AlignmentLine line;
      if (previous < 0) line = entities.AddFixedLine(a, b);
      else
      {
        try { line = entities.AddFixedLine(previous, a, b); }
        catch { line = entities.AddFixedLine(a, b); }
      }
      previous = line.EntityId;
    }
  }

  // =========================================================================
  // regionStations: list / delete / clear a region's added stations (e.g. the ones bowtie_valley adds)
  // =========================================================================

  public static Task<object?> CorridorRegionStationsAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var regionIndex = PluginRuntime.GetOptionalInt(parameters, "regionIndex");
    var regionName = PluginRuntime.GetOptionalString(parameters, "regionName");
    var operation = (PluginRuntime.GetOptionalString(parameters, "operation") ?? "list").Trim().ToLowerInvariant();
    var rebuild = PluginRuntime.GetOptionalBool(parameters, "rebuild") ?? true;
    var stationsNode = PluginRuntime.GetParameter(parameters, "stations") as JsonArray;
    var wanted = new List<double>();
    if (stationsNode != null)
      foreach (var n in stationsNode) if (n != null && double.TryParse(n.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) wanted.Add(v);
    if (operation is not ("list" or "delete" or "clear"))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "operation must be list, delete (with stations) or clear.");
    if (operation == "delete" && wanted.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "delete needs stations (the added stations to remove). Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, operation == "list" ? OpenMode.ForRead : OpenMode.ForWrite);
      var baseline = GetBaseline(corridor, baselineIndex);
      var region = FindRegion(baseline, regionIndex, regionName);
      double[] before;
      try { before = region.AdditionalStations() ?? Array.Empty<double>(); } catch { before = Array.Empty<double>(); }
      var results = new List<Dictionary<string, object?>>();
      string? rebuildError = null;
      if (operation == "clear")
      {
        region.ClearAdditionalStations();
      }
      else if (operation == "delete")
      {
        foreach (var w in wanted)
        {
          var match = before.Where(s => Math.Abs(s - w) < 0.001).Cast<double?>().FirstOrDefault();
          if (!match.HasValue)
          {
            results.Add(new Dictionary<string, object?> { ["station"] = w, ["deleted"] = false, ["reason"] = "not an added station of this region" });
            continue;
          }
          try { region.DeleteStation(match.Value); results.Add(new Dictionary<string, object?> { ["station"] = match.Value, ["deleted"] = true }); }
          catch (Exception ex) { results.Add(new Dictionary<string, object?> { ["station"] = match.Value, ["deleted"] = false, ["reason"] = $"{ex.GetType().Name}: {ex.Message}" }); }
        }
      }
      if (operation != "list" && rebuild) rebuildError = TryRebuild(corridor);
      double[] after;
      try { after = region.AdditionalStations() ?? Array.Empty<double>(); } catch { after = Array.Empty<double>(); }
      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridor.Name,
        ["baselineIndex"] = baselineIndex,
        ["regionName"] = region.Name,
        ["operation"] = operation,
        ["addedStationsBefore"] = before.OrderBy(x => x).ToList(),
        ["addedStations"] = after.OrderBy(x => x).ToList(),
        ["results"] = results,
        ["rebuilt"] = operation != "list" && rebuild && rebuildError == null,
        ["rebuildError"] = rebuildError,
      };
    });
  }

  // =========================================================================
  // checkBowties
  // =========================================================================

  private sealed class BuiltLink
  {
    public double Station;
    public double Ax, Ay, Bx, By;
    public double OuterOffset;
    public string Codes = "";
  }

  public static Task<object?> CheckBowtiesAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var side = (PluginRuntime.GetOptionalString(parameters, "side") ?? "both").Trim().ToLowerInvariant();
    var start = PluginRuntime.GetOptionalDouble(parameters, "startStation");
    var end = PluginRuntime.GetOptionalDouble(parameters, "endStation");
    var linkCode = PluginRuntime.GetOptionalString(parameters, "linkCode") ?? "Top";
    var minOffset = PluginRuntime.GetOptionalDouble(parameters, "minOffset") ?? 0.0;
    var code = PluginRuntime.GetOptionalString(parameters, "code") ?? "Daylight";
    var maxListed = PluginRuntime.GetOptionalInt(parameters, "maxListed") ?? 25;
    var tolerance = PluginRuntime.GetOptionalDouble(parameters, "tolerance") ?? 0.005;
    if (side is not ("left" or "right" or "both"))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "side must be left, right or both.");
    if (tolerance < 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "tolerance must be >= 0.");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForRead);
      var baseline = GetBaseline(corridor, baselineIndex);
      double[] all;
      try { all = baseline.SortedStations() ?? Array.Empty<double>(); }
      catch { all = Array.Empty<double>(); }
      if (all.Length == 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_STATE", $"Corridor '{corridor.Name}' has no applied stations - rebuild it first.");
      var a = start ?? all[0];
      var b = end ?? all[^1];
      var stations = all.Where(s => s >= a - StationTolerance && s <= b + StationTolerance).ToArray();

      var sides = side == "both" ? new[] { "left", "right" } : new[] { side };
      var links = sides.ToDictionary(sd => sd, _ => new List<BuiltLink>());
      var codePts = sides.ToDictionary(sd => sd, _ => new List<(double S, double X, double Y, double Tx, double Ty)>());
      var valleyCount = sides.ToDictionary(sd => sd, _ => 0);
      var unreadable = 0;

      foreach (var s in stations)
      {
        AppliedAssembly applied;
        try { applied = baseline.GetAppliedAssemblyAtStation(s); }
        catch { unreadable++; continue; }
        double tx = 0, ty = 0;
        try { var d = baseline.GetDirectionAtStation(s); var l = Math.Sqrt(d.X * d.X + d.Y * d.Y); if (l > 1e-12) { tx = d.X / l; ty = d.Y / l; } } catch { }

        foreach (var sd in sides)
        {
          var sign = sd == "left" ? -1.0 : 1.0;
          foreach (CalculatedLink link in applied.Links)
          {
            if (!string.IsNullOrWhiteSpace(linkCode) && !HasCode(link.CorridorCodes, linkCode)) continue;
            var pts = link.CalculatedPoints.Cast<CalculatedPoint>().ToList();
            if (pts.Count < 2) continue;
            var o0 = sign * pts[0].StationOffsetElevationToBaseline.Y;
            var o1 = sign * pts[^1].StationOffsetElevationToBaseline.Y;
            if (o0 < -1e-6 || o1 < -1e-6) continue;
            if (Math.Max(o0, o1) <= minOffset + 1e-9) continue;
            var p0 = pts[0].XYZ; var p1 = pts[^1].XYZ;
            if (Math.Abs(p0.X - p1.X) < 1e-9 && Math.Abs(p0.Y - p1.Y) < 1e-9) continue;
            links[sd].Add(new BuiltLink
            {
              Station = s, Ax = p0.X, Ay = p0.Y, Bx = p1.X, By = p1.Y, OuterOffset = Math.Max(o0, o1),
              Codes = string.Join(",", ReadCodes(link.CorridorCodes)),
            });
          }
          CalculatedPoint? best = null;
          var bestOff = double.NegativeInfinity;
          foreach (CalculatedPoint p in applied.Points)
          {
            var o = sign * p.StationOffsetElevationToBaseline.Y;
            if (o <= 1e-9) continue;
            if (HasCode(p.CorridorCodes, "Valley")) valleyCount[sd]++;
            if (HasCode(p.CorridorCodes, code) && o > bestOff) { best = p; bestOff = o; }
          }
          if (best != null) codePts[sd].Add((s, best.XYZ.X, best.XYZ.Y, tx, ty));
        }
      }

      var report = new List<Dictionary<string, object?>>();
      var totalCrossings = 0;
      var totalLoops = 0;
      foreach (var sd in sides)
      {
        var list = links[sd];
        var crossings = new List<Dictionary<string, object?>>();
        var pairs = new HashSet<(double, double)>();
        var count = 0;
        for (var i = 0; i < list.Count; i++)
        {
          var p = list[i];
          var pminx = Math.Min(p.Ax, p.Bx); var pmaxx = Math.Max(p.Ax, p.Bx);
          var pminy = Math.Min(p.Ay, p.By); var pmaxy = Math.Max(p.Ay, p.By);
          for (var j = i + 1; j < list.Count; j++)
          {
            var q = list[j];
            if (Math.Abs(q.Station - p.Station) < StationTolerance) continue;
            if (Math.Max(q.Ax, q.Bx) < pminx || Math.Min(q.Ax, q.Bx) > pmaxx || Math.Max(q.Ay, q.By) < pminy || Math.Min(q.Ay, q.By) > pmaxy) continue;
            if (!ProperCross(p.Ax, p.Ay, p.Bx, p.By, q.Ax, q.Ay, q.Bx, q.By, tolerance, out var x, out var y)) continue;
            count++;
            pairs.Add((Math.Min(p.Station, q.Station), Math.Max(p.Station, q.Station)));
            if (crossings.Count < maxListed)
              crossings.Add(new Dictionary<string, object?>
              {
                ["stations"] = new[] { p.Station, q.Station },
                ["linkCodes"] = new[] { p.Codes, q.Codes },
                ["x"] = Math.Round(x, 4), ["y"] = Math.Round(y, 4),
              });
          }
        }

        // built feature line of `code`
        var fl = codePts[sd];
        var selfCross = 0;
        var loops = new List<Dictionary<string, object?>>();
        for (var i = 0; i + 1 < fl.Count; i++)
        {
          for (var j = i + 2; j + 1 < fl.Count; j++)
          {
            if (fl[j].S - fl[i + 1].S > 200) break;
            if (!ProperCross(fl[i].X, fl[i].Y, fl[i + 1].X, fl[i + 1].Y, fl[j].X, fl[j].Y, fl[j + 1].X, fl[j + 1].Y, tolerance, out var x, out var y)) continue;
            selfCross++;
            if (loops.Count < maxListed)
              loops.Add(new Dictionary<string, object?> { ["fromStation"] = fl[i].S, ["toStation"] = fl[j + 1].S, ["x"] = Math.Round(x, 4), ["y"] = Math.Round(y, 4) });
          }
        }
        var backward = new List<double>();
        for (var i = 0; i + 1 < fl.Count; i++)
        {
          var dx = fl[i + 1].X - fl[i].X; var dy = fl[i + 1].Y - fl[i].Y;
          if (Math.Sqrt(dx * dx + dy * dy) <= Math.Max(tolerance, 1e-6)) continue;
          if (dx * fl[i].Tx + dy * fl[i].Ty < -1e-6) backward.Add(fl[i].S);
        }
        totalCrossings += count;
        totalLoops += selfCross;
        report.Add(new Dictionary<string, object?>
        {
          ["side"] = sd,
          ["linksChecked"] = list.Count,
          ["linkCrossings"] = count,
          ["stationPairsCrossing"] = pairs.Count,
          ["crossings"] = crossings,
          ["featureLine"] = new Dictionary<string, object?>
          {
            ["code"] = code,
            ["points"] = fl.Count,
            ["selfCrossings"] = selfCross,
            ["backwardSteps"] = backward.Count,
            ["backwardAtStations"] = backward.Take(maxListed).ToList(),
            ["loops"] = loops,
          },
          ["valleyPoints"] = valleyCount[sd],
        });
      }

      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridor.Name,
        ["baselineIndex"] = baselineIndex,
        ["startStation"] = a,
        ["endStation"] = b,
        ["stationsRead"] = stations.Length - unreadable,
        ["linkCode"] = linkCode,
        ["minOffset"] = minOffset,
        ["clean"] = totalCrossings == 0 && totalLoops == 0,
        ["sides"] = report,
        ["tolerance"] = tolerance,
        ["method"] = "Built sections only: every pair of links from different applied stations is tested for a plan crossing more than `tolerance` from the link ends; the feature line is the outermost point with the code at each applied station, joined by straight chords.",
      };
    });
  }

  private static IEnumerable<string> ReadCodes(CorridorCodeCollection? codes)
  {
    if (codes == null) yield break;
    foreach (var c in codes) if (c != null) yield return c.ToString()!;
  }

  /// <summary>True when the two segments cross at a point more than tol (metres) from all four endpoints.</summary>
  private static bool ProperCross(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy, double tol, out double x, out double y)
  {
    x = y = 0;
    var rx = bx - ax; var ry = by - ay; var sx = dx - cx; var sy = dy - cy;
    var den = rx * sy - ry * sx;
    if (Math.Abs(den) < 1e-12) return false;
    var qx = cx - ax; var qy = cy - ay;
    var t = (qx * sy - qy * sx) / den;
    var u = (qx * ry - qy * rx) / den;
    var lr = Math.Sqrt(rx * rx + ry * ry);
    var ls = Math.Sqrt(sx * sx + sy * sy);
    var et = lr > 1e-12 ? Math.Max(1e-9, tol / lr) : 1.0;
    var eu = ls > 1e-12 ? Math.Max(1e-9, tol / ls) : 1.0;
    if (t <= et || t >= 1 - et || u <= eu || u >= 1 - eu) return false;
    x = ax + t * rx; y = ay + t * ry;
    return true;
  }
}
