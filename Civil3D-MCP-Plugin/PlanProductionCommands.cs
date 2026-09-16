using System.Collections;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Plan production with the documented Civil 3D 2026 / AutoCAD 2026 APIs — no reflection guesses.
///
/// What the managed Civil 3D API exposes for plan production is READ-ONLY:
///   CivilDocument.GetViewFrameGroupIds() -> ViewFrameGroup (GetViewFrameIds / GetMatchLineIds, label defaults)
///   ViewFrame: AlignmentId, StartStation, EndStation, Sheet, SheetSet (strings), label style/anchor
///   MatchLine: AlignmentId, Station, Number, label styles/anchors
/// View frame groups, match lines and plan/profile/section sheets can only be CREATED by the
/// Civil 3D wizards (CreateViewFrames / CreateSheets / CreateSectionSheets). Those requests are
/// refused here with a clear message instead of being simulated.
///
/// What IS writable, and implemented with the AutoCAD API:
///   layouts (list, create blank, import a layout from a .dwt/.dwg template) and paper-space
///   viewports (create aimed at a model-space centre and scale, set scale).
/// Together with production-placed section view groups (civil3d_section view_group_create with a
/// template) this is enough to build cross-section sheets without the wizard.
/// </summary>
public static class PlanProductionCommands
{
  // -------------------------------------------------------------------------
  // View frame groups (read-only)
  // -------------------------------------------------------------------------

  public static Task<object?> ListSheetSetsAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var groups = new List<Dictionary<string, object?>>();
      foreach (ObjectId groupId in civilDoc.GetViewFrameGroupIds())
      {
        var group = CivilObjectUtils.GetRequiredObject<ViewFrameGroup>(transaction, groupId, OpenMode.ForRead);
        groups.Add(ToViewFrameGroupSummary(group, transaction));
      }

