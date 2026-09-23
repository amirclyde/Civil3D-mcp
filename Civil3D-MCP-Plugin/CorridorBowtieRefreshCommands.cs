using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using K = Utnm.BowtieKernel;

namespace Civil3DMcpPlugin;

/// <summary>
/// Bowtie fixing, part 5: bowtieRefreshSeams. After a design change (slopes, widths, benches, the profile, the ground) the
/// valley lines written by bowtie_seam / bowtie_fix are stale. This re-solves every repaired bend of a corridor (or the one
/// region named) on the design as it now is, in ONE transaction:
///   1. check the built corridor over each repair (the state before);
///   2. unmap the valley lines from ClipTarget / ClipElev in the repaired regions and rebuild, so their sections are the
///      clip assembly's own, unclipped;
///   3. per repair: check the clip assembly still matches the stock assembly it stands in for (the sections either side of
///      each region boundary with its parent region must agree - the copy is not a reference, a design change has to be
///      made in both); solve the valley again with what the repair recorded; compare it with the existing line;
///   4. replace the valley lines that moved by more than the tolerance (the old ones kept aside, renamed) and move the meet
///      stations;
///   5. map everything back and rebuild; check every repair again. A refreshed repair that is not clean is put back as it
///      was (old line, old stations) and rebuilt once more;
///   6. erase the old lines of the refreshed repairs.
/// Nothing relies on a transaction rollback (Civil 3D does not restore every subassembly target on abort - seen live): a dry
/// run, and anything that throws, puts every change back by hand, checks the mappings are as before, and commits. Repairs
/// that cannot be refreshed (stale clip assembly, design conflict, the bend moved out of its region) keep their old valley
/// line and are reported with the reason. The valleys are compared without their level run-on past the end, and the meet
/// stations as solved (the record keeps both, 'meet' and 'runOn', since 23 Sep 2026).
/// </summary>
public static partial class CorridorBowtieCommands
{
  private sealed class SeamMapping
  {
    public string Region = "";
    public double RegionStart;
    public string Subassembly = "";
    public string Logical = "";
    public List<ObjectId> Ids = new();
    public SubassemblyTargetToOption? Option;
  }

  private sealed class SeamGroup
  {
    public ObjectId SeamId;
    public ObjectId? CapId;
    public string SeamName = "";
    public string? CapName;
    public string Description = "";
    public Dictionary<string, string> Record = new(StringComparer.OrdinalIgnoreCase);
    public List<SeamMapping> Mappings = new();
    public List<string> Pieces = new();
    public double From, To;
    public string Side = "";
    public bool LevelFromTarget;

    public string Outcome = "";
    public string Message = "";
    public List<string> Reasons = new();
    public List<string> Warnings = new();
    public List<Dictionary<string, object?>> Drift = new();
    public K.SeamResult? Result;
    public List<Point3d> NewSeam = new(), NewCap = new();
    public double? Plan, Level, StationShift;
    public List<double> OldStations = new();
    /// <summary>The meet stations of the existing valley as solved (not all may have been added as corridor stations).</summary>
    public List<double> OldMeet = new();
    /// <summary>The parent assembly taken from the neighbours where the recorded one was stale (single-region repairs).</summary>
    public string? ParentCorrected;
    public List<double> NewStations = new();
    public bool Update;
    // applied changes (for the revert)
    public ObjectId? NewSeamId, NewCapId;
    public List<(double Station, string Region)> DeletedStations = new();
    public List<(double Station, string Region)> AddedStations = new();
    public bool StationsOut, OldRenamed;
    public List<Dictionary<string, object?>> AssemblyChecks = new();
    public Dictionary<string, object?>? CheckBefore, CheckAfter;
  }

