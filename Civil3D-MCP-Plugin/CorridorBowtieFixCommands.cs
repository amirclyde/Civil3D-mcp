using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using K = Utnm.BowtieKernel;
using CivilAssembly = Autodesk.Civil.DatabaseServices.Assembly;

namespace Civil3DMcpPlugin;

/// <summary>
/// Bowtie fixing, part 4: the pieces the one-call repair needs to work on any corridor.
///
///   bowtieBends  - read-only: every bend of a baseline in a station range (angle points and curves, a reverse curve as two
///                  bends), found from the baseline geometry itself (K.BendFinder, tested offline), with the region each
///                  lies in and whether that region already carries a valley repair (a mapped ClipTarget).
///   bowtieUnfix  - puts a repaired region back: clears ClipTarget / ClipElev in the region, erases the valley lines that
///                  bowtie_seam wrote (only objects carrying its description tag), removes the meet stations it added,
///                  gives the region back its parent assembly (with the same surface targets) and merges it back with the
///                  pieces it was cut from, all as recorded on the valley line when bowtie_fix made it. One transaction;
///                  the rebuild follows. Used by bowtie_fix to roll back a repair that fails half-way, and by hand.
/// </summary>
public static partial class CorridorBowtieCommands
{
  /// <summary>What the engineer should look at from a seam result: section changes, slopes made steeper than designed,
  /// benches whose fall is reversed. Null when there is nothing.</summary>
  private static List<Dictionary<string, object?>>? Highlight(K.SeamResult result)
  {
    string Ratio(double g) => Math.Abs(g) < 1e-9 ? "level" : $"1:{1 / Math.Abs(g):0.##}";
    var rows = new List<Dictionary<string, object?>>();
    foreach (var c in result.SectionChanges)
      rows.Add(new Dictionary<string, object?> { ["kind"] = "section_change", ["from"] = Math.Round(c.From, 3), ["to"] = Math.Round(c.To, 3), ["inBend"] = c.InBend, ["what"] = c.What });
    foreach (var grp in result.SlopeFlags.GroupBy(f => (f.Station, f.Kind)))
    {
      var worst = grp.OrderByDescending(f => Math.Abs(f.NewGrade) - Math.Abs(f.DesignGrade)).First();
      rows.Add(new Dictionary<string, object?>
      {
        ["kind"] = grp.Key.Kind == "steeper" ? "steeper_than_design" : "bench_fall_reversed",
        ["station"] = Math.Round(grp.Key.Station, 3),
        ["offsets"] = grp.Select(f => new[] { Math.Round(f.FromOffset, 3), Math.Round(f.ToOffset, 3) }).ToList(),
        ["design"] = grp.Key.Kind == "steeper" ? Ratio(worst.DesignGrade) : $"{worst.DesignGrade * 100:0.##}%",
        ["asBuilt"] = grp.Key.Kind == "steeper" ? Ratio(worst.NewGrade) : $"{worst.NewGrade * 100:0.##}%",
        ["action"] = grp.Key.Kind == "steeper" ? "steeper than the engineer's ratio: remedial or extra slope protection needed" : "cross-fall reversed: water runs away from the berm drain",
      });
    }
    return rows.Count == 0 ? null : rows;
  }