      return new Dictionary<string, object?>
      {
        ["viewFrameGroups"] = groups,
        // kept for callers that still read the old key
        ["sheetSets"] = groups,
        ["note"] = "Civil 3D plan production objects (view frame groups, view frames, match lines, sheets) are read-only in the managed API; they are created with the CreateViewFrames / CreateSheets wizards.",
      };
    });
  }

  public static Task<object?> GetSheetSetInfoAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var group = FindViewFrameGroupByName(civilDoc, transaction, name);
      var summary = ToViewFrameGroupSummary(group, transaction);
      summary["viewFrames"] = ReadViewFrames(group, transaction);
      summary["matchLines"] = ReadMatchLines(group, transaction);
      return summary;
    });
  }

  public static Task<object?> CreateSheetSetAsync(JsonObject? parameters)
  {
    _ = PluginRuntime.GetRequiredString(parameters, "name");
    throw NotCreatable("a sheet set / view frame group", "CreateViewFrames, then CreateSheets");
  }

  public static Task<object?> AddSheetAsync(JsonObject? parameters)
  {
    _ = PluginRuntime.GetRequiredString(parameters, "sheetSetName");
    _ = PluginRuntime.GetRequiredString(parameters, "sheetName");
    throw NotCreatable("a plan production sheet", "CreateSheets (plan/profile) or CreateSectionSheets");
  }

  public static Task<object?> GetSheetPropertiesAsync(JsonObject? parameters)
  {
    var groupName = PluginRuntime.GetRequiredString(parameters, "sheetSetName");
    var sheetName = PluginRuntime.GetRequiredString(parameters, "sheetName");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var group = FindViewFrameGroupByName(civilDoc, transaction, groupName);
      foreach (var frame in ReadViewFrames(group, transaction))
      {
        if (string.Equals(frame["sheet"]?.ToString(), sheetName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(frame["name"]?.ToString(), sheetName, StringComparison.OrdinalIgnoreCase))
        {
          frame["layout"] = FindLayoutSummary(database, transaction, frame["sheet"]?.ToString());
          return frame;
        }
      }
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
        $"No view frame or sheet named '{sheetName}' in view frame group '{group.Name}'.");
    });
  }

  public static Task<object?> SetSheetTitleBlockAsync(JsonObject? parameters)
  {
    _ = PluginRuntime.GetRequiredString(parameters, "sheetSetName");
    _ = PluginRuntime.GetRequiredString(parameters, "sheetName");
    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      "Sheet title blocks are defined by the sheet template layout, not by a writable property in the Civil 3D API. " +
      "Import the layout with the title block (layout_import_template) or set it in the template. No update was made.");
  }

  public static Task<object?> CreatePlanProfileSheetAsync(JsonObject? parameters)
  {
    _ = PluginRuntime.GetRequiredString(parameters, "sheetSetName");
    _ = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    throw NotCreatable("a plan/profile sheet", "CreateViewFrames, then CreateSheets");
  }

  public static Task<object?> UpdatePlanProfileSheetAlignmentAsync(JsonObject? parameters)
  {
    _ = PluginRuntime.GetRequiredString(parameters, "sheetSetName");
    _ = PluginRuntime.GetRequiredString(parameters, "sheetName");
    _ = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      "A view frame's alignment is fixed at creation (ViewFrame.AlignmentId is read-only in the Civil 3D API). Re-run CreateViewFrames on the new alignment. No update was made.");
  }

  // -------------------------------------------------------------------------
  // Layouts (AutoCAD API, writable)
  // -------------------------------------------------------------------------

  public static Task<object?> ListLayoutsAsync(JsonObject? parameters)
  {
    var includeViewports = PluginRuntime.GetOptionalBool(parameters, "includeViewports") ?? true;
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var layouts = new List<Dictionary<string, object?>>();
      var layoutDict = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
      foreach (DBDictionaryEntry entry in layoutDict)
      {
        var layout = (Layout)transaction.GetObject(entry.Value, OpenMode.ForRead);
        var row = ToLayoutSummary(layout, transaction, includeViewports);
        layouts.Add(row);
      }
      return new Dictionary<string, object?>
      {
        ["layouts"] = layouts.OrderBy(l => Convert.ToInt32(l["tabOrder"])).ToList(),
        ["current"] = LayoutManager.Current.CurrentLayout,
      };
    });
  }

  public static Task<object?> CreateLayoutAsync(JsonObject? parameters)
  {
    var layoutName = PluginRuntime.GetRequiredString(parameters, "layoutName");
    var templatePath = PluginRuntime.GetOptionalString(parameters, "templatePath");
    var templateLayoutName = PluginRuntime.GetOptionalString(parameters, "templateLayoutName");
    var makeCurrent = PluginRuntime.GetOptionalBool(parameters, "makeCurrent") ?? false;

    if (!string.IsNullOrWhiteSpace(templatePath) && !File.Exists(templatePath))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Template '{templatePath}' was not found. No layout was created.");
    if (!string.IsNullOrWhiteSpace(templatePath) && string.IsNullOrWhiteSpace(templateLayoutName))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "templateLayoutName is required with templatePath. No layout was created.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var layoutDict = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
      if (layoutDict.Contains(layoutName))
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Layout '{layoutName}' already exists. No layout was created.");
      if (!string.IsNullOrWhiteSpace(templatePath) &&
          !string.Equals(templateLayoutName, layoutName, StringComparison.OrdinalIgnoreCase) &&
          layoutDict.Contains(templateLayoutName!))
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT",
          $"This drawing already has a layout named '{templateLayoutName}', so the template layout cannot be cloned under that name first. " +
          "Rename or remove the existing layout, or import with layoutName equal to a free name and a template layout that does not clash. No layout was created.");

      ObjectId layoutId;
      string source;
      if (string.IsNullOrWhiteSpace(templatePath))
      {
        layoutId = LayoutManager.Current.CreateLayout(layoutName);
        source = "blank";
      }
      else
      {
        layoutId = ImportLayoutFromTemplate(database, transaction, templatePath!, templateLayoutName!, layoutName);
        source = $"{templatePath} / {templateLayoutName}";
      }

      var layout = (Layout)transaction.GetObject(layoutId, OpenMode.ForRead);
      if (makeCurrent)
      {
        try { LayoutManager.Current.CurrentLayout = layout.LayoutName; } catch (Exception ex) { PluginLog.Warn("PlanProduction", "could not switch layout", ex); }
      }

      var summary = ToLayoutSummary(layout, transaction, includeViewports: true);
      summary["source"] = source;
      summary["created"] = true;
      return summary;
    });
  }

  // -------------------------------------------------------------------------
  // Viewports (AutoCAD API, writable)
  // -------------------------------------------------------------------------

  public static Task<object?> CreateSheetViewAsync(JsonObject? parameters)
  {
    var layoutName = PluginRuntime.GetRequiredString(parameters, "layoutName");
    var viewName = PluginRuntime.GetOptionalString(parameters, "viewName");
    var centerX = PluginRuntime.GetOptionalDouble(parameters, "centerX") ?? 0.0;
    var centerY = PluginRuntime.GetOptionalDouble(parameters, "centerY") ?? 0.0;
    var width = PluginRuntime.GetOptionalDouble(parameters, "width") ?? 8.0;
    var height = PluginRuntime.GetOptionalDouble(parameters, "height") ?? 6.0;
    var scale = PluginRuntime.GetOptionalDouble(parameters, "scale");
    var viewCenterX = PluginRuntime.GetOptionalDouble(parameters, "viewCenterX");
    var viewCenterY = PluginRuntime.GetOptionalDouble(parameters, "viewCenterY");
    var twistDegrees = PluginRuntime.GetOptionalDouble(parameters, "twistDegrees");
    var locked = PluginRuntime.GetOptionalBool(parameters, "locked") ?? true;

    if (width <= 0 || height <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "width and height must be positive paper-space sizes.");
    if (scale is <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "scale must be the plot scale denominator (e.g. 100 for 1:100) and greater than zero.");
    if (viewCenterX.HasValue != viewCenterY.HasValue)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "viewCenterX and viewCenterY must be supplied together.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var layout = FindLayoutByName(database, transaction, layoutName);
      if (layout.ModelType)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Viewports can only be added to paper-space layouts, not Model.");

      // Viewport.On / ViewCenter / CustomScale throw eNotInPaperspace unless the layout is the current paper space.
      MakeLayoutCurrent(layout);

      var layoutBlock = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);
      var viewport = new Viewport
      {
        CenterPoint = new Point3d(centerX, centerY, 0),
        Width = width,
        Height = height,
      };
      layoutBlock.AppendEntity(viewport);
      transaction.AddNewlyCreatedDBObject(viewport, true);
      viewport.On = true;

      if (!string.IsNullOrWhiteSpace(viewName))
        ApplyNamedViewToViewport(database, transaction, viewport, viewName!);

      if (viewCenterX.HasValue)
        viewport.ViewCenter = new Point2d(viewCenterX.Value, viewCenterY!.Value);
      if (twistDegrees.HasValue)
        viewport.TwistAngle = twistDegrees.Value * Math.PI / 180.0;
      if (scale.HasValue)
      {
        // CustomScale is paper units per model unit; ViewHeight follows from the paper height.
        viewport.CustomScale = 1.0 / scale.Value;
        viewport.ViewHeight = height * scale.Value;
      }
      viewport.Locked = locked;

      return new Dictionary<string, object?>
      {
        ["handle"] = viewport.Handle.ToString(),
        ["layoutName"] = layout.LayoutName,
        ["paperCenter"] = new Dictionary<string, object?> { ["x"] = centerX, ["y"] = centerY },
        ["paperSize"] = new Dictionary<string, object?> { ["width"] = width, ["height"] = height },
        ["viewCenter"] = new Dictionary<string, object?> { ["x"] = viewport.ViewCenter.X, ["y"] = viewport.ViewCenter.Y },
        ["scale"] = scale,
        ["customScale"] = viewport.CustomScale,
        ["locked"] = viewport.Locked,
        ["created"] = true,
      };
    });
  }

  public static Task<object?> SetSheetViewScaleAsync(JsonObject? parameters)
  {
    var layoutName = PluginRuntime.GetRequiredString(parameters, "layoutName");
    var viewportHandle = PluginRuntime.GetOptionalString(parameters, "viewportHandle");
    var scale = PluginRuntime.GetRequiredDouble(parameters, "scale");
    var viewCenterX = PluginRuntime.GetOptionalDouble(parameters, "viewCenterX");
    var viewCenterY = PluginRuntime.GetOptionalDouble(parameters, "viewCenterY");
    if (scale <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "scale must be greater than zero (plot scale denominator).");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var layout = FindLayoutByName(database, transaction, layoutName);
      MakeLayoutCurrent(layout);
      var viewport = FindViewport(transaction, layout, viewportHandle);
      viewport.UpgradeOpen();
      var wasLocked = viewport.Locked;
      viewport.Locked = false;
      viewport.CustomScale = 1.0 / scale;
      viewport.ViewHeight = viewport.Height * scale;
      if (viewCenterX.HasValue && viewCenterY.HasValue)
        viewport.ViewCenter = new Point2d(viewCenterX.Value, viewCenterY.Value);
      viewport.Locked = wasLocked;

      return new Dictionary<string, object?>
      {
        ["handle"] = viewport.Handle.ToString(),
        ["layoutName"] = layout.LayoutName,
        ["scale"] = scale,
        ["customScale"] = viewport.CustomScale,
        ["viewCenter"] = new Dictionary<string, object?> { ["x"] = viewport.ViewCenter.X, ["y"] = viewport.ViewCenter.Y },
        ["updated"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // Publishing (not implemented — kept honest)
  // -------------------------------------------------------------------------

  public static Task<object?> PublishSheetPdfAsync(JsonObject? parameters)
  {
    var layoutNamesNode = parameters?["layoutNames"] as JsonArray;
    if (layoutNamesNode == null || layoutNamesNode.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Parameter 'layoutNames' must be a non-empty array.");

    _ = FileBoundary.ResolveExportPath(
      PluginRuntime.GetRequiredString(parameters, "outputPath"),
      PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false,
      ".pdf");
    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      "PDF publishing requires a complete AutoCAD PlotEngine transaction and completion verification, which this plugin does not implement yet. No publish was started.");
  }

  public static Task<object?> ExportSheetSetAsync(JsonObject? parameters)
  {
    _ = PluginRuntime.GetRequiredString(parameters, "sheetSetName");
    _ = FileBoundary.ResolveExportPath(
      PluginRuntime.GetRequiredString(parameters, "outputPath"),
      PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false,
      ".pdf", ".dwf", ".dwfx", ".dst");
    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      "Sheet-set export is not implemented (no PlotEngine workflow yet). No export was started.");
  }

  // =========================================================================
  // Helpers
  // =========================================================================

  private static void MakeLayoutCurrent(Layout layout)
  {
    try
    {
      if (!string.Equals(LayoutManager.Current.CurrentLayout, layout.LayoutName, StringComparison.OrdinalIgnoreCase))
        LayoutManager.Current.CurrentLayout = layout.LayoutName;
    }
    catch (Exception ex)
    {
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Could not switch to layout '{layout.LayoutName}' (viewports need their layout current): {ex.Message}");
    }
  }

  private static JsonRpcDispatchException NotCreatable(string what, string wizard)
    => new("CIVIL3D.API_ERROR",
      $"Civil 3D's managed API cannot create {what}; use the {wizard} wizard in Civil 3D. " +
      "Nothing was created. For cross-section sheets the MCP can instead place section views on a sheet template " +
      "(civil3d_section view_group_create with templatePath) and build layouts and viewports directly (layout_create, sheet_view_create).");

  private static ViewFrameGroup FindViewFrameGroupByName(CivilDocument civilDoc, Transaction transaction, string name)
  {
    var names = new List<string>();
    foreach (ObjectId groupId in civilDoc.GetViewFrameGroupIds())
    {
      var group = CivilObjectUtils.GetRequiredObject<ViewFrameGroup>(transaction, groupId, OpenMode.ForRead);
      if (string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase)) return group;
      names.Add(group.Name);
    }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
      $"View frame group '{name}' was not found. Available: {(names.Count == 0 ? "(none)" : string.Join(", ", names))}.");
  }

  private static Dictionary<string, object?> ToViewFrameGroupSummary(ViewFrameGroup group, Transaction transaction)
  {
    var frameIds = group.GetViewFrameIds();
    var matchLineIds = group.GetMatchLineIds();
    string? alignmentName = null;
    var sheetSets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (ObjectId frameId in frameIds)
    {
      var frame = CivilObjectUtils.GetRequiredObject<ViewFrame>(transaction, frameId, OpenMode.ForRead);
      alignmentName ??= NameOf(transaction, frame.AlignmentId);
      if (!string.IsNullOrWhiteSpace(frame.SheetSet)) sheetSets.Add(frame.SheetSet);
    }

    return new Dictionary<string, object?>
    {
      ["name"] = group.Name,
      ["handle"] = CivilObjectUtils.GetHandle(group),
      ["alignmentName"] = alignmentName,
      ["viewFrameCount"] = frameIds.Count,
      ["matchLineCount"] = matchLineIds.Count,
      ["sheetSets"] = sheetSets.ToList(),
      ["sheetCount"] = frameIds.Count,
    };
  }

  private static List<Dictionary<string, object?>> ReadViewFrames(ViewFrameGroup group, Transaction transaction)
  {
    var rows = new List<Dictionary<string, object?>>();
    foreach (ObjectId frameId in group.GetViewFrameIds())
    {
      var frame = CivilObjectUtils.GetRequiredObject<ViewFrame>(transaction, frameId, OpenMode.ForRead);
      rows.Add(new Dictionary<string, object?>
      {
        ["name"] = frame.Name,
        ["handle"] = CivilObjectUtils.GetHandle(frame),
        ["alignmentName"] = NameOf(transaction, frame.AlignmentId),
        ["startStation"] = frame.StartStation,
        ["endStation"] = frame.EndStation,
        ["sheet"] = frame.Sheet,
        ["sheetSet"] = frame.SheetSet,
        ["labelVisible"] = frame.IsLabelVisible,
      });
    }
    return rows.OrderBy(r => (double)r["startStation"]!).ToList();
  }

  private static List<Dictionary<string, object?>> ReadMatchLines(ViewFrameGroup group, Transaction transaction)
  {
    var rows = new List<Dictionary<string, object?>>();
    foreach (ObjectId id in group.GetMatchLineIds())
    {
      var matchLine = CivilObjectUtils.GetRequiredObject<MatchLine>(transaction, id, OpenMode.ForRead);
      rows.Add(new Dictionary<string, object?>
      {
        ["name"] = matchLine.Name,
        ["handle"] = CivilObjectUtils.GetHandle(matchLine),
        ["number"] = matchLine.Number,
        ["station"] = matchLine.Station,
      });
    }
    return rows.OrderBy(r => (double)r["station"]!).ToList();
  }

  private static string? NameOf(Transaction transaction, ObjectId id)
  {
    if (id.IsNull) return null;
    try { return CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead)); }
    catch { return null; }
  }

  private static Dictionary<string, object?>? FindLayoutSummary(Database database, Transaction transaction, string? layoutName)
  {
    if (string.IsNullOrWhiteSpace(layoutName)) return null;
    var layoutDict = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
    if (!layoutDict.Contains(layoutName)) return null;
    var layout = (Layout)transaction.GetObject(layoutDict.GetAt(layoutName), OpenMode.ForRead);
    return ToLayoutSummary(layout, transaction, includeViewports: true);
  }

  private static Dictionary<string, object?> ToLayoutSummary(Layout layout, Transaction transaction, bool includeViewports)
  {
    var row = new Dictionary<string, object?>
    {
      ["name"] = layout.LayoutName,
      ["handle"] = layout.Handle.ToString(),
      ["tabOrder"] = layout.TabOrder,
      ["isModel"] = layout.ModelType,
      ["paperSize"] = new Dictionary<string, object?>
      {
        ["width"] = layout.PlotPaperSize.X,
        ["height"] = layout.PlotPaperSize.Y,
        ["units"] = layout.PlotPaperUnits.ToString(),
      },
      ["plotDevice"] = layout.PlotConfigurationName,
      ["styleSheet"] = layout.CurrentStyleSheet,
    };

    if (includeViewports && !layout.ModelType)
    {
      var viewports = new List<Dictionary<string, object?>>();
      var block = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
      foreach (ObjectId id in block)
      {
        if (transaction.GetObject(id, OpenMode.ForRead) is not Viewport vp) continue;
        if (vp.Number == 1) continue; // the paper-space "viewport" itself
        viewports.Add(new Dictionary<string, object?>
        {
          ["handle"] = vp.Handle.ToString(),
          ["number"] = vp.Number,
          ["paperCenter"] = new Dictionary<string, object?> { ["x"] = vp.CenterPoint.X, ["y"] = vp.CenterPoint.Y },
          ["paperSize"] = new Dictionary<string, object?> { ["width"] = vp.Width, ["height"] = vp.Height },
          ["viewCenter"] = new Dictionary<string, object?> { ["x"] = vp.ViewCenter.X, ["y"] = vp.ViewCenter.Y },
          ["customScale"] = vp.CustomScale,
          ["scaleDenominator"] = vp.CustomScale > 0 ? 1.0 / vp.CustomScale : null,
          ["twistDegrees"] = vp.TwistAngle * 180.0 / Math.PI,
          ["locked"] = vp.Locked,
          ["on"] = vp.On,
        });
      }
      row["viewports"] = viewports;
    }
    return row;
  }

  private static Layout FindLayoutByName(Database database, Transaction transaction, string layoutName)
  {
    var layoutDict = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
    foreach (DBDictionaryEntry entry in layoutDict)
    {
      if (string.Equals(entry.Key, layoutName, StringComparison.OrdinalIgnoreCase))
        return (Layout)transaction.GetObject(entry.Value, OpenMode.ForRead);
    }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Layout '{layoutName}' was not found.");
  }

  private static Viewport FindViewport(Transaction transaction, Layout layout, string? viewportHandle)
  {
    var layoutBlock = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
    Viewport? first = null;
    foreach (ObjectId id in layoutBlock)
    {
      if (transaction.GetObject(id, OpenMode.ForRead) is not Viewport vp) continue;
      if (vp.Number == 1) continue;
      if (!string.IsNullOrWhiteSpace(viewportHandle) &&
          string.Equals(vp.Handle.ToString(), viewportHandle, StringComparison.OrdinalIgnoreCase))
        return vp;
      first ??= vp;
    }
    if (!string.IsNullOrWhiteSpace(viewportHandle))
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No viewport with handle '{viewportHandle}' in layout '{layout.LayoutName}'.");
    if (first != null) return first;
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No viewport found in layout '{layout.LayoutName}'.");
  }

  private static void ApplyNamedViewToViewport(Database database, Transaction transaction, Viewport viewport, string viewName)
  {
    var viewTable = (ViewTable)transaction.GetObject(database.ViewTableId, OpenMode.ForRead);
    if (!viewTable.Has(viewName))
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Named view '{viewName}' does not exist.");
    var viewRecord = (ViewTableRecord)transaction.GetObject(viewTable[viewName], OpenMode.ForRead);
    viewport.ViewCenter = new Point2d(viewRecord.CenterPoint.X, viewRecord.CenterPoint.Y);
    viewport.ViewHeight = viewRecord.Height;
  }

  /// <summary>
  /// Imports one layout (with its paper-space block: title block, viewports, everything) from a
  /// .dwt/.dwg template into the current drawing under a new name, using WblockCloneObjects on the
  /// layout dictionary — the documented way to bring a layout across drawings.
  /// </summary>
  private static ObjectId ImportLayoutFromTemplate(Database targetDb, Transaction transaction, string templatePath, string templateLayoutName, string newLayoutName)
  {
    using var sourceDb = new Database(false, true);
    sourceDb.ReadDwgFile(templatePath, FileOpenMode.OpenForReadAndAllShare, true, null);
    sourceDb.CloseInput(true);

    ObjectId sourceLayoutId;
    using (var sourceTransaction = sourceDb.TransactionManager.StartTransaction())
    {
      var sourceLayouts = (DBDictionary)sourceTransaction.GetObject(sourceDb.LayoutDictionaryId, OpenMode.ForRead);
      if (!sourceLayouts.Contains(templateLayoutName))
      {
        var names = new List<string>();
        foreach (DBDictionaryEntry e in sourceLayouts) names.Add(e.Key);
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
          $"Layout '{templateLayoutName}' is not in '{templatePath}'. Layouts there: {string.Join(", ", names)}. No layout was created.");
      }
      sourceLayoutId = sourceLayouts.GetAt(templateLayoutName);
      sourceTransaction.Commit();
    }

    var ids = new ObjectIdCollection { sourceLayoutId };
    var mapping = new IdMapping();
    sourceDb.WblockCloneObjects(ids, targetDb.LayoutDictionaryId, mapping, DuplicateRecordCloning.Ignore, false);

    var clonedId = mapping[sourceLayoutId].Value;
    if (clonedId.IsNull)
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", "AutoCAD did not clone the template layout. No layout was created.");

    var layout = (Layout)transaction.GetObject(clonedId, OpenMode.ForWrite);
    if (!string.Equals(layout.LayoutName, newLayoutName, StringComparison.Ordinal))
      LayoutManager.Current.RenameLayout(layout.LayoutName, newLayoutName);
    return clonedId;
  }
}