  public static Task<object?> BowtieRefreshSeamsAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var regionName = PluginRuntime.GetOptionalString(parameters, "regionName");
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    var tolerance = PluginRuntime.GetOptionalDouble(parameters, "tolerance") ?? 0.01;
    var driftTolerance = PluginRuntime.GetOptionalDouble(parameters, "driftTolerance") ?? 0.01;
    var gradeTolerance = PluginRuntime.GetOptionalDouble(parameters, "gradeTolerance") ?? 0.002;
    var checkTolerance = PluginRuntime.GetOptionalDouble(parameters, "checkTolerance") ?? 0.005;
    var searchMargin = PluginRuntime.GetOptionalDouble(parameters, "searchMargin") ?? 60.0;
    var maxLevelAdjust = PluginRuntime.GetOptionalDouble(parameters, "maxLevelAdjust") ?? 0.30;
    var maxLevelStep = PluginRuntime.GetOptionalDouble(parameters, "maxLevelStep") ?? 0.30;
    var acceptOffSurfaceEnds = PluginRuntime.GetOptionalBool(parameters, "acceptOffSurfaceEnds") ?? false;
    var ignoreAssemblyDrift = PluginRuntime.GetOptionalBool(parameters, "ignoreAssemblyDrift") ?? false;
    var adjustFromArg = (PluginRuntime.GetOptionalString(parameters, "adjustFrom") ?? "auto").Trim().ToLowerInvariant();
    var levelRuleArg = (PluginRuntime.GetOptionalString(parameters, "levelRule") ?? "no_steeper").Trim().ToLowerInvariant();
    if (adjustFromArg is not ("auto" or "hinge" or "last_link"))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "adjustFrom must be auto, hinge or last_link.");
    if (levelRuleArg is not ("no_steeper" or "mean"))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "levelRule must be no_steeper or mean.");
    if (tolerance < 0 || driftTolerance < 0 || checkTolerance < 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "tolerance, driftTolerance and checkTolerance must be >= 0.");

    Func<Autodesk.AutoCAD.ApplicationServices.Document, CivilDocument, Database, Transaction, object?> work = (doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForWrite);
      var baseline = GetBaseline(corridor, baselineIndex);
      var warnings = new List<string>();
      var legacy = new List<string>();
      var foreign = new List<string>();

      // ---------------------------------------------------------------- 0. the repairs
      var groups = CollectSeamGroups(baseline, transaction, legacy, foreign);
      if (!string.IsNullOrWhiteSpace(regionName))
      {
        if (IndexOfName(baseline, regionName!) < 0)
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Region '{regionName}' was not found on baseline '{baseline.Name}'.");
        groups = groups.Where(g => g.Pieces.Any(p => string.Equals(p, regionName, StringComparison.OrdinalIgnoreCase))).ToList();
        if (groups.Count == 0)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_STATE", $"Region '{regionName}' carries no valley line written by bowtie_seam / bowtie_fix (feature line tagged '{SeamDescriptionTag}'). Nothing was changed.");
      }
      if (groups.Count == 0)
        return new Dictionary<string, object?>
        {
          ["corridorName"] = corridor.Name, ["dryRun"] = dryRun, ["repairs"] = new List<object>(),
          ["summary"] = new Dictionary<string, object?> { ["repairs"] = 0 },
          ["message"] = "No repaired bend with a feature-line valley on this baseline: nothing to refresh.",
          ["legacyValleys"] = legacy, ["otherClipTargets"] = foreign, ["warnings"] = warnings,
        };

      double[] Stations() { try { return baseline.SortedStations() ?? Array.Empty<double>(); } catch { return Array.Empty<double>(); } }
      Dictionary<string, object?> Check(SeamGroup g)
      {
        var (report, crossings, loops, read) = CheckBuilt(baseline, Stations(), g.From - 5, g.To + 5, new[] { g.Side }, g.Record.GetValueOrDefault("linkCode") is { Length: > 0 } lc ? lc : "Top", 0, "Daylight", 10, checkTolerance);
        var sideRow = report.FirstOrDefault();
        return new Dictionary<string, object?>
        {
          ["clean"] = crossings == 0 && loops == 0, ["linkCrossings"] = crossings, ["loops"] = loops, ["stationsRead"] = read,
          ["valleyPoints"] = sideRow?["valleyPoints"], ["from"] = Math.Round(g.From - 5, 3), ["to"] = Math.Round(g.To + 5, 3),
        };
      }

      // ---------------------------------------------------------------- 1. the state before
      foreach (var g in groups) g.CheckBefore = Check(g);

      // Civil 3D does not restore every subassembly target when a transaction is aborted (seen live: a region's clip
      // mapping on its second subassembly stayed empty after a rollback, and the drawing did not even show as changed). So
      // nothing here relies on a rollback: a dry run, and any failure part-way, put every change back by hand - valley
      // lines, meet stations, target mappings - rebuild, check that the mappings are as they were, and commit.
      var allMappings = groups.SelectMany(g => g.Mappings).ToList();
      void RevertGroup(SeamGroup g)
      {
        foreach (var (st, _) in g.AddedStations)
        {
          var holder = RegionHoldingStation(baseline, st);
          if (holder != null) try { holder.Value.Region.DeleteStation(holder.Value.Station); } catch (Exception ex) { g.Warnings.Add($"New meet station {st:0.###} could not be removed: {ex.Message}"); }
        }
        g.AddedStations.Clear();
        PutBackStations(baseline, g);
        foreach (var id in new[] { g.NewSeamId, g.NewCapId }.Where(x => x.HasValue))
          try { transaction.GetObject(id!.Value, OpenMode.ForWrite).Erase(); } catch (Exception ex) { g.Warnings.Add($"The new valley line could not be erased: {ex.Message}"); }
        g.NewSeamId = null; g.NewCapId = null;
        if (g.OldRenamed)
        {
          try
          {
            CivilObjectUtils.GetRequiredObject<FeatureLine>(transaction, g.SeamId, OpenMode.ForWrite).Name = g.SeamName;
            if (g.CapId.HasValue) CivilObjectUtils.GetRequiredObject<FeatureLine>(transaction, g.CapId.Value, OpenMode.ForWrite).Name = g.CapName!;
            g.OldRenamed = false;
          }
          catch (Exception ex) { g.Warnings.Add($"The old valley line could not be given its name back: {ex.Message}"); }
        }
        g.Update = false;
      }
      List<string> MappingsDiffer()
      {
        var diff = new List<string>();
        foreach (var byRegion in allMappings.GroupBy(m => m.Region, StringComparer.OrdinalIgnoreCase))
        {
          var ri = IndexOfName(baseline, byRegion.Key);
          if (ri < 0) { diff.Add($"region '{byRegion.Key}' is gone"); continue; }
          SubassemblyTargetInfoCollection infos;
          try { infos = baseline.BaselineRegions[ri].GetTargets(); } catch (Exception ex) { diff.Add($"'{byRegion.Key}': targets not readable ({ex.Message})"); continue; }
          foreach (var m in byRegion)
          {
            var now = Enumerable.Range(0, infos.Count).Select(i => infos[i])
              .FirstOrDefault(t => string.Equals(t.SubassemblyName, m.Subassembly, StringComparison.OrdinalIgnoreCase) && t.LogicalName == m.Logical);
            var ids = now == null ? new List<ObjectId>() : now.TargetIds.Cast<ObjectId>().ToList();
            if (!ids.OrderBy(x => x.Handle.Value).SequenceEqual(m.Ids.OrderBy(x => x.Handle.Value))) diff.Add($"'{m.Region}' {m.Subassembly}:{m.Logical}");
          }
        }
        return diff;
      }
      Dictionary<string, object?> RestoreAll()
      {
        foreach (var g in groups) RevertGroup(g);
        var restoreErrors = new List<string>();
        try { SetMappings(baseline, allMappings, m => m.Ids.ToList()); } catch (Exception ex) { restoreErrors.Add($"mappings: {ex.Message}"); }
        var rb = TryRebuild(corridor);
        if (rb != null) restoreErrors.Add($"rebuild: {rb}");
        var differ = MappingsDiffer();
        return new Dictionary<string, object?>
        {
          ["restored"] = restoreErrors.Count == 0 && differ.Count == 0,
          ["mappingsNotAsBefore"] = differ.Count > 0 ? differ : null,
          ["errors"] = restoreErrors.Count > 0 ? restoreErrors : null,
        };
      }

      try
      {
      // ---------------------------------------------------------------- 2. unmap, rebuild: unclipped sections
      SetMappings(baseline, allMappings, m => new List<ObjectId>());
      // the meet stations a repair added are sections placed by its own solve: solved again with them the answer shifts a
      // little each time. They come out for the solve, as they were not there when the repair was made, and go back in
      // (the old ones, or the new ones for a refreshed repair) before the corridor is rebuilt for good.
      foreach (var g in groups)
      {
        g.OldStations = ParseStationList(g.Record.GetValueOrDefault("stations"));
        // the meet stations the solve gave (recorded since 23 Sep 2026); 'stations' holds only those that could be added
        var meet = ParseStationList(g.Record.GetValueOrDefault("meet"));
        g.OldMeet = meet.Count > 0 ? meet : g.OldStations.ToList();
        foreach (var st in g.OldStations)
        {
          var holder = RegionHoldingStation(baseline, st);
          if (holder == null) { g.Warnings.Add($"The recorded meet station {st:0.###} is no longer an added station."); continue; }
          try { holder.Value.Region.DeleteStation(holder.Value.Station); g.DeletedStations.Add((holder.Value.Station, holder.Value.Region.Name)); g.StationsOut = true; }
          catch (Exception ex) { g.Warnings.Add($"Meet station {st:0.###} could not be taken out for the solve: {ex.Message}"); }
        }
      }
      var rebuildError = TryRebuild(corridor);
      if (rebuildError != null)
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"The corridor could not be rebuilt with the valley lines unmapped: {rebuildError}. Nothing was changed.");

      // ---------------------------------------------------------------- 3. per repair: assembly drift, solve, compare
      foreach (var g in groups)
      {
        (g.Drift, g.AssemblyChecks) = AssemblyDrift(baseline, transaction, g, Stations(), gradeTolerance, driftTolerance);
        if (g.Drift.Count > 0 && !ignoreAssemblyDrift)
        {
          g.Outcome = "refused_stale_assembly";
          g.Message = $"The clip assembly of {string.Join(" / ", g.Pieces.Select(p => $"'{p}'"))} no longer matches the stock assembly next to it " +
                      $"({string.Join("; ", g.Drift.Take(3).Select(d => $"{d["side"]} side, {d["clipAssembly"]} at {d["clipStation"]} against {d["stockAssembly"]} at {d["stockStation"]}: {d["what"]}"))}). " +
                      "Make the same design change in the clip assembly (it is a copy, not a reference), then refresh again. The old valley line is kept.";
          continue;
        }
        var range = ParseRange(g.Record.GetValueOrDefault("range"));
        if (range == null)
        {
          g.Outcome = "refused"; g.Message = "The valley line has no recorded bend range: repair it again with bowtie_fix (redo + force). The old valley line is kept.";
          continue;
        }
        var apexPiece = RegionAtStation(baseline, 0.5 * (range.Value.A + range.Value.B));
        string? surface = g.Record.GetValueOrDefault("surface");
        if (!string.IsNullOrWhiteSpace(surface))
        {
          try { FindSurface(civilDoc, transaction, surface!); }
          catch (JsonRpcDispatchException) { g.Warnings.Add($"The recorded surface '{surface}' is gone: the region's surface target is used."); surface = null; }
        }
        SeamSolveOutcome solved;
        try
        {
          solved = SolveSeam(civilDoc, transaction, corridor, baseline, baselineIndex, new SeamSolveArgs
          {
            Start = range.Value.A, End = range.Value.B, SideArg = g.Side,
            LinkCode = g.Record.GetValueOrDefault("linkCode") is { Length: > 0 } lc ? lc : "Top",
            SurfaceName = string.IsNullOrWhiteSpace(surface) ? null : surface,
            Extension = RecordDouble(g.Record, "extension") ?? 3.0, Step = RecordDouble(g.Record, "step") ?? 0.1, CapInset = RecordDouble(g.Record, "capInset") ?? 0.05,
            SearchMargin = searchMargin, MaxLevelStep = maxLevelStep, MaxLevelAdjust = maxLevelAdjust, AcceptOffSurfaceEnds = acceptOffSurfaceEnds,
            AdjustFrom = adjustFromArg, ClipAssembly = apexPiece != null ? AssemblyName(transaction, SafeAssemblyId(apexPiece)) : null,
            LevelRule = levelRuleArg, LevelFromTarget = g.LevelFromTarget,
          });
        }
        catch (JsonRpcDispatchException ex)
        {
          g.Outcome = "refused"; g.Message = $"Could not be solved on the design as it now is: {ex.Message.Replace(" Nothing was changed.", "")} The old valley line is kept.";
          continue;
        }
        g.Warnings.AddRange(solved.Warnings);
        var r = g.Result = solved.Result;
        if (r.Status is K.SeamStatus.NoBowtie or K.SeamStatus.NoBend)
        {
          g.Outcome = "not_needed";
          g.Message = "On the design as it now is this bend has no bowtie: the repair is no longer needed (remove it with bowtie_unfix if you want). The old valley line is kept.";
          continue;
        }
        var blocking = new List<string>();
        if (r.Status != K.SeamStatus.Ok) blocking.AddRange(r.Reasons.DefaultIfEmpty(r.Status.ToString()));
        else
        {
          if (r.LinkCrossingsAfter > 0) blocking.Add($"{r.LinkCrossingsAfter} link crossing(s) would remain after the clip");
          var wrong = r.Stations.Where(x => !x.FirstCrossingIsClip).Select(x => x.Station).ToList();
          if (wrong.Count > 0) blocking.Add($"{wrong.Count} section(s) would meet a clip line somewhere other than where they should stop");
          if (r.MaxClosure > 0.02 && !g.LevelFromTarget) blocking.Add($"the two sides differ by {r.MaxClosure:0.###} m in level along the valley");
          if (r.ClipFrom.HasValue && (r.ClipFrom.Value < g.From - StationTolerance || r.ClipTo!.Value > g.To + StationTolerance))
            blocking.Add($"the valley now needs sections clipped from {r.ClipFrom:0.###} to {r.ClipTo:0.###}, beyond the repaired region(s) {g.From:0.###}-{g.To:0.###} (the bend or its reach moved): repair it again with bowtie_fix (redo + force)");
        }
        if (blocking.Count > 0)
        {
          g.Outcome = "refused"; g.Reasons = blocking;
          g.Message = $"Refused on the design as it now is: {string.Join("; ", blocking.Select(b => b.TrimEnd('.')))}. The old valley line is kept.";
          continue;
        }
        (g.NewSeam, g.NewCap) = SeamLinePoints(r);
        if (g.NewSeam.Count < 2)
        {
          g.Outcome = "refused"; g.Message = "The solve gave no valley line to write. The old valley line is kept.";
          continue;
        }
        var oldSeam = FeatureLinePoints(transaction, g.SeamId);
        // compared without the level run-on past the valley's end: it only gives the sections in the region's margin a line
        // to find beyond their own daylight, and its length jumps (0.1 m up to 30 m) with small changes of the design
        var oldRunOn = RecordDouble(g.Record, "runOn");
        var oldCmp = K.Refresh.WithoutRunOn(oldSeam.Select(P).ToList(), oldRunOn);
        var newCmp = K.Refresh.WithoutRunOn(g.NewSeam.Select(P).ToList(), oldRunOn.HasValue ? r.OvershootLength : null);
        var (plan, level) = K.Refresh.Deviation(oldCmp, newCmp);
        if (g.CapId.HasValue || g.NewCap.Count >= 2)
        {
          if (g.CapId.HasValue && g.NewCap.Count >= 2)
          {
            var (cp, cl) = K.Refresh.Deviation(FeatureLinePoints(transaction, g.CapId.Value).Select(P).ToList(), g.NewCap.Select(P).ToList());
            plan = Math.Max(plan, cp); level = Math.Max(level, cl);
          }
          else plan = double.PositiveInfinity;   // a cap appears or goes
        }
        g.NewStations = new[] { r.MeetA, r.MeetB }.Where(x => x.HasValue).Select(x => x!.Value).OrderBy(x => x).ToList();
        var shift = K.Refresh.StationShift(g.OldMeet, g.NewStations);
        g.Plan = plan; g.Level = level; g.StationShift = shift;
        if (plan <= tolerance && level <= tolerance && (shift <= tolerance || g.OldMeet.Count == 0))
        {
          g.Outcome = "unchanged";
          g.Message = $"The valley on the design as it now is lies within {tolerance} m of the existing one: nothing to do.";
          continue;
        }
        g.Update = true;
      }

      // ---------------------------------------------------------------- 4. replace the lines that moved, move the meet stations
      foreach (var g in groups.Where(x => x.Update))
      {
        var oldSeam = CivilObjectUtils.GetRequiredObject<FeatureLine>(transaction, g.SeamId, OpenMode.ForWrite);
        var layer = oldSeam.LayerId;
        oldSeam.Name = UniqueFeatureLineName(civilDoc, database, transaction, $"{g.SeamName} (before refresh)");
        g.OldRenamed = true;
        var capName = g.CapName ?? (g.NewCap.Count >= 2 ? g.SeamName.Replace(" Valley ", " Apex ") : null);
        if (g.CapId.HasValue)
        {
          var oldCap = CivilObjectUtils.GetRequiredObject<FeatureLine>(transaction, g.CapId.Value, OpenMode.ForWrite);
          oldCap.Name = UniqueFeatureLineName(civilDoc, database, transaction, $"{g.CapName} (before refresh)");
        }
        var inv = CultureInfo.InvariantCulture;
        // meet stations: the old ones are out already (step 2), the new ones go in
        var applied = Stations();
        var ignore = g.DeletedStations.Select(x => x.Station).ToArray();
        foreach (var (st, leg) in new[] { (g.Result!.MeetA, "incoming"), (g.Result!.MeetB, "outgoing") })
        {
          if (!st.HasValue) continue;
          var row = AddMeetStation(baseline, applied, st.Value, leg, ignore);
          if (row["added"] is true) g.AddedStations.Add((st.Value, (string)row["region"]!));
          else g.Warnings.Add($"The new {leg} meet station {st.Value:0.###} was not added ({row["reason"]}).");
        }
        var description = SetRecordField(g.Description, "stations", string.Join("|", g.AddedStations.Select(x => x.Station.ToString("0.####", inv))));
        description = SetRecordField(description, "meet", string.Join("|", g.NewStations.Select(x => x.ToString("0.####", inv))));
        description = SetRecordField(description, "runOn", g.Result!.OvershootLength.ToString("0.####", inv));
        if (g.ParentCorrected != null) description = SetRecordField(description, "parentAssembly", g.ParentCorrected);
        var seamFl = CreateSeamFeatureLine(civilDoc, database, transaction, g.SeamName, g.NewSeam, null);
        seamFl.LayerId = layer;
        seamFl.Description = SetRecordField(description, "partner", g.NewCap.Count >= 2 ? capName! : "");
        g.NewSeamId = seamFl.ObjectId;
        if (g.NewCap.Count >= 2)
        {
          var capFl = CreateSeamFeatureLine(civilDoc, database, transaction, capName!, g.NewCap, null);
          capFl.LayerId = layer;
          capFl.Description = SetRecordField(SetRecordField(description, "role", "cap"), "partner", g.SeamName);
          g.NewCapId = capFl.ObjectId;
        }
      }

      // the repairs that keep their valley line get their own meet stations back
      foreach (var g in groups.Where(x => !x.Update)) PutBackStations(baseline, g);

      // ---------------------------------------------------------------- 5. map back, rebuild, check
      List<ObjectId> Remapped(SeamMapping m, bool useNew)
      {
        var g = groups.First(x => x.Mappings.Contains(m));
        if (!useNew || !g.Update || g.NewSeamId == null) return m.Ids.ToList();
        var ids = new List<ObjectId> { g.NewSeamId.Value };
        if (g.NewCapId.HasValue) ids.Add(g.NewCapId.Value);
        return ids;
      }
      SetMappings(baseline, allMappings, m => Remapped(m, true));
      rebuildError = TryRebuild(corridor);
      if (rebuildError != null)
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"The corridor could not be rebuilt with the refreshed valley lines: {rebuildError}. Nothing was changed.");
      foreach (var g in groups) g.CheckAfter = Check(g);

      // ---------------------------------------------------------------- 6. put back any refreshed repair that is not clean
      var failing = groups.Where(g => g.Update && g.CheckAfter?["clean"] is false).ToList();
      if (failing.Count > 0)
      {
        foreach (var g in failing)
        {
          RevertGroup(g);
          g.Outcome = "reverted";
          g.Message = $"The refreshed valley still left {g.CheckAfter?["linkCrossings"]} link crossing(s) / {g.CheckAfter?["loops"]} loop(s) in the built corridor, so the repair was put back as it was. Look at it in section, or repair it again with bowtie_fix (redo + force).";
        }
        SetMappings(baseline, allMappings, m => Remapped(m, true));
        rebuildError = TryRebuild(corridor);
        if (rebuildError != null)
          throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"The corridor could not be rebuilt after putting back {failing.Count} repair(s): {rebuildError}. Nothing was changed.");
        foreach (var g in failing) g.CheckAfter = Check(g);
      }

      // ---------------------------------------------------------------- 7. the old lines of the refreshed repairs go
      //                                                                     (a dry run instead puts everything back)
      foreach (var g in groups.Where(x => x.Update))
      {
        var moved = $"Valley moved by up to {g.Plan:0.###} m in plan and {g.Level:0.###} m in level{(g.StationShift is double sh && !double.IsInfinity(sh) ? $", meet stations by {sh:0.###} m" : "")}";
        if (dryRun)
        {
          g.Outcome = "would_refresh";
          g.Message = $"{moved}: a real run replaces it and maps it again; the built corridor was clean with it.";
          continue;
        }
        foreach (var id in new[] { (ObjectId?)g.SeamId, g.CapId }.Where(x => x.HasValue))
          try { transaction.GetObject(id!.Value, OpenMode.ForWrite).Erase(); } catch (Exception ex) { g.Warnings.Add($"The old valley line could not be erased: {ex.Message}"); }
        g.OldRenamed = false;
        g.Outcome = "refreshed";
        g.Message = $"{moved}: replaced, mapped again, the built corridor is clean.";
      }
      Dictionary<string, object?>? restore = null;
      if (dryRun) restore = RestoreAll();
      else
      {
        var differ = MappingsDiffer().Where(d => !groups.Any(g => g.Outcome == "refreshed" && g.Pieces.Any(p => d.Contains($"'{p}'")))).ToList();
        if (differ.Count > 0) warnings.Add($"Target mappings not as they were on repairs that were not refreshed: {string.Join(", ", differ)}.");
      }

      var rows = groups.Select(g => new Dictionary<string, object?>
      {
        ["regions"] = g.Pieces,
        ["side"] = g.Side,
        ["valleyLine"] = g.SeamName,
        ["capLine"] = g.NewCapId.HasValue || g.CapId.HasValue ? (g.CapName ?? g.SeamName.Replace(" Valley ", " Apex ")) : null,
        ["outcome"] = g.Outcome,
        ["message"] = g.Message,
        ["moved"] = g.Plan.HasValue ? new Dictionary<string, object?> { ["plan"] = Round(g.Plan), ["level"] = Round(g.Level), ["meetStations"] = Round(g.StationShift) } : null,
        ["meetStations"] = new Dictionary<string, object?>
        {
          ["before"] = g.OldMeet.Select(x => Math.Round(x, 4)).ToList(),
          ["after"] = g.Plan.HasValue ? g.NewStations.Select(x => Math.Round(x, 4)).ToList() : null,
          // the ones that went in as corridor stations (one right next to an applied station is not added)
          ["added"] = g.Outcome == "refreshed" ? g.AddedStations.Select(x => Math.Round(x.Station, 4)).ToList() : null,
        },
        ["parentAssemblyCorrected"] = g.ParentCorrected,
        ["assemblyDrift"] = g.Drift.Count > 0 ? g.Drift : null,
        ["assemblyChecks"] = g.AssemblyChecks,
        ["highlight"] = g.Result != null && g.Outcome is "refreshed" or "would_refresh" or "unchanged" ? Highlight(g.Result) : null,
        ["checks"] = g.Result == null ? null : new Dictionary<string, object?>
        {
          ["status"] = g.Result.Status.ToString(), ["linkCrossingsBefore"] = g.Result.LinkCrossingsBefore, ["linkCrossingsAfter"] = g.Result.LinkCrossingsAfter,
          ["maxLevelAdjust"] = Math.Round(g.Result.MaxLevelAdjust, 4), ["maxSlopeChange"] = Math.Round(g.Result.MaxSlopeChange, 4), ["maxClosure"] = Math.Round(g.Result.MaxClosure, 4),
        },
        ["builtBefore"] = g.CheckBefore,
        ["builtAfter"] = g.CheckAfter,
        ["reasons"] = g.Reasons.Count > 0 ? g.Reasons : null,
        ["warnings"] = g.Warnings,
      }).ToList();
      int Count(string o) => groups.Count(g => g.Outcome == o);
      var summary = new Dictionary<string, object?>
      {
        ["repairs"] = groups.Count, ["refreshed"] = Count("refreshed"), ["wouldRefresh"] = Count("would_refresh"), ["unchanged"] = Count("unchanged"), ["notNeeded"] = Count("not_needed"),
        ["refused"] = Count("refused"), ["staleAssembly"] = Count("refused_stale_assembly"), ["reverted"] = Count("reverted"),
        ["cleanAfter"] = groups.Count(g => g.CheckAfter?["clean"] is true),
      };
      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridor.Name,
        ["baselineIndex"] = baselineIndex,
        ["dryRun"] = dryRun,
        ["restore"] = restore,
        ["summary"] = summary,
        ["message"] = (dryRun ? (restore?["restored"] is true
            ? "Dry run (everything done, then put back by hand and checked - the corridor is as it was; Civil 3D shows the drawing as changed): "
            : "DRY RUN COULD NOT PUT EVERYTHING BACK - see restore; do not save before checking: ") : "") +
          $"{summary["refreshed"]} refreshed, {summary["wouldRefresh"]} would be refreshed, {summary["unchanged"]} unchanged, {summary["notNeeded"]} no longer needed, {summary["refused"]} refused, " +
          $"{summary["staleAssembly"]} with a stale clip assembly, {summary["reverted"]} put back; {summary["cleanAfter"]} of {groups.Count} clean in the built corridor.",
        ["repairs"] = rows,
        ["legacyValleys"] = legacy.Count > 0 ? legacy : null,
        ["otherClipTargets"] = foreign.Count > 0 ? foreign : null,
        ["warnings"] = warnings,
      };
      }
      catch (Exception ex)
      {
        // put everything back by hand and commit that (see above: a rollback would not restore every target)
        var restoredAfterError = RestoreAll();
        return new Dictionary<string, object?>
        {
          ["corridorName"] = corridor.Name,
          ["baselineIndex"] = baselineIndex,
          ["dryRun"] = dryRun,
          ["failed"] = true,
          ["error"] = ex.Message,
          ["restore"] = restoredAfterError,
          ["message"] = restoredAfterError["restored"] is true
            ? $"The refresh stopped ({ex.Message}). Every change was put back by hand and checked: the corridor is as it was."
            : $"The refresh stopped ({ex.Message}) and NOT everything could be put back (see restore): check the repairs with bowtie_bends / bowtie_check before saving.",
          ["repairs"] = groups.Select(g => new Dictionary<string, object?> { ["regions"] = g.Pieces, ["valleyLine"] = g.SeamName, ["outcome"] = g.Outcome, ["message"] = g.Message, ["warnings"] = g.Warnings }).ToList(),
          ["warnings"] = warnings,
        };
      }
    };
    // always a committed transaction: changes are put back by hand where needed (never left to a rollback)
    return CivilExecution.WriteAsync<object?>(work);
  }

  private static K.P3 P(Point3d p) => new(p.X, p.Y, p.Z);

  /// <summary>Puts the meet stations taken out for the solve back into the regions that held them.</summary>
  private static void PutBackStations(Baseline baseline, SeamGroup g)
  {
    if (!g.StationsOut) return;
    g.StationsOut = false;
    foreach (var (st, reg) in g.DeletedStations)
    {
      var ri = IndexOfName(baseline, reg);
      if (ri < 0) { g.Warnings.Add($"Meet station {st:0.###} could not be put back: region '{reg}' is gone."); continue; }
      try { baseline.BaselineRegions[ri].AddStation(st, "Bowtie valley meets daylight"); }
      catch (Exception ex) { g.Warnings.Add($"Meet station {st:0.###} could not be put back: {ex.Message}"); }
    }
  }
  private static double? Round(double? v) => v.HasValue ? (double.IsInfinity(v.Value) ? null : Math.Round(v.Value, 4)) : null;

  private static List<Point3d> FeatureLinePoints(Transaction transaction, ObjectId id)
  {
    var fl = CivilObjectUtils.GetRequiredObject<FeatureLine>(transaction, id, OpenMode.ForRead);
    return fl.GetPoints(FeatureLinePointType.AllPoints).Cast<Point3d>().ToList();
  }

  /// <summary>The points a valley line and its apex bar are written with: the seam at its own levels (the apex point at the
  /// level the converging sections arrive at), the bar level at that apex level.</summary>
  private static (List<Point3d> Seam, List<Point3d> Cap) SeamLinePoints(K.SeamResult result)
  {
    var firstZ = result.Seam.Select(q => q.Z).FirstOrDefault(z => !double.IsNaN(z));
    var apexZ = result.ApexZ ?? firstZ;
    var seam = result.Seam.Select(q => new Point3d(q.X, q.Y, double.IsNaN(q.Z) ? apexZ : q.Z)).ToList();
    var cap = result.Cap.Count >= 2 ? result.Cap.Select(q => new Point3d(q.X, q.Y, apexZ)).ToList() : new List<Point3d>();
    return (seam, cap);
  }

  private static Dictionary<string, string> ParseRecord(string? description)
  {
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (string.IsNullOrWhiteSpace(description)) return map;
    foreach (var part in description.Split(';'))
    {
      var eq = part.IndexOf('=');
      if (eq <= 0) continue;
      var key = part[..eq].Trim();
      if (!map.ContainsKey(key)) map[key] = part[(eq + 1)..].Trim();
    }
    return map;
  }

  /// <summary>The description with one "key=value" part replaced (or appended).</summary>
  private static string SetRecordField(string description, string key, string value)
  {
    var parts = description.Split(';').ToList();
    var clean = value.Replace(";", ",");
    for (var i = 0; i < parts.Count; i++)
    {
      var eq = parts[i].IndexOf('=');
      if (eq > 0 && string.Equals(parts[i][..eq].Trim(), key, StringComparison.OrdinalIgnoreCase)) { parts[i] = $" {key}={clean}"; return string.Join(";", parts); }
    }
    parts.Add($" {key}={clean}");
    return string.Join(";", parts);
  }

  private static (double A, double B)? ParseRange(string? text)
  {
    if (string.IsNullOrWhiteSpace(text)) return null;
    var f = text.Split('|');
    if (f.Length != 2) return null;
    if (!double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) || !double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) || !(b > a)) return null;
    return (a, b);
  }

  private static double? RecordDouble(Dictionary<string, string> record, string key) =>
    record.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

  private static string UniqueFeatureLineName(CivilDocument civilDoc, Database database, Transaction transaction, string wanted)
  {
    if (!FeatureLineNameInUse(civilDoc, database, transaction, wanted)) return wanted;
    for (var i = 2; i < 100; i++) { var n = $"{wanted} ({i})"; if (!FeatureLineNameInUse(civilDoc, database, transaction, n)) return n; }
    return $"{wanted} {Guid.NewGuid():N}";
  }

  /// <summary>The region whose added stations include this station (within 1 mm), and the exact added station.</summary>
  private static (BaselineRegion Region, double Station)? RegionHoldingStation(Baseline baseline, double st)
  {
    var regions = baseline.BaselineRegions;
    for (var i = 0; i < regions.Count; i++)
    {
      double[] added;
      try { added = regions[i].AdditionalStations() ?? Array.Empty<double>(); } catch { continue; }
      foreach (var a in added) if (Math.Abs(a - st) < 1e-3) return (regions[i], a);
    }
    return null;
  }

  /// <summary>Every repair on the baseline: the valley line(s) bowtie_seam wrote (tagged, with their record), the regions
  /// and subassembly targets they are mapped on. A repair over two regions is one group (the regions share the line).</summary>
  private static List<SeamGroup> CollectSeamGroups(Baseline baseline, Transaction transaction, List<string> legacy, List<string> foreign)
  {
    var groups = new Dictionary<ObjectId, SeamGroup>();
    var regions = baseline.BaselineRegions;
    for (var ri = 0; ri < regions.Count; ri++)
    {
      var region = regions[ri];
      SubassemblyTargetInfoCollection infos;
      try { infos = region.GetTargets(); } catch { continue; }
      for (var i = 0; i < infos.Count; i++)
      {
        if (infos[i].LogicalName is not ("ClipTarget" or "ClipElev") || infos[i].TargetIds.Count == 0) continue;
        var ids = infos[i].TargetIds.Cast<ObjectId>().Where(x => !x.IsNull && !x.IsErased).ToList();
        ObjectId? seam = null, cap = null;
        foreach (var id in ids)
        {
          var obj = transaction.GetObject(id, OpenMode.ForRead);
          var desc = obj switch { FeatureLine fl => fl.Description, Alignment al => al.Description, _ => null } ?? "";
          var name = CivilObjectUtils.GetName(obj) ?? id.Handle.ToString();
          if (obj is FeatureLine && desc.StartsWith(SeamDescriptionTag, StringComparison.Ordinal))
          {
            var role = ParseRecord(desc).GetValueOrDefault("role");
            if (string.Equals(role, "cap", StringComparison.OrdinalIgnoreCase)) cap = id; else seam = id;
          }
          else if (obj is Alignment && desc.StartsWith("bowtie-valley", StringComparison.Ordinal)) { if (!legacy.Contains($"{region.Name}: {name}")) legacy.Add($"{region.Name}: {name}"); }
          else if (!foreign.Contains($"{region.Name}: {name}")) foreign.Add($"{region.Name}: {name}");
        }
        var key = seam ?? cap;
        if (key == null) continue;
        if (!groups.TryGetValue(key.Value, out var g))
        {
          var fl = (FeatureLine)transaction.GetObject(key.Value, OpenMode.ForRead);
          g = new SeamGroup { SeamId = key.Value, SeamName = fl.Name, Description = fl.Description ?? "" };
          g.Record = ParseRecord(g.Description);
          g.Side = (g.Record.GetValueOrDefault("side") ?? "").ToLowerInvariant();
          groups[key.Value] = g;
        }
        if (seam != null && cap != null && g.CapId == null)
        {
          g.CapId = cap;
          g.CapName = ((FeatureLine)transaction.GetObject(cap.Value, OpenMode.ForRead)).Name;
        }
        SubassemblyTargetToOption? option = null;
        if (ids.Count >= 2) try { option = infos[i].TargetToOption; } catch { }
        g.Mappings.Add(new SeamMapping { Region = region.Name, RegionStart = region.StartStation, Subassembly = infos[i].SubassemblyName, Logical = infos[i].LogicalName, Ids = ids, Option = option });
        if (infos[i].LogicalName == "ClipElev") g.LevelFromTarget = true;
      }
    }
    foreach (var g in groups.Values)
    {
      g.Pieces = g.Mappings.OrderBy(m => m.RegionStart).Select(m => m.Region).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
      var spans = g.Pieces.Select(p => IndexOfName(baseline, p)).Where(i => i >= 0).Select(i => baseline.BaselineRegions[i]).ToList();
      g.From = spans.Min(r => r.StartStation);
      g.To = spans.Max(r => r.EndStation);
      if (g.Side is not ("left" or "right"))
      {
        var sa = g.Mappings.Select(m => m.Subassembly).FirstOrDefault() ?? "";
        g.Side = System.Text.RegularExpressions.Regex.IsMatch(sa, @"(^|[\s_\-])(l|left)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "left" : "right";
      }
    }
    return groups.Values.OrderBy(g => g.From).ToList();
  }

  /// <summary>Sets the given subassembly targets of the repaired regions (ids from the function; the target option kept).</summary>
  private static void SetMappings(Baseline baseline, List<SeamMapping> mappings, Func<SeamMapping, List<ObjectId>> ids)
  {
    foreach (var byRegion in mappings.GroupBy(m => m.Region, StringComparer.OrdinalIgnoreCase))
    {
      var ri = IndexOfName(baseline, byRegion.Key);
      if (ri < 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_STATE", $"Region '{byRegion.Key}' disappeared during the refresh. Nothing was changed.");
      var region = baseline.BaselineRegions[ri];
      var infos = region.GetTargets();
      foreach (var m in byRegion)
        for (var i = 0; i < infos.Count; i++)
        {
          if (!string.Equals(infos[i].SubassemblyName, m.Subassembly, StringComparison.OrdinalIgnoreCase) || infos[i].LogicalName != m.Logical) continue;
          var list = ids(m);
          var col = new ObjectIdCollection();
          foreach (var id in list) col.Add(id);
          infos[i].TargetIds = col;
          if (list.Count >= 2 && m.Option.HasValue) try { infos[i].TargetToOption = m.Option.Value; } catch { }
        }
      region.SetTargets(infos);
    }
  }

  /// <summary>
  /// Whether the clip assembly of a repair still describes the same design as the stock assembly it stands in for (the
  /// copy is not a reference: a design change has to be made in both). Compared on the built sections, never at the shared
  /// boundary station (where Civil 3D may hand back one section for both regions): sections of the repaired region (its
  /// targets cleared, so unclipped) against sections of the parent region next to it, a few stations each side of the
  /// boundary. A pair is comparable when both have the same number of links and daylight the same way (cut or fill); then
  /// every link grade and every link width but the last (which ends at the ground) must agree. The best comparable pair
  /// decides. Every pair looked at is reported, so a pass is visible too.
  /// </summary>
  private static (List<Dictionary<string, object?>> Drift, List<Dictionary<string, object?>> Checks) AssemblyDrift(Baseline baseline, Transaction transaction,
    SeamGroup g, double[] stations, double gradeTol, double widthTol)
  {
    var found = new List<Dictionary<string, object?>>();
    var checks = new List<Dictionary<string, object?>>();
    var linkCode = g.Record.GetValueOrDefault("linkCode") is { Length: > 0 } lc ? lc : "Top";
    var pieces = new HashSet<string>(g.Pieces, StringComparer.OrdinalIgnoreCase);
    var recordedParent = g.Record.GetValueOrDefault("parentAssembly");
    var pieceParents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var piece in (g.Record.GetValueOrDefault("pieces") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries))
    {
      var f = piece.Split('~');
      if (f.Length >= 3 && f[2].Trim().Length > 0) pieceParents[f[0].Trim()] = f[2].Trim();
    }
    var regions = baseline.BaselineRegions;
    bool IsRepair(BaselineRegion r)
    {
      try { var t = r.GetTargets(); for (var i = 0; i < t.Count; i++) if (t[i].LogicalName is "ClipTarget" && t[i].TargetIds.Count > 0) return true; } catch { }
      return false;
    }
    string? A(BaselineRegion? r) => r == null ? null : AssemblyName(transaction, SafeAssemblyId(r));
    List<double> Near(BaselineRegion r, double at, int n) => stations
      .Where(s => s > r.StartStation + 0.001 && s < r.EndStation - 0.001).OrderBy(s => Math.Abs(s - at)).Take(n).ToList();
    static List<(double W, double G)> Links(K.SectionSample x)
    {
      var list = new List<(double, double)>();
      for (var i = 1; i < x.Template.Count; i++)
      {
        var w = x.Template[i].Off - x.Template[i - 1].Off;
        if (w < 1e-6) continue;
        list.Add((w, (x.Template[i].Dz - x.Template[i - 1].Dz) / w));
      }
      return list;
    }
    static int Kind(List<(double W, double G)> l) => l.Count == 0 ? 0 : l[^1].G > 0.05 ? 1 : l[^1].G < -0.05 ? -1 : 0;

    foreach (var name in g.Pieces)
    {
      var ri = IndexOfName(baseline, name);
      if (ri < 0) continue;
      var region = regions[ri];
      var recorded = pieceParents.GetValueOrDefault(name) ?? (g.Pieces.Count == 1 ? recordedParent : null);
      BaselineRegion? prev = ri > 0 ? regions[ri - 1] : null, next = ri < regions.Count - 1 ? regions[ri + 1] : null;
      // the neighbours decide which stock assembly this piece stands in for; a record naming an assembly that is on neither
      // (e.g. the older clip assembly the repair was cut from) is stale and would leave the check with nothing to compare
      var (parent, note) = ReconcileParentAssembly(baseline, transaction, name, recorded, g.Pieces);
      if (note != null) checks.Add(new Dictionary<string, object?> { ["region"] = name, ["recordedParent"] = recorded, ["parentAssembly"] = parent, ["result"] = note });
      // a refreshed single-region repair writes the corrected parent into its new record (bowtie_unfix corrects it anyway)
      if (g.Pieces.Count == 1 && parent != null && !string.Equals(parent, recorded, StringComparison.OrdinalIgnoreCase)) g.ParentCorrected = parent;
      if (string.IsNullOrWhiteSpace(parent)) { checks.Add(new Dictionary<string, object?> { ["region"] = name, ["result"] = "no parent assembly known: not checked" }); continue; }
      var compared = 0;
      foreach (var (nb, at) in new[] { (prev, region.StartStation), (next, region.EndStation) })
      {
        if (nb == null || pieces.Contains(nb.Name) || IsRepair(nb)) continue;
        if (!string.Equals(A(nb), parent, StringComparison.OrdinalIgnoreCase))
        {
          checks.Add(new Dictionary<string, object?> { ["region"] = name, ["neighbour"] = nb.Name, ["boundary"] = Math.Round(at, 3),
            ["result"] = $"neighbour on '{A(nb)}', not the parent assembly '{parent}': not compared" });
          continue;
        }
        compared++;
        var mine = Near(region, at, 3); var theirs = Near(nb, at, 3);
        foreach (var (sideName, sign) in new[] { ("left", -1.0), ("right", 1.0) })
        {
          (double Grade, double Width, double Clip, double Stock, string What)? best = null;
          var looked = 0;
          foreach (var cs in mine)
          {
            var a = ReadSeamSection(baseline, cs, sign, linkCode);
            if (a == null || a.Clipped) continue;
            var la = Links(a);
            foreach (var ss in theirs)
            {
              var b = ReadSeamSection(baseline, ss, sign, linkCode);
              if (b == null) continue;
              var lb = Links(b);
              looked++;
              if (la.Count != lb.Count || Kind(la) != Kind(lb)) continue;
              double dg = 0, dw = Math.Abs(a.StartOffset - b.StartOffset); var what = "";
              for (var k = 0; k < la.Count; k++)
              {
                var gk = Math.Abs(la[k].G - lb[k].G);
                if (gk > dg) { dg = gk; what = $"link {k + 1}: grade {la[k].G * 100:0.##}% (clip) against {lb[k].G * 100:0.##}% (stock)"; }
                if (k < la.Count - 1)
                {
                  var wk = Math.Abs(la[k].W - lb[k].W);
                  if (wk > dw) { dw = wk; if (wk > widthTol) what = $"link {k + 1}: {la[k].W:0.###} m wide (clip) against {lb[k].W:0.###} m (stock)"; }
                }
              }
              if (best == null || dg + dw < best.Value.Grade + best.Value.Width) best = (dg, dw, cs, ss, what);
            }
          }
          var row = new Dictionary<string, object?> { ["region"] = name, ["neighbour"] = nb.Name, ["side"] = sideName, ["boundary"] = Math.Round(at, 3), ["pairsLooked"] = looked };
          if (best == null) { row["result"] = "no comparable pair (different number of links, or cut against fill): not decided here"; checks.Add(row); continue; }
          row["clipStation"] = Math.Round(best.Value.Clip, 3); row["stockStation"] = Math.Round(best.Value.Stock, 3);
          row["maxGradeDiff"] = Math.Round(best.Value.Grade, 5); row["maxWidthDiff"] = Math.Round(best.Value.Width, 4);
          var drift = best.Value.Grade > gradeTol || best.Value.Width > widthTol;
          row["result"] = drift ? "differs" : "same design";
          checks.Add(row);
          if (drift)
            found.Add(new Dictionary<string, object?>
            {
              ["region"] = name, ["neighbour"] = nb.Name, ["clipAssembly"] = A(region), ["stockAssembly"] = A(nb), ["side"] = sideName,
              ["clipStation"] = Math.Round(best.Value.Clip, 3), ["stockStation"] = Math.Round(best.Value.Stock, 3),
              ["maxGradeDiff"] = Math.Round(best.Value.Grade, 5), ["maxWidthDiff"] = Math.Round(best.Value.Width, 4), ["what"] = best.Value.What,
            });
        }
      }
      if (compared == 0)
        checks.Add(new Dictionary<string, object?> { ["region"] = name, ["parentAssembly"] = parent,
          ["result"] = "no region next to it on the parent assembly (both are repairs, pieces of this one, or on another assembly): not checked" });
    }
    return (found, checks);
  }

}
