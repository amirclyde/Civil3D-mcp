using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Ribbon "Feature Line" drop-down on the documented Civil 3D 2026 API:
///
///   FeatureLine.Create(name, sourceEntityId[, siteId])      - from objects / from points (via a temporary 3D polyline)
///   FeatureLine.AssignElevationsFromSurface(surfaceId, includeIntermediate)
///   FeatureLine.InsertPIPoint / InsertElevationPoint / DeletePIPoint / DeleteElevationPoint
///   FeatureLine.SetPointElevation(index, elevation) / SetCurveRadius(index, radius) / GetPoints(type)
///   FeatureLine.MoveToSite / MoveToNoneSite
/// </summary>
public static class FeatureLineCommands
{
  // -------------------------------------------------------------------------
  // featureLineCreateFromObject: ribbon "Create Feature Lines from Objects"
  // -------------------------------------------------------------------------

  public static Task<object?> CreateFromObjectAsync(JsonObject? parameters)
  {
    var handle = PluginRuntime.GetRequiredString(parameters, "objectHandle");
    var name = PluginRuntime.GetOptionalString(parameters, "name");
    var siteName = PluginRuntime.GetOptionalString(parameters, "siteName");
    var style = PluginRuntime.GetOptionalString(parameters, "style");
    var layer = PluginRuntime.GetOptionalString(parameters, "layer");
    var surfaceName = PluginRuntime.GetOptionalString(parameters, "elevationSurface");
    var includeIntermediate = PluginRuntime.GetOptionalBool(parameters, "includeIntermediatePoints") ?? true;
    var eraseSource = PluginRuntime.GetOptionalBool(parameters, "eraseSource") ?? false;
    var constantElevation = PluginRuntime.GetOptionalDouble(parameters, "elevation");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var sourceId = ResolveHandle(database, handle);
      var source = transaction.GetObject(sourceId, OpenMode.ForRead);
      if (source is not Curve && source is not Polyline3d)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"Object {handle} is a {source.GetType().Name}; feature lines are created from lines, arcs, polylines, 2D/3D polylines and splines.");

