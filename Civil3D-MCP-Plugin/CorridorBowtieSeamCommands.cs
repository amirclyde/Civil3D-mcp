using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using K = Utnm.BowtieKernel;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

/// <summary>
/// Bowtie fixing, part 3: bowtieSeam. The geometry is decided by the bowtie kernel (bowtie-kernel/BowtieKernel, plain C#,
/// tested without Civil 3D); this file only moves data. It reads a snapshot of one bend out of the drawing - the baseline as
/// dense samples, every applied section's UNCLIPPED inside link chain, the daylight surface - hands it to
/// K.SeamSolver.Solve, and reports the result. On a real run it writes the seam and the cap as two siteless alignments
/// (both go on the clip subassembly's ClipTarget, option Nearest) and adds the two meet stations.
///
/// Works for angle points and for curves, in cut, in fill, and across a change between them. It never computes anything
/// from a clipped section: if a section in range already carries a Valley point it refuses and says which.
/// </summary>
public static partial class CorridorBowtieCommands
{
  private const string SeamDescriptionTag = "bowtie-seam v2";

  public static Task<object?> BowtieSeamAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var startStation = PluginRuntime.GetRequiredDouble(parameters, "startStation");
    var endStation = PluginRuntime.GetRequiredDouble(parameters, "endStation");
    var sideArg = (PluginRuntime.GetOptionalString(parameters, "side") ?? "").Trim().ToLowerInvariant();
    var linkCode = PluginRuntime.GetOptionalString(parameters, "linkCode") ?? "Top";
    var surfaceName = PluginRuntime.GetOptionalString(parameters, "surfaceName");
    var extension = PluginRuntime.GetOptionalDouble(parameters, "extension") ?? 3.0;
    var step = PluginRuntime.GetOptionalDouble(parameters, "step") ?? 0.1;
    var capInset = PluginRuntime.GetOptionalDouble(parameters, "capInset") ?? 0.05;
    var searchMargin = PluginRuntime.GetOptionalDouble(parameters, "searchMargin") ?? 60.0;
    var maxLevelStep = PluginRuntime.GetOptionalDouble(parameters, "maxLevelStep") ?? 0.30;
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    var addStations = PluginRuntime.GetOptionalBool(parameters, "addStations") ?? true;
    var seamName = PluginRuntime.GetOptionalString(parameters, "seamName");
    var capName = PluginRuntime.GetOptionalString(parameters, "capName");
    var style = PluginRuntime.GetOptionalString(parameters, "style");
    var layer = PluginRuntime.GetOptionalString(parameters, "layer");
    var snapshotPath = PluginRuntime.GetOptionalString(parameters, "snapshotPath");
    var allStations = PluginRuntime.GetOptionalBool(parameters, "allStations") ?? false;
    var maxLevelAdjust = PluginRuntime.GetOptionalDouble(parameters, "maxLevelAdjust") ?? 0.30;
    var writeAs = (PluginRuntime.GetOptionalString(parameters, "writeAs") ?? "feature_line").Trim().ToLowerInvariant();
    if (writeAs is not ("feature_line" or "alignment"))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "writeAs must be feature_line (default: the valley line with its levels) or alignment.");

    // feature lines carry the agreed level (mean of the two sides), which UTNM_LaneDaylightClip v0.3 reads through ClipElev;
    // an alignment is plan only, so there a level step stays a step
    var levelFromTarget = PluginRuntime.GetOptionalBool(parameters, "levelFromTarget") ?? (writeAs == "feature_line");
    if (levelFromTarget && writeAs != "feature_line")
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "levelFromTarget needs writeAs feature_line: an alignment carries no levels.");

    if (!(endStation > startStation))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "endStation must be greater than startStation: pass the station range of the bend (the curve itself, or a metre either side of an angle point).");
    if (sideArg is not ("" or "left" or "right"))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "side must be left or right, or left out (the inside of the bend is found from the baseline).");
    if (extension < 0 || step <= 0 || step > 1 || capInset <= 0 || capInset > 1 || searchMargin < 5)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "extension must be >= 0, step in (0, 1], capInset in (0, 1] and searchMargin >= 5.");

    Func<Autodesk.AutoCAD.ApplicationServices.Document, CivilDocument, Database, Transaction, object?> work = (doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, dryRun ? OpenMode.ForRead : OpenMode.ForWrite);
      var baseline = GetBaseline(corridor, baselineIndex);
      var stations = AppliedStationsOrThrow(corridor, baseline);
      var warnings = new List<string>();

      // ---------------------------------------------------------------- snapshot: baseline
      var from = Math.Max(baseline.StartStation, startStation - searchMargin);
      var to = Math.Min(baseline.EndStation, endStation + searchMargin);
      var samples = SampleBaselineForSeam(baseline, from, to);
      var turn = samples.Turn(Math.Max(from, startStation), Math.Min(to, endStation));
      if (Math.Abs(turn) < Math.PI / 180)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"The baseline turns only {turn * 180 / Math.PI:0.##} deg between {startStation:0.###} and {endStation:0.###}: there is no bend in that range.");
      var side = turn > 0 ? "left" : "right";
      if (sideArg != "" && sideArg != side)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"The {sideArg} side is the outside of this bend (it turns {side}); the bowtie is on the {side} side.");
      var sign = side == "left" ? -1.0 : 1.0;

      // ---------------------------------------------------------------- snapshot: sections (unclipped inside link chains)
      var sections = new List<K.SectionSample>();
      var clipped = new List<double>();
      var unread = new List<double>();
      foreach (var s in stations)
      {
        if (s < from - StationTolerance || s > to + StationTolerance) continue;
        var sample = ReadSeamSection(baseline, s, sign, linkCode);
        if (sample == null) { unread.Add(s); continue; }
        if (sample.Clipped) clipped.Add(s);
        sections.Add(sample);
      }
      if (sections.Count < 2)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_STATE", $"Fewer than two sections with {linkCode}-coded links on the {side} side between {from:0.###} and {to:0.###}. Rebuild the corridor, or check linkCode.");
      if (unread.Count > 0)
        warnings.Add($"{unread.Count} applied station(s) in range have no {linkCode}-coded inside links and were left out ({string.Join(", ", unread.Take(6).Select(x => x.ToString("0.###")))}{(unread.Count > 6 ? ", ..." : "")}).");

      // ---------------------------------------------------------------- snapshot: daylight surface
      CivilSurface? surface; string? surfaceUsed;
      if (!string.IsNullOrWhiteSpace(surfaceName)) (surface, surfaceUsed) = FindSurface(civilDoc, transaction, surfaceName!);
      else (surface, surfaceUsed) = SurfaceFromTargets(baseline, transaction, 0.5 * (startStation + endStation));
      Func<double, double, double?>? ground = null;
      if (surface != null)
      {
        var sf = surface;
        ground = (x, y) => { try { return sf.FindElevationAtXY(x, y); } catch { return null; } };
      }
      else warnings.Add("No daylight surface (pass surfaceName, or map a surface target in the region): the seam end is taken from where the sections stop covering the same ground.");

      if (!string.IsNullOrWhiteSpace(snapshotPath))
      {
        var snap = new K.Snapshot
        {
          Corridor = corridor.Name, Baseline = baseline.Name, BendFrom = startStation, BendTo = endStation,
          Samples = new K.Snapshot.BaselineDto { S = samples.S, X = samples.X, Y = samples.Y, Dir = samples.Th },
          Sections = sections.Select(x => new K.Snapshot.SectionDto { Station = x.Station, Z0 = x.Z0, Clipped = x.Clipped, Template = x.Template.Select(p => new[] { p.Off, p.Dz }).ToList() }).ToList(),
          Ground = ground == null ? null : SeamGroundGrid(samples, sections, sign, ground),
        };
        var json = JsonSerializer.Serialize(snap, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals });
        var written = FileBoundary.WriteAllTextAtomic(snapshotPath!, json, new UTF8Encoding(false), true, ".json");
        warnings.Add($"Snapshot written to {written} (solve it offline with: dotnet run -c Release -- solve <file>).");
      }

      if (clipped.Count > 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_STATE",
          $"{clipped.Count} section(s) in range are already clipped (they carry a Valley point): {string.Join(", ", clipped.Take(8).Select(x => x.ToString("0.###")))}{(clipped.Count > 8 ? ", ..." : "")}. " +
          "The seam is only ever computed from unclipped sections. Unmap the ClipTarget in that region (or put the stock assembly back), rebuild, and run this again. Nothing was changed.");

      // ---------------------------------------------------------------- solve
      var options = new K.SolveOptions { Step = step, CapInset = capInset, MaxLevelStep = maxLevelStep, LevelFromTarget = levelFromTarget, MaxLevelAdjust = maxLevelAdjust };
      var result = K.SeamSolver.Solve(samples, new K.SectionSet(sections, extension), ground, startStation, endStation, options, from, to);
      warnings.AddRange(result.Warnings);

      var region = RegionAtStation(baseline, result.ApexStation > 0 ? result.ApexStation : 0.5 * (startStation + endStation));
      var blocking = new List<string>();
      if (result.Status != K.SeamStatus.Ok) blocking.AddRange(result.Reasons.DefaultIfEmpty(result.Status.ToString()));
      else
      {
        if (result.LinkCrossingsAfter > 0) blocking.Add($"{result.LinkCrossingsAfter} link crossing(s) would remain after the clip");
        var wrong = result.Stations.Where(x => !x.FirstCrossingIsClip).Select(x => x.Station).ToList();
        if (wrong.Count > 0) blocking.Add($"{wrong.Count} section(s) would meet a clip line somewhere other than where they should stop ({string.Join(", ", wrong.Take(6).Select(x => x.ToString("0.###")))})");
        if (result.MaxClosure > 0.02) blocking.Add($"the two sides differ by {result.MaxClosure:0.###} m in level along the seam");
        if (region != null && result.ClipFrom.HasValue && (result.ClipFrom.Value < region.StartStation - StationTolerance || result.ClipTo!.Value > region.EndStation + StationTolerance))
          blocking.Add($"the sections to clip run {result.ClipFrom:0.###}-{result.ClipTo:0.###}, outside region '{region.Name}' ({region.StartStation:0.###}-{region.EndStation:0.###}): isolate that whole range first (region_isolate)");
      }

      var sideLetter = side[0].ToString().ToUpperInvariant();
      var baseName = $"{corridor.Name} {region?.Name ?? "BT"}";
      var seamAlignmentName = string.IsNullOrWhiteSpace(seamName) ? $"{baseName} Valley {sideLetter}" : seamName!.Trim();
      var capAlignmentName = string.IsNullOrWhiteSpace(capName) ? $"{baseName} Apex {sideLetter}" : capName!.Trim();
      var writeSeam = result.Seam.Count >= 2;
      var writeCap = result.Cap.Count >= 2;
      foreach (var (wanted, name) in new[] { (writeSeam, seamAlignmentName), (writeCap, capAlignmentName) })
      {
        if (!wanted) continue;
        var inUse = writeAs == "alignment" ? AlignmentNameInUse(civilDoc, transaction, name) : FeatureLineNameInUse(civilDoc, database, transaction, name);
        if (inUse) blocking.Add($"{(writeAs == "alignment" ? "an alignment" : "a feature line")} named '{name}' already exists (pass seamName / capName, or delete it first)");
      }

      if (!dryRun && blocking.Count > 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_STATE", $"Refused, nothing was changed: {string.Join("; ", blocking)}. Run with dryRun to inspect it.");

      // ---------------------------------------------------------------- write
      var alignments = new List<Dictionary<string, object?>>();
      var stationsAdded = new List<Dictionary<string, object?>>();
      if (!dryRun)
      {
        var addedList = new List<double>();
        if (addStations && result.MeetA.HasValue && result.MeetB.HasValue)
          foreach (var (st, leg) in new[] { (result.MeetA.Value, "incoming"), (result.MeetB.Value, "outgoing") })
          {
            var row = AddMeetStation(baseline, stations, st, leg, Array.Empty<double>());
            stationsAdded.Add(row);
            if (row["added"] is true) addedList.Add(st);
          }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string Describe(string role, string partner) =>
          $"{SeamDescriptionTag}; role={role}; corridor={corridor.Name.Replace(";", ",")}; baseline={baselineIndex}; side={side}; bend={result.BendType}; " +
          $"range={startStation.ToString("0.####", inv)}|{endStation.ToString("0.####", inv)}; step={step.ToString("0.####", inv)}; extension={extension.ToString("0.####", inv)}; " +
          $"capInset={capInset.ToString("0.####", inv)}; linkCode={linkCode}; surface={(surfaceUsed ?? "").Replace(";", ",")}; partner={partner.Replace(";", ",")}; " +
          $"stations={string.Join("|", addedList.Select(x => x.ToString("0.####", inv)))}";
        // levels: the seam's own level at each point; the apex point and the apex bar at the level the arc sections arrive at
        double firstZ = result.Seam.Select(q => q.Z).FirstOrDefault(z => !double.IsNaN(z));
        var apexZ = result.ApexZ ?? firstZ;
        var seamPts = result.Seam.Select((q, i) => new Point3d(q.X, q.Y, double.IsNaN(q.Z) ? apexZ : q.Z)).ToList();
        var capPts = result.Cap.Select(q => new Point3d(q.X, q.Y, apexZ)).ToList();
        foreach (var (wanted, name, role, partner, pts) in new[]
        {
          (writeSeam, seamAlignmentName, "seam", writeCap ? capAlignmentName : "", seamPts),
          (writeCap, capAlignmentName, "cap", writeSeam ? seamAlignmentName : "", capPts),
        })
        {
          if (!wanted) continue;
          if (writeAs == "alignment")
          {
            var poly = pts.Select(p => (p.X, p.Y, (double?)null, 0.0, 0.0, role)).ToList();
            var id = CreateValleyAlignment(civilDoc, database, transaction, name, poly, style, layer);
            var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, id, OpenMode.ForWrite);
            try { alignment.Description = Describe(role, partner); }
            catch (Exception ex) { warnings.Add($"The description of '{name}' could not be set: {ex.Message}"); }
            alignments.Add(new Dictionary<string, object?> { ["type"] = "alignment", ["role"] = role, ["name"] = alignment.Name, ["handle"] = CivilObjectUtils.GetHandle(alignment), ["length"] = Math.Round(alignment.Length, 4), ["vertices"] = pts.Count });
          }
          else
          {
            var fl = CreateSeamFeatureLine(civilDoc, database, transaction, name, pts, layer);
            try { fl.Description = Describe(role, partner); }
            catch (Exception ex) { warnings.Add($"The description of '{name}' could not be set: {ex.Message}"); }
            alignments.Add(new Dictionary<string, object?> { ["type"] = "feature_line", ["role"] = role, ["name"] = fl.Name, ["handle"] = CivilObjectUtils.GetHandle(fl), ["length"] = Math.Round(fl.Length2D, 4), ["vertices"] = pts.Count,
              ["minZ"] = Math.Round(pts.Min(p => p.Z), 3), ["maxZ"] = Math.Round(pts.Max(p => p.Z), 3) });
          }
        }
      }

      var report = JsonSerializer.SerializeToNode(K.ResultReport.Of(result),
        new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }) as JsonObject;
      if (report != null && !allStations && report["stations"] is JsonArray all)
      {
        var kept = new JsonArray();
        foreach (var node in all.ToList())
        {
          if (node is not JsonObject row) continue;
          if ((string?)row["role"] == "natural" && row["crossings"] is JsonArray c && c.Count == 0) continue;
          all.Remove(node); kept.Add(node);
        }
        report["stations"] = kept;
        report["stationsNote"] = "Only sections that are clipped or that touch a clip line are listed; pass allStations true for every section.";
      }

      var targets = alignments.Select(a => (string)a["name"]!).ToList();
      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridor.Name,
        ["baselineIndex"] = baselineIndex,
        ["dryRun"] = dryRun,
        ["side"] = side,
        ["surface"] = surfaceUsed,
        ["region"] = region == null ? null : new Dictionary<string, object?> { ["name"] = region.Name, ["start"] = Math.Round(region.StartStation, 4), ["end"] = Math.Round(region.EndStation, 4) },
        ["snapshot"] = new Dictionary<string, object?> { ["from"] = Math.Round(from, 3), ["to"] = Math.Round(to, 3), ["baselineSamples"] = samples.S.Length, ["anglePoints"] = samples.Corners.Select(c => Math.Round(c.Station, 4)).ToList(), ["sections"] = sections.Count },
        ["result"] = report,
        ["blocking"] = blocking,
        ["wouldWrite"] = dryRun ? new Dictionary<string, object?> { ["seam"] = writeSeam ? seamAlignmentName : null, ["cap"] = writeCap ? capAlignmentName : null, ["meetStations"] = new[] { result.MeetA, result.MeetB } } : null,
        ["written"] = alignments,
        ["stationsAdded"] = stationsAdded,
        ["rebuilt"] = false,
        ["levelFromTarget"] = levelFromTarget,
        ["nextStep"] = alignments.Count == 0 ? null :
          $"Give the region an assembly with UTNM_LaneDaylightClip{(levelFromTarget ? " v0.3" : "")} on the {side} side, then map ClipTarget{(levelFromTarget ? " AND ClipElev" : "")} to {string.Join(" and ", targets.Select(n => $"'{n}'"))} " +
          $"(target_mapping_set: targetType {writeAs}, targetName + targetNames, targetToOption Nearest{(levelFromTarget ? "; the same objects and option on both targets" : "")}) and rebuild; then run bowtie_check.",
        ["warnings"] = warnings,
      };
    };
    return dryRun ? CivilExecution.ReadAsync<object?>(work) : CivilExecution.WriteAsync<object?>(work);
  }

  private static bool FeatureLineNameInUse(CivilDocument civilDoc, Database database, Transaction transaction, string name)
  {
    try { FeatureLineCommands.Find(civilDoc, database, transaction, name, null, OpenMode.ForRead); return true; }
    catch (JsonRpcDispatchException ex) when (ex.Message.Contains("not found")) { return false; }
    catch (JsonRpcDispatchException) { return true; }
  }

  /// <summary>A siteless feature line through the given 3D points (siteless, so it never splits or is split by site geometry).</summary>
  private static FeatureLine CreateSeamFeatureLine(CivilDocument civilDoc, Database database, Transaction transaction, string name, List<Point3d> pts, string? layer)
  {
    var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
    var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
    var collection = new Point3dCollection();
    foreach (var p in pts)
      if (collection.Count == 0 || Math.Sqrt(Math.Pow(p.X - collection[collection.Count - 1].X, 2) + Math.Pow(p.Y - collection[collection.Count - 1].Y, 2)) > 0.001) collection.Add(p);   // no coincident vertices
    using var polyline = new Polyline3d(Poly3dType.SimplePoly, collection, false);
    var polyId = modelSpace.AppendEntity(polyline);
    transaction.AddNewlyCreatedDBObject(polyline, true);
    ObjectId flId;
    try { flId = FeatureLine.Create(name, polyId); }
    catch (Exception ex) { throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Civil 3D could not create feature line '{name}': {ex.GetType().Name}: {ex.Message}"); }
    if (!polyline.IsErased) polyline.Erase();
    var fl = CivilObjectUtils.GetRequiredObject<FeatureLine>(transaction, flId, OpenMode.ForWrite);
    if (!string.IsNullOrWhiteSpace(layer)) { try { fl.LayerId = LookupUtils.GetLayerId(database, transaction, layer); } catch { } }
    return fl;
  }

  /// <summary>The baseline between two stations as the kernel wants it (see K.SampledBaseline.FromStationFunction, which is
  /// tested offline): all Civil 3D has to answer is where each station is.</summary>
  private static K.SampledBaseline SampleBaselineForSeam(Baseline baseline, double from, double to) =>
    K.SampledBaseline.FromStationFunction(st =>
    {
      Point3d p;
      try { p = baseline.StationOffsetElevationToXYZ(new Point3d(st, 0.0, 0.0)); }
      catch (Exception ex) { throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"The baseline has no point at station {st:0.###}: {ex.Message}"); }
      if (Math.Abs(p.X) < 1e-9 && Math.Abs(p.Y) < 1e-9) throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"The baseline has no point at station {st:0.###}.");
      return new K.P2(p.X, p.Y);
    }, from, to);

  /// <summary>One applied section as the kernel wants it: baseline level and the connected inside chain of linkCode links,
  /// inner to outer, exactly as built (no extension). Null when the section has no such links on that side.</summary>
  private static K.SectionSample? ReadSeamSection(Baseline baseline, double s, double sign, string linkCode)
  {
    AppliedAssembly applied;
    try { applied = baseline.GetAppliedAssemblyAtStation(s); } catch { return null; }
    var z0 = BaselineElevation(applied);
    if (!z0.HasValue) return null;

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
    if (segs.Count == 0) return null;
    var clipped = false;
    foreach (CalculatedPoint p in applied.Points)
      if (sign * p.StationOffsetElevationToBaseline.Y > 1e-6 && HasCode(p.CorridorCodes, "Valley")) clipped = true;

    segs.Sort((u, v) => u.A.O.CompareTo(v.A.O));
    var used = new bool[segs.Count];
    var chain = new List<(double O, double Z)> { segs[0].A, segs[0].B };
    used[0] = true;
    while (true)
    {
      var end = chain[^1];
      var next = -1;
      for (var i = 0; i < segs.Count; i++)
        if (!used[i] && Math.Abs(segs[i].A.O - end.O) < 1e-4 && Math.Abs(segs[i].A.Z - end.Z) < 1e-4) { next = i; break; }
      if (next < 0)
        for (var i = 0; i < segs.Count; i++)
        {
          if (used[i] || segs[i].A.O < end.O - 1e-6) continue;
          if (next < 0 || segs[i].A.O < segs[next].A.O) next = i;
        }
      if (next < 0) break;
      used[next] = true;
      if (Math.Abs(segs[next].A.O - end.O) > 1e-4 || Math.Abs(segs[next].A.Z - end.Z) > 1e-4) chain.Add(segs[next].A);
      chain.Add(segs[next].B);
    }
    var sample = new K.SectionSample { Station = s, Z0 = z0.Value, Clipped = clipped };
    foreach (var p in chain)
    {
      if (sample.Template.Count > 0 && p.O < sample.Template[^1].Off - 1e-6) continue;
      if (sample.Template.Count > 0 && Math.Abs(p.O - sample.Template[^1].Off) < 1e-9) { sample.Template[^1] = (p.O, p.Z); continue; }
      sample.Template.Add((p.O, p.Z));
    }
    return sample.Template.Count >= 2 ? sample : null;
  }

  /// <summary>The daylight surface on a 1 m grid over the inside of the bend, for an offline snapshot.</summary>
  private static K.Snapshot.GroundGridDto SeamGroundGrid(K.SampledBaseline samples, List<K.SectionSample> sections, double sign, Func<double, double, double?> ground)
  {
    var side = sign < 0 ? K.Side.Left : K.Side.Right;
    double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
    foreach (var sec in sections)
    {
      var (c, th) = samples.At(sec.Station);
      var q = c + K.SampledBaseline.InsideNormal(th, side) * (sec.Reach + 5.0);
      x0 = Math.Min(x0, Math.Min(c.X, q.X)); x1 = Math.Max(x1, Math.Max(c.X, q.X));
      y0 = Math.Min(y0, Math.Min(c.Y, q.Y)); y1 = Math.Max(y1, Math.Max(c.Y, q.Y));
    }
    const double cell = 1.0;
    x0 = Math.Floor(x0) - cell; y0 = Math.Floor(y0) - cell;
    var nx = Math.Min(400, (int)Math.Ceiling((x1 - x0) / cell) + 2); var ny = Math.Min(400, (int)Math.Ceiling((y1 - y0) / cell) + 2);
    var z = new double?[nx * ny];
    for (var j = 0; j < ny; j++)
      for (var i = 0; i < nx; i++)
        z[j * nx + i] = ground(x0 + i * cell, y0 + j * cell);
    return new K.Snapshot.GroundGridDto { X0 = x0, Y0 = y0, Cell = cell, Nx = nx, Ny = ny, Z = z };
  }
}
