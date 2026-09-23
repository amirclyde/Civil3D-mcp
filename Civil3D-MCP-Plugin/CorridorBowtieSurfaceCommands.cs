using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using K = Utnm.BowtieKernel;

namespace Civil3DMcpPlugin;

/// <summary>
/// Bowtie fixing, part 6: bowtieSurface. The corridor surface is a TIN of the section points: between two applied stations
/// its triangles are straight, and where a valley line bends between two section ends (the hinge, a bench edge) they cut
/// across it - measured 2-18 cm off the valley on FL-02 / FL-03 (23 Sep 2026). Each repair's valley line, trimmed to the
/// part that is a seam between the two sides' daylight (not across the drain, not the level run-on past its end), goes into
/// every surface of the corridor as a standard breakline by points, tagged with the valley line's handle; the corridor
/// outline boundary is computed again from the built sections. Afterwards the surface is sampled along every valley and its
/// area compared with the outline: closed (no hole, nothing outside) and on the valley within tolerance.
/// Idempotent: the breaklines this tool added before are taken out first, so it can run after every bowtie_fix, bowtie_unfix
/// and bowtie_refresh (they call it themselves). A dry run changes nothing and reports the surfaces as they are.
/// </summary>
public static partial class CorridorBowtieCommands
{
  internal const string SurfaceBreaklineTag = "bowtie-valley-surface v1";
  private const string OutlineBoundaryName = "Corridor outline";
  // the valley breakline starts this far outside the structure's outer edge, so its first point never sits on a vertical wall
  private const double StructureMargin = 0.005;

  /// <summary>Where the ground part of a section starts on this side: the outer end of the leading run of linkCode links
  /// (from the baseline outward) that bound a shape - the drain's wall tops (inner wall top to outer edge), a kerb, a slab.
  /// Inside it the section is structure (with vertical walls and the channel), which a valley breakline must not touch.
  /// With no shape link at the start of the chain it is the chain's start. Null when the section has no chain on that side.</summary>
  private static double? StructureEdge(AppliedAssembly applied, double s, double sign, string linkCode)
  {
    var sec = ReadSeamSection(applied, s, sign, linkCode);
    if (sec == null || sec.Template.Count < 2) return null;
    var shaped = new List<(double A, double B)>();
    try
    {
      foreach (CalculatedShape shape in applied.Shapes)
        foreach (CalculatedLink link in shape.CalculatedLinks)
        {
          if (!HasCode(link.CorridorCodes, linkCode)) continue;
          var pts = link.CalculatedPoints.Cast<CalculatedPoint>().Select(q => q.StationOffsetElevationToBaseline).ToList();
          if (pts.Count < 2) continue;
          double a = sign * pts[0].Y, b = sign * pts[^1].Y;
          if (a < -1e-6 || b < -1e-6) continue;
          shaped.Add((Math.Min(a, b), Math.Max(a, b)));
        }
    }
    catch { }
    var t = sec.Template;
    var edge = t[0].Off;
    for (var i = 0; i + 1 < t.Count; i++)
    {
      double a = t[i].Off, b = t[i + 1].Off;
      if (b - a < 1e-9) { edge = b; continue; }
      if (!shaped.Any(l => l.A <= a + 1e-4 && l.B >= b - 1e-4)) break;
      edge = b;
    }
    return edge;
  }