      var siteId = LookupUtils.GetSiteId(civilDoc, transaction, siteName);
      var flName = name ?? $"FL - {handle}";
      ObjectId flId;
      try
      {
        flId = siteId.IsNull ? FeatureLine.Create(flName, sourceId) : FeatureLine.Create(flName, sourceId, siteId);
      }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Civil 3D could not create a feature line from {handle}: {ex.GetType().Name}: {ex.Message}");
      }

      var fl = CivilObjectUtils.GetRequiredObject<FeatureLine>(transaction, flId, OpenMode.ForWrite);
      ApplyStyleAndLayer(fl, civilDoc, database, transaction, style, layer);

      var elevationSource = "source geometry";
      if (!string.IsNullOrWhiteSpace(surfaceName))
      {
        var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, surfaceName, OpenMode.ForRead);
        try { fl.AssignElevationsFromSurface(surface.ObjectId, includeIntermediate); elevationSource = $"surface '{surface.Name}'"; }
        catch (Exception ex) { throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Feature line created but elevations from '{surfaceName}' failed: {ex.Message}"); }
      }
      else if (constantElevation.HasValue)
      {
        for (var i = 0; i < fl.PointsCount; i++) fl.SetPointElevation(i, constantElevation.Value);
        elevationSource = $"constant {constantElevation.Value:0.###}";
      }

      if (eraseSource)
      {
        var writable = transaction.GetObject(sourceId, OpenMode.ForWrite);
        writable.Erase();
      }

      var result = Describe(fl, transaction);
      result["sourceHandle"] = handle;
      result["sourceErased"] = eraseSource;
      result["elevationSource"] = elevationSource;
      result["created"] = true;
      return result;
    });
  }

  // -------------------------------------------------------------------------
  // featureLineCreateFromLayer: every polyline on a layer → feature line, vertex
  // elevations from the COGO points sitting on the vertices (drain design levels).
  // featureLineElevationsFromCogo: the same elevation assignment on existing feature lines.
  // -------------------------------------------------------------------------

  public static Task<object?> CreateFromLayerAsync(JsonObject? parameters)
  {
    var layer = PluginRuntime.GetRequiredString(parameters, "sourceLayer");
    var namePrefix = PluginRuntime.GetOptionalString(parameters, "namePrefix") ?? "FL";
    var names = (PluginRuntime.GetParameter(parameters, "names") as JsonArray)?.Select(n => n?.GetValue<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Cast<string>().ToList() ?? new List<string>();
    var siteName = PluginRuntime.GetOptionalString(parameters, "siteName");
    var style = PluginRuntime.GetOptionalString(parameters, "style");
    var targetLayer = PluginRuntime.GetOptionalString(parameters, "layer");
    var eraseSource = PluginRuntime.GetOptionalBool(parameters, "eraseSource") ?? false;
    var elevationsFrom = (PluginRuntime.GetOptionalString(parameters, "elevationsFrom") ?? "cogo").Trim().ToLowerInvariant();
    var tolerance = PluginRuntime.GetOptionalDouble(parameters, "tolerance") ?? 0.05;
    var addOnSegments = PluginRuntime.GetOptionalBool(parameters, "addElevationPointsOnSegments") ?? false;
    var pointGroupName = PluginRuntime.GetOptionalString(parameters, "pointGroupName");
    var surfaceName = PluginRuntime.GetOptionalString(parameters, "elevationSurface");
    var dryRun = PluginRuntime.GetOptionalBool(parameters, "dryRun") ?? false;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var ms = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead);
      var sources = new List<Curve>();
      foreach (ObjectId id in ms)
      {
        if (transaction.GetObject(id, OpenMode.ForRead) is not Curve curve) continue;
        if (!string.Equals(curve.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;
        if (curve is Polyline or Polyline2d or Polyline3d or Line or Arc) sources.Add(curve);
      }
      if (sources.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No polylines, lines or arcs on layer '{layer}'.");
      sources = sources.OrderBy(c => c.Handle.Value).ToList();

      var cogo = elevationsFrom == "cogo" ? LoadCogoPoints(civilDoc, transaction, pointGroupName) : new List<CogoRef>();
      if (elevationsFrom == "cogo" && cogo.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", pointGroupName == null ? "The drawing has no COGO points." : $"Point group '{pointGroupName}' has no points.");

      var siteId = LookupUtils.GetSiteId(civilDoc, transaction, siteName);
      var surface = string.IsNullOrWhiteSpace(surfaceName) ? null : CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, surfaceName, OpenMode.ForRead);
      var results = new List<Dictionary<string, object?>>();
      var index = 0;
      foreach (var source in sources)
      {
        index++;
        var flName = index - 1 < names.Count ? names[index - 1] : $"{namePrefix}-{index:00}";
        var entry = new Dictionary<string, object?> { ["sourceHandle"] = source.Handle.ToString(), ["sourceType"] = source.GetType().Name, ["name"] = flName };
        if (dryRun)
        {
          entry["vertices"] = PreviewVertices(source, cogo, tolerance);
          results.Add(entry);
          continue;
        }
        ObjectId flId;
        try { flId = siteId.IsNull ? FeatureLine.Create(flName, source.ObjectId) : FeatureLine.Create(flName, source.ObjectId, siteId); }
        catch (Exception ex)
        {
          entry["error"] = $"{ex.GetType().Name}: {ex.Message}";
          results.Add(entry);
          continue;
        }
        var fl = CivilObjectUtils.GetRequiredObject<FeatureLine>(transaction, flId, OpenMode.ForWrite);
        ApplyStyleAndLayer(fl, civilDoc, database, transaction, style, targetLayer);

        if (elevationsFrom == "cogo")
          entry["elevations"] = ApplyCogoElevations(fl, source, cogo, tolerance, addOnSegments);
        else if (elevationsFrom == "surface" && surface != null)
        {
          try { fl.AssignElevationsFromSurface(surface.ObjectId, true); entry["elevations"] = new Dictionary<string, object?> { ["source"] = $"surface '{surface.Name}'" }; }
          catch (Exception ex) { entry["elevationError"] = ex.Message; }
        }

        if (eraseSource) { transaction.GetObject(source.ObjectId, OpenMode.ForWrite).Erase(); }
        foreach (var kv in Describe(fl, transaction)) entry[kv.Key] = kv.Value;
        entry["created"] = true;
        results.Add(entry);
      }

      return new Dictionary<string, object?>
      {
        ["sourceLayer"] = layer,
        ["sourceCount"] = sources.Count,
        ["cogoPointsConsidered"] = cogo.Count,
        ["tolerance"] = tolerance,
        ["dryRun"] = dryRun,
        ["featureLines"] = results,
        ["unmatchedVertexTotal"] = results.Sum(r => (r.TryGetValue("elevations", out var e) && e is Dictionary<string, object?> d && d.TryGetValue("unmatchedVertexCount", out var u) && u is int n) ? n : 0),
      };
    });
  }

  public static Task<object?> ElevationsFromCogoAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetOptionalString(parameters, "name");
    var handle = PluginRuntime.GetOptionalString(parameters, "handle");
    var tolerance = PluginRuntime.GetOptionalDouble(parameters, "tolerance") ?? 0.05;
    var addOnSegments = PluginRuntime.GetOptionalBool(parameters, "addElevationPointsOnSegments") ?? false;
    var pointGroupName = PluginRuntime.GetOptionalString(parameters, "pointGroupName");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var fl = Find(civilDoc, database, transaction, name, handle, OpenMode.ForWrite);
      var cogo = LoadCogoPoints(civilDoc, transaction, pointGroupName);
      if (cogo.Count == 0) throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", "No COGO points to take elevations from.");
      var result = Describe(fl, transaction);
      result["elevations"] = ApplyCogoElevations(fl, null, cogo, tolerance, addOnSegments);
      foreach (var kv in Describe(fl, transaction)) result[kv.Key] = kv.Value;
      return result;
    });
  }

  private sealed record CogoRef(uint Number, string? Name, string? Description, Point3d Location);

  private static List<CogoRef> LoadCogoPoints(CivilDocument civilDoc, Transaction transaction, string? pointGroupName)
  {
    HashSet<uint>? allowed = null;
    if (!string.IsNullOrWhiteSpace(pointGroupName))
    {
      foreach (ObjectId gid in civilDoc.PointGroups)
      {
        var group = CivilObjectUtils.GetRequiredObject<PointGroup>(transaction, gid, OpenMode.ForRead);
        if (string.Equals(group.Name, pointGroupName, StringComparison.OrdinalIgnoreCase)) { allowed = new HashSet<uint>(group.GetPointNumbers()); break; }
      }
      if (allowed == null) throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Point group '{pointGroupName}' was not found.");
    }
    var list = new List<CogoRef>();
    foreach (ObjectId id in civilDoc.CogoPoints)
    {
      var p = CivilObjectUtils.GetRequiredObject<CogoPoint>(transaction, id, OpenMode.ForRead);
      if (allowed != null && !allowed.Contains(p.PointNumber)) continue;
      string? pname = null, desc = null;
      try { pname = p.PointName; } catch { }
      try { desc = p.RawDescription; } catch { }
      list.Add(new CogoRef(p.PointNumber, pname, desc, p.Location));
    }
    return list;
  }

  private static CogoRef? Nearest(List<CogoRef> cogo, double x, double y, double tolerance, out double distance)
  {
    CogoRef? best = null; distance = double.MaxValue;
    foreach (var c in cogo)
    {
      var d = Math.Sqrt((c.Location.X - x) * (c.Location.X - x) + (c.Location.Y - y) * (c.Location.Y - y));
      if (d < distance) { distance = d; best = c; }
    }
    return best != null && distance <= tolerance ? best : null;
  }

  /// <summary>Sets every PI elevation from the COGO point on it; reports unmatched PIs and COGO points on segments.</summary>
  private static Dictionary<string, object?> ApplyCogoElevations(FeatureLine fl, Curve? sourceCurve, List<CogoRef> cogo, double tolerance, bool addOnSegments)
  {
    var pis = fl.GetPoints(FeatureLinePointType.PIPoint);
    var matched = new List<Dictionary<string, object?>>();
    var unmatched = new List<Dictionary<string, object?>>();
    var used = new HashSet<uint>();
    for (var i = 0; i < pis.Count; i++)
    {
      var p = pis[i];
      var c = Nearest(cogo, p.X, p.Y, tolerance, out var d);
      if (c == null)
      {
        var near = Nearest(cogo, p.X, p.Y, double.MaxValue, out var dn);
        unmatched.Add(new Dictionary<string, object?> { ["piIndex"] = i, ["x"] = p.X, ["y"] = p.Y, ["currentZ"] = p.Z, ["nearestPoint"] = near?.Number, ["nearestDistance"] = Math.Round(dn, 3) });
        continue;
      }
      var idx = IndexOfPoint(fl, p);
      if (idx >= 0) fl.SetPointElevation(idx, c.Location.Z);
      used.Add(c.Number);
      matched.Add(new Dictionary<string, object?> { ["piIndex"] = i, ["x"] = p.X, ["y"] = p.Y, ["z"] = c.Location.Z, ["point"] = c.Number, ["pointName"] = c.Name, ["description"] = c.Description, ["distance"] = Math.Round(d, 4) });
    }

    // COGO points lying on the line but not on a vertex.
    var onSegments = new List<Dictionary<string, object?>>();
    var curve = sourceCurve;
    if (curve != null)
    {
      foreach (var c in cogo)
      {
        if (used.Contains(c.Number)) continue;
        Point3d closest;
        try { closest = curve.GetClosestPointTo(new Point3d(c.Location.X, c.Location.Y, 0), false); }
        catch { continue; }
        var d = Math.Sqrt(Math.Pow(closest.X - c.Location.X, 2) + Math.Pow(closest.Y - c.Location.Y, 2));
        if (d > tolerance) continue;
        var row = new Dictionary<string, object?> { ["point"] = c.Number, ["x"] = c.Location.X, ["y"] = c.Location.Y, ["z"] = c.Location.Z, ["distanceToLine"] = Math.Round(d, 4), ["inserted"] = false };
        if (addOnSegments)
        {
          try
          {
            var ep = new Point3d(closest.X, closest.Y, c.Location.Z);
            fl.InsertElevationPoint(ep);
            var idx = IndexOfPoint(fl, ep);
            if (idx >= 0) fl.SetPointElevation(idx, c.Location.Z);
            row["inserted"] = true;
          }
          catch (Exception ex) { row["error"] = ex.Message; }
        }
        onSegments.Add(row);
      }
    }

    return new Dictionary<string, object?>
    {
      ["source"] = "COGO points",
      ["piCount"] = pis.Count,
      ["matchedVertexCount"] = matched.Count,
      ["unmatchedVertexCount"] = unmatched.Count,
      ["matched"] = matched,
      ["unmatched"] = unmatched,
      ["cogoOnSegmentsNotOnVertex"] = onSegments,
    };
  }

  private static List<Dictionary<string, object?>> PreviewVertices(Curve source, List<CogoRef> cogo, double tolerance)
  {
    var pts = new List<Point3d>();
    switch (source)
    {
      case Polyline pl: for (var i = 0; i < pl.NumberOfVertices; i++) pts.Add(pl.GetPoint3dAt(i)); break;
      case Polyline2d pl2: foreach (ObjectId vid in pl2) if (vid.GetObject(OpenMode.ForRead) is Vertex2d v) pts.Add(v.Position); break;
      case Polyline3d pl3: foreach (ObjectId vid in pl3) if (vid.GetObject(OpenMode.ForRead) is PolylineVertex3d v) pts.Add(v.Position); break;
      default: pts.Add(source.StartPoint); pts.Add(source.EndPoint); break;
    }
    return pts.Select((p, i) =>
    {
      var c = Nearest(cogo, p.X, p.Y, tolerance, out var d);
      return new Dictionary<string, object?> { ["index"] = i, ["x"] = p.X, ["y"] = p.Y, ["point"] = c?.Number, ["z"] = c?.Location.Z, ["distance"] = c == null ? null : Math.Round(d, 4) };
    }).ToList();
  }

  // -------------------------------------------------------------------------
  // featureLineCreateFromPoints: ribbon "Create Feature Line" (draw by points)
  // -------------------------------------------------------------------------

  public static Task<object?> CreateFromPointsAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var siteName = PluginRuntime.GetOptionalString(parameters, "siteName");
    var style = PluginRuntime.GetOptionalString(parameters, "style");
    var layer = PluginRuntime.GetOptionalString(parameters, "layer");
    var closed = PluginRuntime.GetOptionalBool(parameters, "closed") ?? false;
    var surfaceName = PluginRuntime.GetOptionalString(parameters, "elevationSurface");
    var pointsNode = parameters?["points"] as JsonArray
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "points is required: [{x, y, z}] (z optional when elevationSurface is given).");
    if (pointsNode.Count < 2)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "At least two points are required.");

    var points = new List<Point3d>();
    foreach (var node in pointsNode)
    {
      if (node is not JsonObject pt) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Each point must be an object with x, y and optionally z.");
      var x = pt["x"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Point missing x.");
      var y = pt["y"]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Point missing y.");
      var z = pt["z"]?.GetValue<double>();
      if (!z.HasValue && string.IsNullOrWhiteSpace(surfaceName))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Point missing z; give z on every point or an elevationSurface.");
      points.Add(new Point3d(x, y, z ?? 0));
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var siteId = LookupUtils.GetSiteId(civilDoc, transaction, siteName);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, database.CurrentSpaceId, OpenMode.ForWrite);
      var collection = new Point3dCollection();
      foreach (var p in points) collection.Add(p);
      using var polyline = new Polyline3d(Poly3dType.SimplePoly, collection, closed);
      var polyId = modelSpace.AppendEntity(polyline);
      transaction.AddNewlyCreatedDBObject(polyline, true);

      ObjectId flId;
      try { flId = siteId.IsNull ? FeatureLine.Create(name, polyId) : FeatureLine.Create(name, polyId, siteId); }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Civil 3D could not create feature line '{name}': {ex.GetType().Name}: {ex.Message}");
      }
      // FeatureLine.Create keeps the source; the temporary polyline is not wanted.
      if (!polyline.IsErased) polyline.Erase();

      var fl = CivilObjectUtils.GetRequiredObject<FeatureLine>(transaction, flId, OpenMode.ForWrite);
      ApplyStyleAndLayer(fl, civilDoc, database, transaction, style, layer);
      if (!string.IsNullOrWhiteSpace(surfaceName))
      {
        var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, surfaceName, OpenMode.ForRead);
        fl.AssignElevationsFromSurface(surface.ObjectId, true);
      }

      var result = Describe(fl, transaction);
      result["created"] = true;
      return result;
    });
  }

  // -------------------------------------------------------------------------
  // featureLinePoints: PI / elevation points with elevations and grades
  // -------------------------------------------------------------------------

  public static Task<object?> PointsAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetOptionalString(parameters, "name");
    var handle = PluginRuntime.GetOptionalString(parameters, "handle");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var fl = Find(civilDoc, database, transaction, name, handle, OpenMode.ForRead);
      var result = Describe(fl, transaction);
      result["points"] = ReadPoints(fl);
      return result;
    });
  }

  // -------------------------------------------------------------------------
  // featureLineEdit: one action, `operation` selects the edit
  // -------------------------------------------------------------------------

  public static Task<object?> EditAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetOptionalString(parameters, "name");
    var handle = PluginRuntime.GetOptionalString(parameters, "handle");
    var operation = PluginRuntime.GetRequiredString(parameters, "operation").ToLowerInvariant();

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var fl = Find(civilDoc, database, transaction, name, handle, OpenMode.ForWrite);
      var before = Describe(fl, transaction);
      double D(string key) => PluginRuntime.GetOptionalDouble(parameters, key) ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{key} is required for {operation}.");
      int I(string key) => PluginRuntime.GetOptionalInt(parameters, key) ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{key} is required for {operation}.");
      Point3d PointOnLine(bool needZ)
      {
        var x = D("x"); var y = D("y");
        var z = PluginRuntime.GetOptionalDouble(parameters, "z");
        if (needZ && !z.HasValue) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"z is required for {operation}.");
        return new Point3d(x, y, z ?? 0);
      }

      string detail;
      try
      {
        switch (operation)
        {
          case "insert_pi":
          {
            var p = PointOnLine(false);
            var z = PluginRuntime.GetOptionalDouble(parameters, "z");
            fl.InsertPIPoint(p);
            if (z.HasValue) { var idx = IndexOfPoint(fl, p); if (idx >= 0) fl.SetPointElevation(idx, z.Value); }
            detail = $"PI inserted at ({p.X:0.###}, {p.Y:0.###}){(z.HasValue ? $" z={z.Value:0.###}" : " (elevation interpolated)")}";
            break;
          }
          case "insert_elevation_point":
          {
            var p = PointOnLine(true);
            fl.InsertElevationPoint(p);
            var idx = IndexOfPoint(fl, p);
            if (idx >= 0) fl.SetPointElevation(idx, p.Z);
            detail = $"elevation point inserted at ({p.X:0.###}, {p.Y:0.###}) z={p.Z:0.###}";
            break;
          }
          case "delete_pi":
            fl.DeletePIPoint(PointOnLine(false)); detail = "PI deleted"; break;
          case "delete_elevation_point":
            fl.DeleteElevationPoint(PointOnLine(false)); detail = "elevation point deleted"; break;
          case "set_elevation":
          {
            var index = I("pointIndex"); var elevation = D("elevation");
            if (index < 0 || index >= fl.PointsCount) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"pointIndex {index} out of range (0-{fl.PointsCount - 1}).");
            fl.SetPointElevation(index, elevation); detail = $"point {index} elevation = {elevation:0.###}";
            break;
          }
          case "set_all_elevations":
          {
            var elevation = D("elevation");
            for (var i = 0; i < fl.PointsCount; i++) fl.SetPointElevation(i, elevation);
            detail = $"all {fl.PointsCount} points at {elevation:0.###}";
            break;
          }
          case "raise_lower":
          {
            var delta = D("delta");
            var pts = fl.GetPoints(FeatureLinePointType.AllPoints);
            for (var i = 0; i < pts.Count; i++) fl.SetPointElevation(i, pts[i].Z + delta);
            detail = $"all points moved by {delta:+0.###;-0.###}";
            break;
          }
          case "elevations_from_surface":
          {
            var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, PluginRuntime.GetRequiredString(parameters, "surfaceName"), OpenMode.ForRead);
            fl.AssignElevationsFromSurface(surface.ObjectId, PluginRuntime.GetOptionalBool(parameters, "includeIntermediatePoints") ?? true);
            detail = $"elevations from surface '{surface.Name}'";
            break;
          }
          case "set_curve_radius":
          {
            var index = I("curveIndex"); var radius = D("radius");
            fl.SetCurveRadius(index, radius); detail = $"curve {index} radius = {radius:0.###}";
            break;
          }
          case "set_style":
          {
            var styleId = LookupUtils.GetFeatureLineStyleId(civilDoc, transaction, PluginRuntime.GetRequiredString(parameters, "style"));
            fl.StyleId = styleId; detail = $"style = {fl.StyleName}";
            break;
          }
          case "move_to_site":
          {
            var siteName = PluginRuntime.GetOptionalString(parameters, "siteName");
            if (string.IsNullOrWhiteSpace(siteName)) { FeatureLine.MoveToNoneSite(fl.ObjectId); detail = "moved to <none> site"; }
            else
            {
              var siteId = LookupUtils.GetSiteId(civilDoc, transaction, siteName);
              if (siteId.IsNull) throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Site '{siteName}' not found.");
              FeatureLine.MoveToSite(fl.ObjectId, siteId); detail = $"moved to site '{siteName}'";
            }
            break;
          }
          case "rename":
            fl.Name = PluginRuntime.GetRequiredString(parameters, "newName"); detail = $"renamed to '{fl.Name}'"; break;
          default:
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
              $"Unknown operation '{operation}'. Use insert_pi, insert_elevation_point, delete_pi, delete_elevation_point, set_elevation, set_all_elevations, raise_lower, elevations_from_surface, set_curve_radius, set_style, move_to_site, rename.");
        }
      }
      catch (JsonRpcDispatchException) { throw; }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Civil 3D refused {operation} on feature line '{fl.Name}': {ex.GetType().Name}: {ex.Message}. Nothing was changed.");
      }

      var after = Describe(fl, transaction);
      after["operation"] = operation;
      after["detail"] = detail;
      after["before"] = new Dictionary<string, object?> { ["pointCount"] = before["pointCount"], ["minElevation"] = before["minElevation"], ["maxElevation"] = before["maxElevation"], ["length3d"] = before["length3d"] };
      after["points"] = ReadPoints(fl);
      after["success"] = true;
      return after;
    });
  }

  // -------------------------------------------------------------------------
  // helpers
  // -------------------------------------------------------------------------

  internal static FeatureLine Find(CivilDocument civilDoc, Database database, Transaction transaction, string? name, string? handle, OpenMode mode)
  {
    if (!string.IsNullOrWhiteSpace(handle))
    {
      var id = ResolveHandle(database, handle);
      return transaction.GetObject(id, mode) as FeatureLine
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Object {handle} is not a feature line.");
    }
    if (string.IsNullOrWhiteSpace(name))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give the feature line name or handle.");

    var ids = new List<ObjectId>();
    try { foreach (ObjectId id in civilDoc.GetSitelessFeatureLineIds()) ids.Add(id); } catch { }
    foreach (ObjectId siteId in civilDoc.GetSiteIds())
    {
      var site = transaction.GetObject(siteId, OpenMode.ForRead) as Site;
      if (site == null) continue;
      try { foreach (ObjectId id in site.GetFeatureLineIds()) ids.Add(id); } catch { }
    }
    var matches = ids.Select(id => transaction.GetObject(id, OpenMode.ForRead) as FeatureLine)
      .Where(f => f != null && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
    if (matches.Count == 0) throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Feature line '{name}' not found.");
    if (matches.Count > 1)
      throw new JsonRpcDispatchException("CIVIL3D.CONFLICT",
        $"{matches.Count} feature lines are named '{name}'; pick one by handle: {string.Join(", ", matches.Select(m => CivilObjectUtils.GetHandle(m!)))}.");
    return (FeatureLine)transaction.GetObject(matches[0]!.ObjectId, mode);
  }

  private static ObjectId ResolveHandle(Database database, string handle)
  {
    try
    {
      var h = new Handle(Convert.ToInt64(handle, 16));
      if (database.TryGetObjectId(h, out var id) && !id.IsNull) return id;
    }
    catch { }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No object with handle {handle} in the drawing.");
  }

  private static void ApplyStyleAndLayer(FeatureLine fl, CivilDocument civilDoc, Database database, Transaction transaction, string? style, string? layer)
  {
    if (!string.IsNullOrWhiteSpace(style))
    {
      var styleId = LookupUtils.GetFeatureLineStyleId(civilDoc, transaction, style);
      if (!styleId.IsNull) fl.StyleId = styleId;
    }
    if (!string.IsNullOrWhiteSpace(layer))
    {
      var layerId = LookupUtils.GetLayerId(database, transaction, layer);
      if (!layerId.IsNull) fl.LayerId = layerId;
    }
  }

  private static int IndexOfPoint(FeatureLine fl, Point3d p)
  {
    var pts = fl.GetPoints(FeatureLinePointType.AllPoints);
    for (var i = 0; i < pts.Count; i++)
      if (Math.Abs(pts[i].X - p.X) < 1e-6 && Math.Abs(pts[i].Y - p.Y) < 1e-6) return i;
    return -1;
  }

  internal static Dictionary<string, object?> Describe(FeatureLine fl, Transaction transaction)
  {
    string? siteName = null;
    try { if (!fl.SiteId.IsNull) siteName = CivilObjectUtils.GetName(transaction.GetObject(fl.SiteId, OpenMode.ForRead)); } catch { }
    return new Dictionary<string, object?>
    {
      ["name"] = fl.Name,
      ["handle"] = CivilObjectUtils.GetHandle(fl),
      ["site"] = siteName,
      ["style"] = fl.StyleName,
      ["layer"] = fl.Layer,
      ["pointCount"] = fl.PointsCount,
      ["piPointCount"] = fl.PIPointsCount,
      ["elevationPointCount"] = fl.ElevationPointsCount,
      ["curveCount"] = fl.CurvesCount,
      ["length2d"] = fl.Length2D,
      ["length3d"] = fl.Length3D,
      ["minElevation"] = fl.MinElevation,
      ["maxElevation"] = fl.MaxElevation,
      ["minGrade"] = fl.MinGrade,
      ["maxGrade"] = fl.MaxGrade,
    };
  }

  private static List<Dictionary<string, object?>> ReadPoints(FeatureLine fl)
  {
    var all = fl.GetPoints(FeatureLinePointType.AllPoints);
    var pis = fl.GetPoints(FeatureLinePointType.PIPoint);
    var list = new List<Dictionary<string, object?>>();
    for (var i = 0; i < all.Count; i++)
    {
      var p = all[i];
      var isPi = false;
      foreach (Point3d q in pis) if (Math.Abs(q.X - p.X) < 1e-6 && Math.Abs(q.Y - p.Y) < 1e-6) { isPi = true; break; }
      double? gradeIn = null, gradeOut = null;
      try { gradeIn = fl.GetGradeInAtPoint(p); } catch { }
      try { gradeOut = fl.GetGradeOutAtPoint(p); } catch { }
      list.Add(new Dictionary<string, object?>
      {
        ["index"] = i,
        ["type"] = isPi ? "PI" : "elevation",
        ["x"] = p.X,
        ["y"] = p.Y,
        ["z"] = p.Z,
        ["gradeIn"] = gradeIn,
        ["gradeOut"] = gradeOut,
      });
    }
    return list;
  }
}
