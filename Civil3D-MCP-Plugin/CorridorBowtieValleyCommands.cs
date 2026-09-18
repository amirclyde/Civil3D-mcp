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
      var warnings = new List<string>();

      double[] stations;
      try { stations = baseline.SortedStations() ?? Array.Empty<double>(); }
      catch (Exception ex) { throw new JsonRpcDispatchException("CIVIL3D.INVALID_STATE", $"Corridor '{corridor.Name}' has no applied stations ({ex.Message}) - rebuild it first."); }
      if (stations.Length < 4)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_STATE", $"Corridor '{corridor.Name}' has too few applied stations - rebuild it first.");

      // ---- template stations: last applied station at/before the bowtie, first at/after it
      var sA = templateBefore ?? stations.Where(s => s <= startStation + StationTolerance).DefaultIfEmpty(double.NaN).Max();
      var sB = templateAfter ?? stations.Where(s => s >= endStation - StationTolerance).DefaultIfEmpty(double.NaN).Min();
      if (double.IsNaN(sA) || double.IsNaN(sB))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"No applied station before {startStation:0.###} or after {endStation:0.###} to read the leg sections from.");

      var sign = side == "left" ? -1.0 : 1.0;
      var legA = BuildLeg(baseline, stations, sA, sign, linkCode, extension, before: true, warnings);
      var legB = BuildLeg(baseline, stations, sB, sign, linkCode, extension, before: false, warnings);

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

      // ---- angle point or curve: where does the direction change?
      var (turnStart, turnEnd) = TurnZone(baseline, sA, sB);
      var bendType = turnEnd - turnStart > 0.5 ? "curve" : "angle_point";
      double startX = piX, startY = piY, tStart = 0.0;
      if (bendType == "curve")
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
        warnings.Add($"The bend is curved between {turnStart:0.###} and {turnEnd:0.###}: the valley starts at the curve centre ({startX:0.###}, {startY:0.###}), where the curve's sections converge.");
      }

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

      var raw = new List<(double X, double Y, double? Z, double T, double U, string Kind)>
      {
        (startX, startY, null, tStart, 0.0, bendType == "curve" ? "curve_centre" : "pi"),
      };
      foreach (var (t, u) in path)
      {
        var qx = piX + bx * t + mx * u; var qy = piY + by * t + my * u;
        raw.Add((qx, qy, legA.Z(qx, qy), t, u, "exact"));
      }
      var poly = SimplifyPolyline(raw, 0.005);

      // ---- do the two legs carry the same section? (a region boundary with a different drain / lane level at the
      // bend puts the valley far off the bisector and the construction below is no longer reliable)
      var sPiA = legA.SO(piX, piY).S;
      var sPiB = legB.SO(piX, piY).S;
      var levelA = legA.Cz + legA.Grade * sPiA + legA.Template[0].Dz;
      var levelB = legB.Cz + legB.Grade * sPiB + legB.Template[0].Dz;
      var levelStep = levelA - levelB;
      var hingeA = HingeOffset(legA);
      var hingeB = HingeOffset(legB);
      var mismatchReasons = new List<string>();
      if (Math.Abs(levelStep) > 0.05) mismatchReasons.Add($"the inside edge levels differ by {levelStep:0.###} m at the PI");
      if (Math.Abs(legA.Start - legB.Start) > 0.05) mismatchReasons.Add($"the inside links start at different offsets ({legA.Start:0.###} / {legB.Start:0.###} m)");
      if (hingeA.HasValue && hingeB.HasValue && Math.Abs(hingeA.Value - hingeB.Value) > 0.05) mismatchReasons.Add($"the hinges are at different offsets ({hingeA:0.###} / {hingeB:0.###} m)");
      var maxSideways = path.Max(p => Math.Abs(p.U));
      var firstExact = path[0];
      var skewed = Math.Abs(firstExact.U) > 0.25 * firstExact.T + 0.3;
      if (skewed) mismatchReasons.Add($"the valley runs {Math.Abs(firstExact.U):0.##} m off the bisector {firstExact.T:0.#} m out from the PI");
      var templatesMatch = mismatchReasons.Count == 0;
      if (!templatesMatch)
      {
        var why = string.Join("; ", mismatchReasons);
        if (!dryRun && !allowMismatch)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_STATE",
            $"The two legs of this bend do not carry the same inside section ({why}). This is a section change at the bend (e.g. a region boundary " +
            "with a different drain), not a plain bowtie: a valley between two different templates is not the clean mitre the clip daylight expects. " +
            "Nothing was changed. Run with dryRun to inspect it, move the section change off the bend, or pass allowMismatch true to build it anyway.");
        warnings.Add($"The legs do not carry the same inside section ({why}); review the valley before using it.");
      }

      // ---- where the valley meets the daylight surface
      CivilSurface? surface = null;
      string? surfaceUsed = null;
      if (!string.IsNullOrWhiteSpace(surfaceName)) (surface, surfaceUsed) = FindSurface(civilDoc, transaction, surfaceName!);
      else (surface, surfaceUsed) = SurfaceFromTargets(baseline, transaction, piStation);
      Dictionary<string, object?>? meet = null;
      double? meetA = null, meetB = null;
      if (surface == null)
      {
        warnings.Add("No daylight surface (pass surfaceName, or map a surface target in the region): the point where the valley meets the ground was not computed.");
      }
      else
      {
        var hit = ValleyMeetsSurface(poly, legA, surface);
        if (hit == null) warnings.Add($"The valley does not reach surface '{surfaceUsed}' within the section templates (+{extension:0.##} m): widen extension, or check the templates.");
        else
        {
          var (hx, hy, hz) = hit.Value;
          meetA = sA + legA.SO(hx, hy).S;
          meetB = sB + legB.SO(hx, hy).S;
          if (bendType == "angle_point" && (meetA > piStation + 0.01 || meetB < piStation - 0.01))
          {
            warnings.Add($"The valley meets the ground at stations {meetA:0.###} / {meetB:0.###}, on the wrong side of the PI ({piStation:0.###}): no stations were added there.");
            addStations = false;
          }
          meet = new Dictionary<string, object?>
          {
            ["x"] = hx, ["y"] = hy, ["z"] = hz,
            ["surfaceName"] = surfaceUsed,
            ["stationIncoming"] = Math.Round(meetA.Value, 4),
            ["stationOutgoing"] = Math.Round(meetB.Value, 4),
            ["offsetIncoming"] = Math.Round(legA.SO(hx, hy).O, 4),
            ["offsetOutgoing"] = Math.Round(legB.SO(hx, hy).O, 4),
          };
        }
      }

      // ---- write: alignment + stations
      Dictionary<string, object?>? alignmentInfo = null;
      var stationsAdded = new List<Dictionary<string, object?>>();
      var name = string.IsNullOrWhiteSpace(valleyName)
        ? $"{RegionAtStation(baseline, piStation)?.Name ?? "BT"} Valley {side[0].ToString().ToUpperInvariant()}"
        : valleyName!.Trim();
      if (!dryRun && createAlignment)
      {
        foreach (ObjectId aid in civilDoc.GetAlignmentIds())
        {
          var existing = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, aid, OpenMode.ForRead);
          if (string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase))
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"An alignment named '{name}' already exists. Pass another valleyName, or delete it first. Nothing was changed.");
        }
        var alignmentId = CreateValleyAlignment(civilDoc, database, transaction, name, poly, style, layer);
        var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, alignmentId, OpenMode.ForRead);
        alignmentInfo = new Dictionary<string, object?>
        {
          ["name"] = alignment.Name,
          ["handle"] = CivilObjectUtils.GetHandle(alignment),
          ["length"] = Math.Round(alignment.Length, 4),
        };
      }
      if (!dryRun && addStations && meetA.HasValue && meetB.HasValue)
      {
        foreach (var (st, leg) in new[] { (meetA.Value, "incoming"), (meetB.Value, "outgoing") })
        {
          var region = RegionAtStation(baseline, st);
          if (region == null || st <= region.StartStation + RegionMargin || st >= region.EndStation - RegionMargin)
          {
            stationsAdded.Add(new Dictionary<string, object?> { ["station"] = Math.Round(st, 4), ["leg"] = leg, ["added"] = false, ["reason"] = "on a region boundary or outside the regions" });
            continue;
          }
          if (stations.Any(s => Math.Abs(s - st) < 0.01))
          {
            stationsAdded.Add(new Dictionary<string, object?> { ["station"] = Math.Round(st, 4), ["leg"] = leg, ["added"] = false, ["reason"] = "an applied station is already there" });
            continue;
          }
          try
          {
            region.AddStation(st, "Bowtie valley meets daylight");
            stationsAdded.Add(new Dictionary<string, object?> { ["station"] = Math.Round(st, 4), ["leg"] = leg, ["region"] = region.Name, ["added"] = true });
          }
          catch (Exception ex)
          {
            stationsAdded.Add(new Dictionary<string, object?> { ["station"] = Math.Round(st, 4), ["leg"] = leg, ["added"] = false, ["reason"] = $"{ex.GetType().Name}: {ex.Message}" });
          }
        }
      }

      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridor.Name,
        ["baselineIndex"] = baselineIndex,
        ["side"] = side,
        ["dryRun"] = dryRun,
        ["bend"] = new Dictionary<string, object?>
        {
          ["type"] = bendType,
          ["piX"] = piX, ["piY"] = piY,
          ["piStation"] = Math.Round(piStation, 4),
          ["deflectionDeg"] = Math.Round(deflection, 4),
          ["turnZone"] = new[] { Math.Round(turnStart, 3), Math.Round(turnEnd, 3) },
        },
        ["legs"] = new[] { LegInfo(legA, "incoming"), LegInfo(legB, "outgoing") },
        ["valley"] = new Dictionary<string, object?>
        {
          ["start"] = bendType == "curve" ? "curve_centre" : "pi",
          ["pointCount"] = poly.Count,
          ["exactSamples"] = path.Count,
          ["maxSidewaysFromBisector"] = Math.Round(maxSideways, 4),
          ["templatesMatch"] = templatesMatch,
          ["levelStepAtPi"] = Math.Round(levelStep, 4),
          ["length"] = Math.Round(PolylineLength(poly), 4),
          ["points"] = poly.Select(p => new Dictionary<string, object?>
          {
            ["x"] = Math.Round(p.X, 4), ["y"] = Math.Round(p.Y, 4),
            ["z"] = p.Z.HasValue ? Math.Round(p.Z.Value, 4) : null,
            ["t"] = Math.Round(p.T, 3), ["u"] = Math.Round(p.U, 4), ["kind"] = p.Kind,
          }).ToList(),
        },
        ["meetsDaylight"] = meet,
        ["alignment"] = alignmentInfo,
        ["stationsAdded"] = stationsAdded,
        ["rebuilt"] = false,
        ["nextStep"] = alignmentInfo == null ? null :
          $"Give the region an assembly with UTNM_LaneDaylightClip on the {side} side, map its ClipTarget to alignment '{name}' (target_mapping_set, targetType alignment) and rebuild; then run bowtie_check.",
        ["warnings"] = warnings,
      };
    });
  }

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
    if (angles.Count < 2 || acc < 1e-9) return (a, a);
    var s5 = angles.First(x => x.A >= 0.05 * acc).S;
    var s95 = angles.First(x => x.A >= 0.95 * acc).S;
    return (s5, s95);
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
