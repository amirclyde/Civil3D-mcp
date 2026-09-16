using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Generic AutoCAD entity access: list/read plain drawing entities (text, lines, polylines,
/// dimensions, block references with attributes, ...) with layer / type / window / text filters,
/// optionally descending into block references, and select + zoom to entities by handle.
/// Civil 3D objects are reported by type name only; the domain tools describe them properly.
/// </summary>
public static class EntityCommands
{
  private const int MaxNestedDepth = 3;

  // -------------------------------------------------------------------------
  // listEntities
  // -------------------------------------------------------------------------

  public static Task<object?> ListEntitiesAsync(JsonObject? parameters)
  {
    var layerPattern = PluginRuntime.GetOptionalString(parameters, "layer");
    var typesNode = PluginRuntime.GetParameter(parameters, "types") as JsonArray;
    var textContains = PluginRuntime.GetOptionalString(parameters, "textContains");
    var layoutName = PluginRuntime.GetOptionalString(parameters, "layout");
    var includeNested = PluginRuntime.GetOptionalBool(parameters, "includeNested") ?? false;
    var summary = PluginRuntime.GetOptionalBool(parameters, "summary") ?? false;
    var brief = string.Equals(PluginRuntime.GetOptionalString(parameters, "detail"), "brief", StringComparison.OrdinalIgnoreCase);
    var limit = Math.Max(1, PluginRuntime.GetOptionalInt(parameters, "limit") ?? 200);
    var offset = Math.Max(0, PluginRuntime.GetOptionalInt(parameters, "offset") ?? 0);
    var handlesNode = PluginRuntime.GetParameter(parameters, "handles") as JsonArray;

    var window = ReadWindow(PluginRuntime.GetParameter(parameters, "window") as JsonObject);
    var layerRegexes = BuildLayerRegexes(layerPattern);
    var typeSet = BuildTypeSet(typesNode);
    var handleSet = handlesNode == null ? null : new HashSet<string>(handlesNode.Select(n => (n?.ToString() ?? "").Trim().ToUpperInvariant()).Where(s => s.Length > 0));

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var spaceId = ResolveSpace(database, transaction, layoutName, out var spaceLabel);
      var space = transaction.GetObject(spaceId, OpenMode.ForRead) as BlockTableRecord
        ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", "Could not open the requested space.");

      var matches = new List<Dictionary<string, object?>>();
      var countsByType = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      var countsByLayer = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      var scanned = 0;
      double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;

      void Visit(Entity entity, Matrix3d transform, int depth, string? blockName, string? blockRefHandle, bool ancestorMatched)
      {
        scanned++;
        var typeName = entity.GetType().Name;
        var layer = entity.Layer ?? "";
        var ownHandle = entity.Handle.ToString().ToUpperInvariant();
        var handleMatched = handleSet != null && handleSet.Contains(ownHandle);

        if (handleSet != null && !handleMatched && !ancestorMatched && !(entity is BlockReference && includeNested))
        {
          return; // handles given: only those objects (and, with includeNested, whatever sits inside named block references)
        }

        Extents3d? ext = null;
        try
        {
          var e = entity.GeometricExtents;
          if (depth > 0) e.TransformBy(transform);
          ext = e;
        }
        catch { }

        var layerOk = layerRegexes == null || layerRegexes.Any(r => r.IsMatch(layer));
        var typeOk = typeSet == null || typeSet.Contains(typeName) || (typeSet.Contains("text") && IsTextLike(entity)) || (typeSet.Contains("curve") && entity is Curve);
        var windowOk = window == null || (ext.HasValue && Intersects(ext.Value, window.Value));
        var handleOk = handleSet == null || handleMatched || ancestorMatched;

        if (layerOk && typeOk && windowOk && handleOk)
        {
          var text = ExtractText(entity, transaction);
          var textOk = string.IsNullOrEmpty(textContains) || (text != null && text.IndexOf(textContains, StringComparison.OrdinalIgnoreCase) >= 0);
          if (textOk)
          {
            countsByType[typeName] = countsByType.TryGetValue(typeName, out var c) ? c + 1 : 1;
            countsByLayer[layer] = countsByLayer.TryGetValue(layer, out var l) ? l + 1 : 1;
            if (ext.HasValue)
            {
              minX = Math.Min(minX, ext.Value.MinPoint.X); minY = Math.Min(minY, ext.Value.MinPoint.Y);
              maxX = Math.Max(maxX, ext.Value.MaxPoint.X); maxY = Math.Max(maxY, ext.Value.MaxPoint.Y);
            }
            if (!summary)
            {
              var row = Describe(entity, transform, depth, ext, text, brief, transaction);
              if (depth > 0) { row["nested"] = true; row["blockName"] = blockName; row["blockRefHandle"] = blockRefHandle; }
              matches.Add(row);
            }
          }
        }

        if (includeNested && depth < MaxNestedDepth && entity is BlockReference br)
        {
          BlockTableRecord? btr = null;
          try { btr = transaction.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord; } catch { }
          if (btr == null || btr.IsFromExternalReference || btr.IsLayout) return;
          var childTransform = br.BlockTransform.PreMultiplyBy(transform);
          foreach (ObjectId childId in btr)
          {
            Entity? child = null;
            try { child = transaction.GetObject(childId, OpenMode.ForRead) as Entity; } catch { }
            if (child == null || !child.Visible) continue;
            Visit(child, childTransform, depth + 1, btr.Name, entity.Handle.ToString(), ancestorMatched || handleMatched);
          }
        }
      }

      foreach (ObjectId id in space)
      {
        Entity? entity = null;
        try { entity = transaction.GetObject(id, OpenMode.ForRead) as Entity; } catch { }
        if (entity == null) continue;
        Visit(entity, Matrix3d.Identity, 0, null, null, false);
      }

      var total = matches.Count;
      var page = summary ? new List<Dictionary<string, object?>>() : matches.Skip(offset).Take(limit).ToList();
      return new Dictionary<string, object?>
      {
        ["space"] = spaceLabel,
        ["scanned"] = scanned,
        ["matchedCount"] = summary ? countsByType.Values.Sum() : total,
        ["returnedCount"] = page.Count,
        ["offset"] = offset,
        ["limit"] = limit,
        ["truncated"] = !summary && offset + page.Count < total,
        ["extents"] = minX == double.MaxValue ? null : new Dictionary<string, object?> { ["minX"] = R(minX), ["minY"] = R(minY), ["maxX"] = R(maxX), ["maxY"] = R(maxY) },
        ["countsByType"] = countsByType.ToDictionary(k => k.Key, k => (object?)k.Value),
        ["countsByLayer"] = countsByLayer.ToDictionary(k => k.Key, k => (object?)k.Value),
        ["entities"] = page,
      };
    });
  }

  // -------------------------------------------------------------------------
  // selectEntities: set the implied selection (grips) and zoom to the objects
  // -------------------------------------------------------------------------

  public static Task<object?> SelectEntitiesAsync(JsonObject? parameters)
  {
    var handlesNode = PluginRuntime.GetParameter(parameters, "handles") as JsonArray;
    var single = PluginRuntime.GetOptionalString(parameters, "handle");
    var zoom = PluginRuntime.GetOptionalBool(parameters, "zoom") ?? true;
    var select = PluginRuntime.GetOptionalBool(parameters, "select") ?? true;
    var margin = PluginRuntime.GetOptionalDouble(parameters, "zoomMargin") ?? 0.25;

    var handles = new List<string>();
    if (handlesNode != null) handles.AddRange(handlesNode.Select(n => (n?.ToString() ?? "").Trim()).Where(s => s.Length > 0));
    if (!string.IsNullOrWhiteSpace(single)) handles.Add(single.Trim());
    if (handles.Count == 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give handle or handles[].");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var ids = new List<ObjectId>();
      var missing = new List<string>();
      Extents3d? union = null;
      foreach (var h in handles)
      {
        ObjectId id;
        try { id = database.TryGetObjectId(new Handle(Convert.ToInt64(h, 16)), out var found) && !found.IsNull ? found : ObjectId.Null; }
        catch { id = ObjectId.Null; }
        if (id.IsNull || id.IsErased) { missing.Add(h); continue; }
        ids.Add(id);
        try
        {
          if (transaction.GetObject(id, OpenMode.ForRead) is Entity ent)
          {
            var e = ent.GeometricExtents;
            if (union == null) union = e; else { var u = union.Value; u.AddExtents(e); union = u; }
          }
        }
        catch { }
      }
      if (ids.Count == 0) throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"None of the handles exist: {string.Join(", ", missing)}");

      var editor = doc.Editor;
      if (select)
      {
        try { editor.SetImpliedSelection(ids.ToArray()); }
        catch (Exception ex) { throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Could not select: {ex.Message}"); }
      }

      Dictionary<string, object?>? view = null;
      if (zoom && union.HasValue)
      {
        var e = union.Value;
        var w = Math.Max(e.MaxPoint.X - e.MinPoint.X, 1e-3);
        var hgt = Math.Max(e.MaxPoint.Y - e.MinPoint.Y, 1e-3);
        var cx = (e.MinPoint.X + e.MaxPoint.X) / 2; var cy = (e.MinPoint.Y + e.MaxPoint.Y) / 2;
        using var vtr = editor.GetCurrentView();
        var aspect = vtr.Height > 0 ? vtr.Width / vtr.Height : 1.0;
        var vw = w * (1 + margin * 2); var vh = hgt * (1 + margin * 2);
        if (vw / vh < aspect) vw = vh * aspect; else vh = vw / aspect;
        vtr.CenterPoint = new Point2d(cx, cy);
        vtr.Width = vw; vtr.Height = vh;
        try { editor.SetCurrentView(vtr); }
        catch (Exception ex) { throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Could not zoom: {ex.Message}"); }
        view = new Dictionary<string, object?> { ["centerX"] = R(cx), ["centerY"] = R(cy), ["width"] = R(vw), ["height"] = R(vh) };
      }
      try { editor.UpdateScreen(); } catch { }

      return new Dictionary<string, object?>
      {
        ["selectedCount"] = select ? ids.Count : 0,
        ["handles"] = ids.Select(i => i.Handle.ToString()).ToList(),
        ["missing"] = missing,
        ["extents"] = union.HasValue ? new Dictionary<string, object?> { ["minX"] = R(union.Value.MinPoint.X), ["minY"] = R(union.Value.MinPoint.Y), ["maxX"] = R(union.Value.MaxPoint.X), ["maxY"] = R(union.Value.MaxPoint.Y) } : null,
        ["view"] = view,
      };
    });
  }

  // -------------------------------------------------------------------------
  // Helpers
  // -------------------------------------------------------------------------

  private static ObjectId ResolveSpace(Database database, Transaction transaction, string? layoutName, out string label)
  {
    if (string.IsNullOrWhiteSpace(layoutName) || string.Equals(layoutName, "model", StringComparison.OrdinalIgnoreCase))
    {
      label = "Model";
      return SymbolUtilityServices.GetBlockModelSpaceId(database);
    }
    if (string.Equals(layoutName, "current", StringComparison.OrdinalIgnoreCase) || layoutName == "*")
    {
      var btr = transaction.GetObject(database.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
      label = btr?.Name ?? "current";
      return database.CurrentSpaceId;
    }
    var layouts = transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead) as DBDictionary
      ?? throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", "Layout dictionary unavailable.");
    foreach (DBDictionaryEntry entry in layouts)
    {
      if (!string.Equals(entry.Key, layoutName, StringComparison.OrdinalIgnoreCase)) continue;
      var layout = transaction.GetObject(entry.Value, OpenMode.ForRead) as Layout;
      if (layout == null) continue;
      label = layout.LayoutName;
      return layout.BlockTableRecordId;
    }
    var names = new List<string>();
    foreach (DBDictionaryEntry entry in layouts) names.Add(entry.Key);
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Layout '{layoutName}' not found. Layouts: {string.Join(", ", names)}");
  }

  private static (double x1, double y1, double x2, double y2)? ReadWindow(JsonObject? node)
  {
    if (node == null) return null;
    double G(string k) => node[k]?.GetValue<double>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"window.{k} is required.");
    var x1 = G("x1"); var y1 = G("y1"); var x2 = G("x2"); var y2 = G("y2");
    return (Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
  }

  private static bool Intersects(Extents3d e, (double x1, double y1, double x2, double y2) w)
    => e.MaxPoint.X >= w.x1 && e.MinPoint.X <= w.x2 && e.MaxPoint.Y >= w.y1 && e.MinPoint.Y <= w.y2;

  private static List<Regex>? BuildLayerRegexes(string? pattern)
  {
    if (string.IsNullOrWhiteSpace(pattern)) return null;
    var list = new List<Regex>();
    foreach (var raw in pattern.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
      var rx = "^" + Regex.Escape(raw).Replace("\\*", ".*").Replace("\\?", ".") + "$";
      list.Add(new Regex(rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }
    return list.Count == 0 ? null : list;
  }

  private static HashSet<string>? BuildTypeSet(JsonArray? node)
  {
    if (node == null || node.Count == 0) return null;
    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var n in node)
    {
      var s = (n?.ToString() ?? "").Trim();
      if (s.Length == 0) continue;
      switch (s.ToLowerInvariant())
      {
        case "text": set.Add("text"); break;
        case "curve": case "curves": set.Add("curve"); break;
        case "block": case "blockreference": case "insert": set.Add("BlockReference"); break;
        case "dbtext": case "singletext": set.Add("DBText"); break;
        case "mtext": set.Add("MText"); break;
        case "polyline": case "lwpolyline": set.Add("Polyline"); break;
        case "dimension": case "dimensions": set.Add("RotatedDimension"); set.Add("AlignedDimension"); set.Add("RadialDimension"); set.Add("DiametricDimension"); set.Add("OrdinateDimension"); set.Add("LineAngularDimension2"); set.Add("Point3AngularDimension"); set.Add("ArcDimension"); set.Add("RadialDimensionLarge"); break;
        default: set.Add(s); break;
      }
    }
    return set;
  }

  private static bool IsTextLike(Entity e) => e is DBText || e is MText || e is MLeader || e is Dimension || e is AttributeReference;

  private static string? ExtractText(Entity entity, Transaction transaction)
  {
    try
    {
      switch (entity)
      {
        case AttributeReference ar: return $"{ar.Tag}={ar.TextString}";
        case DBText t: return t.TextString;
        case MText m: return m.Text;
        case MLeader ml:
          if (ml.ContentType == ContentType.MTextContent) { using var mt = ml.MText; return mt?.Text; }
          if (ml.ContentType == ContentType.BlockContent) return BlockNameOf(ml.BlockContentId, transaction);
          return null;
        case Dimension d: return string.IsNullOrEmpty(d.DimensionText) || d.DimensionText == "<>" ? d.Measurement.ToString("0.###") : d.DimensionText;
        case BlockReference br:
        {
          var parts = new List<string> { BlockNameOf(br.BlockTableRecord, transaction) ?? "" };
          foreach (ObjectId aid in br.AttributeCollection)
          {
            if (transaction.GetObject(aid, OpenMode.ForRead) is AttributeReference ar) parts.Add($"{ar.Tag}={ar.TextString}");
          }
          return string.Join(" | ", parts);
        }
        default: return null;
      }
    }
    catch { return null; }
  }

  private static string? BlockNameOf(ObjectId btrId, Transaction transaction)
  {
    try { return (transaction.GetObject(btrId, OpenMode.ForRead) as BlockTableRecord)?.Name; } catch { return null; }
  }

  private static Dictionary<string, object?> Describe(Entity entity, Matrix3d transform, int depth, Extents3d? ext, string? text, bool brief, Transaction transaction)
  {
    var row = new Dictionary<string, object?>
    {
      ["handle"] = entity.Handle.ToString(),
      ["type"] = entity.GetType().Name,
      ["layer"] = entity.Layer,
    };
    if (ext.HasValue)
    {
      row["bounds"] = new Dictionary<string, object?> { ["minX"] = R(ext.Value.MinPoint.X), ["minY"] = R(ext.Value.MinPoint.Y), ["maxX"] = R(ext.Value.MaxPoint.X), ["maxY"] = R(ext.Value.MaxPoint.Y) };
      row["center"] = Pt(new Point3d((ext.Value.MinPoint.X + ext.Value.MaxPoint.X) / 2, (ext.Value.MinPoint.Y + ext.Value.MaxPoint.Y) / 2, 0));
    }
    if (text != null) row["text"] = text;
    if (brief) return row;

    Point3d T(Point3d p) => depth > 0 ? p.TransformBy(transform) : p;
    double S(double v) => depth > 0 ? v * transform.GetScale() : v;

    try
    {
      switch (entity)
      {
        case AttributeReference ar: // derives from DBText, so it must come first
          row["tag"] = ar.Tag; row["position"] = Pt(T(ar.Position)); row["height"] = R(S(ar.Height));
          break;
        case DBText t:
          row["position"] = Pt(T(t.Position)); row["height"] = R(S(t.Height)); row["rotationDeg"] = R(t.Rotation * 180 / Math.PI); row["style"] = t.TextStyleName;
          break;
        case MText m:
          row["position"] = Pt(T(m.Location)); row["height"] = R(S(m.TextHeight)); row["width"] = R(S(m.Width)); row["rotationDeg"] = R(m.Rotation * 180 / Math.PI); row["contentsRaw"] = m.Contents;
          break;
        case Dimension d:
          row["measurement"] = R(d.Measurement); row["dimensionText"] = d.DimensionText; row["textPosition"] = Pt(T(d.TextPosition)); row["dimensionStyle"] = d.DimensionStyleName;
          if (d is RotatedDimension rd) { row["xLine1"] = Pt(T(rd.XLine1Point)); row["xLine2"] = Pt(T(rd.XLine2Point)); row["rotationDeg"] = R(rd.Rotation * 180 / Math.PI); }
          else if (d is AlignedDimension ad) { row["xLine1"] = Pt(T(ad.XLine1Point)); row["xLine2"] = Pt(T(ad.XLine2Point)); }
          break;
        case MLeader ml:
          try { row["firstVertex"] = Pt(T(ml.GetFirstVertex(0))); row["lastVertex"] = Pt(T(ml.GetLastVertex(0))); } catch { }
          break;
        case Line l:
          row["start"] = Pt(T(l.StartPoint)); row["end"] = Pt(T(l.EndPoint)); row["length"] = R(S(l.Length));
          break;
        case Polyline pl:
        {
          var verts = new List<object?>();
          for (var i = 0; i < pl.NumberOfVertices; i++)
          {
            var p = T(pl.GetPoint3dAt(i));
            verts.Add(new Dictionary<string, object?> { ["x"] = R(p.X), ["y"] = R(p.Y), ["z"] = R(p.Z), ["bulge"] = R(pl.GetBulgeAt(i)) });
          }
          row["vertices"] = verts; row["closed"] = pl.Closed; row["length"] = R(S(pl.Length)); row["elevation"] = R(pl.Elevation);
          break;
        }
        case Polyline2d p2:
        {
          var verts = new List<object?>();
          foreach (ObjectId vid in p2) { if (transaction.GetObject(vid, OpenMode.ForRead) is Vertex2d v) verts.Add(Pt(T(v.Position))); }
          row["vertices"] = verts; row["closed"] = p2.Closed; row["length"] = R(S(p2.Length));
          break;
        }
        case Polyline3d p3:
        {
          var verts = new List<object?>();
          foreach (ObjectId vid in p3) { if (transaction.GetObject(vid, OpenMode.ForRead) is PolylineVertex3d v) verts.Add(Pt(T(v.Position))); }
          row["vertices"] = verts; row["closed"] = p3.Closed; row["length"] = R(S(p3.Length));
          break;
        }
        case Arc a:
          row["center"] = Pt(T(a.Center)); row["radius"] = R(S(a.Radius)); row["startAngleDeg"] = R(a.StartAngle * 180 / Math.PI); row["endAngleDeg"] = R(a.EndAngle * 180 / Math.PI); row["start"] = Pt(T(a.StartPoint)); row["end"] = Pt(T(a.EndPoint));
          break;
        case Circle c:
          row["center"] = Pt(T(c.Center)); row["radius"] = R(S(c.Radius));
          break;
        case Ellipse el:
          row["center"] = Pt(T(el.Center)); row["majorRadius"] = R(S(el.MajorRadius)); row["minorRadius"] = R(S(el.MinorRadius));
          break;
        case Spline sp:
          try { row["start"] = Pt(T(sp.StartPoint)); row["end"] = Pt(T(sp.EndPoint)); row["controlPoints"] = sp.NumControlPoints; } catch { }
          break;
        case Hatch h:
          row["pattern"] = h.PatternName; try { row["area"] = R(h.Area); } catch { }
          break;
        case BlockReference br:
        {
          row["blockName"] = BlockNameOf(br.BlockTableRecord, transaction);
          row["position"] = Pt(T(br.Position)); row["rotationDeg"] = R(br.Rotation * 180 / Math.PI);
          row["scale"] = new Dictionary<string, object?> { ["x"] = R(br.ScaleFactors.X), ["y"] = R(br.ScaleFactors.Y), ["z"] = R(br.ScaleFactors.Z) };
          var attrs = new Dictionary<string, object?>();
          foreach (ObjectId aid in br.AttributeCollection)
          {
            if (transaction.GetObject(aid, OpenMode.ForRead) is AttributeReference ar) attrs[ar.Tag] = ar.TextString;
          }
          if (attrs.Count > 0) row["attributes"] = attrs;
          try { row["isDynamic"] = br.IsDynamicBlock; } catch { }
          break;
        }
        case Autodesk.AutoCAD.DatabaseServices.DBPoint dp:
          row["position"] = Pt(T(dp.Position));
          break;
        default:
          break;
      }
    }
    catch (Exception ex)
    {
      row["describeError"] = ex.Message;
    }
    return row;
  }

  private static double R(double v) => Math.Round(v, 4);
  private static Dictionary<string, object?> Pt(Point3d p) => new() { ["x"] = R(p.X), ["y"] = R(p.Y), ["z"] = R(p.Z) };
}
