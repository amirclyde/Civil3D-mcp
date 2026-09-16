using System.IO;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using System.Linq;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

/// <summary>
/// Surface creation, definition, editing, extraction and analysis against the documented
/// Civil 3D 2026 managed API (TinSurface / GridSurface / TinVolumeSurface / GridVolumeSurface,
/// SurfaceDefinition* collections, TinSurface edit operations, Surface.Analysis, masks, snapshots).
/// Complements SurfaceCommands.cs (list/get/elevation/volumes/contours/DEM).
/// </summary>
public static class SurfaceExtraCommands
{
  // ═══════════════════════════ creation ═══════════════════════════

  /// <summary>create_ex: tin | grid | tin_volume | grid_volume.</summary>
  public static Task<object?> CreateAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var surfaceType = (PluginRuntime.GetOptionalString(parameters, "surfaceType") ?? "tin").Trim().ToLowerInvariant();
    var style = PluginRuntime.GetOptionalString(parameters, "style");
    var layer = PluginRuntime.GetOptionalString(parameters, "layer");
    var description = PluginRuntime.GetOptionalString(parameters, "description");
    var baseSurfaceName = PluginRuntime.GetOptionalString(parameters, "baseSurface");
    var comparisonSurfaceName = PluginRuntime.GetOptionalString(parameters, "comparisonSurface");
    var spacingX = PluginRuntime.GetOptionalDouble(parameters, "spacingX") ?? 10.0;
    var spacingY = PluginRuntime.GetOptionalDouble(parameters, "spacingY") ?? spacingX;
    var orientation = PluginRuntime.GetOptionalDouble(parameters, "orientationDegrees") ?? 0.0;
    var cutFactor = PluginRuntime.GetOptionalDouble(parameters, "cutFactor");
    var fillFactor = PluginRuntime.GetOptionalDouble(parameters, "fillFactor");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      EnsureNoSurfaceNamed(civilDoc, transaction, name);
      var styleId = LookupUtils.GetSurfaceStyleId(civilDoc, transaction, style);
      ObjectId id;
      try
      {
        switch (surfaceType)
        {
          case "tin":
            id = TinSurface.Create(name, styleId);
            break;
          case "grid":
            id = GridSurface.Create(name, spacingX, spacingY, orientation * Math.PI / 180.0, styleId);
            break;
          case "tin_volume":
          case "grid_volume":
          {
            if (string.IsNullOrWhiteSpace(baseSurfaceName) || string.IsNullOrWhiteSpace(comparisonSurfaceName))
              throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Volume surfaces need baseSurface and comparisonSurface. Nothing was created.");
            var baseSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, baseSurfaceName, OpenMode.ForRead);
            var compSurface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, comparisonSurfaceName, OpenMode.ForRead);
            id = surfaceType == "tin_volume"
              ? TinVolumeSurface.Create(name, baseSurface.ObjectId, compSurface.ObjectId, styleId)
              : GridVolumeSurface.Create(name, baseSurface.ObjectId, compSurface.ObjectId, spacingX, spacingY, orientation * Math.PI / 180.0, styleId);
            break;
          }
          default:
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"surfaceType '{surfaceType}' is not one of tin, grid, tin_volume, grid_volume.");
        }
      }
      catch (JsonRpcDispatchException) { throw; }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Civil 3D refused to create the {surfaceType} surface '{name}': {ex.GetType().Name}: {ex.Message}. Nothing was created.");
      }

      var surface = CivilObjectUtils.GetRequiredObject<CivilSurface>(transaction, id, OpenMode.ForWrite);
      if (!string.IsNullOrWhiteSpace(layer)) CivilObjectUtils.TrySetLayer(surface, layer, database, transaction);
      if (!string.IsNullOrWhiteSpace(description)) surface.Description = description;
      if (surface is TinVolumeSurface tv) { if (cutFactor.HasValue) tv.CutFactor = cutFactor.Value; if (fillFactor.HasValue) tv.FillFactor = fillFactor.Value; }
      if (surface is GridVolumeSurface gv) { if (cutFactor.HasValue) gv.CutFactor = cutFactor.Value; if (fillFactor.HasValue) gv.FillFactor = fillFactor.Value; }

      var result = Summary(surface, transaction, database);
      result["created"] = true;
      return result;
    });
  }

  /// <summary>create_from_file: landxml | tin | corridor_surface | cropping.</summary>
  public static Task<object?> CreateFromSourceAsync(JsonObject? parameters)
  {
    var source = PluginRuntime.GetRequiredString(parameters, "source").Trim().ToLowerInvariant();
    var name = PluginRuntime.GetOptionalString(parameters, "name");
    var filePath = PluginRuntime.GetOptionalString(parameters, "filePath");
    var landXmlSurfaceName = PluginRuntime.GetOptionalString(parameters, "landXmlSurfaceName");
    var corridorName = PluginRuntime.GetOptionalString(parameters, "corridorName");
    var corridorSurfaceName = PluginRuntime.GetOptionalString(parameters, "corridorSurfaceName");
    var sourceSurfaceName = PluginRuntime.GetOptionalString(parameters, "sourceSurface");
    var style = PluginRuntime.GetOptionalString(parameters, "style");
    var layer = PluginRuntime.GetOptionalString(parameters, "layer");

    if ((source == "landxml" || source == "tin") && (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"filePath '{filePath}' was not found. Nothing was created.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      ObjectId id;
      var notes = new Dictionary<string, object?>();
      try
      {
        switch (source)
        {
          case "landxml":
          {
            var newName = name ?? Path.GetFileNameWithoutExtension(filePath!);
            EnsureNoSurfaceNamed(civilDoc, transaction, newName);
            var xmlSurface = string.IsNullOrWhiteSpace(landXmlSurfaceName) ? FirstLandXmlSurfaceName(filePath!) : landXmlSurfaceName!;
            id = TinSurface.CreateFromLandXML(database, newName, filePath!, xmlSurface);
            notes["landXmlSurfaceName"] = xmlSurface;
            break;
          }
          case "tin":
            id = TinSurface.CreateFromTin(database, filePath!);
            break;
          case "corridor_surface":
          {
            if (string.IsNullOrWhiteSpace(corridorName) || string.IsNullOrWhiteSpace(corridorSurfaceName))
              throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "corridor_surface needs corridorName and corridorSurfaceName.");
            var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForRead);
            var cs = corridor.CorridorSurfaces.Cast<CorridorSurface>().FirstOrDefault(s => string.Equals(s.Name, corridorSurfaceName, StringComparison.OrdinalIgnoreCase))
              ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Corridor surface '{corridorSurfaceName}' was not found on '{corridor.Name}'.");
            var newName = name ?? $"{corridor.Name} - {cs.Name} (static)";
            EnsureNoSurfaceNamed(civilDoc, transaction, newName);
            id = TinSurface.CreateFromCorridorSurface(newName, cs);
            notes["sourceCorridor"] = corridor.Name;
            break;
          }
          case "cropping":
          {
            if (string.IsNullOrWhiteSpace(sourceSurfaceName))
              throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "cropping needs sourceSurface and a polygon (points or polylineHandle).");
            var src = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, sourceSurfaceName, OpenMode.ForRead);
            // TinSurface.CreateByCropping only crops INTO ANOTHER drawing ("srcSurfaceId can't in destDatabase").
            // In the same drawing the equivalent is: new TIN + paste the source + outer boundary from the polygon.
            var polygon = ReadPolygon3d(parameters, database, transaction, "points", "polylineHandle");
            var newName = name ?? $"{src.Name} (crop)";
            EnsureNoSurfaceNamed(civilDoc, transaction, newName);
            var cropStyleId = LookupUtils.GetSurfaceStyleId(civilDoc, transaction, style);
            id = TinSurface.Create(newName, cropStyleId.IsNull ? src.StyleId : cropStyleId);
            var crop = CivilObjectUtils.GetRequiredObject<TinSurface>(transaction, id, OpenMode.ForWrite);
            crop.PasteSurface(src.ObjectId);
            crop.BoundariesDefinition.AddBoundaries(polygon, 1.0, SurfaceBoundaryType.Outer, true);
            crop.Rebuild();
            notes["sourceSurface"] = src.Name;
            notes["polygonPointCount"] = polygon.Count;
            notes["method"] = "paste + outer boundary (static copy)";
            break;
          }
          default:
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"source '{source}' is not one of landxml, tin, corridor_surface, cropping.");
        }
      }
      catch (JsonRpcDispatchException) { throw; }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Surface creation from {source} failed: {ex.GetType().Name}: {ex.Message}. Nothing was created.");
      }

      var surface = CivilObjectUtils.GetRequiredObject<CivilSurface>(transaction, id, OpenMode.ForWrite);
      if (source == "tin" && !string.IsNullOrWhiteSpace(name)) { try { surface.Name = name; } catch (Exception ex) { notes["renameError"] = ex.Message; } }
      if (!string.IsNullOrWhiteSpace(style))
      {
        var styleId = LookupUtils.GetSurfaceStyleId(civilDoc, transaction, style);
        if (!styleId.IsNull) surface.StyleId = styleId;
      }
      if (!string.IsNullOrWhiteSpace(layer)) CivilObjectUtils.TrySetLayer(surface, layer, database, transaction);
      var result = Summary(surface, transaction, database);
      foreach (var kv in notes) result[kv.Key] = kv.Value;
      result["source"] = source;
      result["created"] = true;
      return result;
    });
  }

  // ═══════════════════════════ definition (Add Data ▾) ═══════════════════════════

  public static Task<object?> AddDataAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var kind = PluginRuntime.GetRequiredString(parameters, "kind").Trim().ToLowerInvariant();
    var rebuild = PluginRuntime.GetOptionalBool(parameters, "rebuild") ?? true;
    var description = PluginRuntime.GetOptionalString(parameters, "description") ?? string.Empty;
    var midOrdinate = PluginRuntime.GetOptionalDouble(parameters, "midOrdinateDistance") ?? 1.0;
    var maximumDistance = PluginRuntime.GetOptionalDouble(parameters, "maximumDistance") ?? 0.0;
    var weedingDistance = PluginRuntime.GetOptionalDouble(parameters, "weedingDistance") ?? 0.0;
    var weedingAngle = PluginRuntime.GetOptionalDouble(parameters, "weedingAngleDegrees") ?? 0.0;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForWrite);
      var result = new Dictionary<string, object?> { ["surfaceName"] = surface.Name, ["kind"] = kind };
      var tin = surface as TinSurface;
      try
      {
        switch (kind)
        {
          case "points":
          {
            var pts = ReadPoint3ds(parameters, "points");
            if (pts.Count == 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "points: give [{x,y,z}] or [[x,y,z]].");
            var t = RequireTin(surface, "points");
            t.AddVertices(pts);
            result["pointsAdded"] = pts.Count;
            break;
          }
          case "point_group":
          {
            var groupName = PluginRuntime.GetRequiredString(parameters, "pointGroupName");
            var t = RequireTin(surface, "point_group");
            var group = FindPointGroup(civilDoc, transaction, groupName);
            t.PointGroupsDefinition.AddPointGroup(group.ObjectId);
            result["pointGroup"] = group.Name;
            break;
          }
          case "point_file":
          {
            var path = PluginRuntime.GetRequiredString(parameters, "filePath");
            var formatName = PluginRuntime.GetRequiredString(parameters, "pointFileFormat");
            if (!File.Exists(path)) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Point file '{path}' was not found.");
            var t = RequireTin(surface, "point_file");
            var formats = PointFileFormatCollection.GetPointFileFormats(database);
            PointFileFormat? format = null;
            try { format = formats[formatName]; } catch { }
            if (format == null)
            {
              var names = new List<string>();
              for (var i = 0; i < formats.Count; i++) names.Add(formats[i].Name);
              throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Point file format '{formatName}' was not found. Available: {string.Join(", ", names)}.");
            }
            t.PointFilesDefinition.AddPointFile(path, format);
            result["filePath"] = path; result["format"] = format.Name;
            break;
          }
          case "dem_file":
          {
            var path = PluginRuntime.GetRequiredString(parameters, "filePath");
            if (!File.Exists(path)) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"DEM file '{path}' was not found.");
            var cs = PluginRuntime.GetOptionalString(parameters, "coordinateSystemCode");
            var nullElev = PluginRuntime.GetOptionalDouble(parameters, "customNullElevation");
            SurfaceDefinitionDEMFiles dem = surface switch
            {
              TinSurface ts => ts.DEMFilesDefinition,
              GridSurface gs => gs.DEMFilesDefinition,
              _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "DEM files can only be added to TIN or grid surfaces."),
            };
            if (!string.IsNullOrWhiteSpace(cs)) dem.AddDEMFile(path, cs, nullElev.HasValue, nullElev ?? 0);
            else if (nullElev.HasValue) dem.AddDEMFile(path, true, nullElev.Value);
            else dem.AddDEMFile(path);
            result["filePath"] = path;
            break;
          }
          case "drawing_objects":
          {
            var t = RequireTin(surface, "drawing_objects");
            var maintainEdges = PluginRuntime.GetOptionalBool(parameters, "maintainEdges") ?? false;
            var ids = ResolveEntityIds(parameters, database, transaction, out var typeCounts);
            if (ids.Count == 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "drawing_objects: give handles[] or layer (+ optional objectType).");
            var byKind = GroupDrawingObjects(ids, transaction);
            var added = new Dictionary<string, object?>();
            if (byKind.points.Count > 0) { t.DrawingObjectsDefinition.AddFromPoints(byKind.points, description); added["points"] = byKind.points.Count; }
            if (byKind.blocks.Count > 0) { t.DrawingObjectsDefinition.AddFromBlocks(byKind.blocks, description); added["blocks"] = byKind.blocks.Count; }
            if (byKind.texts.Count > 0) { t.DrawingObjectsDefinition.AddFromTexts(byKind.texts, description); added["texts"] = byKind.texts.Count; }
            if (byKind.lines.Count > 0) { t.DrawingObjectsDefinition.AddFromLines(byKind.lines, maintainEdges, description); added["lines"] = byKind.lines.Count; }
            if (byKind.faces.Count > 0) { t.DrawingObjectsDefinition.AddFrom3DFaces(byKind.faces, maintainEdges, description); added["faces3d"] = byKind.faces.Count; }
            if (byKind.polyfaces.Count > 0) { t.DrawingObjectsDefinition.AddFromPolyFaces(byKind.polyfaces, maintainEdges, description); added["polyfaces"] = byKind.polyfaces.Count; }
            if (byKind.skipped.Count > 0) added["skipped"] = byKind.skipped;
            result["added"] = added;
            break;
          }
          case "breaklines":
          {
            var t = RequireTin(surface, "breaklines");
            var blType = (PluginRuntime.GetOptionalString(parameters, "breaklineType") ?? "standard").Trim().ToLowerInvariant();
            var ids = ResolveEntityIds(parameters, database, transaction, out _);
            var pts = ids.Count == 0 ? ReadPoint3ds(parameters, "points") : new Point3dCollection();
            if (ids.Count == 0 && pts.Count < 2) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "breaklines: give handles[] (polylines / 3D polylines / feature lines / lines) or points[] (≥2).");
            var d = t.BreaklinesDefinition;
            SurfaceOperationAddBreakline op = blType switch
            {
              "standard" => ids.Count > 0 ? d.AddStandardBreaklines(ids, midOrdinate, maximumDistance, weedingDistance, Deg2Rad(weedingAngle)) : d.AddStandardBreaklines(pts, midOrdinate, maximumDistance, weedingDistance, Deg2Rad(weedingAngle)),
              "proximity" => ids.Count > 0 ? d.AddProximityBreaklines(ids, midOrdinate) : d.AddProximityBreaklines(pts, midOrdinate),
              "non_destructive" or "nondestructive" => ids.Count > 0 ? d.AddNonDestructiveBreaklines(ids, midOrdinate) : d.AddNonDestructiveBreaklines(pts, midOrdinate),
              _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"breaklineType '{blType}' is not one of standard, proximity, non_destructive (wall breaklines need per-vertex offsets — use the UI)."),
            };
            if (!string.IsNullOrWhiteSpace(description)) { try { op.Description = description; } catch { } }
            result["breaklineType"] = blType;
            result["breaklineCount"] = ids.Count > 0 ? ids.Count : 1;
            break;
          }
          case "breakline_file":
          {
            var t = RequireTin(surface, "breakline_file");
            var path = PluginRuntime.GetRequiredString(parameters, "filePath");
            if (!File.Exists(path)) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Breakline file '{path}' was not found.");
            t.BreaklinesDefinition.AddBreaklinesFromFile(path);
            result["filePath"] = path;
            break;
          }
          case "boundary":
          {
            var bType = ParseBoundaryType(PluginRuntime.GetOptionalString(parameters, "boundaryType") ?? "outer");
            var nonDestructive = PluginRuntime.GetOptionalBool(parameters, "nonDestructiveBreakline") ?? (bType != SurfaceBoundaryType.DataClip);
            var ids = ResolveEntityIds(parameters, database, transaction, out _);
            if (ids.Count > 0)
              surface.BoundariesDefinition.AddBoundaries(ids, midOrdinate, bType, nonDestructive);
            else
            {
              var pts = ReadPoint3ds(parameters, "points");
              if (pts.Count < 3) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "boundary: give handles[] (closed polylines) or points[] (≥3).");
              surface.BoundariesDefinition.AddBoundaries(pts, midOrdinate, bType, nonDestructive);
            }
            result["boundaryType"] = bType.ToString();
            result["nonDestructiveBreakline"] = nonDestructive;
            break;
          }
          case "contours":
          {
            var t = RequireTin(surface, "contours");
            var ids = ResolveEntityIds(parameters, database, transaction, out _);
            var minimize = ReadMinimizeOptions(parameters);
            if (ids.Count > 0)
              t.ContoursDefinition.AddContours(ids, midOrdinate, maximumDistance, weedingDistance, Deg2Rad(weedingAngle), minimize);
            else
            {
              var pts = ReadPoint3ds(parameters, "points");
              if (pts.Count < 2) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "contours: give handles[] (polylines with elevation) or points[] of one contour.");
              t.ContoursDefinition.AddContours(pts, midOrdinate, maximumDistance, weedingDistance, Deg2Rad(weedingAngle), minimize);
            }
            result["contourCount"] = ids.Count > 0 ? ids.Count : 1;
            break;
          }
          default:
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
              $"kind '{kind}' is not one of points, point_group, point_file, dem_file, drawing_objects, breaklines, breakline_file, boundary, contours.");
        }
      }
      catch (JsonRpcDispatchException) { throw; }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Adding {kind} to surface '{surface.Name}' failed: {ex.GetType().Name}: {ex.Message}");
      }

      if (rebuild) RebuildQuietly(surface, result);
      result["operationCount"] = surface.Operations.Count;
      result["statistics"] = Stats(surface);
      return result;
    });
  }

  // ═══════════════════════════ definition operations ═══════════════════════════

  public static Task<object?> OperationsAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var action = (PluginRuntime.GetOptionalString(parameters, "operation") ?? "list").Trim().ToLowerInvariant();
    var index = PluginRuntime.GetOptionalInt(parameters, "index");
    var rebuild = PluginRuntime.GetOptionalBool(parameters, "rebuild") ?? true;

    if (action == "list")
      return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
      {
        var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
        return new Dictionary<string, object?>
        {
          ["surfaceName"] = surface.Name,
          ["isOutOfDate"] = surface.IsOutOfDate,
          ["autoRebuild"] = surface.AutoRebuild,
          ["locked"] = surface.Lock,
          ["hasSnapshot"] = surface.HasSnapshot,
          ["operations"] = ListOperations(surface, transaction),
          ["buildOptions"] = ReadBuildOptions(surface),
        };
      });

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForWrite);
      var ops = surface.Operations;
      var result = new Dictionary<string, object?> { ["surfaceName"] = surface.Name, ["operation"] = action };
      try
      {
        switch (action)
        {
          case "enable":
          case "disable":
          {
            var op = OpAt(ops, index);
            op.Enabled = action == "enable";
            result["index"] = index; result["enabled"] = op.Enabled;
            break;
          }
          case "remove":
          {
            var op = OpAt(ops, index);
            result["removed"] = op.GetType().Name;
            ops.Remove(op);
            break;
          }
          case "move_up": OpAt(ops, index).MoveUp(); break;
          case "move_down": OpAt(ops, index).MoveDown(); break;
          case "move_top": OpAt(ops, index).MoveToTop(); break;
          case "move_bottom": OpAt(ops, index).MoveToBottom(); break;
          case "enable_type":
          case "disable_type":
          {
            var typeName = PluginRuntime.GetRequiredString(parameters, "operationType");
            var type = ResolveOperationType(typeName);
            if (action == "enable_type") ops.EnableOperations(type); else ops.DisableOperations(type);
            result["operationType"] = type.Name;
            break;
          }
          default:
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"operation '{action}' is not one of list, enable, disable, remove, move_up, move_down, move_top, move_bottom, enable_type, disable_type.");
        }
      }
      catch (JsonRpcDispatchException) { throw; }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{action} on surface '{surface.Name}' definition failed: {ex.GetType().Name}: {ex.Message}");
      }
      if (rebuild) RebuildQuietly(surface, result);
      result["operations"] = ListOperations(surface, transaction);
      return result;
    });
  }

  // ═══════════════════════════ Edit Surface ▾ ═══════════════════════════

  public static Task<object?> EditAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var operation = PluginRuntime.GetRequiredString(parameters, "operation").Trim().ToLowerInvariant();
    var rebuild = PluginRuntime.GetOptionalBool(parameters, "rebuild") ?? true;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForWrite);
      var result = new Dictionary<string, object?> { ["surfaceName"] = surface.Name, ["operation"] = operation };
      try
      {
        switch (operation)
        {
          case "raise":
          case "lower":
          {
            var delta = PluginRuntime.GetRequiredDouble(parameters, "deltaElevation");
            if (operation == "lower") delta = -Math.Abs(delta);
            switch (surface)
            {
              case TinSurface t: t.RaiseSurface(delta); break;
              case GridSurface g: g.RaiseSurface(delta); break;
              default: throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "raise/lower works on TIN and grid surfaces only.");
            }
            result["deltaElevation"] = delta;
            break;
          }
          case "paste":
          {
            var t = RequireTin(surface, "paste");
            var pasteName = PluginRuntime.GetRequiredString(parameters, "pasteSurface");
            var paste = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, pasteName, OpenMode.ForRead);
            if (paste.ObjectId == surface.ObjectId) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "A surface cannot be pasted into itself.");
            t.PasteSurface(paste.ObjectId);
            result["pastedSurface"] = paste.Name;
            break;
          }
          case "add_point":
          {
            var t = RequireTin(surface, "add_point");
            var pts = ReadPoint3ds(parameters, "points");
            if (pts.Count == 0)
              pts.Add(new Point3d(PluginRuntime.GetRequiredDouble(parameters, "x"), PluginRuntime.GetRequiredDouble(parameters, "y"), PluginRuntime.GetRequiredDouble(parameters, "z")));
            if (pts.Count == 1) t.AddVertex(pts[0]); else t.AddVertices(pts);
            result["pointsAdded"] = pts.Count;
            break;
          }
          case "delete_point":
          {
            var t = RequireTin(surface, "delete_point");
            var v = VertexAt(t, parameters);
            result["deleted"] = P(v.Location);
            t.DeleteVertex(v);
            break;
          }
          case "modify_point":
          {
            var t = RequireTin(surface, "modify_point");
            var v = VertexAt(t, parameters);
            var newZ = PluginRuntime.GetOptionalDouble(parameters, "z") ?? PluginRuntime.GetRequiredDouble(parameters, "elevation");
            result["before"] = P(v.Location);
            t.SetVertexElevation(v, newZ);
            result["elevation"] = newZ;
            break;
          }
          case "move_point":
          {
            var t = RequireTin(surface, "move_point");
            var v = VertexAt(t, parameters);
            var nx = PluginRuntime.GetRequiredDouble(parameters, "newX");
            var ny = PluginRuntime.GetRequiredDouble(parameters, "newY");
            result["before"] = P(v.Location);
            t.MoveVertex(v, new Point2d(nx, ny));
            result["after"] = new[] { nx, ny };
            break;
          }
          case "add_line":
          {
            var t = RequireTin(surface, "add_line");
            var v1 = t.FindVertexAtXY(PluginRuntime.GetRequiredDouble(parameters, "x1"), PluginRuntime.GetRequiredDouble(parameters, "y1"));
            var v2 = t.FindVertexAtXY(PluginRuntime.GetRequiredDouble(parameters, "x2"), PluginRuntime.GetRequiredDouble(parameters, "y2"));
            if (v1 == null || v2 == null) throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", "No TIN vertex near one of the given points.");
            result["vertex1"] = P(v1.Location); result["vertex2"] = P(v2.Location);
            t.AddLine(v1, v2);
            break;
          }
          case "delete_line":
          case "swap_edge":
          {
            var t = RequireTin(surface, operation);
            var e = t.FindEdgeAtXY(PluginRuntime.GetRequiredDouble(parameters, "x"), PluginRuntime.GetRequiredDouble(parameters, "y"));
            if (e == null) throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", "No TIN edge near the given point.");
            result["edge"] = new Dictionary<string, object?> { ["vertex1"] = P(e.Vertex1.Location), ["vertex2"] = P(e.Vertex2.Location), ["locked"] = e.IsLocked };
            if (operation == "delete_line") t.DeleteLine(e); else t.SwapEdge(e);
            break;
          }
          case "delete_points_in_polygon":
          case "raise_points_in_polygon":
          case "set_points_elevation_in_polygon":
          {
            var t = RequireTin(surface, operation);
            var border = ReadPolygon3d(parameters, database, transaction, "points", "polylineHandle");
            var vertices = t.GetVerticesInsideBorder(border);
            if (vertices.Length == 0) throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", "No TIN vertices inside the polygon.");
            if (operation == "delete_points_in_polygon") t.DeleteVertices(vertices);
            else if (operation == "raise_points_in_polygon") t.RaiseVertices(vertices, PluginRuntime.GetRequiredDouble(parameters, "deltaElevation"));
            else t.SetVerticesElevation(vertices, PluginRuntime.GetRequiredDouble(parameters, "elevation"));
            result["vertexCount"] = vertices.Length;
            break;
          }
          case "minimize_flat_areas":
          {
            var t = RequireTin(surface, "minimize_flat_areas");
            var opts = ReadMinimizeOptions(parameters);
            t.MinimizeFlatAreas(opts);
            result["options"] = new Dictionary<string, object?> { ["fillGaps"] = opts.FillGaps, ["swapEdges"] = opts.SwapEdges, ["addPointsToTriangles"] = opts.AddPointsToTriangles, ["addPointsToEdges"] = opts.AddPointsToEdges };
            break;
          }
          case "simplify":
          {
            var t = RequireTin(surface, "simplify");
            var method = (PluginRuntime.GetOptionalString(parameters, "method") ?? "point_removal").Trim().ToLowerInvariant();
            var opts = new SurfaceSimplifyOptions(method is "edge_contraction" or "edgecontraction" ? SurfaceSimplifyType.EdgeContraction : SurfaceSimplifyType.PointRemoval);
            var region = TryReadPolygon3d(parameters, database, transaction, "points", "polylineHandle");
            if (region != null) opts.UserSpecifiedPolygonRegion = region; else opts.SetSurfaceBorderAsRegion();
            var percent = PluginRuntime.GetOptionalDouble(parameters, "percentToRemove");
            var maxChange = PluginRuntime.GetOptionalDouble(parameters, "maxElevationChange");
            if (percent.HasValue) { opts.UsePercentageToRemove = true; opts.PercentageToRemove = percent.Value; } else opts.UsePercentageToRemove = false;
            if (maxChange.HasValue) { opts.UseMaximumChangeInElevation = true; opts.MaximumChangeInElevation = maxChange.Value; } else opts.UseMaximumChangeInElevation = false;
            if (!percent.HasValue && !maxChange.HasValue) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "simplify needs percentToRemove and/or maxElevationChange.");
            var before = t.GetGeneralProperties().NumberOfPoints;
            t.SimplifySurface(opts);
            result["method"] = opts.SimplifyMethod.ToString();
            result["pointsBefore"] = before;
            break;
          }
          case "smooth":
          {
            var t = RequireTin(surface, "smooth");
            var output = new SurfacePointOutputOptions();
            var locations = (PluginRuntime.GetOptionalString(parameters, "outputLocations") ?? "grid").Trim().ToLowerInvariant();
            output.OutputLocations = locations switch
            {
              "grid" or "grid_based" => SurfacePointOutputLocationsType.GridBased,
              "centroids" => SurfacePointOutputLocationsType.Centroids,
              "random" => SurfacePointOutputLocationsType.RandomPoints,
              "edge_midpoints" or "edges" => SurfacePointOutputLocationsType.EdgeMidPoints,
              _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"outputLocations '{locations}' is not one of grid, centroids, random, edge_midpoints."),
            };
            var spacing = PluginRuntime.GetOptionalDouble(parameters, "gridSpacing") ?? 5.0;
            output.GridSpacingX = PluginRuntime.GetOptionalDouble(parameters, "gridSpacingX") ?? spacing;
            output.GridSpacingY = PluginRuntime.GetOptionalDouble(parameters, "gridSpacingY") ?? spacing;
            output.GridOrientation = Deg2Rad(PluginRuntime.GetOptionalDouble(parameters, "gridOrientationDegrees") ?? 0);
            output.RandomPointsNumber = PluginRuntime.GetOptionalInt(parameters, "randomPoints") ?? 100;
            var region = TryReadPolygon3d(parameters, database, transaction, "points", "polylineHandle") ?? ExtentsPolygon(t);
            output.OutputRegions = new[] { region };
            var method = (PluginRuntime.GetOptionalString(parameters, "method") ?? "nni").Trim().ToLowerInvariant();
            if (method == "nni" || method == "natural_neighbor")
              t.SmoothSurfaceByNNI(output);
            else if (method == "kriging")
            {
              var k = new KrigingMethodOptions
              {
                SampleVertices = t.Vertices,
                SemivariogramModel = ParseSemivariogram(PluginRuntime.GetOptionalString(parameters, "semivariogram")),
                VariogramParamA = PluginRuntime.GetOptionalDouble(parameters, "variogramA") ?? 1.0,
                VariogramParamC = PluginRuntime.GetOptionalDouble(parameters, "variogramC") ?? 1.0,
                NuggetEffect = PluginRuntime.GetOptionalDouble(parameters, "nuggetEffect") ?? 0.0,
              };
              t.SmoothSurfaceByKriging(k, output);
            }
            else throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"method '{method}' is not one of nni, kriging.");
            result["method"] = method; result["outputLocations"] = output.OutputLocations.ToString();
            break;
          }
          default:
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
              $"operation '{operation}' is not one of raise, lower, paste, add_point, delete_point, modify_point, move_point, add_line, delete_line, swap_edge, delete_points_in_polygon, raise_points_in_polygon, set_points_elevation_in_polygon, minimize_flat_areas, simplify, smooth.");
        }
      }
      catch (JsonRpcDispatchException) { throw; }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{operation} on surface '{surface.Name}' failed: {ex.GetType().Name}: {ex.Message}");
      }

      if (rebuild) RebuildQuietly(surface, result);
      result["statistics"] = Stats(surface);
      return result;
    });
  }

  // ═══════════════════════════ rebuild / snapshot / build options / properties ═══════════════════════════

  public static Task<object?> ManageAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var operation = PluginRuntime.GetRequiredString(parameters, "operation").Trim().ToLowerInvariant();
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForWrite);
      var result = new Dictionary<string, object?> { ["surfaceName"] = surface.Name, ["operation"] = operation };
      try
      {
        switch (operation)
        {
          case "rebuild": surface.Rebuild(); result["rebuilt"] = true; break;
          case "snapshot_create": surface.CreateSnapshot(); break;
          case "snapshot_rebuild": surface.RebuildSnapshot(); break;
          case "snapshot_remove": surface.RemoveSnapshot(); break;
          case "set_auto_rebuild": surface.AutoRebuild = PluginRuntime.GetOptionalBool(parameters, "value") ?? true; break;
          case "set_lock": surface.Lock = PluginRuntime.GetOptionalBool(parameters, "value") ?? true; break;
          case "rename":
            surface.Name = PluginRuntime.GetRequiredString(parameters, "newName");
            result["surfaceName"] = surface.Name;
            break;
          case "set_style":
          {
            var styleId = LookupUtils.GetSurfaceStyleId(civilDoc, transaction, PluginRuntime.GetRequiredString(parameters, "style"));
            surface.StyleId = styleId;
            break;
          }
          case "set_description": surface.Description = PluginRuntime.GetOptionalString(parameters, "description") ?? string.Empty; break;
          case "set_layer": CivilObjectUtils.TrySetLayer(surface, PluginRuntime.GetRequiredString(parameters, "layer"), database, transaction); break;
          case "build_options":
          {
            var bo = surface.BuildOptions;
            var maxLen = PluginRuntime.GetOptionalDouble(parameters, "maximumTriangleLength");
            var useMaxLen = PluginRuntime.GetOptionalBool(parameters, "useMaximumTriangleLength");
            var minElev = PluginRuntime.GetOptionalDouble(parameters, "excludeBelowElevation");
            var maxElev = PluginRuntime.GetOptionalDouble(parameters, "excludeAboveElevation");
            var useMinElev = PluginRuntime.GetOptionalBool(parameters, "useExcludeBelowElevation");
            var useMaxElev = PluginRuntime.GetOptionalBool(parameters, "useExcludeAboveElevation");
            var maxAngle = PluginRuntime.GetOptionalDouble(parameters, "maximumAngleDegrees");
            var useMaxAngle = PluginRuntime.GetOptionalBool(parameters, "useMaximumAngle");
            var crossing = PluginRuntime.GetOptionalString(parameters, "crossingBreaklines");
            var copyDeleted = PluginRuntime.GetOptionalBool(parameters, "copyDeletedDependentObjects");
            var convertBreaklines = PluginRuntime.GetOptionalBool(parameters, "convertProximityBreaklines");
            if (maxLen.HasValue) { bo.MaximumTriangleLength = maxLen.Value; bo.UseMaximumTriangleLength = useMaxLen ?? true; }
            else if (useMaxLen.HasValue) bo.UseMaximumTriangleLength = useMaxLen.Value;
            if (minElev.HasValue) { bo.MinimumElevation = minElev.Value; bo.ExecludeMinimumElevation = useMinElev ?? true; }
            else if (useMinElev.HasValue) bo.ExecludeMinimumElevation = useMinElev.Value;
            if (maxElev.HasValue) { bo.MaximumElevation = maxElev.Value; bo.ExecludeMaximumElevation = useMaxElev ?? true; }
            else if (useMaxElev.HasValue) bo.ExecludeMaximumElevation = useMaxElev.Value;
            if (maxAngle.HasValue) { bo.MaximumAngleBetweenAdjacentTinLines = Deg2Rad(maxAngle.Value); bo.UseMaximumAngle = useMaxAngle ?? true; }
            else if (useMaxAngle.HasValue) bo.UseMaximumAngle = useMaxAngle.Value;
            if (!string.IsNullOrWhiteSpace(crossing))
              bo.CrossingBreaklinesElevationOption = crossing.Trim().ToLowerInvariant().Replace("_", "") switch
              {
                "first" or "usefirst" => CrossingBreaklinesElevationType.UseFirst,
                "last" or "uselast" => CrossingBreaklinesElevationType.UseLast,
                "average" or "useaverage" => CrossingBreaklinesElevationType.UseAverage,
                "none" or "usenone" => CrossingBreaklinesElevationType.UseNone,
                _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "crossingBreaklines must be first, last, average or none."),
              };
            if (copyDeleted.HasValue) bo.CopyDeletedDependentObjects = copyDeleted.Value;
            if (convertBreaklines.HasValue) bo.NeedConvertBreaklines = convertBreaklines.Value;
            if (PluginRuntime.GetOptionalBool(parameters, "rebuild") ?? true) RebuildQuietly(surface, result);
            break;
          }
          default:
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
              $"operation '{operation}' is not one of rebuild, snapshot_create, snapshot_rebuild, snapshot_remove, set_auto_rebuild, set_lock, rename, set_style, set_description, set_layer, build_options.");
        }
      }
      catch (JsonRpcDispatchException) { throw; }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{operation} on surface '{surface.Name}' failed: {ex.GetType().Name}: {ex.Message}");
      }
      result["surface"] = Summary(surface, transaction, database);
      return result;
    });
  }

  // ═══════════════════════════ extract objects / water drop / solids / DEM / bounded volumes ═══════════════════════════

  public static Task<object?> ExtractAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var what = PluginRuntime.GetRequiredString(parameters, "what").Trim().ToLowerInvariant();
    var layer = PluginRuntime.GetOptionalString(parameters, "layer");
    var maxListed = PluginRuntime.GetOptionalInt(parameters, "maxListed") ?? 50;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
      var settings = (PluginRuntime.GetOptionalString(parameters, "settings") ?? "plan").Trim().ToLowerInvariant() == "model"
        ? SurfaceExtractionSettingsType.Model : SurfaceExtractionSettingsType.Plan;
      var smoothing = PluginRuntime.GetOptionalString(parameters, "smoothing");
      var smoothFactor = PluginRuntime.GetOptionalInt(parameters, "smoothFactor") ?? 0;
      Autodesk.Civil.DatabaseServices.Styles.ContourSmoothingType smoothType = string.Equals(smoothing, "spline", StringComparison.OrdinalIgnoreCase) ? Autodesk.Civil.DatabaseServices.Styles.ContourSmoothingType.SplineCurve : Autodesk.Civil.DatabaseServices.Styles.ContourSmoothingType.AddVertices;
      var smooth = !string.IsNullOrWhiteSpace(smoothing) && !string.Equals(smoothing, "none", StringComparison.OrdinalIgnoreCase);

      ObjectIdCollection ids;
      var result = new Dictionary<string, object?> { ["surfaceName"] = surface.Name, ["what"] = what };
      try
      {
        switch (what)
        {
          case "border":
            ids = surface switch { TinSurface t => t.ExtractBorder(settings), GridSurface g => g.ExtractBorder(settings), _ => throw NotTinOrGrid(what) };
            break;
          case "watershed":
            ids = surface switch { TinSurface t => t.ExtractWatershed(settings), GridSurface g => g.ExtractWatershed(settings), _ => throw NotTinOrGrid(what) };
            break;
          case "gridded":
            ids = surface switch { TinSurface t => t.ExtractGridded(settings), GridSurface g => g.ExtractGridded(settings), _ => throw NotTinOrGrid(what) };
            break;
          case "major_contours":
            ids = surface switch
            {
              TinSurface t => smooth ? t.ExtractMajorContours(settings, smoothType, smoothFactor) : t.ExtractMajorContours(settings),
              GridSurface g => smooth ? g.ExtractMajorContours(settings, smoothType, smoothFactor) : g.ExtractMajorContours(settings),
              _ => throw NotTinOrGrid(what),
            };
            break;
          case "minor_contours":
            ids = surface switch
            {
              TinSurface t => smooth ? t.ExtractMinorContours(settings, smoothType, smoothFactor) : t.ExtractMinorContours(settings),
              GridSurface g => smooth ? g.ExtractMinorContours(settings, smoothType, smoothFactor) : g.ExtractMinorContours(settings),
              _ => throw NotTinOrGrid(what),
            };
            break;
          case "contour_at":
          {
            var elevation = PluginRuntime.GetRequiredDouble(parameters, "elevation");
            ids = surface switch
            {
              TinSurface t => smooth ? t.ExtractContoursAt(elevation, smoothType, smoothFactor) : t.ExtractContoursAt(elevation),
              GridSurface g => smooth ? g.ExtractContoursAt(elevation, smoothType, smoothFactor) : g.ExtractContoursAt(elevation),
              _ => throw NotTinOrGrid(what),
            };
            result["elevation"] = elevation;
            break;
          }
          case "contours":
          {
            var interval = PluginRuntime.GetRequiredDouble(parameters, "interval");
            var low = PluginRuntime.GetOptionalDouble(parameters, "lowElevation");
            var high = PluginRuntime.GetOptionalDouble(parameters, "highElevation");
            var ranged = low.HasValue && high.HasValue;
            ids = surface switch
            {
              TinSurface t => ranged
                ? (smooth ? t.ExtractContours(low!.Value, high!.Value, interval, smoothType, smoothFactor) : t.ExtractContours(low!.Value, high!.Value, interval))
                : (smooth ? t.ExtractContours(interval, smoothType, smoothFactor) : t.ExtractContours(interval)),
              GridSurface g => ranged
                ? (smooth ? g.ExtractContours(low!.Value, high!.Value, interval, smoothType, smoothFactor) : g.ExtractContours(low!.Value, high!.Value, interval))
                : (smooth ? g.ExtractContours(interval, smoothType, smoothFactor) : g.ExtractContours(interval)),
              _ => throw NotTinOrGrid(what),
            };
            result["interval"] = interval;
            break;
          }
          case "water_drop":
          {
            var x = PluginRuntime.GetRequiredDouble(parameters, "x");
            var y = PluginRuntime.GetRequiredDouble(parameters, "y");
            var as3d = string.Equals(PluginRuntime.GetOptionalString(parameters, "objectType") ?? "polyline3d", "polyline2d", StringComparison.OrdinalIgnoreCase) ? WaterdropObjectType.Polyline2D : WaterdropObjectType.Polyline3D;
            ids = surface.Analysis.CreateWaterdrop(new Point2d(x, y), as3d);
            result["start"] = new[] { x, y };
            result["objectType"] = as3d.ToString();
            var paths = new List<Dictionary<string, object?>>();
            foreach (ObjectId id in ids)
            {
              var pts = ReadCurvePoints(transaction.GetObject(id, OpenMode.ForRead));
              if (pts.Count == 0) continue;
              double len = 0; for (var i = 1; i < pts.Count; i++) len += pts[i - 1].DistanceTo(pts[i]);
              paths.Add(new Dictionary<string, object?>
              {
                ["handle"] = id.Handle.ToString(),
                ["pointCount"] = pts.Count,
                ["length"] = Math.Round(len, 3),
                ["startElevation"] = SafeElevation(surface, pts[0].X, pts[0].Y),
                ["endPoint"] = new[] { pts[^1].X, pts[^1].Y },
                ["endElevation"] = SafeElevation(surface, pts[^1].X, pts[^1].Y),
              });
            }
            result["paths"] = paths;
            break;
          }
          case "solids":
          {
            var t = RequireTin(surface, "solids");
            var mode = (PluginRuntime.GetOptionalString(parameters, "solidMode") ?? "depth").Trim().ToLowerInvariant();
            var solidLayer = layer ?? surface.Layer;
            var color = (ushort)(PluginRuntime.GetOptionalInt(parameters, "colorIndex") ?? 256);
            switch (mode)
            {
              case "depth": ids = t.CreateSolidsAtDepth(PluginRuntime.GetRequiredDouble(parameters, "depth"), solidLayer, color); break;
              case "elevation": ids = t.CreateSolidsAtFixedElevation(PluginRuntime.GetRequiredDouble(parameters, "elevation"), solidLayer, color); break;
              case "surface":
              {
                var bottom = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, PluginRuntime.GetRequiredString(parameters, "bottomSurface"), OpenMode.ForRead);
                ids = t.CreateSolidsAtSurface(bottom.ObjectId, solidLayer, color);
                result["bottomSurface"] = bottom.Name;
                break;
              }
              default: throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "solidMode must be depth, elevation or surface.");
            }
            result["solidMode"] = mode;
            double totalVolume = 0;
            foreach (ObjectId id in ids)
              if (transaction.GetObject(id, OpenMode.ForRead) is Solid3d s) { try { totalVolume += s.MassProperties.Volume; } catch { } }
            result["totalVolume"] = Math.Round(totalVolume, 4);
            layer = null; // already on the requested layer
            break;
          }
          default:
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
              $"what '{what}' is not one of border, watershed, gridded, major_contours, minor_contours, contour_at, contours, water_drop, solids.");
        }
      }
      catch (JsonRpcDispatchException) { throw; }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Extracting {what} from surface '{surface.Name}' failed: {ex.GetType().Name}: {ex.Message}. Nothing was created.");
      }

      var handles = new List<string>();
      var types = new Dictionary<string, int>();
      foreach (ObjectId id in ids)
      {
        if (id.IsNull) continue;
        var obj = transaction.GetObject(id, string.IsNullOrWhiteSpace(layer) ? OpenMode.ForRead : OpenMode.ForWrite);
        if (!string.IsNullOrWhiteSpace(layer) && obj is Autodesk.AutoCAD.DatabaseServices.Entity ent) { try { ent.Layer = layer; } catch { } }
        var tn = obj.GetType().Name;
        types[tn] = types.TryGetValue(tn, out var c) ? c + 1 : 1;
        if (handles.Count < maxListed) handles.Add(id.Handle.ToString());
      }
      result["objectCount"] = ids.Count;
      result["objectTypes"] = types;
      result["handles"] = handles;
      result["created"] = ids.Count > 0;
      return result;
    });
  }

  public static Task<object?> BoundedVolumeAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var datum = PluginRuntime.GetOptionalDouble(parameters, "datumElevation");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
      var polygon = ReadPolygon3d(parameters, database, transaction, "points", "polylineHandle");
      SurfaceVolumeInfo info;
      try { info = datum.HasValue ? surface.GetBoundedVolumes(polygon, datum.Value) : surface.GetBoundedVolumes(polygon); }
      catch (Exception ex) { throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"GetBoundedVolumes failed on '{surface.Name}': {ex.Message}"); }
      return new Dictionary<string, object?>
      {
        ["surfaceName"] = surface.Name,
        ["surfaceType"] = surface.GetType().Name,
        ["datumElevation"] = datum,
        ["polygonPointCount"] = polygon.Count,
        ["cut"] = info.Cut, ["fill"] = info.Fill, ["net"] = info.Net,
        ["units"] = CivilObjectUtils.VolumeUnits(database),
        ["note"] = surface.IsVolumeSurface
          ? "Cut/fill between the volume surface's base and comparison surfaces inside the polygon."
          : (datum.HasValue ? "Cut/fill relative to the datum elevation inside the polygon." : "Cut/fill relative to the surface's own minimum elevation (no datum given)."),
      };
    });
  }

  public static Task<object?> ExportDemAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var outputPath = PluginRuntime.GetRequiredString(parameters, "outputPath");
    var gridSpacing = PluginRuntime.GetRequiredDouble(parameters, "gridSpacing");
    var cs = PluginRuntime.GetOptionalString(parameters, "coordinateSystemCode") ?? string.Empty;
    var method = (PluginRuntime.GetOptionalString(parameters, "elevationMethod") ?? "sample").Trim().ToLowerInvariant();
    var nullElev = PluginRuntime.GetOptionalDouble(parameters, "customNullElevation");
    if (gridSpacing <= 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "gridSpacing must be positive.");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
      var det = method == "average" ? ExportDetermineElevationType.Average : ExportDetermineElevationType.SampleSurfaceAtGridPoint;
      try
      {
        if (nullElev.HasValue) surface.ExportToDEM(outputPath, cs, gridSpacing, det, true, (float)nullElev.Value);
        else surface.ExportToDEM(outputPath, cs, gridSpacing, det);
      }
      catch (Exception ex) { throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"ExportToDEM failed on '{surface.Name}': {ex.Message}"); }
      return new Dictionary<string, object?>
      {
        ["surfaceName"] = surface.Name, ["outputPath"] = outputPath, ["gridSpacing"] = gridSpacing,
        ["elevationMethod"] = det.ToString(), ["exists"] = File.Exists(outputPath),
        ["sizeBytes"] = File.Exists(outputPath) ? (object?)new FileInfo(outputPath).Length : null,
      };
    });
  }

  // ═══════════════════════════ masks ═══════════════════════════

  public static Task<object?> MaskAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var operation = (PluginRuntime.GetOptionalString(parameters, "operation") ?? "list").Trim().ToLowerInvariant();
    if (operation == "list")
      return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
      {
        var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForRead);
        return new Dictionary<string, object?> { ["surfaceName"] = surface.Name, ["masks"] = ListMasks(surface) };
      });

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForWrite);
      var result = new Dictionary<string, object?> { ["surfaceName"] = surface.Name, ["operation"] = operation };
      try
      {
        switch (operation)
        {
          case "add":
          {
            var maskName = PluginRuntime.GetOptionalString(parameters, "maskName") ?? $"Mask {surface.Masks.Count + 1}";
            var ids = ResolveEntityIds(parameters, database, transaction, out _);
            if (ids.Count == 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "mask add needs handles[] of closed polylines (or layer).");
            var maskType = string.Equals(PluginRuntime.GetOptionalString(parameters, "maskType") ?? "outside", "inside", StringComparison.OrdinalIgnoreCase) ? SurfaceMaskType.InSide : SurfaceMaskType.OutSide;
            var data = new SurfaceMaskCreationData(maskName, PluginRuntime.GetOptionalString(parameters, "description") ?? string.Empty, surface.ObjectId, ids,
              PluginRuntime.GetOptionalDouble(parameters, "midOrdinateDistance") ?? 1.0, ObjectId.Null, maskType, PluginRuntime.GetOptionalBool(parameters, "renderOnly") ?? false);
            var mask = surface.Masks.Add(data);
            result["mask"] = DescribeMask(mask);
            break;
          }
          case "remove":
          {
            var maskName = PluginRuntime.GetRequiredString(parameters, "maskName");
            SurfaceMask? mask = null;
            try { mask = surface.Masks[maskName]; } catch { }
            if (mask == null) throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Mask '{maskName}' was not found on '{surface.Name}'.");
            surface.Masks.Remove(mask);
            result["removed"] = maskName;
            break;
          }
          default:
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "operation must be list, add or remove.");
        }
      }
      catch (JsonRpcDispatchException) { throw; }
      catch (Exception ex) { throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Mask {operation} on '{surface.Name}' failed: {ex.GetType().Name}: {ex.Message}"); }
      result["masks"] = ListMasks(surface);
      return result;
    });
  }

  // ═══════════════════════════ analysis ranges ═══════════════════════════

  public static Task<object?> AnalysisSetAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var analysis = PluginRuntime.GetRequiredString(parameters, "analysis").Trim().ToLowerInvariant();
    var rangeCount = PluginRuntime.GetOptionalInt(parameters, "rangeCount") ?? 8;
    var rangesNode = PluginRuntime.GetParameter(parameters, "ranges") as JsonArray;
    var firstColor = PluginRuntime.GetOptionalInt(parameters, "firstColorIndex") ?? 10;
    var colorStep = PluginRuntime.GetOptionalInt(parameters, "colorStep") ?? 20;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForWrite);
      var props = surface.GetGeneralProperties();
      List<(double min, double max, short color)> ranges;
      if (rangesNode != null && rangesNode.Count > 0)
      {
        ranges = new();
        var i = 0;
        foreach (var node in rangesNode.OfType<JsonObject>())
        {
          var mn = PluginRuntime.GetRequiredDouble(node, "min");
          var mx = PluginRuntime.GetRequiredDouble(node, "max");
          var col = (short)(PluginRuntime.GetOptionalInt(node, "colorIndex") ?? ((firstColor + i * colorStep) % 250 + 1));
          ranges.Add((mn, mx, col)); i++;
        }
      }
      else
      {
        double lo, hi;
        if (analysis == "elevation") { lo = props.MinimumElevation; hi = props.MaximumElevation; }
        else
        {
          var terrain = surface switch { TinSurface t => t.GetTerrainProperties(), GridSurface g => g.GetTerrainProperties(), _ => null };
          if (terrain == null) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Slope analysis needs a TIN or grid surface.");
          lo = terrain.MinimumGradeOrSlope; hi = terrain.MaximumGradeOrSlope;
        }
        if (hi <= lo) hi = lo + 1;
        ranges = new();
        var step = (hi - lo) / Math.Max(1, rangeCount);
        for (var i = 0; i < rangeCount; i++)
          ranges.Add((lo + i * step, i == rangeCount - 1 ? hi : lo + (i + 1) * step, (short)((firstColor + i * colorStep) % 250 + 1)));
      }

      try
      {
        switch (analysis)
        {
          case "elevation":
            surface.Analysis.SetElevationData(ranges.Select(r => new SurfaceAnalysisElevationData(r.min, r.max, Color.FromColorIndex(ColorMethod.ByAci, r.color))).ToArray());
            break;
          case "slope":
            surface.Analysis.SetSlopeData(ranges.Select(r => new SurfaceAnalysisSlopeData(r.min, r.max, Color.FromColorIndex(ColorMethod.ByAci, r.color))).ToArray());
            break;
          case "slope_arrow":
            surface.Analysis.SetSlopeArrowData(ranges.Select(r => new SurfaceAnalysisSlopeArrowData(r.min, r.max, Color.FromColorIndex(ColorMethod.ByAci, r.color))).ToArray());
            break;
          case "direction":
            surface.Analysis.SetDirectionData(ranges.Select(r => new SurfaceAnalysisDirectionData(r.min, r.max, Color.FromColorIndex(ColorMethod.ByAci, r.color))).ToArray());
            break;
          default:
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "analysis must be elevation, slope, slope_arrow or direction.");
        }
      }
      catch (JsonRpcDispatchException) { throw; }
      catch (Exception ex) { throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Setting {analysis} analysis on '{surface.Name}' failed: {ex.GetType().Name}: {ex.Message}"); }

      return new Dictionary<string, object?>
      {
        ["surfaceName"] = surface.Name,
        ["analysis"] = analysis,
        ["rangeCount"] = ranges.Count,
        ["ranges"] = ranges.Select(r => new Dictionary<string, object?> { ["min"] = r.min, ["max"] = r.max, ["colorIndex"] = r.color }).ToList(),
        ["note"] = "Ranges are stored on the surface; the surface style must display the matching analysis component (Elevations / Slopes / Slope Arrows / Directions) to see them.",
      };
    });
  }

  public static Task<object?> PointFileFormatsAsync(JsonObject? parameters)
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var formats = PointFileFormatCollection.GetPointFileFormats(database);
      var list = new List<Dictionary<string, object?>>();
      for (var i = 0; i < formats.Count; i++)
      {
        var f = formats[i];
        list.Add(new Dictionary<string, object?> { ["name"] = f.Name, ["delimiter"] = f.Delimiter, ["extension"] = f.FileExtension, ["type"] = f.FileFormatType.ToString() });
      }
      return new Dictionary<string, object?> { ["formats"] = list, ["count"] = list.Count };
    });
  }

  // ═══════════════════════════ helpers ═══════════════════════════

  /// <summary>Name of the first &lt;Surface&gt; element in a LandXML file (the 3-argument CreateFromLandXML overload is obsolete).</summary>
  private static string FirstLandXmlSurfaceName(string filePath)
  {
    try
    {
      using var reader = System.Xml.XmlReader.Create(filePath, new System.Xml.XmlReaderSettings { IgnoreWhitespace = true, DtdProcessing = System.Xml.DtdProcessing.Ignore });
      while (reader.Read())
        if (reader.NodeType == System.Xml.XmlNodeType.Element && reader.LocalName == "Surface")
        {
          var n = reader.GetAttribute("name");
          if (!string.IsNullOrWhiteSpace(n)) return n;
        }
    }
    catch (Exception ex)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Could not read '{filePath}' as LandXML: {ex.Message}");
    }
    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{filePath}' contains no <Surface> element; give landXmlSurfaceName explicitly.");
  }

  private static void EnsureNoSurfaceNamed(CivilDocument civilDoc, Transaction transaction, string name)
  {
    foreach (ObjectId id in civilDoc.GetSurfaceIds())
    {
      var s = CivilObjectUtils.GetRequiredObject<CivilSurface>(transaction, id, OpenMode.ForRead);
      if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"A surface named '{name}' already exists. Nothing was created.");
    }
  }

  private static TinSurface RequireTin(CivilSurface surface, string what)
  {
    return surface as TinSurface
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{what} needs a TIN surface; '{surface.Name}' is a {surface.GetType().Name}.");
  }

  private static JsonRpcDispatchException NotTinOrGrid(string what)
    => new("CIVIL3D.INVALID_INPUT", $"{what} works on TIN and grid surfaces only.");

  private static PointGroup FindPointGroup(CivilDocument civilDoc, Transaction transaction, string name)
  {
    foreach (ObjectId id in civilDoc.PointGroups)
    {
      var g = CivilObjectUtils.GetRequiredObject<PointGroup>(transaction, id, OpenMode.ForRead);
      if (string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) return g;
    }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Point group '{name}' was not found.");
  }

  private static SurfaceOperation OpAt(SurfaceOperationCollection ops, int? index)
  {
    if (!index.HasValue || index.Value < 0 || index.Value >= ops.Count)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"index must be between 0 and {ops.Count - 1} (see operation list).");
    return ops[index.Value];
  }

  private static Type ResolveOperationType(string typeName)
  {
    var wanted = typeName.Trim();
    if (!wanted.StartsWith("SurfaceOperation", StringComparison.OrdinalIgnoreCase)) wanted = "SurfaceOperation" + wanted;
    Type? type = null;
    try { type = typeof(SurfaceOperation).Assembly.GetType("Autodesk.Civil.DatabaseServices." + wanted, false, true); } catch { }
    if (type != null && !typeof(SurfaceOperation).IsAssignableFrom(type)) type = null;
    return type ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unknown surface operation type '{typeName}' (e.g. AddBreakline, AddBoundary, AddPointGroup, AddDrawingObject, AddContour, AddDEMFile, Raise, PasteSurface, Smooth, Simplify).");
  }

  private static List<Dictionary<string, object?>> ListOperations(CivilSurface surface, Transaction transaction)
  {
    var list = new List<Dictionary<string, object?>>();
    var ops = surface.Operations;
    for (var i = 0; i < ops.Count; i++)
    {
      var op = ops[i];
      var d = new Dictionary<string, object?>
      {
        ["index"] = i,
        ["type"] = op.GetType().Name == "SurfaceOperation" ? "Other (snapshot / unclassified)" : op.GetType().Name.Replace("SurfaceOperation", string.Empty),
        ["enabled"] = Try(() => op.Enabled),
        ["guid"] = Try(() => op.Guid.ToString()),
      };
      switch (op)
      {
        case SurfaceOperationAddBoundary b:
          d["name"] = Try(() => b.Name); d["boundaryType"] = Try(() => b.BoundaryType.ToString()); d["count"] = Try(() => b.Count); break;
        case SurfaceOperationAddBreakline bl:
          d["description"] = Try(() => bl.Description); d["breaklineType"] = Try(() => bl.BreaklineType.ToString()); d["count"] = Try(() => bl.Count); break;
        case SurfaceOperationAddPointGroup pg:
          d["pointGroup"] = Try(() => CivilObjectUtils.GetName(transaction.GetObject(pg.PointGroupId, OpenMode.ForRead))); break;
        case SurfaceOperationAddDrawingObject dobj:
          d["description"] = Try(() => dobj.Description); d["objectType"] = Try(() => dobj.ObjectType.ToString()); break;
        case SurfaceOperationAddBreaklineFromFile bf:
          d["file"] = Try(() => bf.BreaklineFileName); break;
      }
      list.Add(d);
    }
    return list;
  }

  private static Dictionary<string, object?> ReadBuildOptions(CivilSurface surface)
  {
    try
    {
      var bo = surface.BuildOptions;
      return new Dictionary<string, object?>
      {
        ["useMaximumTriangleLength"] = bo.UseMaximumTriangleLength, ["maximumTriangleLength"] = bo.MaximumTriangleLength,
        ["excludeBelowElevation"] = bo.ExecludeMinimumElevation ? (object?)bo.MinimumElevation : null,
        ["excludeAboveElevation"] = bo.ExecludeMaximumElevation ? (object?)bo.MaximumElevation : null,
        ["useMaximumAngle"] = bo.UseMaximumAngle, ["maximumAngleDegrees"] = bo.MaximumAngleBetweenAdjacentTinLines * 180.0 / Math.PI,
        ["crossingBreaklines"] = bo.CrossingBreaklinesElevationOption.ToString(),
        ["copyDeletedDependentObjects"] = bo.CopyDeletedDependentObjects,
        ["convertProximityBreaklines"] = bo.NeedConvertBreaklines,
      };
    }
    catch (Exception ex) { return new Dictionary<string, object?> { ["error"] = ex.Message }; }
  }

  private static void RebuildQuietly(CivilSurface surface, Dictionary<string, object?> result)
  {
    try { surface.Rebuild(); result["rebuilt"] = true; }
    catch (Exception ex) { result["rebuilt"] = false; result["rebuildError"] = ex.Message; }
  }

  private static Dictionary<string, object?> Summary(CivilSurface surface, Transaction transaction, Database database)
  {
    var d = new Dictionary<string, object?>
    {
      ["name"] = surface.Name,
      ["handle"] = CivilObjectUtils.GetHandle(surface),
      ["type"] = surface.GetType().Name,
      ["style"] = Try(() => CivilObjectUtils.GetName(transaction.GetObject(surface.StyleId, OpenMode.ForRead))),
      ["layer"] = surface.Layer,
      ["description"] = Try(() => surface.Description),
      ["isOutOfDate"] = Try(() => surface.IsOutOfDate),
      ["autoRebuild"] = Try(() => surface.AutoRebuild),
      ["locked"] = Try(() => surface.Lock),
      ["hasSnapshot"] = Try(() => surface.HasSnapshot),
      ["operationCount"] = Try(() => surface.Operations.Count),
      ["statistics"] = Stats(surface),
      ["units"] = CivilObjectUtils.LinearUnits(database),
    };
    if (surface is TinVolumeSurface tv)
      d["volume"] = VolumeInfo(tv.GetVolumeProperties(), tv.CutFactor, tv.FillFactor, transaction);
    else if (surface is GridVolumeSurface gv)
      d["volume"] = VolumeInfo(gv.GetVolumeProperties(), gv.CutFactor, gv.FillFactor, transaction);
    if (surface is GridSurface g) { var gp = g.GetGridProperties(); d["grid"] = new Dictionary<string, object?> { ["spacingX"] = gp.SpacingX, ["spacingY"] = gp.SpacingY, ["orientationDegrees"] = gp.Orientation * 180.0 / Math.PI }; }
    return d;
  }

  private static Dictionary<string, object?> VolumeInfo(VolumeSurfaceProperties vp, double cutFactor, double fillFactor, Transaction transaction)
  {
    return new Dictionary<string, object?>
    {
      ["baseSurface"] = Try(() => CivilObjectUtils.GetName(transaction.GetObject(vp.BaseSurface, OpenMode.ForRead))),
      ["comparisonSurface"] = Try(() => CivilObjectUtils.GetName(transaction.GetObject(vp.ComparisonSurface, OpenMode.ForRead))),
      ["cut"] = vp.UnadjustedCutVolume, ["fill"] = vp.UnadjustedFillVolume, ["net"] = vp.UnadjustedNetVolume,
      ["adjustedCut"] = vp.AdjustedCutVolume, ["adjustedFill"] = vp.AdjustedFillVolume, ["adjustedNet"] = vp.AdjustedNetVolume,
      ["cutFactor"] = cutFactor, ["fillFactor"] = fillFactor,
    };
  }

  private static Dictionary<string, object?> Stats(CivilSurface surface)
  {
    try
    {
      var p = surface.GetGeneralProperties();
      var d = new Dictionary<string, object?>
      {
        ["pointCount"] = p.NumberOfPoints, ["minElevation"] = p.MinimumElevation, ["maxElevation"] = p.MaximumElevation, ["meanElevation"] = p.MeanElevation,
        ["minX"] = p.MinimumCoordinateX, ["minY"] = p.MinimumCoordinateY, ["maxX"] = p.MaximumCoordinateX, ["maxY"] = p.MaximumCoordinateY,
      };
      if (surface is TinSurface t) { var tp = t.GetTinProperties(); var tr = t.GetTerrainProperties(); d["triangleCount"] = tp.NumberOfTriangles; d["area2d"] = tr.SurfaceArea2D; d["area3d"] = tr.SurfaceArea3D; d["minSlope"] = tr.MinimumGradeOrSlope; d["maxSlope"] = tr.MaximumGradeOrSlope; }
      else if (surface is GridSurface g) { var tr = g.GetTerrainProperties(); d["area2d"] = tr.SurfaceArea2D; d["area3d"] = tr.SurfaceArea3D; }
      return d;
    }
    catch (Exception ex) { return new Dictionary<string, object?> { ["error"] = ex.Message }; }
  }

  private static List<Dictionary<string, object?>> ListMasks(CivilSurface surface)
  {
    var list = new List<Dictionary<string, object?>>();
    try { for (var i = 0; i < surface.Masks.Count; i++) list.Add(DescribeMask(surface.Masks[i])); } catch { }
    return list;
  }

  private static Dictionary<string, object?> DescribeMask(SurfaceMask m) => new()
  {
    ["name"] = m.Name, ["description"] = Try(() => m.Description), ["type"] = Try(() => m.Type == SurfaceMaskType.InSide ? "inside" : "outside"),
    ["renderOnly"] = Try(() => m.IsRenderOnly),
    ["linkages"] = Try(() => m.Linkages.Cast<ObjectId>().Select(id => id.Handle.ToString()).ToList()),
  };

  private static TinSurfaceVertex VertexAt(TinSurface t, JsonObject? parameters)
  {
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");
    var v = t.FindVertexAtXY(x, y) ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No TIN vertex near ({x}, {y}).");
    var tolerance = PluginRuntime.GetOptionalDouble(parameters, "tolerance") ?? 0.5;
    var dist = Math.Sqrt(Math.Pow(v.Location.X - x, 2) + Math.Pow(v.Location.Y - y, 2));
    if (dist > tolerance)
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Nearest TIN vertex is {dist:F3} away at ({v.Location.X:F3}, {v.Location.Y:F3}); increase tolerance (default 0.5) to use it.");
    return v;
  }

  private static double[] P(Point3d p) => new[] { p.X, p.Y, p.Z };
  private static double Deg2Rad(double d) => d * Math.PI / 180.0;

  private static SurfaceBoundaryType ParseBoundaryType(string value) => value.Trim().ToLowerInvariant().Replace("_", "") switch
  {
    "outer" => SurfaceBoundaryType.Outer,
    "show" => SurfaceBoundaryType.Show,
    "hide" => SurfaceBoundaryType.Hide,
    "dataclip" => SurfaceBoundaryType.DataClip,
    _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"boundaryType '{value}' is not one of outer, show, hide, data_clip."),
  };

  private static KrigingSemivariogramType ParseSemivariogram(string? value) => (value ?? "spherical").Trim().ToLowerInvariant() switch
  {
    "spherical" => KrigingSemivariogramType.Spherical,
    "exponential" => KrigingSemivariogramType.Exponential,
    "linear" => KrigingSemivariogramType.Linear,
    "gaussian" => KrigingSemivariogramType.Gaussian,
    "monomial" => KrigingSemivariogramType.Monomial,
    _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "semivariogram must be spherical, exponential, linear, gaussian or monomial."),
  };

  private static SurfaceMinimizeFlatAreaOptions ReadMinimizeOptions(JsonObject? parameters)
  {
    var o = new SurfaceMinimizeFlatAreaOptions
    {
      FillGaps = PluginRuntime.GetOptionalBool(parameters, "fillGaps") ?? true,
      SwapEdges = PluginRuntime.GetOptionalBool(parameters, "swapEdges") ?? true,
      AddPointsToTriangles = PluginRuntime.GetOptionalBool(parameters, "addPointsToTriangles") ?? true,
      AddPointsToEdges = PluginRuntime.GetOptionalBool(parameters, "addPointsToEdges") ?? true,
    };
    return o;
  }

  private static Point3dCollection ReadPoint3ds(JsonObject? parameters, string key)
  {
    var pts = new Point3dCollection();
    if (PluginRuntime.GetParameter(parameters, key) is not JsonArray arr) return pts;
    foreach (var p in arr)
    {
      if (p is JsonArray xyz && xyz.Count >= 2)
        pts.Add(new Point3d(xyz[0]!.GetValue<double>(), xyz[1]!.GetValue<double>(), xyz.Count > 2 ? xyz[2]!.GetValue<double>() : 0));
      else if (p is JsonObject o)
        pts.Add(new Point3d(PluginRuntime.GetRequiredDouble(o, "x"), PluginRuntime.GetRequiredDouble(o, "y"), PluginRuntime.GetOptionalDouble(o, "z") ?? 0));
    }
    return pts;
  }

  private static Point3dCollection? TryReadPolygon3d(JsonObject? parameters, Database database, Transaction transaction, string pointsKey, string handleKey)
  {
    var handle = PluginRuntime.GetOptionalString(parameters, handleKey);
    if (!string.IsNullOrWhiteSpace(handle))
    {
      var pts = ReadCurvePoints(transaction.GetObject(ResolveHandle(database, handle), OpenMode.ForRead));
      if (pts.Count < 3) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Object {handle} does not give a polygon (needs ≥3 vertices).");
      if (pts[0].DistanceTo(pts[pts.Count - 1]) > 1e-6) pts.Add(pts[0]);
      var col = new Point3dCollection(); foreach (var p in pts) col.Add(p); return col;
    }
    var list = ReadPoint3ds(parameters, pointsKey);
    if (list.Count < 3) return null;
    // GetBoundedVolumes and friends reject an open polygon ("illegal bounding polygon"); close it.
    if (list[0].DistanceTo(list[list.Count - 1]) > 1e-6) list.Add(list[0]);
    return list;
  }

  private static Point3dCollection ReadPolygon3d(JsonObject? parameters, Database database, Transaction transaction, string pointsKey, string handleKey)
    => TryReadPolygon3d(parameters, database, transaction, pointsKey, handleKey)
       ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Give a polygon as {pointsKey} [[x,y],...] (≥3) or {handleKey} of a closed polyline.");

  private static Point2dCollection ReadPolygon2d(JsonObject? parameters, Database database, Transaction transaction, string pointsKey, string handleKey)
  {
    var p3 = ReadPolygon3d(parameters, database, transaction, pointsKey, handleKey);
    var p2 = new Point2dCollection();
    foreach (Point3d p in p3) p2.Add(new Point2d(p.X, p.Y));
    return p2;
  }

  private static Point3dCollection ExtentsPolygon(CivilSurface surface)
  {
    var p = surface.GetGeneralProperties();
    return new Point3dCollection
    {
      new(p.MinimumCoordinateX, p.MinimumCoordinateY, 0), new(p.MaximumCoordinateX, p.MinimumCoordinateY, 0),
      new(p.MaximumCoordinateX, p.MaximumCoordinateY, 0), new(p.MinimumCoordinateX, p.MaximumCoordinateY, 0),
    };
  }

  private static List<Point3d> ReadCurvePoints(Autodesk.AutoCAD.DatabaseServices.DBObject obj)
  {
    var pts = new List<Point3d>();
    switch (obj)
    {
      case Polyline pl:
        for (var i = 0; i < pl.NumberOfVertices; i++) pts.Add(pl.GetPoint3dAt(i));
        break;
      case Polyline2d pl2:
        foreach (ObjectId vid in pl2) if (vid.GetObject(OpenMode.ForRead) is Vertex2d v) pts.Add(v.Position);
        break;
      case Polyline3d pl3:
        foreach (ObjectId vid in pl3) if (vid.GetObject(OpenMode.ForRead) is PolylineVertex3d v) pts.Add(v.Position);
        break;
      case FeatureLine fl:
        foreach (Point3d p in fl.GetPoints(FeatureLinePointType.AllPoints)) pts.Add(p);
        break;
      case Curve c:
      {
        var n = 32;
        var s0 = c.StartParam; var s1 = c.EndParam;
        for (var i = 0; i <= n; i++) pts.Add(c.GetPointAtParameter(s0 + (s1 - s0) * i / n));
        break;
      }
    }
    return pts;
  }

  /// <summary>handles[] and/or layer (+objectType filter) → ObjectIdCollection of model-space entities.</summary>
  private static ObjectIdCollection ResolveEntityIds(JsonObject? parameters, Database database, Transaction transaction, out Dictionary<string, int> typeCounts)
  {
    var ids = new ObjectIdCollection();
    typeCounts = new Dictionary<string, int>();
    if (PluginRuntime.GetParameter(parameters, "handles") is JsonArray arr)
      foreach (var h in arr)
        if (h is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) ids.Add(ResolveHandle(database, s));
    var single = PluginRuntime.GetOptionalString(parameters, "handle");
    if (!string.IsNullOrWhiteSpace(single)) ids.Add(ResolveHandle(database, single));

    var layer = PluginRuntime.GetOptionalString(parameters, "layer");
    var objectType = PluginRuntime.GetOptionalString(parameters, "objectType");
    if (!string.IsNullOrWhiteSpace(layer))
    {
      var ms = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead);
      foreach (ObjectId id in ms)
      {
        if (transaction.GetObject(id, OpenMode.ForRead) is not Autodesk.AutoCAD.DatabaseServices.Entity ent) continue;
        if (!string.Equals(ent.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;
        if (!string.IsNullOrWhiteSpace(objectType) && !ent.GetType().Name.Contains(objectType, StringComparison.OrdinalIgnoreCase)) continue;
        ids.Add(id);
      }
    }
    foreach (ObjectId id in ids)
    {
      var tn = transaction.GetObject(id, OpenMode.ForRead).GetType().Name;
      typeCounts[tn] = typeCounts.TryGetValue(tn, out var c) ? c + 1 : 1;
    }
    return ids;
  }

  private static (ObjectIdCollection points, ObjectIdCollection blocks, ObjectIdCollection texts, ObjectIdCollection lines, ObjectIdCollection faces, ObjectIdCollection polyfaces, List<string> skipped)
    GroupDrawingObjects(ObjectIdCollection ids, Transaction transaction)
  {
    var r = (points: new ObjectIdCollection(), blocks: new ObjectIdCollection(), texts: new ObjectIdCollection(), lines: new ObjectIdCollection(), faces: new ObjectIdCollection(), polyfaces: new ObjectIdCollection(), skipped: new List<string>());
    foreach (ObjectId id in ids)
    {
      var obj = transaction.GetObject(id, OpenMode.ForRead);
      switch (obj)
      {
        case DBPoint: r.points.Add(id); break;
        case BlockReference: r.blocks.Add(id); break;
        case DBText: case MText: r.texts.Add(id); break;
        case Face: r.faces.Add(id); break;
        case PolyFaceMesh: r.polyfaces.Add(id); break;
        case Line: case Polyline: case Polyline3d: case Polyline2d: r.lines.Add(id); break;
        default: r.skipped.Add($"{id.Handle} ({obj.GetType().Name})"); break;
      }
    }
    return r;
  }

  private static double? SafeElevation(CivilSurface surface, double x, double y)
  {
    try { return surface.FindElevationAtXY(x, y); } catch { return null; }
  }

  private static object? Try(Func<object?> read)
  {
    try { return read(); } catch { return null; }
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
}