  /// <summary>Whether the clip part takes the level move over the whole daylight side (hinge) or on the clipped link alone
  /// (last_link), read from the clip assembly: the region's own, or the one named (the assembly bowtie_fix will swap in).
  /// A subassembly on the inside with a Spread From Hinge parameter decides it; a clip part without one is v0.3 (last link);
  /// with no clip part at all the default of the current parts (hinge) is assumed.</summary>
  private static string DetectAdjustFrom(CivilDocument civilDoc, Transaction transaction, string? assemblyName, BaselineRegion? region, string side, List<string> warnings)
  {
    ObjectId? asmId = null;
    try { asmId = !string.IsNullOrWhiteSpace(assemblyName) ? FindAssemblyId(civilDoc, transaction, assemblyName!) : region == null ? null : SafeAssemblyId(region); }
    catch (Exception ex) { warnings.Add($"Clip assembly not read ({ex.Message}); the level move is assumed spread from the hinge."); }
    if (!asmId.HasValue || asmId.Value.IsNull) return "hinge";
    var sawClipPart = false;
    try
    {
      var assembly = CivilObjectUtils.GetRequiredObject<CivilAssembly>(transaction, asmId.Value, OpenMode.ForRead);
      foreach (var group in assembly.Groups)
        foreach (ObjectId id in group.GetSubassemblyIds())
        {
          var sub = CivilObjectUtils.GetRequiredObject<Subassembly>(transaction, id, OpenMode.ForRead);
          var subSide = "";
          try { subSide = sub.Side.ToString().ToLowerInvariant(); } catch { }
          if (subSide.Length > 0 && subSide != side && subSide != "none") continue;
          var name = sub.Name ?? "";
          if (name.Contains("Clip", StringComparison.OrdinalIgnoreCase)) sawClipPart = true;
          foreach (var item in sub.ParamsLong)
          {
            var display = Civil3DCompatibility.GetPropertyValue(item, "DisplayName")?.ToString() ?? "";
            var key = Civil3DCompatibility.GetPropertyValue(item, "Key")?.ToString() ?? "";
            if (!display.Equals("Spread From Hinge", StringComparison.OrdinalIgnoreCase) && !key.Equals("SpreadFromHinge", StringComparison.OrdinalIgnoreCase)) continue;
            var value = Convert.ToInt64(Civil3DCompatibility.GetPropertyValue(item, "Value") ?? 10);
            return value == 11 ? "last_link" : "hinge";
          }
        }
    }
    catch (Exception ex) { warnings.Add($"Clip assembly parameters not read ({ex.Message}); the level move is assumed spread from the hinge."); }
    if (sawClipPart) { warnings.Add("The clip part has no Spread From Hinge parameter (UTNM_LaneDaylightClip v0.3 or older): only the clipped link takes the level move."); return "last_link"; }
    return "hinge";
  }

  // =========================================================================
  // bowtieBends
  // =========================================================================