  public static Task<object?> BowtieSurfaceAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var surfaceName = PluginRuntime.GetOptionalString(parameters, "surfaceName");
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    var outline = PluginRuntime.GetOptionalBool(parameters, "outline") ?? true;
    var tolerance = PluginRuntime.GetOptionalDouble(parameters, "tolerance") ?? 0.005;
    Func<Autodesk.AutoCAD.ApplicationServices.Document, CivilDocument, Database, Transaction, object?> work = (doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, dryRun ? OpenMode.ForRead : OpenMode.ForWrite);
      return UpdateValleySurfaces(transaction, corridor, baselineIndex, surfaceName, outline, dryRun, tolerance);
    };
    return dryRun ? CivilExecution.ReadAsync<object?>(work) : CivilExecution.WriteAsync<object?>(work);
  }

  /// <summary>One line per surface of what UpdateValleySurfaces did and found, for the reports of bowtie_fix / unfix / refresh /
  /// check (bowtie_surface gives the full detail).</summary>
  internal static Dictionary<string, object?> SurfaceSummary(Dictionary<string, object?> full)
  {
    var list = new List<Dictionary<string, object?>>();
    foreach (var row in full.GetValueOrDefault("surfaces") as List<Dictionary<string, object?>> ?? new())
    {
      var bl = row.GetValueOrDefault("valleyBreaklines") as Dictionary<string, object?>;
      var ol = row.GetValueOrDefault("outline") as Dictionary<string, object?>;
      var ck = row.GetValueOrDefault("check") as Dictionary<string, object?>;
      list.Add(new Dictionary<string, object?>
      {
        ["surface"] = row.GetValueOrDefault("surface"), ["status"] = row.GetValueOrDefault("status"),
        ["changed"] = bl?.ContainsKey("added") == true, ["valleyBreaklines"] = bl?.GetValueOrDefault("added") ?? bl?.GetValueOrDefault("inSurface"),
        ["outlineUpdated"] = ol?.GetValueOrDefault("updated") ?? false,
        ["maxLevelOffValley"] = ck?.GetValueOrDefault("maxLevelOffValley"), ["holesOnValleys"] = ck?.GetValueOrDefault("holesOnValleys"),
        ["areaNotCovered"] = ck?.GetValueOrDefault("areaNotCovered"), ["worstAt"] = ck?.GetValueOrDefault("worstAt"),
      });
    }
    return new Dictionary<string, object?>
    {
      ["surfaces"] = list, ["valleyParts"] = full.GetValueOrDefault("valleyParts"),
      ["allClosedAndFollowing"] = list.Count > 0 && list.All(r => Equals(r["status"], "closed_and_following")),
      ["rebuildError"] = full.GetValueOrDefault("rebuildError"), ["warnings"] = full.GetValueOrDefault("warnings"),
    };
  }

  /// <summary>The valley lines of every repair on the baseline, trimmed for the surface: one entry per part.</summary>
  private static List<(SeamGroup Group, string Line, string Handle, List<K.P3> Points)> ValleyBreaklines(Baseline baseline, Transaction transaction, List<string> warnings, List<Dictionary<string, object?>> trims)
  {
    var legacy = new List<string>(); var foreign = new List<string>();
    var groups = CollectSeamGroups(baseline, transaction, legacy, foreign);
    if (legacy.Count > 0) warnings.Add($"Alignment valleys from the older method are not put in the surface: {string.Join("; ", legacy)}.");
    var result = new List<(SeamGroup, string, string, List<K.P3>)>();
    foreach (var g in groups)
    {
      var sign = g.Side == "left" ? -1.0 : 1.0;
      var linkCode = g.Record.GetValueOrDefault("linkCode") is { Length: > 0 } lc ? lc : "Top";
      // where the ground part starts (the drain's outer edge: the end of the structure's wall-top links, not the first Top
      // link, which is the drain's inner wall top), per applied station near the repair: the valley is a seam of the two
      // sides' daylight only beyond it
      double[] all;
      try { all = baseline.SortedStations() ?? Array.Empty<double>(); } catch { all = Array.Empty<double>(); }
      var starts = new List<(double S, double Start)>();
      foreach (var s in all.Where(s => s >= g.From - 20 && s <= g.To + 20))
      {
        AppliedAssembly applied;
        try { applied = baseline.GetAppliedAssemblyAtStation(s); } catch { continue; }
        if (StructureEdge(applied, s, sign, linkCode) is double e) starts.Add((s, e));
      }
      if (starts.Count == 0) { warnings.Add($"'{g.SeamName}': no section read near the repair; its valley is not put in the surface."); continue; }
      double StartAt(double st) => starts.OrderBy(x => Math.Abs(x.S - st)).First().Start;
      K.SampledBaseline bl;
      try { bl = SampleBaselineForSeam(baseline, Math.Max(baseline.StartStation, g.From - 60), Math.Min(baseline.EndStation, g.To + 60)); }
      catch (Exception ex) { warnings.Add($"'{g.SeamName}': baseline not read ({ex.Message})."); continue; }
      double Clearance(K.P2 p) { var (st, d) = K.SurfaceLines.NearestOnBaseline(bl, p); return d - StartAt(st) - StructureMargin; }
      var runOn = RecordDouble(g.Record, "runOn");
      foreach (var (id, name, isCap) in new[] { ((ObjectId?)g.SeamId, g.SeamName, false), (g.CapId, g.CapName ?? "", true) })
      {
        if (!id.HasValue) continue;
        List<K.P3> pts;
        try { pts = FeatureLinePoints(transaction, id.Value).Select(P).ToList(); } catch (Exception ex) { warnings.Add($"'{name}' not read ({ex.Message})."); continue; }
        var kept = K.SurfaceLines.Trim(pts, isCap ? 0 : runOn, Clearance);
        foreach (var part in kept)
          result.Add((g, name, id.Value.Handle.ToString(), part));
        trims.Add(new Dictionary<string, object?>
        {
          ["line"] = name, ["structureEdge"] = Math.Round(starts.Min(x => x.Start), 3), ["structureEdgeMax"] = Math.Round(starts.Max(x => x.Start), 3),
          ["length"] = Math.Round(K.SurfaceLines.Length(pts), 3), ["keptLength"] = Math.Round(kept.Sum(k => K.SurfaceLines.Length(k)), 3), ["parts"] = kept.Count,
        });
      }
    }
    return result;
  }

  /// <summary>Puts the valley breaklines into the corridor's surfaces (all, or the one named), renews the outline boundary, and
  /// checks the result. With dryRun nothing changes: the surfaces are checked as they are.</summary>
  internal static Dictionary<string, object?> UpdateValleySurfaces(Transaction transaction, Corridor corridor, int baselineIndex, string? surfaceName, bool outline, bool dryRun, double tolerance)
  {
    var baseline = GetBaseline(corridor, baselineIndex);
    var warnings = new List<string>();
    var trims = new List<Dictionary<string, object?>>();
    var lines = ValleyBreaklines(baseline, transaction, warnings, trims);
    var surfaces = corridor.CorridorSurfaces.Cast<CorridorSurface>()
      .Where(cs => string.IsNullOrWhiteSpace(surfaceName) || string.Equals(cs.Name, surfaceName, StringComparison.OrdinalIgnoreCase)).ToList();
    if (!string.IsNullOrWhiteSpace(surfaceName) && surfaces.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Corridor '{corridor.Name}' has no surface '{surfaceName}' ({string.Join(", ", corridor.CorridorSurfaces.SurfaceNames())}).");

    // the outline as the corridor is built now
    Point3dCollection? outlinePts = null; string? outlineError = null;
    // each repair's valley tip: the end of its (trimmed) valley line where it meets the ground - the outline's corner there
    var tips = lines.Where(l => string.Equals(l.Line, l.Group.SeamName, StringComparison.Ordinal))
      .GroupBy(l => l.Handle).Select(gr => gr.Last().Points[^1]).Select(q => new Point3d(q.X, q.Y, q.Z)).ToList();
    try { outlinePts = CorridorSurfaceCommands.ComputeOutlinePolygon(corridor, baselineIndex, tips); }
    catch (JsonRpcDispatchException ex) { outlineError = ex.Message; }
    var outlineRing = outlinePts?.Cast<Point3d>().Select(p => new K.P2(p.X, p.Y)).ToList();

    var rows = new List<Dictionary<string, object?>>();
    var changed = false;
    string Key(string handle, IReadOnlyList<K.P3> pts) => $"{handle}|{K.SurfaceLines.Fingerprint(pts)}";
    var wanted = lines.Select(l => Key(l.Handle, l.Points)).OrderBy(x => x, StringComparer.Ordinal).ToList();
    foreach (var cs in surfaces)
    {
      var row = new Dictionary<string, object?> { ["surface"] = cs.Name };
      rows.Add(row);
      if (cs.SurfaceId.IsNull) { row["status"] = "not_built"; continue; }
      var tin = transaction.GetObject(cs.SurfaceId, OpenMode.ForRead) as TinSurface;
      if (tin == null) { row["status"] = "not_a_tin"; continue; }

      // ---- the breaklines this tool put in before
      var mine = new List<SurfaceOperation>();
      var have = new List<string>();
      var ops = tin.Operations;
      for (var i = 0; i < ops.Count; i++)
      {
        var op = ops[i];
        string? desc = null;
        try { desc = (op as SurfaceOperationAddBreakline)?.Description; } catch { }
        if (desc == null || !desc.StartsWith(SurfaceBreaklineTag, StringComparison.Ordinal)) continue;
        mine.Add(op);
        var rec = ParseRecord(desc);
        have.Add($"{rec.GetValueOrDefault("seam") ?? ""}|{rec.GetValueOrDefault("hash") ?? ""}");
      }
      have.Sort(StringComparer.Ordinal);
      var wantHandles = lines.Select(l => l.Handle).ToHashSet();
      var haveHandles = have.Select(h => h.Split('|')[0]).ToHashSet();
      var breaklinesCurrent = have.SequenceEqual(wanted);
      var blRow = new Dictionary<string, object?>
      {
        ["inSurface"] = mine.Count, ["wanted"] = lines.Count, ["current"] = breaklinesCurrent,
        ["stale"] = haveHandles.Where(h => !wantHandles.Contains(h)).ToList(),
        ["missing"] = lines.Where(l => !haveHandles.Contains(l.Handle)).Select(l => l.Line).Distinct().ToList(),
      };
      row["valleyBreaklines"] = blRow;

      // ---- the outline boundary: ours is renewed; one is added when the surface has none; someone else's is left alone
      var boundaries = cs.Boundaries.Cast<CorridorSurfaceBoundary>().ToList();
      var ours = boundaries.FirstOrDefault(b => string.Equals(b.Name, OutlineBoundaryName, StringComparison.OrdinalIgnoreCase));
      double? shift = null;
      if (ours != null && outlineRing != null)
        try { shift = K.SurfaceLines.RingDeviation(ours.PolygonPoints().Select(p => new K.P2(p.X, p.Y)).ToList(), outlineRing); } catch { }
      var manageOutline = outline && outlinePts != null && (ours != null || boundaries.Count == 0);
      var outlineCurrent = !manageOutline || (ours != null && shift.HasValue && shift.Value <= 0.002);
      var outlineRow = new Dictionary<string, object?>
      {
        ["name"] = ours?.Name, ["otherBoundaries"] = boundaries.Where(b => b != ours).Select(b => b.Name).ToList(),
        ["shiftFromCurrentOutline"] = shift.HasValue ? Math.Round(shift.Value, 3) : null, ["current"] = outlineCurrent, ["error"] = outlineError,
      };
      if (ours == null && boundaries.Count > 0)
        outlineRow["note"] = $"The surface has its own boundary ({string.Join(", ", boundaries.Select(b => b.Name))}), left alone; the check compares the surface with the corridor outline as built now.";
      row["outline"] = outlineRow;
      row["upToDate"] = breaklinesCurrent && outlineCurrent;
      if (dryRun || (breaklinesCurrent && outlineCurrent)) continue;

      tin.UpgradeOpen();
      // by index, from the end, each fetched again: an operation handle stands for its index, so removing by the handles
      // collected first skips every second one once the list has shifted (FL-03, 24 Sep 2026: 8 removed, 3 left behind)
      var removed = 0;
      for (var i = tin.Operations.Count - 1; i >= 0; i--)
      {
        string? d = null;
        try { d = (tin.Operations[i] as SurfaceOperationAddBreakline)?.Description; } catch { }
        if (d == null || !d.StartsWith(SurfaceBreaklineTag, StringComparison.Ordinal)) continue;
        tin.Operations.Remove(tin.Operations[i]); removed++;
      }
      var leftOver = 0;
      for (var i = 0; i < tin.Operations.Count; i++)
      {
        string? d = null;
        try { d = (tin.Operations[i] as SurfaceOperationAddBreakline)?.Description; } catch { }
        if (d != null && d.StartsWith(SurfaceBreaklineTag, StringComparison.Ordinal)) leftOver++;
      }
      if (leftOver > 0)
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"'{cs.Name}': {leftOver} valley breakline(s) of an earlier run could not be taken out; nothing was changed.");
      var added = 0;
      foreach (var (g, name, handle, pts) in lines)
      {
        var coll = new Point3dCollection();
        foreach (var p in pts) coll.Add(new Point3d(p.X, p.Y, p.Z));
        var op = tin.BreaklinesDefinition.AddStandardBreaklines(coll, 0.01, 0.0, 0.0, 0.0);
        try { op.Description = $"{SurfaceBreaklineTag}; seam={handle}; hash={K.SurfaceLines.Fingerprint(pts)}; name={name.Replace(";", ",")}; corridor={corridor.Name.Replace(";", ",")}"; } catch { }
        added++;
      }
      blRow["removed"] = removed; blRow["added"] = added;
      if (manageOutline && !outlineCurrent)
      {
        if (ours != null) cs.Boundaries.Remove(ours.Name);
        var b = cs.Boundaries.Add(OutlineBoundaryName, outlinePts!);
        b.BoundaryType = CorridorSurfaceBoundaryType.OutsideBoundary;
        outlineRow["updated"] = true; outlineRow["name"] = OutlineBoundaryName; outlineRow["vertices"] = outlinePts!.Count;
      }
      changed = true;
    }
    string? rebuildError = null;
    if (changed) rebuildError = TryRebuild(corridor);

    // ---- the check: along every valley, and closed
    foreach (var (cs, row) in surfaces.Zip(rows))
    {
      if (cs.SurfaceId.IsNull || row.GetValueOrDefault("status") is string) continue;
      var tin = transaction.GetObject(cs.SurfaceId, OpenMode.ForRead) as TinSurface;
      if (tin == null) continue;
      double worst = 0; var holes = 0; var sampled = 0; string? worstAt = null;
      var perLine = new List<Dictionary<string, object?>>();
      foreach (var byLine in lines.GroupBy(l => l.Line))
      {
        double lineWorst = 0; var lineHoles = 0; string? holeFrom = null, holeTo = null;
        foreach (var (_, _, _, pts) in byLine)
          foreach (var q in K.SurfaceLines.Samples(pts, 0.25))
          {
            sampled++;
            double z;
            try { z = tin.FindElevationAtXY(q.X, q.Y); }
            catch { holes++; lineHoles++; holeTo = $"{q.X:0.###}, {q.Y:0.###}"; holeFrom ??= holeTo; continue; }
            var d = Math.Abs(z - q.Z);
            if (d > lineWorst) lineWorst = d;
            if (d > worst) { worst = d; worstAt = $"{byLine.Key} at {q.X:0.###}, {q.Y:0.###}"; }
          }
        perLine.Add(new Dictionary<string, object?> { ["valleyLine"] = byLine.Key, ["maxLevelOff"] = Math.Round(lineWorst, 4), ["holes"] = lineHoles,
          ["holesFrom"] = holeFrom, ["holesTo"] = holeTo });
      }
      double? area2d = null;
      try { area2d = tin.GetTerrainProperties().SurfaceArea2D; } catch { }
      double? outlineArea = null; string? against = null;
      try
      {
        var ours = cs.Boundaries.Cast<CorridorSurfaceBoundary>().FirstOrDefault(b => string.Equals(b.Name, OutlineBoundaryName, StringComparison.OrdinalIgnoreCase));
        if (ours != null) { outlineArea = K.SurfaceLines.PolygonArea(ours.PolygonPoints().Select(p => new K.P2(p.X, p.Y)).ToList()); against = $"boundary '{ours.Name}'"; }
        else if (outlineRing != null) { outlineArea = K.SurfaceLines.PolygonArea(outlineRing); against = "the corridor outline as built now"; }
      }
      catch { }
      var areaGap = area2d.HasValue && outlineArea.HasValue ? outlineArea.Value - area2d.Value : (double?)null;
      // where the surface does not reach its boundary: walk the boundary 5 cm inside it, every 0.5 m
      var uncovered = new List<Dictionary<string, object?>>();
      try
      {
        var ringB = cs.Boundaries.Cast<CorridorSurfaceBoundary>().FirstOrDefault(b => string.Equals(b.Name, OutlineBoundaryName, StringComparison.OrdinalIgnoreCase))?.PolygonPoints()
          .Select(p => new K.P2(p.X, p.Y)).ToList() ?? outlineRing;
        if (ringB != null && ringB.Count >= 3)
        {
          double sa = 0; for (var i = 0; i < ringB.Count; i++) { var p = ringB[i]; var q = ringB[(i + 1) % ringB.Count]; sa += p.X * q.Y - q.X * p.Y; }
          var ccw = sa > 0;
          (double X, double Y)? runStart = null; (double X, double Y) runEnd = default; var runN = 0;
          void Flush() { if (runStart.HasValue && uncovered.Count < 20) uncovered.Add(new Dictionary<string, object?> { ["from"] = $"{runStart.Value.X:0.###}, {runStart.Value.Y:0.###}", ["to"] = $"{runEnd.X:0.###}, {runEnd.Y:0.###}", ["samples"] = runN }); runStart = null; runN = 0; }
          for (var i = 0; i < ringB.Count; i++)
          {
            var a = ringB[i]; var b = ringB[(i + 1) % ringB.Count];
            var ex = b.X - a.X; var ey = b.Y - a.Y; var len = Math.Sqrt(ex * ex + ey * ey);
            if (len < 1e-6) continue;
            var nx = (ccw ? -ey : ey) / len * 0.05; var ny = (ccw ? ex : -ex) / len * 0.05;
            var n = Math.Max(1, (int)Math.Ceiling(len / 0.5));
            for (var k = 0; k < n; k++)
            {
              var t = (k + 0.5) / n; double x = a.X + ex * t + nx, y = a.Y + ey * t + ny;
              var hole = false;
              try { tin.FindElevationAtXY(x, y); } catch { hole = true; }
              if (hole) { runStart ??= (x, y); runEnd = (x, y); runN++; } else Flush();
            }
          }
          Flush();
        }
      }
      catch { }
      var follows = worst <= tolerance && holes == 0;
      var closed = areaGap.HasValue ? Math.Abs(areaGap.Value) <= Math.Max(0.5, 1e-4 * outlineArea!.Value) : (bool?)null;
      row["check"] = new Dictionary<string, object?>
      {
        ["valleyPointsSampled"] = sampled, ["maxLevelOffValley"] = Math.Round(worst, 4), ["worstAt"] = worst > tolerance ? worstAt : null, ["holesOnValleys"] = holes,
        ["area2d"] = area2d.HasValue ? Math.Round(area2d.Value, 2) : null, ["outlineArea"] = outlineArea.HasValue ? Math.Round(outlineArea.Value, 2) : null,
        ["areaNotCovered"] = areaGap.HasValue ? Math.Round(areaGap.Value, 2) : null, ["areaComparedWith"] = against, ["boundaryNotReached"] = uncovered, ["perValley"] = perLine,
      };
      row["status"] = !follows ? "not_following_valleys" : closed == false ? "not_closed" : closed == null ? "follows_valleys_no_outline" : "closed_and_following";
      // inside the transaction that changed it the surface is not rebuilt yet: this check sees it as it was
      if (!dryRun && changed) row["checkNote"] = "Read inside the change, before Civil 3D rebuilt the surface; the MCP tool checks it again in a new read and reports that.";
    }
    return new Dictionary<string, object?>
    {
      ["corridorName"] = corridor.Name,
      ["dryRun"] = dryRun,
      ["valleyParts"] = lines.Count,
      ["valleyLines"] = lines.Select(l => l.Line).Distinct().ToList(),
      ["valleyTrim"] = trims,
      ["surfaces"] = rows,
      ["rebuildError"] = rebuildError,
      ["warnings"] = warnings,
      ["note"] = "Each valley goes into the surface from 5 mm outside the structure's outer edge (structureEdge: the end of the drain's wall-top links) to where it meets the ground (the level run-on past its end is left out), as a breakline by points tagged with its line's handle. closed_and_following: the surface is on every valley within tolerance, has a point everywhere along them, and its plan area equals the outline's (no hole, nothing outside it).",
    };
  }
}
