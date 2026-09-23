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
          ["assemblyName"] = region != null ? AssemblyName(transaction, SafeAssemblyId(region)) : null,
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
    var ctxFrequency = PluginRuntime.GetOptionalString(parameters, "parentFrequency");
    var inferParent = PluginRuntime.GetOptionalBool(parameters, "inferParent") ?? true;

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
      // a repair over two regions records each piece: "region~parent~parentAssembly~before~after[~frequency]|..."; every piece
      // is put back, not only the one named (the valley line they share is erased, so the others would be left half-repaired)
      var pieces = new List<UnfixPiece>();
      if (record.TryGetValue("pieces", out var piecesText) && piecesText.Length > 0)
        foreach (var pieceText in piecesText.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
          var f = pieceText.Split('~');
          if (f.Length < 5 || f[0].Trim().Length == 0) continue;
          pieces.Add(new UnfixPiece(f[0].Trim(), Blank(f[1]), Blank(f[2]), Blank(f[3]), Blank(f[4]), f.Length > 5 ? Blank(f[5]) : null));
        }
      string? Rec(string key, string? given) => !string.IsNullOrWhiteSpace(given) ? given : record.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
      var own = pieces.FirstOrDefault(x => string.Equals(x.Region, region.Name, StringComparison.OrdinalIgnoreCase));
      var ownPiece = new UnfixPiece(region.Name,
        !string.IsNullOrWhiteSpace(ctxParent) ? ctxParent : own?.Parent ?? Rec("parent", null),
        !string.IsNullOrWhiteSpace(ctxAssembly) ? ctxAssembly : own?.ParentAssembly ?? Rec("parentAssembly", null),
        !string.IsNullOrWhiteSpace(ctxBefore) ? ctxBefore : own != null ? own.Before : Rec("before", null),
        !string.IsNullOrWhiteSpace(ctxAfter) ? ctxAfter : own != null ? own.After : Rec("after", null),
        !string.IsNullOrWhiteSpace(ctxFrequency) ? ctxFrequency : own?.Frequency ?? Rec("parentFrequency", null));
      // A repair made before the fix context was recorded says nothing about where it came from. It was cut out of one
      // region: when the regions either side carry the same assembly and are not repairs themselves, that is the parent
      // (its assembly, name and frequency), and the three are merged back. Anything else is left split, with a warning.
      string? inferredFrom = null;
      if (inferParent && ownPiece.Parent == null && ownPiece.ParentAssembly == null && ownPiece.Before == null && ownPiece.After == null)
      {
        var at = IndexOfName(baseline, region.Name);
        var all = baseline.BaselineRegions;
        BaselineRegion? prev = at > 0 ? all[at - 1] : null, next = at >= 0 && at < all.Count - 1 ? all[at + 1] : null;
        bool IsRepair(BaselineRegion r)
        {
          try { var t = r.GetTargets(); for (var i = 0; i < t.Count; i++) if (t[i].LogicalName is "ClipTarget" && t[i].TargetIds.Count > 0) return true; } catch { }
          return false;
        }
        var prevAsm = prev != null ? AssemblyName(transaction, SafeAssemblyId(prev)) : null;
        var nextAsm = next != null ? AssemblyName(transaction, SafeAssemblyId(next)) : null;
        if (prev != null && next != null && prevAsm != null && string.Equals(prevAsm, nextAsm, StringComparison.OrdinalIgnoreCase) && !IsRepair(prev) && !IsRepair(next))
        {
          ownPiece = new UnfixPiece(region.Name, prev.Name, prevAsm, prev.Name, next.Name, FrequencySignature(prev));
          inferredFrom = $"neighbours '{prev.Name}' and '{next.Name}' (both {prevAsm})";
          warnings.Add($"'{region.Name}' has no record of where it came from (a repair made before bowtie_fix recorded it): its parent is taken from the regions either side, {inferredFrom}.");
        }
        else
          warnings.Add($"'{region.Name}' has no record of where it came from, and the regions either side do not share one assembly ({prevAsm ?? "none"} / {nextAsm ?? "none"}): its assembly is kept and it is not merged.");
      }
      // a recorded parent assembly that is on neither region next to the piece is a stale record (e.g. a repair cut from a
      // region still on an older clip assembly): the assembly of the neighbours is given back instead, and said so
      var pieceNames = pieces.Select(p => p.Region).Append(region.Name).ToList();
      var reconciled = new List<string>();
      UnfixPiece Reconciled(UnfixPiece p)
      {
        if (p.ParentAssembly == null) return p;
        var (asm, note) = ReconcileParentAssembly(baseline, transaction, p.Region, p.ParentAssembly, pieceNames);
        if (note != null) warnings.Add(note);
        if (asm == null || string.Equals(asm, p.ParentAssembly, StringComparison.OrdinalIgnoreCase)) return p;
        reconciled.Add($"{p.Region}: {p.ParentAssembly} -> {asm}");
        return p with { ParentAssembly = asm };
      }
      ownPiece = Reconciled(ownPiece);
      var work = new List<UnfixPiece> { ownPiece };
      foreach (var other in pieces)
        if (!string.Equals(other.Region, region.Name, StringComparison.OrdinalIgnoreCase) && IndexOfName(baseline, other.Region) >= 0) work.Add(Reconciled(other));
      foreach (var other in pieces)
        if (!string.Equals(other.Region, region.Name, StringComparison.OrdinalIgnoreCase) && IndexOfName(baseline, other.Region) < 0)
          warnings.Add($"The piece '{other.Region}' recorded for this repair is not on the baseline any more: it is left as it is.");
      var stations = (Rec("stations", null) ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN).Where(d => !double.IsNaN(d)).ToList();

      var plan = new Dictionary<string, object?>
      {
        ["clearTargets"] = cleared,
        ["eraseValleyLines"] = toErase.Select(x => x.Name).ToList(),
        ["deleteStations"] = stations.Select(x => Math.Round(x, 4)).ToList(),
        ["restoreAssembly"] = restoreAssembly ? ownPiece.ParentAssembly : null,
        ["mergeWith"] = merge ? new[] { ownPiece.Before, ownPiece.After }.Where(x => !string.IsNullOrEmpty(x)).ToList() : new List<string?>(),
        ["restoreName"] = merge ? ownPiece.Parent : null,
        ["restoreFrequency"] = ownPiece.Frequency,
        ["inferredFrom"] = inferredFrom,
        ["parentAssemblyCorrected"] = reconciled.Count > 0 ? reconciled : null,
        ["pieces"] = work.Select(x => new Dictionary<string, object?>
        {
          ["region"] = x.Region, ["restoreAssembly"] = restoreAssembly ? x.ParentAssembly : null,
          ["mergeWith"] = merge ? new[] { x.Before, x.After }.Where(n => !string.IsNullOrEmpty(n)).ToList() : new List<string?>(),
          ["restoreName"] = merge ? x.Parent : null, ["restoreFrequency"] = x.Frequency,
        }).ToList(),
      };
      if (dryRun)
        return new Dictionary<string, object?> { ["corridorName"] = corridor.Name, ["regionName"] = region.Name, ["dryRun"] = true, ["plan"] = plan, ["warnings"] = warnings };

      // ---- 1. clear the clip targets BEFORE erasing the objects on them (a target left pointing at an erased object
      //         empties the whole region on the next rebuild) - in this region, and in any other region that maps the same
      //         valley lines (a repair over two regions shares them)
      if (cleared.Count > 0)
      {
        for (var i = 0; i < infos.Count; i++)
          if (infos[i].LogicalName is "ClipTarget" or "ClipElev") infos[i].TargetIds = new ObjectIdCollection();
        region.SetTargets(infos);
      }
      var erasing = new HashSet<ObjectId>(toErase.Select(x => x.Id));
      var regionsAll = baseline.BaselineRegions;
      for (var ri = 0; ri < regionsAll.Count; ri++)
      {
        var other = regionsAll[ri];
        if (string.Equals(other.Name, region.Name, StringComparison.OrdinalIgnoreCase)) continue;
        SubassemblyTargetInfoCollection oi;
        try { oi = other.GetTargets(); } catch { continue; }
        var touched = false;
        for (var i = 0; i < oi.Count; i++)
        {
          var ids = oi[i].TargetIds;
          if (ids.Count == 0 || !ids.Cast<ObjectId>().Any(erasing.Contains)) continue;
          var keep = new ObjectIdCollection();
          foreach (ObjectId id in ids) if (!erasing.Contains(id)) keep.Add(id);
          oi[i].TargetIds = keep; touched = true;
          cleared.Add($"{other.Name}/{oi[i].SubassemblyName}:{oi[i].LogicalName}");
        }
        if (touched) other.SetTargets(oi);
      }
      // ---- 2. the valley lines
      var erased = new List<string>();
      foreach (var (id, name, _) in toErase)
      {
        try { var ent = transaction.GetObject(id, OpenMode.ForWrite); ent.Erase(); erased.Add(name); }
        catch (Exception ex) { warnings.Add($"'{name}' could not be erased: {ex.Message}"); }
      }
      // ---- 3. the meet stations bowtie_seam added, from whichever piece holds each one
      var deleted = new List<double>();
      foreach (var st in stations)
      {
        var found = false;
        foreach (var piece in work)
        {
          var idx = IndexOfName(baseline, piece.Region);
          if (idx < 0) continue;
          var holder = baseline.BaselineRegions[idx];
          double[] added;
          try { added = holder.AdditionalStations() ?? Array.Empty<double>(); } catch { added = Array.Empty<double>(); }
          var match = added.Cast<double?>().FirstOrDefault(a => Math.Abs(a!.Value - st) < 1e-3);
          if (!match.HasValue) continue;
          try { holder.DeleteStation(match.Value); deleted.Add(Math.Round(match.Value, 4)); }
          catch (Exception ex) { warnings.Add($"Station {st:0.###} could not be removed from '{holder.Name}': {ex.Message}"); }
          found = true; break;
        }
        if (!found) warnings.Add($"Station {st:0.###} was not found as an added station of the repaired region(s).");
      }
      // ---- 4./5. every piece: its parent assembly (with the surface targets it has now), merged back with the pieces it was
      //            cut from, the parent's name and frequency. Highest station first, so a merge never moves a piece still to do.
      string? restored = null;
      Dictionary<string, object?>? merged = null;
      var restoredPieces = new List<Dictionary<string, object?>>();
      foreach (var piece in work.OrderByDescending(x => { var i = IndexOfName(baseline, x.Region); return i < 0 ? -1 : baseline.BaselineRegions[i].StartStation; }))
      {
        var idx = IndexOfName(baseline, piece.Region);
        if (idx < 0) continue;
        var target = baseline.BaselineRegions[idx];
        var pieceRestored = RestoreParentAssembly(civilDoc, transaction, target, restoreAssembly ? piece.ParentAssembly : null);
        var pieceMerged = merge ? MergeBack(baseline, target.Name, piece, warnings) : null;
        var finalName = (string?)pieceMerged?["name"] ?? target.Name;
        if (!string.IsNullOrWhiteSpace(piece.Frequency))
        {
          var fi = IndexOfName(baseline, finalName);
          if (fi >= 0 && !TryApplyFrequencySignature(baseline.BaselineRegions[fi], piece.Frequency!))
            warnings.Add($"The frequency '{piece.Frequency}' recorded for '{piece.Region}' could not be put back on '{finalName}'.");
        }
        if (ReferenceEquals(piece, ownPiece)) { restored = pieceRestored; merged = pieceMerged; }
        restoredPieces.Add(new Dictionary<string, object?> { ["region"] = piece.Region, ["assemblyRestored"] = pieceRestored, ["merged"] = pieceMerged, ["name"] = finalName, ["frequency"] = piece.Frequency });
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
        ["pieces"] = restoredPieces,
        ["rebuilt"] = rebuild && rebuildError == null,
        ["rebuildError"] = rebuildError,
        ["regions"] = ListRegions(baseline, transaction),
        ["warnings"] = warnings,
      };
    };
    return dryRun ? CivilExecution.ReadAsync<object?>(work) : CivilExecution.WriteAsync<object?>(work);
  }

  private sealed record UnfixPiece(string Region, string? Parent, string? ParentAssembly, string? Before, string? After, string? Frequency);

  /// <summary>Whether a region is a valley repair itself (a mapped ClipTarget).</summary>
  private static bool IsClipRepair(BaselineRegion r)
  {
    try { var t = r.GetTargets(); for (var i = 0; i < t.Count; i++) if (t[i].LogicalName is "ClipTarget" && t[i].TargetIds.Count > 0) return true; } catch { }
    return false;
  }

  /// <summary>
  /// The stock assembly a repaired region stands in for, checked against the regions right next to it. A repair is cut out of
  /// its parent, so the parent's assembly is on the pieces either side - unless the record is stale: a repair cut from a
  /// region that was still on an older clip assembly records that clip as its parent, and a later design change can put a
  /// new assembly on the neighbours. The neighbours that are neither pieces of the same repair nor repairs themselves decide:
  /// when the recorded assembly is on one of them it stands; when it is on none and they all carry one assembly, that one is
  /// taken and the reason given; when they differ, the record stands (with the reason). Returns the assembly and a note
  /// (null when the record was confirmed).
  /// </summary>
  private static (string? Assembly, string? Note) ReconcileParentAssembly(Baseline baseline, Transaction transaction, string regionName, string? recorded,
    ICollection<string> pieces)
  {
    var ri = IndexOfName(baseline, regionName);
    if (ri < 0) return (recorded, null);
    var all = baseline.BaselineRegions;
    var near = new List<(string Region, string Assembly)>();
    foreach (var i in new[] { ri - 1, ri + 1 })
    {
      if (i < 0 || i >= all.Count) continue;
      var r = all[i];
      if (pieces.Contains(r.Name, StringComparer.OrdinalIgnoreCase) || IsClipRepair(r)) continue;
      var a = AssemblyName(transaction, SafeAssemblyId(r));
      if (!string.IsNullOrWhiteSpace(a)) near.Add((r.Name, a!));
    }
    if (near.Count == 0) return (recorded, null);
    if (!string.IsNullOrWhiteSpace(recorded) && near.Any(n => string.Equals(n.Assembly, recorded, StringComparison.OrdinalIgnoreCase))) return (recorded, null);
    var kinds = near.Select(n => n.Assembly).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    var where = string.Join(" and ", near.Select(n => $"'{n.Region}'"));
    if (kinds.Count == 1)
      return (kinds[0], string.IsNullOrWhiteSpace(recorded)
        ? $"'{regionName}': no parent assembly recorded; taken from {where} next to it: {kinds[0]}."
        : $"'{regionName}': the recorded parent assembly '{recorded}' is on neither region next to it (a stale record); taken from {where}: {kinds[0]}.");
    return (recorded, $"'{regionName}': the recorded parent assembly '{recorded ?? "none"}' is on neither region next to it, and they differ ({string.Join(" / ", kinds)}): the record is kept.");
  }

  private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

  /// <summary>Gives a region back its parent assembly, with the surface targets it has now. Returns the assembly name, or null when
  /// nothing changed.</summary>
  private static string? RestoreParentAssembly(CivilDocument civilDoc, Transaction transaction, BaselineRegion region, string? parentAssembly)
  {
    if (string.IsNullOrWhiteSpace(parentAssembly) || string.Equals(AssemblyName(transaction, SafeAssemblyId(region)), parentAssembly, StringComparison.OrdinalIgnoreCase)) return null;
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
    return parentAssembly;
  }

  /// <summary>Merges a repaired region back with the regions it was split from and gives the result the parent's name.</summary>
  private static Dictionary<string, object?>? MergeBack(Baseline baseline, string regionName, UnfixPiece piece, List<string> warnings)
  {
    if (!string.IsNullOrEmpty(piece.Before) || !string.IsNullOrEmpty(piece.After))
    {
      var mid = IndexOfName(baseline, regionName);
      var first = !string.IsNullOrEmpty(piece.Before) ? IndexOfName(baseline, piece.Before!) : mid;
      var last = !string.IsNullOrEmpty(piece.After) ? IndexOfName(baseline, piece.After!) : mid;
      if (first >= 0 && last > first && mid >= first && mid <= last && last - first <= 2)
      {
        var regions = baseline.BaselineRegions;
        var a = regions[first]; var b = regions[last];
        try
        {
          a.Merge(a, b);
          if (!string.IsNullOrWhiteSpace(piece.Parent)) a.Name = UniqueRegionName(baseline, piece.Parent!, a);
          return new Dictionary<string, object?> { ["name"] = a.Name, ["startStation"] = a.StartStation, ["endStation"] = a.EndStation };
        }
        catch (Exception ex) { warnings.Add($"'{regionName}' could not be merged back ({ex.GetType().Name}: {ex.Message}); the regions are left split."); return null; }
      }
      warnings.Add($"The pieces '{piece.Before}' / '{piece.After}' recorded for '{regionName}' are not next to it any more: the regions are left split.");
      return null;
    }
    if (!string.IsNullOrWhiteSpace(piece.Parent) && !string.Equals(regionName, piece.Parent, StringComparison.OrdinalIgnoreCase))
    {
      var i = IndexOfName(baseline, regionName);
      if (i >= 0)
      {
        var r = baseline.BaselineRegions[i];
        try { r.Name = UniqueRegionName(baseline, piece.Parent!, r); return new Dictionary<string, object?> { ["name"] = r.Name, ["startStation"] = r.StartStation, ["endStation"] = r.EndStation, ["renamedOnly"] = true }; } catch { }
      }
    }
    return null;
  }

  /// <summary>Applies a frequency written as "tangents/curves/spirals/profileCurves" (FrequencySignature) to a region.</summary>
  private static bool TryApplyFrequencySignature(BaselineRegion region, string signature)
  {
    var parts = signature.Split('/');
    if (parts.Length != 4) return false;
    var v = new double[4];
    for (var i = 0; i < 4; i++)
      if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) || v[i] <= 0) return false;
    try
    {
      var s = region.AppliedAssemblySetting;
      s.FrequencyAlongTangents = v[0]; s.FrequencyAlongCurves = v[1]; s.FrequencyAlongSpirals = v[2]; s.FrequencyAlongProfileCurves = v[3];
      return true;
    }
    catch { return false; }
  }
}
