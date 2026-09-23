using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CivilAssembly = Autodesk.Civil.DatabaseServices.Assembly;

namespace Civil3DMcpPlugin;

/// <summary>
/// Corridor bowtie prediction and region isolation (Civil 3D 2026 API, no reflection):
///
///   predictCorridorBowties  - read-only. Rebuilds the inside edge of every region as a dense polyline
///                             P(s) = SOE->XYZ(s, +/-w(s)) and reports where it runs backwards against the
///                             baseline direction or crosses itself (the bowtie), with the station range to isolate.
///                             w(s) comes from the built corridor (outermost calculated point per side, optionally one
///                             point code) or from a fixed width.
///   splitCorridorRegion     - BaselineRegion.Split(station), optional Match(parent, All) and rename.
///   isolateCorridorRanges   - splits regions so each station range gets its own region (BT-01, BT-02 ...), keeps the
///                             parent's assembly / targets / frequency (Match), optional frequency or assembly for the
///                             isolated region. One rebuild at the end.
///   mergeCorridorRegions    - BaselineRegion.Merge(first, last): undo for the two actions above.
///
///   API used: BaseBaseline.StationOffsetElevationToXYZ / GetDirectionAtStation / StartStation / EndStation,
///   Baseline.SortedStations / GetAppliedAssemblyAtStation / BaselineRegions, CalculatedPoint.StationOffsetElevationToBaseline,
///   BaselineRegion.Split / Merge / Match(RegionMatchType) / Name / AssemblyId / GetTargets / AppliedAssemblySetting.
/// </summary>
public static partial class CorridorBowtieCommands
{
  /// <summary>BaselineRegion.Split needs the split station at least 0.01 inside the region.</summary>
  private const double RegionMargin = 0.01;
  private const double StationTolerance = 1e-6;

  // =========================================================================
  // predictCorridorBowties
  // =========================================================================

  private sealed class EdgeSample
  {
    public double Station;
    public double X;
    public double Y;
    public double Width;
    public double Dx;
    public double Dy;
  }

  private sealed class Loop
  {
    public int BaselineIndex;
    public string Side = "";
    public int IntervalIndex;
    public double IntervalStart;
    public double IntervalEnd;
    public double Start;
    public double End;
    public bool HasCrossing;
    public int CrossingCount;
    public double CrossX;
    public double CrossY;
    public double CrossSpan;
    public int BackwardSteps;
    public double MaxWidth;
    public double DeflectionDeg;
    public double SharpestTurnDeg;
    public double? MinRadius;
    public List<EdgeSample> Samples = new();
  }

  public static Task<object?> PredictCorridorBowtiesAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex");
    var side = (PluginRuntime.GetOptionalString(parameters, "side") ?? "both").Trim().ToLowerInvariant();
    var widthSource = (PluginRuntime.GetOptionalString(parameters, "widthSource") ?? "built").Trim().ToLowerInvariant();
    var insideWidth = PluginRuntime.GetOptionalDouble(parameters, "insideWidth");
    var leftWidth = PluginRuntime.GetOptionalDouble(parameters, "leftWidth") ?? insideWidth;
    var rightWidth = PluginRuntime.GetOptionalDouble(parameters, "rightWidth") ?? insideWidth;
    var code = PluginRuntime.GetOptionalString(parameters, "code");
    var sampleStep = PluginRuntime.GetOptionalDouble(parameters, "sampleStep");
    var padding = PluginRuntime.GetOptionalDouble(parameters, "padding") ?? 1.0;
    var mergeGap = PluginRuntime.GetOptionalDouble(parameters, "mergeGap") ?? 2.0;
    var maxLoopLength = PluginRuntime.GetOptionalDouble(parameters, "maxLoopLength") ?? 200.0;
    var includeEdges = PluginRuntime.GetOptionalBool(parameters, "includeEdges") ?? false;