  public static Task<object?> BowtieBendsAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var startStation = PluginRuntime.GetOptionalDouble(parameters, "startStation");
    var endStation = PluginRuntime.GetOptionalDouble(parameters, "endStation");
    var minTurn = PluginRuntime.GetOptionalDouble(parameters, "minTurnDegrees") ?? 3.0;
    var margin = PluginRuntime.GetOptionalDouble(parameters, "margin") ?? 1.0;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForRead);
      var baseline = GetBaseline(corridor, baselineIndex);
      var from = Math.Max(baseline.StartStation, startStation ?? baseline.StartStation);
      var to = Math.Min(baseline.EndStation, endStation ?? baseline.EndStation);
      if (!(to > from)) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "endStation must be greater than startStation.");
      var samples = SampleBaselineForSeam(baseline, from, to);
      var bends = K.BendFinder.Find(samples, from, to, minTurn);
      var rows = new List<Dictionary<string, object?>>();
      foreach (var b in bends)
      {
        // an angle point is reported a metre either side of the PI, as bowtie_seam wants it
        var corner = samples.Corners.Where(c => c.Station >= b.From - 0.2 && c.Station <= b.To + 0.2).OrderByDescending(c => Math.Abs(c.Jump)).FirstOrDefault();
        var isCorner = corner.Jump != 0 && Math.Abs(corner.Jump) >= 0.5 * Math.Abs(b.TurnDegrees * Math.PI / 180);
        var s0 = isCorner ? corner.Station - margin : b.From;
        var s1 = isCorner ? corner.Station + margin : b.To;
        var region = RegionAtStation(baseline, 0.5 * (s0 + s1));
        var clipMapped = false;
        if (region != null)
          try
          {
            var infos = region.GetTargets();
            for (var i = 0; i < infos.Count; i++)
              if (infos[i].LogicalName is "ClipTarget" && infos[i].TargetIds.Count > 0) clipMapped = true;
          }
          catch { }
        rows.Add(new Dictionary<string, object?>
        {
          ["startStation"] = Math.Round(Math.Max(from, s0), 4),
          ["endStation"] = Math.Round(Math.Min(to, s1), 4),
          ["side"] = b.Side == K.Side.Left ? "left" : "right",
          ["type"] = isCorner ? "angle_point" : "curve",
          ["turnDegrees"] = Math.Round(Math.Abs(b.TurnDegrees), 3),
          ["region"] = region?.Name,
          ["repaired"] = clipMapped,
        });
      }
      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridor.Name,
        ["baselineIndex"] = baselineIndex,
        ["range"] = new[] { Math.Round(from, 4), Math.Round(to, 4) },
        ["bends"] = rows,
        ["note"] = "Every bend of the baseline in the range, from its geometry (whether it has a bowtie is decided per bend by bowtie_seam / bowtie_fix). repaired = the region already has a mapped ClipTarget.",
      };
    });
  }

  // =========================================================================
  // bowtieUnfix
  // =========================================================================

  public static Task<object?> BowtieUnfixAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var regionName = PluginRuntime.GetRequiredString(parameters, "regionName");
    var merge = PluginRuntime.GetOptionalBool(parameters, "merge") ?? true;
    var restoreAssembly = PluginRuntime.GetOptionalBool(parameters, "restoreAssembly") ?? true;
    var rebuild = PluginRuntime.GetOptionalBool(parameters, "rebuild") ?? true;
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    // what bowtie_fix knows when it rolls back a repair that never got as far as writing a valley line
    var ctxParent = PluginRuntime.GetOptionalString(parameters, "parentRegion");
    var ctxAssembly = PluginRuntime.GetOptionalString(parameters, "parentAssembly");
    var ctxBefore = PluginRuntime.GetOptionalString(parameters, "splitBefore");
    var ctxAfter = PluginRuntime.GetOptionalString(parameters, "splitAfter");

    Func<Autodesk.AutoCAD.ApplicationServices.Document, CivilDocument, Database, Transaction, object?> work = (doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, dryRun ? OpenMode.ForRead : OpenMode.ForWrite);
      var baseline = GetBaseline(corridor, baselineIndex);
      var region = FindRegion(baseline, null, regionName);
      var warnings = new List<string>();

      // ---- what is mapped on the clip targets, and what bowtie_seam recorded on it
      var cleared = new List<string>();
      var valleyIds = new List<ObjectId>();
      SubassemblyTargetInfoCollection infos;
      try { infos = region.GetTargets(); }
      catch (Exception ex) { throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"The targets of region '{region.Name}' could not be read: {ex.Message}"); }
      for (var i = 0; i < infos.Count; i++)
      {
        if (infos[i].LogicalName is not ("ClipTarget" or "ClipElev") || infos[i].TargetIds.Count == 0) continue;
        foreach (ObjectId id in infos[i].TargetIds) if (!id.IsNull && !valleyIds.Contains(id)) valleyIds.Add(id);
        cleared.Add($"{infos[i].SubassemblyName}:{infos[i].LogicalName}");
      }
      var record = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      var toErase = new List<(ObjectId Id, string Name, string Kind)>();
      foreach (var id in valleyIds)
      {
        var obj = transaction.GetObject(id, OpenMode.ForRead);
        var description = obj switch { FeatureLine fl => fl.Description, Alignment al => al.Description, _ => null } ?? "";
        var name = CivilObjectUtils.GetName(obj) ?? id.Handle.ToString();
        if (!description.StartsWith(SeamDescriptionTag, StringComparison.Ordinal) && !description.StartsWith("bowtie-valley", StringComparison.Ordinal))
        {
          warnings.Add($"'{name}' is mapped on a clip target but was not written by bowtie_seam: it is unmapped and kept.");
          continue;
        }
        foreach (var part in description.Split(';'))
        {
          var kv = part.Split('=', 2);
          if (kv.Length == 2 && !record.ContainsKey(kv[0].Trim())) record[kv[0].Trim()] = kv[1].Trim();
        }
        toErase.Add((id, name, obj is FeatureLine ? "feature_line" : "alignment"));
      }
      string? Rec(string key, string? given) => !string.IsNullOrWhiteSpace(given) ? given : record.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
      var parent = Rec("parent", ctxParent);
      var parentAssembly = Rec("parentAssembly", ctxAssembly);
      var before = Rec("before", ctxBefore);
      var after = Rec("after", ctxAfter);
      var stations = (Rec("stations", null) ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN).Where(d => !double.IsNaN(d)).ToList();

      var plan = new Dictionary<string, object?>
      {
        ["clearTargets"] = cleared,
        ["eraseValleyLines"] = toErase.Select(x => x.Name).ToList(),
        ["deleteStations"] = stations.Select(x => Math.Round(x, 4)).ToList(),
        ["restoreAssembly"] = restoreAssembly ? parentAssembly : null,
        ["mergeWith"] = merge ? new[] { before, after }.Where(x => !string.IsNullOrEmpty(x)).ToList() : new List<string?>(),
        ["restoreName"] = merge ? parent : null,
      };
      if (dryRun)
        return new Dictionary<string, object?> { ["corridorName"] = corridor.Name, ["regionName"] = region.Name, ["dryRun"] = true, ["plan"] = plan, ["warnings"] = warnings };

      // ---- 1. clear the clip targets BEFORE erasing the objects on them (a target left pointing at an erased object
      //         empties the whole region on the next rebuild)
      if (cleared.Count > 0)
      {
        for (var i = 0; i < infos.Count; i++)
          if (infos[i].LogicalName is "ClipTarget" or "ClipElev") infos[i].TargetIds = new ObjectIdCollection();
        region.SetTargets(infos);
      }
      // ---- 2. the valley lines
      var erased = new List<string>();
      foreach (var (id, name, _) in toErase)
      {
        try { var ent = transaction.GetObject(id, OpenMode.ForWrite); ent.Erase(); erased.Add(name); }
        catch (Exception ex) { warnings.Add($"'{name}' could not be erased: {ex.Message}"); }
      }
      // ---- 3. the meet stations bowtie_seam added
      var deleted = new List<double>();
      if (stations.Count > 0)
      {
        double[] added;
        try { added = region.AdditionalStations() ?? Array.Empty<double>(); } catch { added = Array.Empty<double>(); }
        foreach (var st in stations)
        {
          var match = added.Cast<double?>().FirstOrDefault(a => Math.Abs(a!.Value - st) < 1e-3);
          if (!match.HasValue) continue;
          try { region.DeleteStation(match.Value); deleted.Add(Math.Round(match.Value, 4)); }
          catch (Exception ex) { warnings.Add($"Station {st:0.###} could not be removed: {ex.Message}"); }
        }
      }
      // ---- 4. the parent assembly, with the surface targets the region has now
      string? restored = null;
      if (restoreAssembly && !string.IsNullOrWhiteSpace(parentAssembly) && !string.Equals(AssemblyName(transaction, SafeAssemblyId(region)), parentAssembly, StringComparison.OrdinalIgnoreCase))
      {
        var asmId = FindAssemblyId(civilDoc, transaction, parentAssembly!);
        ObjectIdCollection? surfaces = null;
        var now = region.GetTargets();
        for (var i = 0; i < now.Count; i++)
          if (now[i].TargetType == SubassemblyLogicalNameType.Surface && now[i].TargetIds.Count > 0) { surfaces = now[i].TargetIds; break; }
        region.AssemblyId = asmId;
        if (surfaces != null)
        {
          var fresh = region.GetTargets();
          var set = false;
          for (var i = 0; i < fresh.Count; i++)
          {
            if (fresh[i].TargetType != SubassemblyLogicalNameType.Surface || fresh[i].TargetIds.Count > 0) continue;
            var ids = new ObjectIdCollection(); foreach (ObjectId id in surfaces) ids.Add(id);
            fresh[i].TargetIds = ids; set = true;
          }
          if (set) region.SetTargets(fresh);
        }
        restored = parentAssembly;
      }
      // ---- 5. merge back with the pieces it was cut from
      Dictionary<string, object?>? merged = null;
      if (merge && (!string.IsNullOrEmpty(before) || !string.IsNullOrEmpty(after)))
      {
        var mid = IndexOfName(baseline, region.Name);
        var first = !string.IsNullOrEmpty(before) ? IndexOfName(baseline, before!) : mid;
        var last = !string.IsNullOrEmpty(after) ? IndexOfName(baseline, after!) : mid;
        if (first >= 0 && last > first && mid >= first && mid <= last && last - first <= 2)
        {
          var regions = baseline.BaselineRegions;
          var a = regions[first]; var b = regions[last];
          try
          {
            a.Merge(a, b);
            if (!string.IsNullOrWhiteSpace(parent)) a.Name = UniqueRegionName(baseline, parent!, a);
            merged = new Dictionary<string, object?> { ["name"] = a.Name, ["startStation"] = a.StartStation, ["endStation"] = a.EndStation };
          }
          catch (Exception ex) { warnings.Add($"The regions could not be merged back ({ex.GetType().Name}: {ex.Message}); they are left split."); }
        }
        else warnings.Add($"The pieces '{before}' / '{after}' recorded for '{region.Name}' are not next to it any more: the regions are left split.");
      }
      else if (merge && !string.IsNullOrWhiteSpace(parent) && !string.Equals(region.Name, parent, StringComparison.OrdinalIgnoreCase))
      {
        try { region.Name = UniqueRegionName(baseline, parent!, region); } catch { }
      }

      var rebuildError = rebuild ? TryRebuild(corridor) : null;
      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridor.Name,
        ["regionName"] = regionName,
        ["dryRun"] = false,
        ["targetsCleared"] = cleared,
        ["valleyLinesErased"] = erased,
        ["stationsDeleted"] = deleted,
        ["assemblyRestored"] = restored,
        ["merged"] = merged,
        ["rebuilt"] = rebuild && rebuildError == null,
        ["rebuildError"] = rebuildError,
        ["regions"] = ListRegions(baseline, transaction),
        ["warnings"] = warnings,
      };
    };
    return dryRun ? CivilExecution.ReadAsync<object?>(work) : CivilExecution.WriteAsync<object?>(work);
  }
}
