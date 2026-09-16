using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using System.Linq;

namespace Civil3DMcpPlugin;

/// <summary>
/// Corridor surfaces (Corridor Properties → Surfaces / Boundaries tabs) and the
/// "Extract Corridor Solids" export, written against the Civil 3D 2026 managed API:
///   Corridor.CorridorSurfaces.Add(name[, styleId]) → CorridorSurface
///   CorridorSurface.AddLinkCode(code, asBreakline) / AddFeatureLineCode(code) / OverhangCorrection
///   CorridorSurface.Boundaries.AddCorridorExtentsBoundary(name) / Add(name, featureLineCode | polylineId | points)
///   Corridor.ExportSolids(ExportCorridorSolidsParams, Database) → ObjectIdCollection of Solid3d
/// </summary>
public static class CorridorSurfaceCommands
{
  // ───────────────────────────── surface_info ─────────────────────────────

  public static Task<object?> InfoAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var surfaceName = PluginRuntime.GetOptionalString(parameters, "surfaceName");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, name, OpenMode.ForRead);
      var surfaces = corridor.CorridorSurfaces.Cast<CorridorSurface>()
        .Where(s => string.IsNullOrWhiteSpace(surfaceName) || string.Equals(s.Name, surfaceName, StringComparison.OrdinalIgnoreCase))
        .Select(s => Describe(s, transaction))
        .ToList();
      if (!string.IsNullOrWhiteSpace(surfaceName) && surfaces.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Corridor surface '{surfaceName}' was not found on corridor '{corridor.Name}'.");

      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridor.Name,
        ["state"] = corridor.IsOutOfDate ? "out_of_date" : "built",
        ["availableLinkCodes"] = SafeArray(() => corridor.GetLinkCodes()),
        ["availablePointCodes"] = SafeArray(() => corridor.GetPointCodes()),
        ["availableFeatureLineCodes"] = corridor.FeatureLineCodeInfos.Cast<FeatureLineCodeInfo>().Select(f => f.CodeName).ToList(),
        ["surfaceCount"] = corridor.CorridorSurfaces.Count,
        ["surfaces"] = surfaces,
      };
    });
  }

  // ───────────────────────────── surface_create ─────────────────────────────

  public static Task<object?> CreateAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var surfaceName = PluginRuntime.GetRequiredString(parameters, "surfaceName");
    var style = PluginRuntime.GetOptionalString(parameters, "style");
    var description = PluginRuntime.GetOptionalString(parameters, "description");
    var overhang = PluginRuntime.GetOptionalString(parameters, "overhangCorrection");
    var rebuild = PluginRuntime.GetOptionalBool(parameters, "rebuild") ?? true;
    var linkCodes = ReadCodeList(PluginRuntime.GetParameter(parameters, "linkCodes"));
    var featureLineCodes = ReadStringList(PluginRuntime.GetParameter(parameters, "featureLineCodes"));
    var boundaries = PluginRuntime.GetParameter(parameters, "boundaries") as JsonArray;

    if (linkCodes.Count == 0 && featureLineCodes.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        "A corridor surface needs at least one link code (e.g. Top, Datum) or feature line code. Nothing was created.");

    return CivilExecution.WriteAsCommandAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, name, OpenMode.ForWrite);
      if (corridor.CorridorSurfaces.Cast<CorridorSurface>().Any(s => string.Equals(s.Name, surfaceName, StringComparison.OrdinalIgnoreCase)))
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Corridor '{corridor.Name}' already has a surface named '{surfaceName}'. Nothing was created.");

      ValidateCodes(corridor, linkCodes.Select(c => c.code), featureLineCodes);

      var styleId = LookupUtils.GetSurfaceStyleId(civilDoc, transaction, style);
      CorridorSurface surface;
      try
      {
        surface = styleId.IsNull ? corridor.CorridorSurfaces.Add(surfaceName) : corridor.CorridorSurfaces.Add(surfaceName, styleId);
      }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Civil 3D refused to add corridor surface '{surfaceName}': {ex.GetType().Name}: {ex.Message}. Nothing was created.");
      }

      var result = new Dictionary<string, object?> { ["corridorName"] = corridor.Name, ["surfaceName"] = surfaceName, ["created"] = true };
      try
      {
        if (!string.IsNullOrWhiteSpace(description)) surface.Description = description;
        foreach (var (code, breakline) in linkCodes) surface.AddLinkCode(code, breakline);
        foreach (var code in featureLineCodes) surface.AddFeatureLineCode(code);
        if (!string.IsNullOrWhiteSpace(overhang)) surface.OverhangCorrection = ParseOverhang(overhang);
        surface.IsBuild = true;

        var boundaryResults = new List<Dictionary<string, object?>>();
        if (boundaries != null)
          foreach (var node in boundaries.OfType<JsonObject>())
            boundaryResults.Add(AddBoundary(surface, node, database, transaction));
        result["boundaries"] = boundaryResults;
      }
      catch (JsonRpcDispatchException) { throw; }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Corridor surface '{surfaceName}' was added but configuring it failed: {ex.GetType().Name}: {ex.Message}");
      }

      if (rebuild) RebuildInCommand(corridor, result);
      result["surface"] = Describe(surface, transaction);
      return result;
    });
  }

  // ───────────────────────────── surface_edit ─────────────────────────────

  public static Task<object?> EditAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var surfaceName = PluginRuntime.GetRequiredString(parameters, "surfaceName");
    var operation = PluginRuntime.GetRequiredString(parameters, "operation").Trim().ToLowerInvariant();
    var rebuild = PluginRuntime.GetOptionalBool(parameters, "rebuild") ?? true;

    return CivilExecution.WriteAsCommandAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, name, OpenMode.ForWrite);
      var surface = Find(corridor, surfaceName);
      var result = new Dictionary<string, object?> { ["corridorName"] = corridor.Name, ["surfaceName"] = surface.Name, ["operation"] = operation };
      try
      {
        switch (operation)
        {
          case "add_link_code":
          {
            var codes = ReadCodeList(PluginRuntime.GetParameter(parameters, "linkCodes"));
            var single = PluginRuntime.GetOptionalString(parameters, "code");
            if (!string.IsNullOrWhiteSpace(single)) codes.Add((single, PluginRuntime.GetOptionalBool(parameters, "breakline") ?? false));
            if (codes.Count == 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "add_link_code needs 'code' or 'linkCodes'.");
            ValidateCodes(corridor, codes.Select(c => c.code), Array.Empty<string>());
            foreach (var (code, breakline) in codes) surface.AddLinkCode(code, breakline);
            result["added"] = codes.Select(c => c.code).ToList();
            break;
          }
          case "remove_link_code":
          {
            var code = PluginRuntime.GetRequiredString(parameters, "code");
            result["removed"] = surface.RemoveLinkCode(code);
            break;
          }
          case "set_breakline":
          {
            var code = PluginRuntime.GetRequiredString(parameters, "code");
            var breakline = PluginRuntime.GetOptionalBool(parameters, "breakline") ?? true;
            surface.SetLinkCodeAsBreakLine(code, breakline);
            result["breakline"] = surface.IsLinkCodeAsBreakLine(code);
            break;
          }
          case "add_feature_line_code":
          {
            var codes = ReadStringList(PluginRuntime.GetParameter(parameters, "featureLineCodes"));
            var single = PluginRuntime.GetOptionalString(parameters, "code");
            if (!string.IsNullOrWhiteSpace(single)) codes.Add(single);
            if (codes.Count == 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "add_feature_line_code needs 'code' or 'featureLineCodes'.");
            ValidateCodes(corridor, Array.Empty<string>(), codes);
            foreach (var code in codes) surface.AddFeatureLineCode(code);
            result["added"] = codes;
            break;
          }
          case "remove_feature_line_code":
          {
            var code = PluginRuntime.GetRequiredString(parameters, "code");
            result["removed"] = surface.RemoveFeatureLineCode(code);
            break;
          }
          case "set_overhang":
          {
            var overhang = PluginRuntime.GetRequiredString(parameters, "overhangCorrection");
            surface.OverhangCorrection = ParseOverhang(overhang);
            result["overhangCorrection"] = surface.OverhangCorrection.ToString();
            break;
          }
          case "add_boundary":
          {
            var node = PluginRuntime.GetParameter(parameters, "boundary") as JsonObject ?? parameters ?? new JsonObject();
            result["boundary"] = AddBoundary(surface, node, database, transaction);
            break;
          }
          case "remove_boundary":
          {
            var boundaryName = PluginRuntime.GetRequiredString(parameters, "boundaryName");
            result["removed"] = surface.Boundaries.Remove(boundaryName);
            break;
          }
          case "set_style":
          {
            var style = PluginRuntime.GetRequiredString(parameters, "style");
            var styleId = LookupUtils.GetSurfaceStyleId(civilDoc, transaction, style);
            if (styleId.IsNull) throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Surface style '{style}' was not found.");
            surface.SurfaceStyleId = styleId;
            break;
          }
          case "rename":
          {
            var newName = PluginRuntime.GetRequiredString(parameters, "newName");
            surface.Name = newName;
            result["surfaceName"] = newName;
            break;
          }
          case "set_description":
            surface.Description = PluginRuntime.GetOptionalString(parameters, "description") ?? string.Empty;
            break;
          case "set_build":
            surface.IsBuild = PluginRuntime.GetOptionalBool(parameters, "build") ?? true;
            break;
          default:
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
              $"operation '{operation}' is not one of add_link_code, remove_link_code, set_breakline, add_feature_line_code, remove_feature_line_code, set_overhang, add_boundary, remove_boundary, set_style, rename, set_description, set_build.");
        }
      }
      catch (JsonRpcDispatchException) { throw; }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{operation} on corridor surface '{surface.Name}' failed: {ex.GetType().Name}: {ex.Message}");
      }

      if (rebuild) RebuildInCommand(corridor, result);
      result["surface"] = Describe(surface, transaction);
      return result;
    });
  }

  // ───────────────────────────── surface_delete ─────────────────────────────

  public static Task<object?> DeleteAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var surfaceName = PluginRuntime.GetRequiredString(parameters, "surfaceName");
    var rebuild = PluginRuntime.GetOptionalBool(parameters, "rebuild") ?? true;
    return CivilExecution.WriteAsCommandAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, name, OpenMode.ForWrite);
      var surface = Find(corridor, surfaceName);
      var actualName = surface.Name;
      bool removed;
      try { removed = corridor.CorridorSurfaces.Remove(surface); }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Could not remove corridor surface '{actualName}': {ex.GetType().Name}: {ex.Message}");
      }
      var result = new Dictionary<string, object?> { ["corridorName"] = corridor.Name, ["surfaceName"] = actualName, ["removed"] = removed, ["remainingSurfaces"] = corridor.CorridorSurfaces.Count };
      if (rebuild && removed) RebuildInCommand(corridor, result);
      return result;
    });
  }

  /// <summary>Corridor.Rebuild() inside the same real command, as in Autodesk's corridor-surface sample.</summary>
  private static void RebuildInCommand(Corridor corridor, Dictionary<string, object?> result)
  {
    try { corridor.Rebuild(); result["rebuilt"] = true; result["state"] = corridor.IsOutOfDate ? "out_of_date" : "built"; }
    catch (Exception ex) { result["rebuilt"] = false; result["rebuildError"] = ex.Message; }
  }

  // ───────────────────────────── export_solids ─────────────────────────────

  public static Task<object?> ExportSolidsAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var exportShapes = PluginRuntime.GetOptionalBool(parameters, "exportShapes") ?? true;
    var exportLinks = PluginRuntime.GetOptionalBool(parameters, "exportLinks") ?? false;
    var createSolidForShape = PluginRuntime.GetOptionalBool(parameters, "createSolidForShape") ?? true;
    var sweepSolidForShape = PluginRuntime.GetOptionalBool(parameters, "sweepSolidForShape") ?? false;
    var includedCodes = ReadStringList(PluginRuntime.GetParameter(parameters, "includedCodes"));
    var excludedCodes = ReadStringList(PluginRuntime.GetParameter(parameters, "excludedCodes"));
    var layer = PluginRuntime.GetOptionalString(parameters, "layer");
    var outputPath = PluginRuntime.GetOptionalString(parameters, "outputPath");
    var maxListed = PluginRuntime.GetOptionalInt(parameters, "maxListed") ?? 50;

    if (!exportShapes && !exportLinks)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Nothing to export: enable exportShapes and/or exportLinks.");
    if (!string.IsNullOrWhiteSpace(outputPath) && !outputPath.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "outputPath must be a .dwg path (the solids are written to a new drawing).");

    return CivilExecution.WriteAsCommandAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, name, OpenMode.ForRead);
      if (corridor.IsOutOfDate)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Corridor '{corridor.Name}' is out of date; rebuild it before extracting solids.");

      var exportParams = new ExportCorridorSolidsParams
      {
        ExportShapes = exportShapes,
        ExportLinks = exportLinks,
        CreateSolidForShape = createSolidForShape,
        SweepSolidForShape = sweepSolidForShape,
      };
      if (includedCodes.Count > 0) exportParams.IncludedCodes = includedCodes.ToArray();
      if (excludedCodes.Count > 0) exportParams.ExcludedCodes = excludedCodes.ToArray();

      var toNewDrawing = !string.IsNullOrWhiteSpace(outputPath);
      Database targetDb = database;
      if (toNewDrawing) targetDb = new Database(true, true);

      ObjectIdCollection ids;
      var started = DateTime.UtcNow;
      try
      {
        ids = corridor.ExportSolids(exportParams, targetDb);
      }
      catch (Exception ex)
      {
        if (toNewDrawing) targetDb.Dispose();
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Corridor.ExportSolids failed on '{corridor.Name}': {ex.GetType().Name}: {ex.Message}. Nothing was created.");
      }

      var result = new Dictionary<string, object?>
      {
        ["corridorName"] = corridor.Name,
        ["solidCount"] = ids.Count,
        ["exportShapes"] = exportShapes,
        ["exportLinks"] = exportLinks,
        ["createSolidForShape"] = createSolidForShape,
        ["sweepSolidForShape"] = sweepSolidForShape,
        ["includedCodes"] = includedCodes,
        ["excludedCodes"] = excludedCodes,
        ["elapsedSeconds"] = Math.Round((DateTime.UtcNow - started).TotalSeconds, 2),
      };

      if (toNewDrawing)
      {
        // Read the solids from the new database with its own transaction, save it, and release it.
        using (targetDb)
        {
          using (var tr = targetDb.TransactionManager.StartTransaction())
          {
            result["solids"] = SummariseSolids(ids, tr, layer, maxListed, result);
            tr.Commit();
          }
          try { targetDb.SaveAs(outputPath, DwgVersion.Current); }
          catch (Exception ex)
          {
            throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Solids were extracted but the drawing could not be saved to '{outputPath}': {ex.Message}");
          }
        }
        result["outputPath"] = outputPath;
      }
      else
      {
        result["solids"] = SummariseSolids(ids, transaction, layer, maxListed, result);
        result["outputPath"] = null;
      }
      return result;
    });
  }

  // ───────────────────────────── helpers ─────────────────────────────

  private static List<Dictionary<string, object?>> SummariseSolids(ObjectIdCollection ids, Transaction transaction, string? layer, int maxListed, Dictionary<string, object?> result)
  {
    var listed = new List<Dictionary<string, object?>>();
    var byLayer = new Dictionary<string, (int count, double volume)>(StringComparer.OrdinalIgnoreCase);
    double totalVolume = 0;
    var handles = new List<string>();
    foreach (ObjectId id in ids)
    {
      if (id.IsNull) continue;
      var obj = transaction.GetObject(id, string.IsNullOrWhiteSpace(layer) ? OpenMode.ForRead : OpenMode.ForWrite);
      if (obj is not Solid3d solid) continue;
      if (!string.IsNullOrWhiteSpace(layer)) { try { solid.Layer = layer; } catch { } }
      double volume = 0;
      try { volume = solid.MassProperties.Volume; } catch { }
      totalVolume += volume;
      (int count, double volume) entry = byLayer.TryGetValue(solid.Layer, out var v) ? v : (0, 0.0);
      byLayer[solid.Layer] = (entry.count + 1, entry.volume + volume);
      handles.Add(id.Handle.ToString());
      if (listed.Count < maxListed)
      {
        var extents = TryExtents(solid);
        listed.Add(new Dictionary<string, object?>
        {
          ["handle"] = id.Handle.ToString(),
          ["layer"] = solid.Layer,
          ["volume"] = Math.Round(volume, 4),
          ["minPoint"] = extents?.MinPoint is { } mn ? new[] { mn.X, mn.Y, mn.Z } : null,
          ["maxPoint"] = extents?.MaxPoint is { } mx ? new[] { mx.X, mx.Y, mx.Z } : null,
        });
      }
    }
    result["totalVolume"] = Math.Round(totalVolume, 4);
    result["byLayer"] = byLayer.OrderBy(kv => kv.Key).Select(kv => new Dictionary<string, object?>
    {
      ["layer"] = kv.Key, ["count"] = kv.Value.count, ["volume"] = Math.Round(kv.Value.volume, 4),
    }).ToList();
    result["handles"] = handles;
    result["listedCount"] = listed.Count;
    return listed;
  }

  private static Extents3d? TryExtents(Autodesk.AutoCAD.DatabaseServices.Entity entity)
  {
    try { return entity.GeometricExtents; } catch { return null; }
  }

  private static Dictionary<string, object?> AddBoundary(CorridorSurface surface, JsonObject node, Database database, Transaction transaction)
  {
    var type = (PluginRuntime.GetOptionalString(node, "type") ?? "corridor_extents").Trim().ToLowerInvariant();
    var boundaryName = PluginRuntime.GetOptionalString(node, "boundaryName") ?? PluginRuntime.GetOptionalString(node, "name") ?? DefaultBoundaryName(surface, type);
    var useAs = (PluginRuntime.GetOptionalString(node, "useAs") ?? "outside").Trim().ToLowerInvariant();
    if (surface.Boundaries.BoundaryNames().Any(b => string.Equals(b, boundaryName, StringComparison.OrdinalIgnoreCase)))
      throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Corridor surface '{surface.Name}' already has a boundary named '{boundaryName}'.");

    CorridorSurfaceBoundary boundary;
    switch (type)
    {
      case "corridor_extents":
      case "extents":
        boundary = surface.Boundaries.AddCorridorExtentsBoundary(boundaryName);
        break;
      case "feature_line":
      case "feature_line_code":
      {
        var code = PluginRuntime.GetOptionalString(node, "code") ?? PluginRuntime.GetOptionalString(node, "featureLineCode")
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "A feature_line boundary needs 'code' (a corridor feature line code, e.g. Daylight).");
        boundary = surface.Boundaries.Add(boundaryName, code);
        break;
      }
      case "polyline":
      {
        var handle = PluginRuntime.GetOptionalString(node, "polylineHandle") ?? PluginRuntime.GetOptionalString(node, "handle")
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "A polyline boundary needs 'polylineHandle' (closed polyline).");
        var id = ResolveHandle(database, handle);
        var obj = transaction.GetObject(id, OpenMode.ForRead);
        if (obj is not Curve curve || !curve.Closed)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Object {handle} must be a closed polyline to use as a corridor surface boundary.");
        boundary = surface.Boundaries.Add(boundaryName, id);
        break;
      }
      case "points":
      {
        var pts = new Point3dCollection();
        if (PluginRuntime.GetParameter(node, "points") is JsonArray arr)
          foreach (var p in arr)
          {
            if (p is JsonArray xyz && xyz.Count >= 2)
              pts.Add(new Point3d(xyz[0]!.GetValue<double>(), xyz[1]!.GetValue<double>(), xyz.Count > 2 ? xyz[2]!.GetValue<double>() : 0));
            else if (p is JsonObject o)
              pts.Add(new Point3d(PluginRuntime.GetRequiredDouble(o, "x"), PluginRuntime.GetRequiredDouble(o, "y"), PluginRuntime.GetOptionalDouble(o, "z") ?? 0));
          }
        if (pts.Count < 3) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "A points boundary needs at least 3 points ([[x,y],[x,y],...]).");
        boundary = surface.Boundaries.Add(boundaryName, pts);
        break;
      }
      default:
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Boundary type '{type}' is not one of corridor_extents, feature_line, polyline, points.");
    }

    // A corridor-extents (shrink-wrap) boundary is always an outside boundary; Civil 3D throws if the type is touched.
    var wantInside = useAs is "inside" or "hide" or "hide_inside";
    if (type is "corridor_extents" or "extents")
    {
      if (wantInside)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "A corridor_extents boundary is always an outside boundary; use a feature_line, polyline or points boundary for an inside (hide) boundary.");
    }
    else
    {
      boundary.BoundaryType = wantInside ? CorridorSurfaceBoundaryType.InsideBoundary : CorridorSurfaceBoundaryType.OutsideBoundary;
    }
    return DescribeBoundary(boundary);
  }

  private static string DefaultBoundaryName(CorridorSurface surface, string type)
  {
    var stem = type switch { "feature_line" or "feature_line_code" => "Feature line boundary", "polyline" => "Polyline boundary", "points" => "Point boundary", _ => "Corridor extents" };
    var existing = surface.Boundaries.BoundaryNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (!existing.Contains(stem)) return stem;
    for (var i = 2; i < 1000; i++) if (!existing.Contains($"{stem} ({i})")) return $"{stem} ({i})";
    return $"{stem} {Guid.NewGuid():N}";
  }

  private static void ValidateCodes(Corridor corridor, IEnumerable<string> linkCodes, IEnumerable<string> featureLineCodes)
  {
    var availableLinks = SafeArray(() => corridor.GetLinkCodes()).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var availableFls = corridor.FeatureLineCodeInfos.Cast<FeatureLineCodeInfo>().Select(f => f.CodeName).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var badLinks = linkCodes.Where(c => availableLinks.Count > 0 && !availableLinks.Contains(c)).ToList();
    var badFls = featureLineCodes.Where(c => availableFls.Count > 0 && !availableFls.Contains(c)).ToList();
    if (badLinks.Count > 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"Link code(s) {string.Join(", ", badLinks)} are not used by corridor '{corridor.Name}'. Available: {string.Join(", ", availableLinks.OrderBy(x => x))}.");
    if (badFls.Count > 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"Feature line code(s) {string.Join(", ", badFls)} are not used by corridor '{corridor.Name}'. Available: {string.Join(", ", availableFls.OrderBy(x => x))}.");
  }

  private static CorridorSurface Find(Corridor corridor, string surfaceName)
  {
    return corridor.CorridorSurfaces.Cast<CorridorSurface>()
      .FirstOrDefault(s => string.Equals(s.Name, surfaceName, StringComparison.OrdinalIgnoreCase))
      ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
        $"Corridor surface '{surfaceName}' was not found on corridor '{corridor.Name}'. Surfaces: {string.Join(", ", corridor.CorridorSurfaces.SurfaceNames())}.");
  }

  private static OverhangCorrectionType ParseOverhang(string value)
  {
    return value.Trim().ToLowerInvariant().Replace("_", "").Replace(" ", "") switch
    {
      "none" or "" => OverhangCorrectionType.None,
      "top" or "toplinks" => OverhangCorrectionType.TopLinks,
      "bottom" or "bottomlinks" => OverhangCorrectionType.BottomLinks,
      _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"overhangCorrection '{value}' is not one of none, top_links, bottom_links."),
    };
  }

  private static Dictionary<string, object?> Describe(CorridorSurface surface, Transaction transaction)
  {
    var d = new Dictionary<string, object?>
    {
      ["name"] = surface.Name,
      ["description"] = Try(() => surface.Description),
      ["surfaceHandle"] = surface.SurfaceId.IsNull ? null : surface.SurfaceId.Handle.ToString(),
      ["style"] = Try(() => CivilObjectUtils.GetName(transaction.GetObject(surface.SurfaceStyleId, OpenMode.ForRead))),
      ["isBuild"] = Try(() => surface.IsBuild),
      ["overhangCorrection"] = Try(() => surface.OverhangCorrection.ToString()),
      ["linkCodes"] = SafeArray(() => surface.LinkCodes()).Select(c => new Dictionary<string, object?> { ["code"] = c, ["breakline"] = Try(() => surface.IsLinkCodeAsBreakLine(c)) }).ToList(),
      ["pointCodes"] = SafeArray(() => surface.PointCodes()),
      ["featureLineCodes"] = SafeArray(() => surface.FeatureLineCodes()),
      ["boundaries"] = surface.Boundaries.Cast<CorridorSurfaceBoundary>().Select(DescribeBoundary).ToList(),
      ["masks"] = SafeArray(() => surface.Masks.MaskNames()),
    };
    if (!surface.SurfaceId.IsNull)
    {
      try
      {
        var built = transaction.GetObject(surface.SurfaceId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
        if (built != null)
        {
          var props = built.GetGeneralProperties();
          d["built"] = new Dictionary<string, object?>
          {
            ["objectType"] = built.GetType().Name,
            ["layer"] = built.Layer,
            ["pointCount"] = props.NumberOfPoints,
            ["minElevation"] = props.MinimumElevation,
            ["maxElevation"] = props.MaximumElevation,
            ["meanElevation"] = props.MeanElevation,
            ["minX"] = props.MinimumCoordinateX, ["minY"] = props.MinimumCoordinateY,
            ["maxX"] = props.MaximumCoordinateX, ["maxY"] = props.MaximumCoordinateY,
          };
        }
      }
      catch (Exception ex) { d["built"] = new Dictionary<string, object?> { ["error"] = ex.Message }; }
    }
    return d;
  }

  private static Dictionary<string, object?> DescribeBoundary(CorridorSurfaceBoundary b)
  {
    return new Dictionary<string, object?>
    {
      ["name"] = b.Name,
      ["useAs"] = Try(() => b.BoundaryType == CorridorSurfaceBoundaryType.InsideBoundary ? "inside" : "outside"),
      ["isCorridorExtents"] = Try(() => b.IsCorridorExtents),
      ["isDefinedFromPolygon"] = Try(() => b.IsDefinedFromPolygon),
      ["pointCount"] = Try(() => b.PolygonPoints()?.Length),
    };
  }

  private static List<(string code, bool breakline)> ReadCodeList(object? node)
  {
    var list = new List<(string, bool)>();
    if (node is not JsonArray arr) return list;
    foreach (var item in arr)
    {
      if (item is JsonObject o)
      {
        var code = PluginRuntime.GetOptionalString(o, "code");
        if (!string.IsNullOrWhiteSpace(code)) list.Add((code, PluginRuntime.GetOptionalBool(o, "breakline") ?? false));
      }
      else if (item is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
        list.Add((s, false));
    }
    return list;
  }

  private static List<string> ReadStringList(object? node)
  {
    var list = new List<string>();
    if (node is not JsonArray arr) return list;
    foreach (var item in arr)
      if (item is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) list.Add(s);
    return list;
  }

  private static List<string> SafeArray(Func<string[]?> read)
  {
    try { return (read() ?? Array.Empty<string>()).ToList(); } catch { return new List<string>(); }
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