    if (side is not ("left" or "right" or "both"))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "side must be left, right or both.");
    if (widthSource is not ("built" or "fixed"))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "widthSource must be built (read the corridor sections) or fixed (use insideWidth / leftWidth / rightWidth).");
    if (widthSource == "fixed" && ((side != "right" && !(leftWidth > 0)) || (side != "left" && !(rightWidth > 0))))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "widthSource fixed needs insideWidth (or leftWidth / rightWidth) greater than zero for every side scanned.");
    if (sampleStep is <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "sampleStep must be greater than zero.");
    if (padding < 0 || mergeGap < 0 || maxLoopLength <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "padding and mergeGap must be >= 0 and maxLoopLength > 0.");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForRead);
      var baselineIndices = new List<int>();
      if (baselineIndex.HasValue)
      {
        if (baselineIndex.Value < 0 || baselineIndex.Value >= corridor.Baselines.Count)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"Baseline index {baselineIndex.Value} is out of range. Corridor '{corridor.Name}' has {corridor.Baselines.Count} baseline(s).");
        baselineIndices.Add(baselineIndex.Value);
      }
      else
      {
        for (var i = 0; i < corridor.Baselines.Count; i++) baselineIndices.Add(i);
      }

      var warnings = new List<string>();
      var allLoops = new List<Loop>();
      var baselineReports = new List<Dictionary<string, object?>>();
      var splitPlan = new List<Dictionary<string, object?>>();

      foreach (var bi in baselineIndices)
      {
        var baseline = corridor.Baselines[bi];
        var intervals = CoveredIntervals(baseline);
        if (intervals.Count == 0)
        {
          warnings.Add($"Baseline {bi} ('{baseline.Name}') has no regions.");
          continue;
        }

        var totalLength = intervals.Sum(iv => iv.End - iv.Start);
        var step = sampleStep ?? Math.Clamp(totalLength / 4000.0, 0.1, 0.5);
        var widthTable = widthSource == "built" ? BuildWidthTable(baseline, code) : new List<(double Station, double? Left, double? Right)>();
        if (widthSource == "built" && widthTable.Count == 0)
          warnings.Add($"Baseline {bi} ('{baseline.Name}') has no applied sections - rebuild the corridor, or use widthSource fixed.");

        var sides = side == "both" ? new[] { "left", "right" } : new[] { side };
        var baselineLoops = new List<Loop>();
        var samplesPerSide = new Dictionary<string, int>();

        for (var ii = 0; ii < intervals.Count; ii++)
        {
          var (a, b) = intervals[ii];
          var stations = SampleStations(a, b, step);
          foreach (var sd in sides)
          {
            var sign = sd == "left" ? -1.0 : 1.0;
            var edge = new List<EdgeSample>(stations.Count);
            foreach (var s in stations)
            {
              double? w = widthSource == "fixed"
                ? (sd == "left" ? leftWidth : rightWidth)
                : InterpolateWidth(widthTable, s, a, b, sd == "left");
              if (w is not > 1e-6) continue;
              var sample = MakeEdgeSample(baseline, s, sign * w.Value, step);
              if (sample != null) { sample.Width = w.Value; edge.Add(sample); }
            }
            samplesPerSide[sd] = (samplesPerSide.TryGetValue(sd, out var c) ? c : 0) + edge.Count;
            if (edge.Count < 3) continue;
            foreach (var loop in DetectLoops(edge, maxLoopLength, step))
            {
              loop.BaselineIndex = bi;
              loop.Side = sd;
              loop.IntervalIndex = ii;
              loop.IntervalStart = a;
              loop.IntervalEnd = b;
              baselineLoops.Add(loop);
            }
          }
        }

        baselineLoops.Sort((x, y) => x.Start.CompareTo(y.Start));
        allLoops.AddRange(baselineLoops);

        // Split plan: pad each loop, clamp to its covered interval, merge across sides when close.
        var ranges = baselineLoops
          .Select((l, idx) => (Start: Math.Max(l.IntervalStart, l.Start - padding), End: Math.Min(l.IntervalEnd, l.End + padding), l.IntervalIndex, Loop: l))
          .OrderBy(r => r.Start)
          .ToList();
        var merged = new List<(double Start, double End, int Interval, List<Loop> Loops)>();
        foreach (var r in ranges)
        {
          if (merged.Count > 0 && merged[^1].Interval == r.IntervalIndex && r.Start <= merged[^1].End + mergeGap)
          {
            var last = merged[^1];
            last.Loops.Add(r.Loop);
            merged[^1] = (last.Start, Math.Max(last.End, r.End), last.Interval, last.Loops);
          }
          else
          {
            merged.Add((r.Start, r.End, r.IntervalIndex, new List<Loop> { r.Loop }));
          }
        }

        foreach (var m in merged)
        {
          var touched = RegionsTouching(baseline, m.Start, m.End);
          splitPlan.Add(new Dictionary<string, object?>
          {
            ["baselineIndex"] = bi,
            ["startStation"] = Math.Round(m.Start, 3),
            ["endStation"] = Math.Round(m.End, 3),
            ["length"] = Math.Round(m.End - m.Start, 3),
            ["bowtieIds"] = m.Loops.Select(l => LoopId(allLoops, l)).ToList(),
            ["sides"] = m.Loops.Select(l => l.Side).Distinct().ToList(),
            ["regions"] = touched,
            ["crossesRegionBoundary"] = touched.Count > 1,
          });
        }

        baselineReports.Add(new Dictionary<string, object?>
        {
          ["baselineIndex"] = bi,
          ["baselineName"] = baseline.Name,
          ["featureLineBased"] = SafeIsFeatureLineBased(baseline),
          ["startStation"] = SafeDouble(() => baseline.StartStation),
          ["endStation"] = SafeDouble(() => baseline.EndStation),
          ["coveredIntervals"] = intervals.Select(iv => new Dictionary<string, object?> { ["startStation"] = iv.Start, ["endStation"] = iv.End }).ToList(),
          ["sampleStep"] = step,
          ["samplesPerSide"] = samplesPerSide,
          ["appliedSectionsRead"] = widthTable.Count,
          ["bowtieCount"] = baselineLoops.Count,
        });
      }

      var loopRows = allLoops.Select(l => LoopToRow(allLoops, l, includeEdges, padding)).ToList();

      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridor.Name,
        ["widthSource"] = widthSource,
        ["code"] = code,
        ["side"] = side,
        ["padding"] = padding,
        ["mergeGap"] = mergeGap,
        ["maxLoopLength"] = maxLoopLength,
        ["method"] = "Inside edge rebuilt as P(s) = baseline point + w(s) along the section; a bowtie is where P runs backwards against the baseline direction or crosses itself. Station range = from the crossing on the incoming side to the crossing on the outgoing side.",
        ["baselines"] = baselineReports,
        ["bowtieCount"] = allLoops.Count,
        ["bowties"] = loopRows,
        ["splitPlan"] = splitPlan,
        ["warnings"] = warnings,
      };
    });
  }

  private static string LoopId(List<Loop> all, Loop loop) => $"B{loop.BaselineIndex}-{loop.Side[0].ToString().ToUpperInvariant()}{all.IndexOf(loop) + 1:00}";

  private static Dictionary<string, object?> LoopToRow(List<Loop> all, Loop l, bool includeEdges, double padding)
  {
    string cause;
    double? formulaHalfRange = null;
    if (l.SharpestTurnDeg >= 5.0)
    {
      cause = "angle_point";
      var half = Math.Abs(l.DeflectionDeg) * Math.PI / 360.0;
      if (half < Math.PI / 2 - 1e-6) formulaHalfRange = Math.Round(l.MaxWidth * Math.Tan(half), 3);
    }
    else if (l.MinRadius.HasValue && l.MinRadius.Value < l.MaxWidth)
      cause = "curve_radius_below_width";
    else
      cause = "width_change_or_other";

    var row = new Dictionary<string, object?>
    {
      ["id"] = LoopId(all, l),
      ["baselineIndex"] = l.BaselineIndex,
      ["side"] = l.Side,
      ["startStation"] = Math.Round(l.Start, 3),
      ["endStation"] = Math.Round(l.End, 3),
      ["length"] = Math.Round(l.End - l.Start, 3),
      ["selfCrossing"] = l.HasCrossing,
      ["crossingCount"] = l.CrossingCount,
      ["crossingPoint"] = l.HasCrossing ? new Dictionary<string, object?> { ["x"] = l.CrossX, ["y"] = l.CrossY } : null,
      ["backwardSteps"] = l.BackwardSteps,
      ["maxInsideWidth"] = Math.Round(l.MaxWidth, 3),
      ["deflectionDeg"] = Math.Round(l.DeflectionDeg, 3),
      ["sharpestTurnDeg"] = Math.Round(l.SharpestTurnDeg, 3),
      ["minRadius"] = l.MinRadius.HasValue ? Math.Round(l.MinRadius.Value, 3) : null,
      ["cause"] = cause,
      ["formulaHalfRange"] = formulaHalfRange,
    };
    if (includeEdges)
    {
      row["edge"] = l.Samples
        .Where(p => p.Station >= l.Start - padding - 1e-9 && p.Station <= l.End + padding + 1e-9)
        .Select(p => new Dictionary<string, object?> { ["station"] = Math.Round(p.Station, 4), ["x"] = p.X, ["y"] = p.Y, ["width"] = Math.Round(p.Width, 4) })
        .ToList();
    }
    return row;
  }

  /// <summary>Union of the baseline's region station ranges (regions that touch are joined).</summary>
  private static List<(double Start, double End)> CoveredIntervals(Baseline baseline)
  {
    var list = new List<(double Start, double End)>();
    var regions = baseline.BaselineRegions;
    for (var i = 0; i < regions.Count; i++)
    {
      try
      {
        var r = regions[i];
        if (r.EndStation > r.StartStation) list.Add((r.StartStation, r.EndStation));
      }
      catch (Exception ex) { PluginLog.Debug("Bowtie", "Region station read failed", ex); }
    }
    list.Sort((x, y) => x.Start.CompareTo(y.Start));
    var merged = new List<(double Start, double End)>();
    foreach (var iv in list)
    {
      if (merged.Count > 0 && iv.Start <= merged[^1].End + 1e-4)
        merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, iv.End));
      else
        merged.Add(iv);
    }
    return merged;
  }

  private static List<double> SampleStations(double a, double b, double step)
  {
    var list = new List<double>();
    var n = Math.Max(1, (int)Math.Ceiling((b - a) / step));
    var d = (b - a) / n;
    for (var k = 0; k <= n; k++) list.Add(a + k * d);
    return list;
  }

  /// <summary>Outermost calculated point offset on each side at every applied station (optionally only points with one code).</summary>
  private static List<(double Station, double? Left, double? Right)> BuildWidthTable(Baseline baseline, string? code)
  {
    var rows = new List<(double Station, double? Left, double? Right)>();
    double[] stations;
    try { stations = baseline.SortedStations() ?? Array.Empty<double>(); }
    catch (Exception ex) { PluginLog.Debug("Bowtie", "SortedStations failed", ex); return rows; }

    foreach (var s in stations)
    {
      AppliedAssembly applied;
      try { applied = baseline.GetAppliedAssemblyAtStation(s); }
      catch { continue; }
      double? left = null, right = null;
      try
      {
        foreach (CalculatedPoint p in applied.Points)
        {
          if (!string.IsNullOrWhiteSpace(code) && !HasCode(p.CorridorCodes, code!)) continue;
          var offset = p.StationOffsetElevationToBaseline.Y;
          if (offset < -1e-9) left = Math.Max(left ?? 0.0, -offset);
          else if (offset > 1e-9) right = Math.Max(right ?? 0.0, offset);
        }
      }
      catch (Exception ex) { PluginLog.Debug("Bowtie", $"Applied points at {s:0.###} not readable", ex); continue; }
      rows.Add((s, left, right));
    }
    rows.Sort((x, y) => x.Station.CompareTo(y.Station));
    return rows;
  }

  private static bool HasCode(CorridorCodeCollection? codes, string code)
  {
    if (codes == null) return false;
    try
    {
      foreach (var c in codes)
        if (string.Equals(c?.ToString(), code, StringComparison.OrdinalIgnoreCase)) return true;
    }
    catch { }
    return false;
  }

  /// <summary>Linear interpolation of the side width between the nearest applied stations inside [a, b] that have a value.</summary>
  private static double? InterpolateWidth(List<(double Station, double? Left, double? Right)> table, double s, double a, double b, bool left)
  {
    (double Station, double Value)? before = null, after = null;
    foreach (var row in table)
    {
      if (row.Station < a - StationTolerance || row.Station > b + StationTolerance) continue;
      var v = left ? row.Left : row.Right;
      if (!v.HasValue) continue;
      if (row.Station <= s + StationTolerance) before = (row.Station, v.Value);
      if (row.Station >= s - StationTolerance) { after = (row.Station, v.Value); break; }
    }
    if (before.HasValue && after.HasValue)
    {
      var span = after.Value.Station - before.Value.Station;
      if (span < StationTolerance) return Math.Max(before.Value.Value, after.Value.Value);
      var t = (s - before.Value.Station) / span;
      return before.Value.Value + t * (after.Value.Value - before.Value.Value);
    }
    return before?.Value ?? after?.Value;
  }

  private static EdgeSample? MakeEdgeSample(Baseline baseline, double station, double signedOffset, double step)
  {
    Point3d p;
    try { p = baseline.StationOffsetElevationToXYZ(new Point3d(station, signedOffset, 0.0)); }
    catch { return null; }
    if (Math.Abs(p.X) < 1e-9 && Math.Abs(p.Y) < 1e-9) return null; // the API returns (0,0,0) instead of throwing

    double dx = 0, dy = 0;
    try
    {
      var v = baseline.GetDirectionAtStation(station);
      dx = v.X; dy = v.Y;
    }
    catch { }
    var len = Math.Sqrt(dx * dx + dy * dy);
    if (len < 1e-12)
    {
      try
      {
        var h = Math.Max(step * 0.25, 0.01);
        var p0 = baseline.StationOffsetElevationToXYZ(new Point3d(station - h, 0.0, 0.0));
        var p1 = baseline.StationOffsetElevationToXYZ(new Point3d(station + h, 0.0, 0.0));
        dx = p1.X - p0.X; dy = p1.Y - p0.Y;
        len = Math.Sqrt(dx * dx + dy * dy);
      }
      catch { }
    }
    if (len < 1e-12) return null;
    return new EdgeSample { Station = station, X = p.X, Y = p.Y, Dx = dx / len, Dy = dy / len };
  }

  /// <summary>
  /// Finds the loops of one side's edge polyline: self-crossings (segment i crosses segment j, j >= i + 2, within
  /// maxLoopLength of station) and backward steps (the step between two samples points against the baseline direction).
  /// Overlapping crossing ranges are merged into one loop; backward runs not inside a crossing loop become their own entry.
  /// </summary>
  private static List<Loop> DetectLoops(List<EdgeSample> e, double maxLoopLength, double step)
  {
    var n = e.Count;
    var backward = new bool[n - 1];
    for (var k = 0; k < n - 1; k++)
    {
      var vx = e[k + 1].X - e[k].X;
      var vy = e[k + 1].Y - e[k].Y;
      var tx = e[k].Dx + e[k + 1].Dx;
      var ty = e[k].Dy + e[k + 1].Dy;
      var tl = Math.Sqrt(tx * tx + ty * ty);
      if (tl < 1e-12) { tx = e[k].Dx; ty = e[k].Dy; tl = 1.0; }
      backward[k] = (vx * tx + vy * ty) / tl < -1e-9;
    }

    // Segment bounding boxes for a cheap pre-test.
    var minX = new double[n - 1]; var maxX = new double[n - 1]; var minY = new double[n - 1]; var maxY = new double[n - 1];
    for (var k = 0; k < n - 1; k++)
    {
      minX[k] = Math.Min(e[k].X, e[k + 1].X); maxX[k] = Math.Max(e[k].X, e[k + 1].X);
      minY[k] = Math.Min(e[k].Y, e[k + 1].Y); maxY[k] = Math.Max(e[k].Y, e[k + 1].Y);
    }

    var crossings = new List<(double Start, double End, double X, double Y)>();
    for (var i = 0; i < n - 1; i++)
    {
      for (var j = i + 2; j < n - 1; j++)
      {
        if (e[j].Station - e[i + 1].Station > maxLoopLength) break;
        if (maxX[i] < minX[j] || maxX[j] < minX[i] || maxY[i] < minY[j] || maxY[j] < minY[i]) continue;
        if (!SegmentIntersection(e[i], e[i + 1], e[j], e[j + 1], out var ti, out var tj, out var x, out var y)) continue;
        var sa = e[i].Station + ti * (e[i + 1].Station - e[i].Station);
        var sb = e[j].Station + tj * (e[j + 1].Station - e[j].Station);
        crossings.Add((Math.Min(sa, sb), Math.Max(sa, sb), x, y));
      }
    }

    var loops = new List<Loop>();
    foreach (var c in crossings.OrderBy(c => c.Start))
    {
      var last = loops.Count > 0 ? loops[^1] : null;
      if (last != null && c.Start <= last.End + 1e-9)
      {
        last.End = Math.Max(last.End, c.End);
        last.CrossingCount++;
        if (c.End - c.Start > last.CrossSpan) { last.CrossSpan = c.End - c.Start; last.CrossX = c.X; last.CrossY = c.Y; }
      }
      else
      {
        loops.Add(new Loop { Start = c.Start, End = c.End, HasCrossing = true, CrossingCount = 1, CrossX = c.X, CrossY = c.Y, CrossSpan = c.End - c.Start });
      }
    }

    // Backward runs outside any crossing loop (e.g. a fold that doesn't close before the region ends).
    var k0 = 0;
    while (k0 < n - 1)
    {
      if (!backward[k0]) { k0++; continue; }
      var k1 = k0;
      while (k1 + 1 < n - 1 && backward[k1 + 1]) k1++;
      var rs = e[k0].Station;
      var re = e[k1 + 1].Station;
      if (!loops.Any(l => rs <= l.End + step && re >= l.Start - step))
        loops.Add(new Loop { Start = rs, End = re, HasCrossing = false });
      k0 = k1 + 1;
    }
    loops.Sort((x, y) => x.Start.CompareTo(y.Start));

    // Per-loop statistics.
    foreach (var l in loops)
    {
      var lo = l.Start - 2 * step;
      var hi = l.End + 2 * step;
      l.Samples = e.Where(p => p.Station >= lo - 50 * step && p.Station <= hi + 50 * step).ToList();
      var inRange = new List<int>();
      for (var k = 0; k < n; k++) if (e[k].Station >= lo && e[k].Station <= hi) inRange.Add(k);
      if (inRange.Count == 0) continue;
      l.MaxWidth = inRange.Max(k => e[k].Width);
      l.BackwardSteps = inRange.Count(k => k < n - 1 && backward[k]);
      var first = e[inRange[0]];
      var lastSample = e[inRange[^1]];
      l.DeflectionDeg = Math.Atan2(first.Dx * lastSample.Dy - first.Dy * lastSample.Dx, first.Dx * lastSample.Dx + first.Dy * lastSample.Dy) * 180.0 / Math.PI;
      double sharpest = 0;
      double? minRadius = null;
      for (var q = 0; q < inRange.Count - 1; q++)
      {
        var p0 = e[inRange[q]];
        var p1 = e[inRange[q + 1]];
        var turn = Math.Abs(Math.Atan2(p0.Dx * p1.Dy - p0.Dy * p1.Dx, p0.Dx * p1.Dx + p0.Dy * p1.Dy));
        sharpest = Math.Max(sharpest, turn);
        var ds = p1.Station - p0.Station;
        if (turn > 1e-9 && ds > 0)
        {
          var r = ds / turn;
          minRadius = minRadius.HasValue ? Math.Min(minRadius.Value, r) : r;
        }
      }
      l.SharpestTurnDeg = sharpest * 180.0 / Math.PI;
      l.MinRadius = minRadius;
    }
    return loops;
  }

  private static bool SegmentIntersection(EdgeSample a0, EdgeSample a1, EdgeSample b0, EdgeSample b1, out double ta, out double tb, out double x, out double y)
  {
    ta = tb = x = y = 0;
    var rx = a1.X - a0.X; var ry = a1.Y - a0.Y;
    var sx = b1.X - b0.X; var sy = b1.Y - b0.Y;
    var denom = rx * sy - ry * sx;
    if (Math.Abs(denom) < 1e-12) return false; // parallel / collinear: not a proper crossing
    var qx = b0.X - a0.X; var qy = b0.Y - a0.Y;
    ta = (qx * sy - qy * sx) / denom;
    tb = (qx * ry - qy * rx) / denom;
    // Inclusive: non-adjacent segments that only touch at a vertex (exact on a synthetic 90-degree corner) still count.
    const double eps = 1e-9;
    if (ta < -eps || ta > 1 + eps || tb < -eps || tb > 1 + eps) return false;
    x = a0.X + ta * rx;
    y = a0.Y + ta * ry;
    return true;
  }

  private static List<Dictionary<string, object?>> RegionsTouching(Baseline baseline, double start, double end)
  {
    var list = new List<Dictionary<string, object?>>();
    var regions = baseline.BaselineRegions;
    for (var i = 0; i < regions.Count; i++)
    {
      var r = regions[i];
      if (r.EndStation <= start + StationTolerance || r.StartStation >= end - StationTolerance) continue;
      list.Add(new Dictionary<string, object?>
      {
        ["regionIndex"] = i,
        ["regionName"] = r.Name,
        ["startStation"] = r.StartStation,
        ["endStation"] = r.EndStation,
      });
    }
    return list;
  }

  // =========================================================================
  // splitCorridorRegion
  // =========================================================================

  public static Task<object?> SplitCorridorRegionAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var regionIndex = PluginRuntime.GetOptionalInt(parameters, "regionIndex");
    var regionName = PluginRuntime.GetOptionalString(parameters, "regionName");
    var station = PluginRuntime.GetRequiredDouble(parameters, "station");
    var newRegionName = PluginRuntime.GetOptionalString(parameters, "newRegionName");
    var matchParent = PluginRuntime.GetOptionalBool(parameters, "matchParent") ?? true;
    var rebuild = PluginRuntime.GetOptionalBool(parameters, "rebuild") ?? true;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForWrite);
      var baseline = GetBaseline(corridor, baselineIndex);
      var region = regionIndex.HasValue || !string.IsNullOrWhiteSpace(regionName)
        ? FindRegion(baseline, regionIndex, regionName)
        : RegionAtStation(baseline, station)
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"No region of baseline {baselineIndex} contains station {station:0.###}. Nothing was split.");

      if (station <= region.StartStation + RegionMargin || station >= region.EndStation - RegionMargin)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"Station {station:0.###} must be more than {RegionMargin} inside region '{region.Name}' ({region.StartStation:0.###}-{region.EndStation:0.###}). Nothing was split.");

      var parentName = region.Name;
      var parentTargets = TargetSignature(region, transaction);
      var parentFrequency = FrequencySignature(region);

      BaselineRegion created;
      try { created = region.Split(station); }
      catch (Exception ex) when (ex is not JsonRpcDispatchException)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Civil 3D refused to split region '{parentName}' at {station:0.###}: {ex.GetType().Name}: {ex.Message}. Nothing was split.");
      }

      var inherited = new Dictionary<string, object?>
      {
        ["targets"] = TargetSignature(created, transaction).SequenceEqual(parentTargets),
        ["frequency"] = FrequencySignature(created) == parentFrequency,
        ["assembly"] = SafeAssemblyId(created) == SafeAssemblyId(region),
      };
      if (matchParent) TryMatch(created, region);
      if (!string.IsNullOrWhiteSpace(newRegionName)) created.Name = UniqueRegionName(baseline, newRegionName!, created);

      var rebuildError = rebuild ? TryRebuild(corridor) : null;
      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridor.Name,
        ["baselineIndex"] = baselineIndex,
        ["splitStation"] = station,
        ["originalRegion"] = parentName,
        ["newRegion"] = created.Name,
        ["splitKeptFromParent"] = inherited,
        ["matchedToParent"] = matchParent,
        ["rebuilt"] = rebuild && rebuildError == null,
        ["rebuildError"] = rebuildError,
        ["regions"] = ListRegions(baseline, transaction),
      };
    });
  }

  // =========================================================================
  // isolateCorridorRanges
  // =========================================================================

  public static Task<object?> IsolateCorridorRangesAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var namePrefix = PluginRuntime.GetOptionalString(parameters, "namePrefix") ?? "BT";
    var frequency = PluginRuntime.GetOptionalDouble(parameters, "frequency");
    var assemblyName = PluginRuntime.GetOptionalString(parameters, "assemblyName");
    var matchParent = PluginRuntime.GetOptionalBool(parameters, "matchParent") ?? true;
    var carrySurfaceTargets = PluginRuntime.GetOptionalBool(parameters, "carrySurfaceTargets") ?? true;
    var rebuild = PluginRuntime.GetOptionalBool(parameters, "rebuild") ?? true;
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;
    // parent assembly name -> assembly to give that part (a clash range over two regions with different assemblies)
    var assemblyMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (PluginRuntime.GetParameter(parameters, "assemblyMap") is JsonObject mapNode)
      foreach (var kv in mapNode) if (kv.Value is JsonValue v && v.TryGetValue<string>(out var target) && !string.IsNullOrWhiteSpace(target)) assemblyMap[kv.Key] = target;
    var rangesNode = PluginRuntime.GetParameter(parameters, "ranges") as JsonArray
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "ranges array is required: [{startStation, endStation, name?}] (use bowtie_predict's splitPlan).");
    if (frequency is <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "frequency must be greater than zero.");

    var ranges = new List<(double Start, double End, string? Name)>();
    foreach (var node in rangesNode)
    {
      if (node is not JsonObject o) continue;
      var s = o["startStation"]?.GetValue<double>();
      var e = o["endStation"]?.GetValue<double>();
      if (!s.HasValue || !e.HasValue || e.Value <= s.Value)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Every range needs startStation < endStation. Nothing was changed.");
      ranges.Add((s.Value, e.Value, o["name"]?.GetValue<string>()));
    }
    if (ranges.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "ranges is empty. Nothing was changed.");
    ranges.Sort((x, y) => x.Start.CompareTo(y.Start));
    for (var i = 1; i < ranges.Count; i++)
      if (ranges[i].Start < ranges[i - 1].End - StationTolerance)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"Ranges {ranges[i - 1].Start:0.###}-{ranges[i - 1].End:0.###} and {ranges[i].Start:0.###}-{ranges[i].End:0.###} overlap - merge them first. Nothing was changed.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, dryRun ? OpenMode.ForRead : OpenMode.ForWrite);
      var baseline = GetBaseline(corridor, baselineIndex);
      ObjectId? assemblyId = string.IsNullOrWhiteSpace(assemblyName) ? null : FindAssemblyId(civilDoc, transaction, assemblyName!);

      var counter = NextNumber(baseline, namePrefix);
      var isolated = new List<Dictionary<string, object?>>();
      var skipped = new List<Dictionary<string, object?>>();
      var names = new List<(string Before, string Mid, string After, string Parent)>();

      foreach (var range in ranges)
      {
        // A range that crosses region boundaries is isolated part by part (one region per part).
        var parts = new List<(double Start, double End, BaselineRegion Region)>();
        var regions = baseline.BaselineRegions;
        for (var i = 0; i < regions.Count; i++)
        {
          var r = regions[i];
          var a = Math.Max(range.Start, r.StartStation);
          var b = Math.Min(range.End, r.EndStation);
          if (b - a > StationTolerance) parts.Add((a, b, r));
        }
        if (parts.Count == 0)
        {
          skipped.Add(new Dictionary<string, object?> { ["startStation"] = range.Start, ["endStation"] = range.End, ["reason"] = "not inside any region (gap)" });
          continue;
        }

        for (var pi = 0; pi < parts.Count; pi++)
        {
          var (a, b, partRegion) = parts[pi];
          // Splitting an earlier part shifts the region list: look the region up again by station.
          var region = RegionAtStation(baseline, 0.5 * (a + b)) ?? partRegion;
          if (b - a < 2 * RegionMargin)
          {
            skipped.Add(new Dictionary<string, object?> { ["startStation"] = a, ["endStation"] = b, ["reason"] = $"shorter than {2 * RegionMargin} m" });
            continue;
          }
          var splitStart = a - region.StartStation > RegionMargin + StationTolerance;
          var splitEnd = region.EndStation - b > RegionMargin + StationTolerance;
          var baseName = !string.IsNullOrWhiteSpace(range.Name) ? range.Name! : $"{namePrefix}-{counter:00}";
          var targetName = parts.Count > 1 ? $"{baseName}{(char)('a' + pi)}" : baseName;

          if (dryRun)
          {
            isolated.Add(new Dictionary<string, object?>
            {
              ["name"] = targetName,
              ["startStation"] = a,
              ["endStation"] = b,
              ["fromRegion"] = region.Name,
              ["splitAtStart"] = splitStart,
              ["splitAtEnd"] = splitEnd,
              ["planned"] = true,
            });
            continue;
          }

          var parentName = region.Name;
          var parentTargets = TargetSignature(region, transaction);
          var parentFrequency = FrequencySignature(region);
          var parentAssembly = SafeAssemblyId(region);
          var parentAssemblyName = AssemblyName(transaction, parentAssembly);
          var partAssemblyId = assemblyId;
          if (parentAssemblyName != null && assemblyMap.TryGetValue(parentAssemblyName, out var mapped)) partAssemblyId = FindAssemblyId(civilDoc, transaction, mapped);

          BaselineRegion mid = region;
          BaselineRegion? tail = null;
          try
          {
            if (splitStart) mid = region.Split(a);
            if (splitEnd) tail = mid.Split(b);
          }
          catch (Exception ex) when (ex is not JsonRpcDispatchException)
          {
            throw new JsonRpcDispatchException("CIVIL3D.API_ERROR",
              $"Civil 3D refused to split region '{parentName}' for {a:0.###}-{b:0.###}: {ex.GetType().Name}: {ex.Message}. The transaction was rolled back; nothing was changed.");
          }

          var kept = new Dictionary<string, object?>();
          if (splitStart || splitEnd)
          {
            var probe = splitStart ? mid : tail!;
            kept["targets"] = TargetSignature(probe, transaction).SequenceEqual(parentTargets);
            kept["frequency"] = FrequencySignature(probe) == parentFrequency;
            kept["assembly"] = SafeAssemblyId(probe) == parentAssembly;
          }

          if (matchParent)
          {
            if (splitStart) TryMatch(mid, region);
            if (tail != null) TryMatch(tail, region);
          }

          var beforeName = splitStart ? region.Name : "";
          mid.Name = UniqueRegionName(baseline, targetName, mid);
          if (tail != null)
          {
            // Split names the remainder "<parent> [Copy] [Copy]"; use "<parent> (2)", "(3)" ... instead.
            try { tail.Name = UniqueRegionName(baseline, StripCopySuffix(parentName) + " (2)", tail); } catch (Exception ex) { PluginLog.Debug("Bowtie", "Rename of split remainder failed", ex); }
          }
          var afterName = tail?.Name ?? "";

          var carried = new List<string>();
          if (partAssemblyId.HasValue)
          {
            // A new assembly drops every target of the region. Its surface targets are given the parent's surface straight
            // away, in this same transaction, so the region is never rebuilt with "Surface target is not specified".
            ObjectIdCollection? parentSurfaces = null;
            try
            {
              var parentInfos = region.GetTargets();
              for (var ti = 0; ti < parentInfos.Count; ti++)
                if (parentInfos[ti].TargetType == SubassemblyLogicalNameType.Surface && parentInfos[ti].TargetIds.Count > 0)
                { parentSurfaces = parentInfos[ti].TargetIds; break; }
            }
            catch (Exception ex) { PluginLog.Debug("Bowtie", "Parent targets not readable", ex); }

            mid.AssemblyId = partAssemblyId.Value;

            if (carrySurfaceTargets && parentSurfaces != null)
            {
              try
              {
                var infos = mid.GetTargets();
                for (var ti = 0; ti < infos.Count; ti++)
                {
                  if (infos[ti].TargetType != SubassemblyLogicalNameType.Surface || infos[ti].TargetIds.Count > 0) continue;
                  var ids = new ObjectIdCollection();
                  foreach (ObjectId id in parentSurfaces) ids.Add(id);
                  infos[ti].TargetIds = ids;
                  carried.Add($"{infos[ti].SubassemblyName}:{infos[ti].LogicalName}");
                }
                if (carried.Count > 0) mid.SetTargets(infos);
              }
              catch (Exception ex) when (ex is not JsonRpcDispatchException)
              {
                throw new JsonRpcDispatchException("CIVIL3D.API_ERROR",
                  $"The assembly of region '{mid.Name}' was changed but its surface targets could not be set: {ex.GetType().Name}: {ex.Message}. The transaction was rolled back; nothing was changed.");
              }
            }
          }
          if (frequency.HasValue) ApplyFrequency(mid, frequency.Value);

          names.Add((beforeName, mid.Name, afterName, parentName));
          isolated.Add(new Dictionary<string, object?>
          {
            ["name"] = mid.Name,
            ["startStation"] = mid.StartStation,
            ["endStation"] = mid.EndStation,
            ["fromRegion"] = parentName,
            ["parentAssemblyName"] = parentAssemblyName,
            ["regionBefore"] = splitStart ? beforeName : null,
            ["regionAfter"] = afterName.Length > 0 ? afterName : null,
            ["splitAtStart"] = splitStart,
            ["splitAtEnd"] = splitEnd,
            ["splitKeptFromParent"] = kept.Count > 0 ? kept : null,
            ["matchedToParent"] = matchParent,
            ["assemblyName"] = AssemblyName(transaction, SafeAssemblyId(mid)),
            ["surfaceTargetsCarried"] = carried,
            ["frequency"] = ReadFrequency(mid),
            ["parentFrequency"] = parentFrequency,
          });
        }
        counter++;
      }

      string? rebuildError = null;
      if (!dryRun && rebuild && isolated.Count > 0) rebuildError = TryRebuild(corridor);

      // Undo plan: merge each BT region back with the pieces it was cut from (highest index first so indices stay valid).
      var undo = new List<Dictionary<string, object?>>();
      if (!dryRun)
      {
        foreach (var (before, mid, after, parent) in names)
        {
          var first = !string.IsNullOrEmpty(before) ? IndexOfName(baseline, before) : IndexOfName(baseline, mid);
          var last = !string.IsNullOrEmpty(after) ? IndexOfName(baseline, after) : IndexOfName(baseline, mid);
          if (first >= 0 && last > first)
            undo.Add(new Dictionary<string, object?> { ["firstRegionIndex"] = first, ["lastRegionIndex"] = last, ["regions"] = new[] { before, mid, after }.Where(x => !string.IsNullOrEmpty(x)).ToList(), ["restoreName"] = parent });
        }
        undo = undo.OrderByDescending(u => (int)u["firstRegionIndex"]!).ToList();
      }

      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridor.Name,
        ["baselineIndex"] = baselineIndex,
        ["dryRun"] = dryRun,
        ["isolated"] = isolated,
        ["skipped"] = skipped,
        ["rebuilt"] = !dryRun && rebuild && rebuildError == null && isolated.Count > 0,
        ["rebuildError"] = rebuildError,
        ["regions"] = ListRegions(baseline, transaction),
        ["undo"] = undo,
        ["undoNote"] = undo.Count > 0 ? "Run region_merge for each entry in this order (highest index first, newRegionName = restoreName) to put the regions back." : null,
      };
    });
  }

  // =========================================================================
  // mergeCorridorRegions
  // =========================================================================

  public static Task<object?> MergeCorridorRegionsAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var firstIndex = PluginRuntime.GetRequiredInt(parameters, "firstRegionIndex");
    var lastIndex = PluginRuntime.GetRequiredInt(parameters, "lastRegionIndex");
    var newRegionName = PluginRuntime.GetOptionalString(parameters, "newRegionName");
    var rebuild = PluginRuntime.GetOptionalBool(parameters, "rebuild") ?? true;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForWrite);
      var baseline = GetBaseline(corridor, baselineIndex);
      var regions = baseline.BaselineRegions;
      if (firstIndex < 0 || lastIndex >= regions.Count || lastIndex <= firstIndex)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"Need 0 <= firstRegionIndex < lastRegionIndex < {regions.Count}. Nothing was merged.");

      var first = regions[firstIndex];
      var last = regions[lastIndex];
      var mergedNames = new List<string>();
      for (var i = firstIndex; i <= lastIndex; i++) mergedNames.Add(regions[i].Name);
      var start = first.StartStation;
      var end = last.EndStation;

      try { first.Merge(first, last); }
      catch (Exception ex) when (ex is not JsonRpcDispatchException)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Civil 3D refused to merge regions {firstIndex}-{lastIndex}: {ex.GetType().Name}: {ex.Message}. Nothing was merged.");
      }
      if (!string.IsNullOrWhiteSpace(newRegionName)) first.Name = UniqueRegionName(baseline, newRegionName!, first);

      var rebuildError = rebuild ? TryRebuild(corridor) : null;
      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridor.Name,
        ["baselineIndex"] = baselineIndex,
        ["mergedRegions"] = mergedNames,
        ["result"] = new Dictionary<string, object?> { ["name"] = first.Name, ["startStation"] = first.StartStation, ["endStation"] = first.EndStation },
        ["expectedRange"] = new Dictionary<string, object?> { ["startStation"] = start, ["endStation"] = end },
        ["rebuilt"] = rebuild && rebuildError == null,
        ["rebuildError"] = rebuildError,
        ["regions"] = ListRegions(baseline, transaction),
      };
    });
  }

  // =========================================================================
  // Helpers
  // =========================================================================

  private static Baseline GetBaseline(Corridor corridor, int index)
  {
    if (index < 0 || index >= corridor.Baselines.Count)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"Baseline index {index} is out of range. Corridor '{corridor.Name}' has {corridor.Baselines.Count} baseline(s).");
    return corridor.Baselines[index];
  }

  private static BaselineRegion FindRegion(Baseline baseline, int? index, string? name)
  {
    var regions = baseline.BaselineRegions;
    if (index.HasValue)
    {
      if (index.Value < 0 || index.Value >= regions.Count)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Region index {index.Value} is out of range ({regions.Count} region(s)).");
      return regions[index.Value];
    }
    for (var i = 0; i < regions.Count; i++)
      if (string.Equals(regions[i].Name, name, StringComparison.OrdinalIgnoreCase)) return regions[i];
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Region '{name}' was not found on baseline '{baseline.Name}'.");
  }

  private static BaselineRegion? RegionAtStation(Baseline baseline, double station)
  {
    var regions = baseline.BaselineRegions;
    for (var i = 0; i < regions.Count; i++)
    {
      var r = regions[i];
      if (station > r.StartStation + StationTolerance && station < r.EndStation - StationTolerance) return r;
    }
    return null;
  }

  private static int IndexOfName(Baseline baseline, string name)
  {
    var regions = baseline.BaselineRegions;
    for (var i = 0; i < regions.Count; i++)
      if (string.Equals(regions[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
    return -1;
  }

  private static int NextNumber(Baseline baseline, string prefix)
  {
    var max = 0;
    var regions = baseline.BaselineRegions;
    for (var i = 0; i < regions.Count; i++)
    {
      var n = regions[i].Name ?? "";
      if (!n.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase)) continue;
      var digits = new string(n.Substring(prefix.Length + 1).TakeWhile(char.IsDigit).ToArray());
      if (int.TryParse(digits, out var v)) max = Math.Max(max, v);
    }
    return max + 1;
  }

  private static string UniqueRegionName(Baseline baseline, string wanted, BaselineRegion self)
  {
    var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var regions = baseline.BaselineRegions;
    for (var i = 0; i < regions.Count; i++)
    {
      var r = regions[i];
      if (ReferenceEquals(r, self) || (Math.Abs(r.StartStation - self.StartStation) < StationTolerance && Math.Abs(r.EndStation - self.EndStation) < StationTolerance)) continue;
      existing.Add(r.Name ?? "");
    }
    if (!existing.Contains(wanted)) return wanted;
    for (var k = 2; k < 1000; k++)
    {
      var candidate = $"{wanted} ({k})";
      if (!existing.Contains(candidate)) return candidate;
    }
    return $"{wanted} {Guid.NewGuid():N}";
  }

  private static string StripCopySuffix(string name)
  {
    var n = (name ?? "").Trim();
    while (n.EndsWith("[Copy]", StringComparison.OrdinalIgnoreCase)) n = n[..^6].TrimEnd();
    // "(k)" from an earlier rename: keep the base only
    var m = System.Text.RegularExpressions.Regex.Match(n, @"^(.*?)\s\(\d+\)$");
    return m.Success ? m.Groups[1].Value : n;
  }

  private static void TryMatch(BaselineRegion target, BaselineRegion source)
  {
    try { target.Match(source, RegionMatchType.All); }
    catch (Exception ex) { PluginLog.Warn("Bowtie", $"Match of region '{target.Name}' to '{source.Name}' failed", ex); }
  }

  private static ObjectId? SafeAssemblyId(BaselineRegion region)
  {
    try { return region.AssemblyId; } catch { return null; }
  }

  private static string? AssemblyName(Transaction transaction, ObjectId? id)
  {
    if (!id.HasValue || id.Value.IsNull) return null;
    try { return (transaction.GetObject(id.Value, OpenMode.ForRead) as CivilAssembly)?.Name; } catch { return null; }
  }

  private static ObjectId FindAssemblyId(CivilDocument civilDoc, Transaction transaction, string assemblyName)
  {
    foreach (ObjectId aid in civilDoc.AssemblyCollection)
    {
      var asm = CivilObjectUtils.GetRequiredObject<CivilAssembly>(transaction, aid, OpenMode.ForRead);
      if (string.Equals(asm.Name, assemblyName, StringComparison.OrdinalIgnoreCase)) return aid;
    }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Assembly '{assemblyName}' was not found. Nothing was changed.");
  }

  /// <summary>"subassembly:logicalName=target1|target2" per target, for comparing a split piece with its parent.</summary>
  private static List<string> TargetSignature(BaselineRegion region, Transaction transaction)
  {
    var list = new List<string>();
    try
    {
      var infos = region.GetTargets();
      for (var i = 0; i < infos.Count; i++)
      {
        var info = infos[i];
        var names = new List<string>();
        try
        {
          foreach (ObjectId id in info.TargetIds)
          {
            if (id.IsNull) continue;
            names.Add(CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead)) ?? id.Handle.ToString());
          }
        }
        catch { }
        list.Add($"{info.SubassemblyName}:{info.LogicalName}={string.Join("|", names)}");
      }
    }
    catch (Exception ex) { PluginLog.Debug("Bowtie", "GetTargets failed", ex); }
    list.Sort(StringComparer.Ordinal);
    return list;
  }

  private static string FrequencySignature(BaselineRegion region)
  {
    try
    {
      var s = region.AppliedAssemblySetting;
      var inv = System.Globalization.CultureInfo.InvariantCulture;
      return string.Join("/", new[] { s.FrequencyAlongTangents, s.FrequencyAlongCurves, s.FrequencyAlongSpirals, s.FrequencyAlongProfileCurves }.Select(f => f.ToString("0.###", inv)));
    }
    catch { return "?"; }
  }

  private static void ApplyFrequency(BaselineRegion region, double frequency)
  {
    var setting = region.AppliedAssemblySetting;
    setting.FrequencyAlongTangents = frequency;
    setting.FrequencyAlongCurves = frequency;
    setting.FrequencyAlongSpirals = frequency;
    setting.FrequencyAlongProfileCurves = frequency;
  }

  private static Dictionary<string, object?> ReadFrequency(BaselineRegion region)
  {
    try
    {
      var s = region.AppliedAssemblySetting;
      return new Dictionary<string, object?>
      {
        ["alongTangents"] = s.FrequencyAlongTangents,
        ["alongCurves"] = s.FrequencyAlongCurves,
        ["alongSpirals"] = s.FrequencyAlongSpirals,
        ["alongProfileCurves"] = s.FrequencyAlongProfileCurves,
      };
    }
    catch { return new Dictionary<string, object?>(); }
  }

  private static List<Dictionary<string, object?>> ListRegions(Baseline baseline, Transaction transaction)
  {
    var list = new List<Dictionary<string, object?>>();
    var regions = baseline.BaselineRegions;
    for (var i = 0; i < regions.Count; i++)
    {
      var r = regions[i];
      list.Add(new Dictionary<string, object?>
      {
        ["regionIndex"] = i,
        ["name"] = r.Name,
        ["startStation"] = r.StartStation,
        ["endStation"] = r.EndStation,
        ["assemblyName"] = AssemblyName(transaction, SafeAssemblyId(r)),
        ["frequency"] = FrequencySignature(r),
        ["targetsSet"] = TargetSignature(r, transaction).Count(t => !t.EndsWith("=", StringComparison.Ordinal)),
      });
    }
    return list;
  }

  private static string? TryRebuild(Corridor corridor)
  {
    try
    {
      corridor.Rebuild();
      return null;
    }
    catch (Exception ex)
    {
      PluginLog.Warn("Bowtie", $"Rebuild of '{corridor.Name}' failed", ex);
      return $"{ex.GetType().Name}: {ex.Message}";
    }
  }

  private static bool? SafeIsFeatureLineBased(Baseline baseline)
  {
    try { return baseline.IsFeatureLineBased(); } catch { return null; }
  }

  private static double? SafeDouble(Func<double> read)
  {
    try { return read(); } catch { return null; }
  }
}
