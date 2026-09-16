using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

/// <summary>
/// civil3d_style: list / get / create / copy / edit / delete / apply / library_apply / export for
/// Civil 3D object styles (phase 1: surface, alignment, profile, corridor, feature_line, marker,
/// code_set, link, shape, point, assembly, sample_line, section, profile_view, section_view ...).
///
/// The style spec is a JSON document that <c>get</c> emits and <c>create</c>/<c>edit</c> accept:
///
///   { "family": "surface", "name": "...", "description": "...",
///     "display": { "plan": { "MajorContour": { "visible": true, "color": 1, "layer": "C-TOPO-MAJR",
///                                              "linetype": "Continuous", "linetypeScale": 1, "lineweight": 0.35, "plotStyle": "ByLayer" } },
///                  "model": { ... }, "section": { ... }, "profile": { ... } },
///     "settings":   { "contourStyle": { "majorContourInterval": 2.5, "minorContourInterval": 0.5, "smoothContours": true, ... },
///                     "boundaryStyle": { ... } },          // sub-style objects, API property names in camelCase
///     "properties": { "arrowHeadOption": "...", "radiusSnapValue": 0 },   // scalar/enum properties on the style itself
///     "references": { "beginPointMarkerStyle": "Marker name", ... } }     // ObjectId properties resolved by style name
///
/// Everything is discovered by reflection on the managed API (AeccDbMgd):
///   * display views  = methods  Get[X]DisplayStyle{Plan|Model|Section|Profile}(enum)  and properties typed DisplayStyle
///   * settings       = properties whose type lives in Autodesk.Civil.DatabaseServices.Styles (SurfaceContourStyle, GridStyle, AxisStyle ...)
///   * properties     = bool / int / double / string / enum properties
///   * references     = ObjectId properties (marker styles, legend table styles ...)
/// so new families and new API properties work without code changes. Friendly aliases exist for the
/// common surface contour settings (see <see cref="Aliases"/>).
/// </summary>
public static class StyleCommands
{
  // ---------------------------------------------------------------------------------------------
  // Families
  // ---------------------------------------------------------------------------------------------

  /// <summary>family key → StylesRoot property name.</summary>
  private static readonly Dictionary<string, string> Families = new(StringComparer.OrdinalIgnoreCase)
  {
    ["surface"] = "SurfaceStyles",
    ["alignment"] = "AlignmentStyles",
    ["profile"] = "ProfileStyles",
    ["corridor"] = "CorridorStyles",
    ["feature_line"] = "FeatureLineStyles",
    ["marker"] = "MarkerStyles",
    ["code_set"] = "CodeSetStyles",
    ["link"] = "LinkStyles",
    ["shape"] = "ShapeStyles",
    ["point"] = "PointStyles",
    ["assembly"] = "AssemblyStyles",
    ["sample_line"] = "SampleLineStyles",
    ["section"] = "SectionStyles",
    ["profile_view"] = "ProfileViewStyles",
    ["section_view"] = "SectionViewStyles",
    ["profile_view_band_set"] = "ProfileViewBandSetStyles",
    ["section_view_band_set"] = "SectionViewBandSetStyles",
    ["pipe"] = "PipeStyles",
    ["structure"] = "StructureStyles",
    ["parcel"] = "ParcelStyles",
    ["grading"] = "GradingStyles",
    ["catchment"] = "CatchmentStyles",
    ["intersection"] = "IntersectionStyles",
    ["group_plot"] = "GroupPlotStyles",
    ["view_frame"] = "ViewFrameStyles",
    ["match_line"] = "MatchLineStyles",
    ["mass_haul_line"] = "MassHaulLineStyles",
    ["mass_haul_view"] = "MassHaulViewStyles",
    ["superelevation_view"] = "SuperelevationViewStyles",
    ["cant_view"] = "CantViewStyles",
    ["projection"] = "ProjectionStyles",
    ["slope_pattern"] = "SlopePatternStyles",
    ["survey_figure"] = "SurveyFigureStyles",
    ["survey_network"] = "SurveyNetworkStyles",
    ["sheet"] = "SheetStyles",
    ["building_site"] = "BuildingSiteStyles",
    ["interference"] = "InterferenceStyles",
    ["point_cloud"] = "PointCloudStyles",
    ["table"] = "TableStyles",
    // phase 3: band styles live under StylesRoot.BandStyles.* (dotted path, see TryGetCollection)
    ["band:profile_data"] = "BandStyles.ProfileViewProfileDataBandStyles",
    ["band:horizontal_geometry"] = "BandStyles.ProfileViewHorizontalGeometryBandStyles",
    ["band:vertical_geometry"] = "BandStyles.ProfileViewVerticalGeometryBandStyles",
    ["band:superelevation"] = "BandStyles.ProfileViewSuperElevationBandStyles",
    ["band:sectional_data"] = "BandStyles.ProfileViewSectionalDataBandStyles",
    ["band:pipe_network"] = "BandStyles.ProfileViewPipeNetworkBandStyles",
    ["band:section_data"] = "BandStyles.SectionViewSectionDataBandStyles",
    ["band:section_segments"] = "BandStyles.SectionViewSegmentsBandStyles",
  };

  /// <summary>Friendly spec keys → API property names, per settings object (camelCase keys).</summary>
  private static readonly Dictionary<string, Dictionary<string, string>> Aliases = new(StringComparer.OrdinalIgnoreCase)
  {
    ["contourStyle"] = new(StringComparer.OrdinalIgnoreCase)
    {
      ["majorInterval"] = "MajorContourInterval",
      ["minorInterval"] = "MinorContourInterval",
      ["baseElevation"] = "BaseElevationInterval",
      ["smooth"] = "SmoothContours",
      ["depressions"] = "DisplayDepressions",
    },
    ["boundaryStyle"] = new(StringComparer.OrdinalIgnoreCase)
    {
      ["exterior"] = "DisplayExteriorBoundaries",
      ["interior"] = "DisplayInteriorBoundaries",
    },
  };

  /// <summary>Spec keys for settings that map to API property names (the design doc's `surface.contours` shape).</summary>
  private static readonly Dictionary<string, string> SettingsAliases = new(StringComparer.OrdinalIgnoreCase)
  {
    ["contours"] = "contourStyle",
    ["boundary"] = "boundaryStyle",
    ["points"] = "pointStyle",
    ["triangles"] = "triangleStyle",
    ["elevations"] = "elevationStyle",
    ["slopes"] = "slopeStyle",
    ["slopeArrows"] = "slopeArrowStyle",
    ["grid"] = "gridStyle",
    ["directions"] = "directionStyle",
    ["watersheds"] = "watershedStyle",
  };

  private const string StylesNamespace = "Autodesk.Civil.DatabaseServices.Styles";
  private static readonly Regex DisplayMethodPattern = new("^Get(?<prefix>\\w*?)DisplayStyle(?<view>Plan|Model|Section|Profile)$", RegexOptions.Compiled);
  private static readonly Regex DisplayPropertyPattern = new("^(?<prefix>\\w*?)DisplayStyle(?<view>Plan|Model|Section|Profile)$", RegexOptions.Compiled);

  // ---------------------------------------------------------------------------------------------
  // list
  // ---------------------------------------------------------------------------------------------

  public static Task<object?> ListStylesAsync(JsonObject? parameters)
  {
    var family = PluginRuntime.GetOptionalString(parameters, "family")
      ?? PluginRuntime.GetOptionalString(parameters, "objectType");
    var includeUsage = PluginRuntime.GetOptionalBool(parameters, "includeUsage") ?? false;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      if (string.IsNullOrWhiteSpace(family))
      {
        // Overview: every family with its style count.
        var families = new List<Dictionary<string, object?>>();
        foreach (var pair in Families)
        {
          var collection = TryGetCollection(civilDoc, pair.Key);
          if (collection == null) continue;
          families.Add(new Dictionary<string, object?>
          {
            ["family"] = pair.Key,
            ["collection"] = pair.Value,
            ["count"] = CivilObjectUtils.ToObjectIds(collection).Count(),
          });
        }
        List<Dictionary<string, object?>> labelFamilies;
        try { labelFamilies = LabelStyleCommands.ListFamilies(civilDoc); }
        catch (Exception ex) { labelFamilies = new() { new() { ["error"] = Civil3DCompatibility.DescribeException(ex) } }; }
        return new Dictionary<string, object?> { ["families"] = families, ["labelFamilies"] = labelFamilies };
      }

      var styles = GetCollection(civilDoc, family);
      Dictionary<ObjectId, List<string>>? usage = includeUsage ? ScanStyleUsage(civilDoc, transaction) : null;
      var items = new List<Dictionary<string, object?>>();
      foreach (var id in CivilObjectUtils.ToObjectIds(styles))
      {
        if (transaction.GetObject(id, OpenMode.ForRead) is not AcDbObject obj) continue;
        var item = new Dictionary<string, object?>
        {
          ["name"] = CivilObjectUtils.GetName(obj),
          ["handle"] = CivilObjectUtils.GetHandle(obj),
          ["type"] = obj.GetType().Name,
          ["createdBy"] = CivilObjectUtils.GetStringProperty(obj, "CreateBy"),
          ["modifiedBy"] = CivilObjectUtils.GetStringProperty(obj, "ModifiedBy"),
          ["dateModified"] = CivilObjectUtils.GetStringProperty(obj, "DateModified"),
        };
        if (usage != null) item["usedBy"] = usage.TryGetValue(id, out var users) ? users : new List<string>();
        items.Add(item);
      }

      return new Dictionary<string, object?>
      {
        ["family"] = family,
        ["objectType"] = family,
        ["count"] = items.Count,
        ["styles"] = items,
      };
    });
  }

  // ---------------------------------------------------------------------------------------------
  // get
  // ---------------------------------------------------------------------------------------------

  public static Task<object?> GetStyleAsync(JsonObject? parameters)
  {
    var family = RequireFamily(parameters);
    var name = PluginRuntime.GetOptionalString(parameters, "name")
      ?? PluginRuntime.GetRequiredString(parameters, "styleName");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var id = FindStyleId(civilDoc, transaction, family, name);
      var style = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForRead);
      var spec = ReadSpec(style, family, transaction);
      spec["usedBy"] = ScanStyleUsage(civilDoc, transaction).TryGetValue(id, out var users) ? users : new List<string>();
      return spec;
    });
  }

  // ---------------------------------------------------------------------------------------------
  // create / edit / copy
  // ---------------------------------------------------------------------------------------------

  public static Task<object?> CreateStyleAsync(JsonObject? parameters)
  {
    var family = RequireFamily(parameters);
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var spec = GetSpec(parameters);
    var ifExists = (PluginRuntime.GetOptionalString(parameters, "ifExists") ?? "error").ToLowerInvariant();

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
      CreateOrUpdate(civilDoc, database, transaction, family, name, spec, ifExists));
  }

  public static Task<object?> EditStyleAsync(JsonObject? parameters)
  {
    var family = RequireFamily(parameters);
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var spec = GetSpec(parameters);
    var newName = PluginRuntime.GetOptionalString(parameters, "newName");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var id = FindStyleId(civilDoc, transaction, family, name);
      var style = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForWrite);
      var report = new ApplyReport();
      ApplySpec(style, family, spec, civilDoc, database, transaction, report);
      if (!string.IsNullOrWhiteSpace(newName) && !string.Equals(newName, name, StringComparison.Ordinal))
      {
        Civil3DCompatibility.TrySetProperty(style, "Name", newName);
        report.Applied.Add("name");
      }
      var result = ReadSpec(style, family, transaction);
      result["updated"] = true;
      result["applied"] = report.Applied;
      result["warnings"] = report.Warnings;
      return result;
    });
  }

  public static Task<object?> CopyStyleAsync(JsonObject? parameters)
  {
    var family = RequireFamily(parameters);
    var source = PluginRuntime.GetRequiredString(parameters, "source");
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var spec = GetSpec(parameters, required: false);
    var ifExists = (PluginRuntime.GetOptionalString(parameters, "ifExists") ?? "error").ToLowerInvariant();

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var existing = TryFindStyleId(civilDoc, transaction, family, name);
      if (!existing.IsNull)
      {
        if (ifExists == "skip")
        {
          var r = ReadSpec(CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, existing, OpenMode.ForRead), family, transaction);
          r["skipped"] = true;
          return r;
        }
        if (ifExists != "update")
          throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"{family} style '{name}' already exists. Pass ifExists: 'update' or 'skip'.");
        return CreateOrUpdate(civilDoc, database, transaction, family, name, spec ?? new JsonObject(), "update");
      }

      var sourceId = FindStyleId(civilDoc, transaction, family, source);
      var sourceStyle = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, sourceId, OpenMode.ForRead);
      var copyId = CopyAsSibling(sourceStyle, name);
      var copy = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, copyId, OpenMode.ForWrite);
      if (!string.Equals(CivilObjectUtils.GetName(copy), name, StringComparison.Ordinal))
        Civil3DCompatibility.TrySetProperty(copy, "Name", name);

      var report = new ApplyReport();
      if (spec != null) ApplySpec(copy, family, spec, civilDoc, database, transaction, report);
      var result = ReadSpec(copy, family, transaction);
      result["created"] = true;
      result["copiedFrom"] = source;
      result["applied"] = report.Applied;
      result["warnings"] = report.Warnings;
      return result;
    });
  }

  private static Dictionary<string, object?> CreateOrUpdate(
    CivilDocument civilDoc, Database database, Transaction transaction,
    string family, string name, JsonObject spec, string ifExists)
  {
    var existing = TryFindStyleId(civilDoc, transaction, family, name);
    var report = new ApplyReport();
    AcDbObject style;
    bool created;

    if (!existing.IsNull)
    {
      if (ifExists == "skip")
      {
        var r = ReadSpec(CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, existing, OpenMode.ForRead), family, transaction);
        r["skipped"] = true;
        r["created"] = false;
        return r;
      }
      if (ifExists != "update")
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT",
          $"{family} style '{name}' already exists. Pass ifExists: 'update' to change it in place, or 'skip'.");
      style = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, existing, OpenMode.ForWrite);
      created = false;
    }
    else
    {
      var collection = GetCollection(civilDoc, family);
      var newId = InvokeAdd(collection, name, family);
      style = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, newId, OpenMode.ForWrite);
      created = true;
      if (style is LabelStyle && spec["replaceComponents"] == null) { spec = (JsonObject)spec.DeepClone(); spec["replaceComponents"] = true; }
    }

    try { ApplySpec(style, family, spec, civilDoc, database, transaction, report); }
    catch when (created && TryDiscardNewStyle(civilDoc, family, name, style))
    {
      throw; // the half-built style is gone; rethrow the original error
    }
    var result = ReadSpec(style, family, transaction);
    result["created"] = created;
    result["updated"] = !created;
    result["applied"] = report.Applied;
    result["warnings"] = report.Warnings;
    return result;
  }

  /// <summary>Remove a style that was created in this call but failed to apply, so library_apply never leaves half-built styles behind. Always returns false so the caller rethrows.</summary>
  private static bool TryDiscardNewStyle(CivilDocument civilDoc, string family, string name, AcDbObject style)
  {
    try
    {
      var collection = GetCollection(civilDoc, family);
      Civil3DCompatibility.InvokeMatchingOverload(collection, null, "Remove", new object?[] { name }, out var error);
      if (error != null) { try { style.Erase(); } catch { } }
    }
    catch { }
    return false;
  }

  // ---------------------------------------------------------------------------------------------
  // delete
  // ---------------------------------------------------------------------------------------------

  public static Task<object?> DeleteStyleAsync(JsonObject? parameters)
  {
    var family = RequireFamily(parameters);
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var replaceWith = PluginRuntime.GetOptionalString(parameters, "replaceWith");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var id = FindStyleId(civilDoc, transaction, family, name);
      var styleObj = transaction.GetObject(id, OpenMode.ForRead);
      var isUsed = CivilObjectUtils.GetBoolProperty(styleObj, "IsUsed") ?? false;
      var usage = ScanStyleUsage(civilDoc, transaction);
      var users = usage.TryGetValue(id, out var list) ? list : new List<string>();
      var reassigned = new List<string>();

      if (isUsed && users.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT",
          $"{family} style '{name}' is in use (Civil 3D reports IsUsed) by objects this plugin cannot enumerate (labels, other styles, code sets, views ...). Reassign them in Civil 3D first; deleting now would leave dangling style references.");

      if (users.Count > 0)
      {
        if (string.IsNullOrWhiteSpace(replaceWith))
          throw new JsonRpcDispatchException("CIVIL3D.CONFLICT",
            $"{family} style '{name}' is used by {users.Count} object(s): {string.Join(", ", users.Take(10))}{(users.Count > 10 ? ", …" : "")}. Pass replaceWith: '<other style>' to reassign them first.");
        var replacementId = FindStyleId(civilDoc, transaction, family, replaceWith);
        foreach (var (objectId, label) in EnumerateStyledObjects(civilDoc, transaction))
        {
          var obj = transaction.GetObject(objectId, OpenMode.ForRead);
          if (ReadStyleId(obj) == id)
          {
            obj.UpgradeOpen();
            Civil3DCompatibility.TrySetProperty(obj, "StyleId", replacementId);
            reassigned.Add(label);
          }
        }
      }

      // IsUsed does not refresh inside the open transaction, so re-scan the drawing objects instead.
      if (reassigned.Count > 0 && ScanStyleUsage(civilDoc, transaction).TryGetValue(id, out var stillUsing) && stillUsing.Count > 0)
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT",
          $"{family} style '{name}' is still used by {string.Join(", ", stillUsing.Take(5))} after reassigning. Not deleted.");

      var collection = GetCollection(civilDoc, family);
      var removed = false;
      string? error = null;
      foreach (var arg in new object?[] { name, id })
      {
        try
        {
          Civil3DCompatibility.InvokeMatchingOverload(collection, null, "Remove", new[] { arg }, out error);
          if (error == null) { removed = true; break; }
        }
        catch (Exception ex) { error = Civil3DCompatibility.DescribeException(ex); }
      }
      if (!removed)
      {
        try
        {
          var style = CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForWrite);
          style.Erase();
          removed = true;
        }
        catch (Exception ex)
        {
          throw new JsonRpcDispatchException("CIVIL3D.API_ERROR",
            $"Civil 3D refused to delete {family} style '{name}': {error ?? Civil3DCompatibility.DescribeException(ex)}");
        }
      }

      return new Dictionary<string, object?>
      {
        ["family"] = family,
        ["name"] = name,
        ["deleted"] = removed,
        ["reassignedTo"] = replaceWith,
        ["reassigned"] = reassigned,
      };
    });
  }

  // ---------------------------------------------------------------------------------------------
  // apply (set StyleId on objects)
  // ---------------------------------------------------------------------------------------------

  public static Task<object?> ApplyStyleAsync(JsonObject? parameters)
  {
    var family = RequireFamily(parameters);
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var objects = parameters?["objects"] as JsonArray
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "apply needs objects: [{type, name} | {handle}].");
    var replaceGroups = parameters?["replaceGroups"]?.GetValue<bool>() ?? true;
    var dimensionAnchorScale = parameters?["dimensionAnchorScale"]?.GetValue<double>() ?? 1.0;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var styleId = FindStyleId(civilDoc, transaction, family, name);
      var applied = new List<Dictionary<string, object?>>();
      foreach (var node in objects)
      {
        if (node is not JsonObject spec) continue;
        var target = ResolveTarget(civilDoc, database, transaction, spec);
        if (LabelStyleCommands.IsLabelSetFamily(family))
        {
          applied.Add(LabelStyleCommands.ApplyLabelSetTo(target, styleId, database, transaction, replaceGroups, dimensionAnchorScale));
          continue;
        }
        if (LabelStyleCommands.IsLabelFamily(family))
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Label styles are applied through label sets (family label_set:*) or civil3d_style place_labels, not with apply.");
        var prev = ReadStyleId(target);
        var previous = prev.IsNull ? null : SafeName(transaction, prev);
        if (!Civil3DCompatibility.TrySetPropertyValue(target, "StyleId", styleId, out var error))
          throw new JsonRpcDispatchException("CIVIL3D.API_ERROR",
            $"Could not set style on {target.GetType().Name} {CivilObjectUtils.GetName(target)}: {error}");
        applied.Add(new Dictionary<string, object?>
        {
          ["type"] = target.GetType().Name,
          ["name"] = CivilObjectUtils.GetName(target),
          ["handle"] = CivilObjectUtils.GetHandle(target),
          ["previousStyle"] = previous,
        });
      }
      return new Dictionary<string, object?>
      {
        ["family"] = family,
        ["style"] = name,
        ["appliedCount"] = applied.Count,
        ["applied"] = applied,
      };
    });
  }

  // ---------------------------------------------------------------------------------------------
  // library_apply
  // ---------------------------------------------------------------------------------------------

  public static Task<object?> ApplyStyleLibraryAsync(JsonObject? parameters)
  {
    var path = PluginRuntime.GetOptionalString(parameters, "path");
    var items = parameters?["items"] as JsonArray;
    var defaultIfExists = (PluginRuntime.GetOptionalString(parameters, "ifExists") ?? "update").ToLowerInvariant();

    if (items == null && string.IsNullOrWhiteSpace(path))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "library_apply needs items[] (style specs) or path (a JSON file containing {\"styles\": [...]} or an array).");

    if (items == null)
    {
      var resolved = FileBoundary.ResolveImportPath(path!, ".json");
      var text = File.ReadAllText(resolved);
      var parsed = JsonNode.Parse(text);
      items = parsed as JsonArray ?? (parsed as JsonObject)?["styles"] as JsonArray
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{resolved}' must be a JSON array of style specs or an object with a 'styles' array.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var results = new List<Dictionary<string, object?>>();
      int created = 0, updated = 0, skipped = 0, failed = 0;
      foreach (var node in items)
      {
        if (node is not JsonObject spec) continue;
        var family = spec["family"]?.GetValue<string>() ?? spec["objectType"]?.GetValue<string>();
        var name = spec["name"]?.GetValue<string>();
        var entry = new Dictionary<string, object?> { ["family"] = family, ["name"] = name };
        try
        {
          if (string.IsNullOrWhiteSpace(family) || string.IsNullOrWhiteSpace(name))
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Each library item needs family and name.");
          var ifExists = (spec["ifExists"]?.GetValue<string>() ?? defaultIfExists).ToLowerInvariant();
          var r = CreateOrUpdate(civilDoc, database, transaction, family, name, spec, ifExists);
          var wasCreated = r.TryGetValue("created", out var c) && c is true;
          var wasSkipped = r.TryGetValue("skipped", out var s) && s is true;
          entry["status"] = wasSkipped ? "skipped" : wasCreated ? "created" : "updated";
          entry["warnings"] = r.TryGetValue("warnings", out var w) ? w : null;
          if (wasSkipped) skipped++; else if (wasCreated) created++; else updated++;
        }
        catch (JsonRpcDispatchException ex)
        {
          entry["status"] = "failed";
          entry["error"] = ex.Message;
          failed++;
        }
        results.Add(entry);
      }
      return new Dictionary<string, object?>
      {
        ["source"] = path,
        ["total"] = results.Count,
        ["created"] = created,
        ["updated"] = updated,
        ["skipped"] = skipped,
        ["failed"] = failed,
        ["results"] = results,
      };
    });
  }

  // ---------------------------------------------------------------------------------------------
  // export (StyleBase.ExportTo into a side database → .dwg)
  // ---------------------------------------------------------------------------------------------

  public static Task<object?> ExportStylesAsync(JsonObject? parameters)
  {
    var family = RequireFamily(parameters);
    var names = (parameters?["names"] as JsonArray)?.Select(n => n?.GetValue<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Cast<string>().ToList()
      ?? new List<string>();
    var outputPath = PluginRuntime.GetRequiredString(parameters, "outputPath");
    var overwrite = PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false;
    var resolved = FileBoundary.ResolveExportPath(outputPath, overwrite, ".dwg");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var ids = names.Count == 0
        ? CivilObjectUtils.ToObjectIds(GetCollection(civilDoc, family)).ToList()
        : names.Select(n => FindStyleId(civilDoc, transaction, family, n)).ToList();

      var exported = new List<string>();
      var errors = new List<string>();
      using var target = new Database(true, true);
      foreach (var id in ids)
      {
        var style = transaction.GetObject(id, OpenMode.ForRead);
        var styleName = CivilObjectUtils.GetName(style) ?? id.ToString();
        try
        {
          // ExportTo(Database, StyleConflictResolverType{Ignore|Rename|Override|CancelRemaining}); the enum is
          // passed by name so the overload matcher converts it (Override = replace a same-named style in the target).
          Civil3DCompatibility.InvokeMatchingOverload(style, null, "ExportTo", new object?[] { target, "Override" }, out var error);
          if (error != null) errors.Add($"{styleName}: {error}");
          else exported.Add(styleName);
        }
        catch (Exception ex) { errors.Add($"{styleName}: {Civil3DCompatibility.DescribeException(ex)}"); }
      }
      target.SaveAs(resolved, DwgVersion.Current);
      return new Dictionary<string, object?>
      {
        ["family"] = family,
        ["outputPath"] = resolved,
        ["exported"] = exported,
        ["errors"] = errors,
        ["note"] = "The DWG contains only the exported styles (plus dependants Civil 3D pulls in). Import them elsewhere with Manage → Import Styles, or paste specs with library_apply.",
      };
    });
  }

  // ---------------------------------------------------------------------------------------------
  // Spec reading
  // ---------------------------------------------------------------------------------------------

  private static Dictionary<string, object?> ReadSpec(AcDbObject style, string family, Transaction transaction)
  {
    var spec = new Dictionary<string, object?>
    {
      ["family"] = family,
      ["name"] = CivilObjectUtils.GetName(style),
      ["handle"] = CivilObjectUtils.GetHandle(style),
      ["type"] = style.GetType().Name,
      ["description"] = CivilObjectUtils.GetStringProperty(style, "Description"),
      ["createdBy"] = CivilObjectUtils.GetStringProperty(style, "CreateBy"),
      ["dateModified"] = CivilObjectUtils.GetStringProperty(style, "DateModified"),
    };

    var display = ReadDisplayViews(style);
    if (display.Count > 0) spec["display"] = display;

    var settings = new Dictionary<string, object?>();
    var properties = new Dictionary<string, object?>();
    var references = new Dictionary<string, object?>();
    ReadMembers(style, transaction, settings, properties, references, depth: 0);
    if (settings.Count > 0) spec["settings"] = settings;
    if (properties.Count > 0) spec["properties"] = properties;
    if (references.Count > 0) spec["references"] = references;

    if (style is CodeSetStyle codeSet) spec["items"] = ReadCodeSetItems(codeSet, transaction);
    if (style is LabelStyle labelStyle) LabelStyleCommands.ReadLabelStyle(labelStyle, spec, transaction);
    else if (style is BaseLabelSetStyle labelSet) LabelStyleCommands.ReadLabelSet(labelSet, spec, transaction);
    else if (style is BandStyle band) BandStyleCommands.ReadBand(band, spec, transaction);
    else if (style is BandSetStyle bandSet) BandStyleCommands.ReadBandSet(bandSet, spec, transaction);
    return spec;
  }

  private static Dictionary<string, object?> ReadDisplayViews(object style)
  {
    var views = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    var type = style.GetType();

    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
    {
      var m = DisplayMethodPattern.Match(method.Name);
      if (!m.Success) continue;
      var ps = method.GetParameters();
      if (ps.Length != 1 || !ps[0].ParameterType.IsEnum) continue;
      if (method.ReturnType != typeof(DisplayStyle)) continue;
      var viewKey = m.Groups["view"].Value.ToLowerInvariant();
      var view = GetOrAddView(views, viewKey);
      foreach (var enumValue in Enum.GetValues(ps[0].ParameterType))
      {
        try
        {
          if (method.Invoke(style, new[] { enumValue }) is DisplayStyle ds)
            view[enumValue.ToString()!] = ReadDisplayStyle(ds);
        }
        catch { /* some enum members are not valid for every style */ }
      }
    }

    foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
    {
      if (property.PropertyType != typeof(DisplayStyle) || property.GetIndexParameters().Length != 0) continue;
      var m = DisplayPropertyPattern.Match(property.Name);
      if (!m.Success) continue;
      var viewKey = m.Groups["view"].Value.ToLowerInvariant();
      var componentName = string.IsNullOrEmpty(m.Groups["prefix"].Value) ? "Default" : m.Groups["prefix"].Value;
      var view = GetOrAddView(views, viewKey);
      if (view.ContainsKey(componentName)) continue;
      try
      {
        if (property.GetValue(style) is DisplayStyle ds) view[componentName] = ReadDisplayStyle(ds);
      }
      catch { }
    }

    return views;
  }

  private static Dictionary<string, object?> GetOrAddView(Dictionary<string, object?> views, string key)
  {
    if (views.TryGetValue(key, out var existing) && existing is Dictionary<string, object?> d) return d;
    var created = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    views[key] = created;
    return created;
  }

  private static Dictionary<string, object?> ReadDisplayStyle(DisplayStyle ds)
  {
    var d = new Dictionary<string, object?>();
    try { d["visible"] = ds.Visible; } catch { }
    try { d["color"] = ColorToSpec(ds.Color); } catch { }
    try { d["layer"] = ds.Layer; } catch { }
    try { d["linetype"] = ds.Linetype; } catch { }
    try { d["linetypeScale"] = ds.LinetypeScale; } catch { }
    try { d["lineweight"] = LineweightToSpec(ds.Lineweight); } catch { }
    try { d["plotStyle"] = ds.PlotStyle; } catch { }
    return d;
  }

  private static readonly HashSet<string> SkippedProperties = new(StringComparer.Ordinal)
  {
    "Name", "Description", "CreateBy", "ModifiedBy", "DateCreated", "DateModified", "Handle", "ObjectId", "Id",
    "IsErased", "IsEraseStatusToggled", "IsModified", "IsModifiedGraphics", "IsModifiedXData", "IsNewObject",
    "IsNotifyEnabled", "IsNotifying", "IsObjectIdsInFlux", "IsPersistent", "IsReadEnabled", "IsReallyClosing",
    "IsTransactionResident", "IsUndoing", "IsWriteEnabled", "IsAProxy", "IsCancelling", "HasSaveVersionOverride",
    "OwnerId", "Database", "ClassID", "XData", "ExtensionDictionary", "Annotative", "MergeStyle", "UnmanagedObject",
    "AutoDelete", "IsDisposed", "Drawable", "ParentLabelStyleId", "ChildrenCount", "Item", "Properties",
    "IsUsed", "PaperOrientation", "HasFields", "DrawableType", "Visible",
  };

  private static void ReadMembers(object target, Transaction transaction,
    Dictionary<string, object?> settings, Dictionary<string, object?> properties, Dictionary<string, object?> references, int depth)
  {
    var type = target.GetType();
    foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
    {
      if (!property.CanRead || property.GetIndexParameters().Length != 0) continue;
      if (SkippedProperties.Contains(property.Name)) continue;
      if (DisplayPropertyPattern.IsMatch(property.Name)) continue;
      var pt = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
      var key = CamelCase(property.Name);
      object? value;
      try { value = property.GetValue(target); }
      catch { continue; }

      if (pt == typeof(DisplayStyle)) continue;
      if (value != null && LabelStyleCommands.IsPropertyWrapper(pt))
      {
        // Civil 3D Property<T> wrapper (band styles, label styles): expose its Value as a scalar or reference.
        var valueType = LabelStyleCommands.ValueProperty(pt)?.PropertyType;
        var read = LabelStyleCommands.ReadWrapper(value, property.Name, transaction, paperSizes: false, null);
        if (valueType == typeof(ObjectId)) references[key] = read; else properties[key] = read;
        continue;
      }
      if (pt == typeof(ObjectId))
      {
        var oid = (ObjectId)value!;
        references[key] = oid.IsNull ? null : SafeName(transaction, oid);
        continue;
      }
      if (pt == typeof(Color))
      {
        properties[key] = value == null ? null : ColorToSpec((Color)value);
        continue;
      }
      if (pt == typeof(LineWeight))
      {
        properties[key] = LineweightToSpec((LineWeight)value!);
        continue;
      }
      if (pt.IsEnum)
      {
        properties[key] = value?.ToString();
        continue;
      }
      if (pt == typeof(bool) || pt == typeof(int) || pt == typeof(double) || pt == typeof(string) || pt == typeof(short) || pt == typeof(long) || pt == typeof(float))
      {
        properties[key] = value;
        continue;
      }
      if (value != null && pt.IsClass && pt.Namespace == StylesNamespace && depth < 3)
      {
        var sub = new Dictionary<string, object?>();
        var subProps = new Dictionary<string, object?>();
        var subRefs = new Dictionary<string, object?>();
        var subSettings = new Dictionary<string, object?>();
        ReadMembers(value, transaction, subSettings, subProps, subRefs, depth + 1);
        foreach (var kv in subProps) sub[kv.Key] = kv.Value;
        foreach (var kv in subRefs) sub[kv.Key] = kv.Value;
        foreach (var kv in subSettings) sub[kv.Key] = kv.Value;
        var subDisplay = ReadDisplayViews(value);
        if (subDisplay.Count > 0) sub["display"] = subDisplay;
        if (sub.Count > 0) settings[key] = sub;
      }
    }
  }

  private static List<Dictionary<string, object?>> ReadCodeSetItems(CodeSetStyle codeSet, Transaction transaction)
  {
    var items = new List<Dictionary<string, object?>>();
    try
    {
      foreach (var item in codeSet)
      {
        if (item == null) continue;
        var d = new Dictionary<string, object?>
        {
          ["code"] = CivilObjectUtils.GetStringProperty(item, "Code") ?? CivilObjectUtils.GetName(item),
          ["kind"] = CivilObjectUtils.GetStringProperty(item, "CodeSetStyleItemType") ?? CivilObjectUtils.GetStringProperty(item, "ItemType"),
          ["description"] = CivilObjectUtils.GetStringProperty(item, "Description"),
        };
        if (Civil3DCompatibility.GetPropertyValue(item, "StyleId") is ObjectId sid && !sid.IsNull) d["style"] = SafeName(transaction, sid);
        if (Civil3DCompatibility.GetPropertyValue(item, "LabelStyleId") is ObjectId lid && !lid.IsNull) d["labelStyle"] = SafeName(transaction, lid);
        items.Add(d);
      }
    }
    catch { }
    return items;
  }

  // ---------------------------------------------------------------------------------------------
  // Spec application
  // ---------------------------------------------------------------------------------------------

  private sealed class ApplyReport
  {
    public List<string> Applied { get; } = new();
    public List<string> Warnings { get; } = new();
    public HashSet<string> CreatedLayers { get; } = new(StringComparer.OrdinalIgnoreCase);
  }

  private static void ApplySpec(AcDbObject style, string family, JsonObject spec,
    CivilDocument civilDoc, Database database, Transaction transaction, ApplyReport report)
  {
    if (spec["description"] is JsonValue descriptionNode)
    {
      Civil3DCompatibility.TrySetProperty(style, "Description", descriptionNode.GetValue<string?>());
      report.Applied.Add("description");
    }

    if (style is LabelStyle labelStyle)
    {
      LabelStyleCommands.ApplyLabelStyle(labelStyle, spec, civilDoc, transaction, report.Applied, report.Warnings);
      return;
    }
    if (style is BaseLabelSetStyle labelSet)
    {
      LabelStyleCommands.ApplyLabelSet(labelSet, family, spec, civilDoc, transaction, report.Applied, report.Warnings);
      return;
    }

    if (style is BandStyle) BandStyleCommands.ScaleBandSpec(spec);

    if (spec["display"] is JsonObject display)
      ApplyDisplayViews(style, display, database, transaction, report, "display");

    // settings: either "settings": {contourStyle: {...}} or the design-doc family shorthand "surface": {contours: {...}}
    var settingsNode = spec["settings"] as JsonObject;
    var shorthand = spec[family] as JsonObject ?? spec[family.Replace("_", "")] as JsonObject;
    if (shorthand != null)
    {
      settingsNode ??= new JsonObject();
      foreach (var kv in shorthand)
      {
        if (kv.Value is not JsonObject subSpec) continue;
        var apiKey = SettingsAliases.TryGetValue(kv.Key, out var mapped) ? mapped : kv.Key;
        settingsNode[apiKey] = subSpec.DeepClone();
      }
    }
    if (settingsNode != null)
      ApplySettings(style, settingsNode, civilDoc, database, transaction, report, "settings", depth: 0);

    if (spec["properties"] is JsonObject props)
      foreach (var kv in props)
        ApplyScalar(style, kv.Key, kv.Value, civilDoc, transaction, report, $"properties.{kv.Key}");

    var refs = spec["references"] as JsonObject ?? spec["markers"] as JsonObject;
    if (refs != null)
      foreach (var kv in refs)
        ApplyScalar(style, kv.Key, kv.Value, civilDoc, transaction, report, $"references.{kv.Key}");

    if (style is BandStyle bandStyle && spec["labels"] is JsonObject bandLabels)
      BandStyleCommands.ApplyBandLabels(bandStyle, bandLabels, civilDoc, transaction, report.Applied, report.Warnings);
    if (style is BandSetStyle bandSetStyle)
      BandStyleCommands.ApplyBandSet(bandSetStyle, spec, civilDoc, transaction, report.Applied, report.Warnings);
  }

  private static void ApplyDisplayViews(object style, JsonObject display, Database database, Transaction transaction, ApplyReport report, string path)
  {
    var type = style.GetType();
    var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
      .Select(m => (method: m, match: DisplayMethodPattern.Match(m.Name)))
      .Where(t => t.match.Success && t.method.GetParameters().Length == 1 && t.method.GetParameters()[0].ParameterType.IsEnum && t.method.ReturnType == typeof(DisplayStyle))
      .ToList();
    var singles = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
      .Select(p => (property: p, match: DisplayPropertyPattern.Match(p.Name)))
      .Where(t => t.match.Success && t.property.PropertyType == typeof(DisplayStyle))
      .ToList();

    foreach (var viewPair in display)
    {
      if (viewPair.Value is not JsonObject components) continue;
      var viewKey = viewPair.Key;
      foreach (var componentPair in components)
      {
        if (componentPair.Value is not JsonObject componentSpec) continue;
        var componentName = componentPair.Key;
        DisplayStyle? ds = null;

        foreach (var (method, match) in methods)
        {
          if (!string.Equals(match.Groups["view"].Value, viewKey, StringComparison.OrdinalIgnoreCase)) continue;
          var enumType = method.GetParameters()[0].ParameterType;
          object enumValue;
          try { enumValue = Enum.Parse(enumType, componentName, ignoreCase: true); }
          catch { continue; }
          try { ds = method.Invoke(style, new[] { enumValue }) as DisplayStyle; } catch { ds = null; }
          if (ds != null) break;
        }
        if (ds == null)
        {
          foreach (var (property, match) in singles)
          {
            if (!string.Equals(match.Groups["view"].Value, viewKey, StringComparison.OrdinalIgnoreCase)) continue;
            var prefix = string.IsNullOrEmpty(match.Groups["prefix"].Value) ? "Default" : match.Groups["prefix"].Value;
            if (!string.Equals(prefix, componentName, StringComparison.OrdinalIgnoreCase)) continue;
            try { ds = property.GetValue(style) as DisplayStyle; } catch { ds = null; }
            if (ds != null) break;
          }
        }
        if (ds == null)
        {
          var available = methods.Where(t => string.Equals(t.match.Groups["view"].Value, viewKey, StringComparison.OrdinalIgnoreCase))
            .SelectMany(t => Enum.GetNames(t.method.GetParameters()[0].ParameterType))
            .Concat(singles.Where(t => string.Equals(t.match.Groups["view"].Value, viewKey, StringComparison.OrdinalIgnoreCase)).Select(t => string.IsNullOrEmpty(t.match.Groups["prefix"].Value) ? "Default" : t.match.Groups["prefix"].Value))
            .Distinct().ToList();
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"{type.Name} has no '{componentName}' component in the {viewKey} view. Available: {(available.Count > 0 ? string.Join(", ", available) : "(this style has no " + viewKey + " view)")}.");
        }

        ApplyDisplayStyle(ds, componentSpec, database, transaction, report, $"{path}.{viewKey}.{componentName}");
      }
    }
  }

  private static void ApplyDisplayStyle(DisplayStyle ds, JsonObject spec, Database database, Transaction transaction, ApplyReport report, string path)
  {
    foreach (var kv in spec)
    {
      var key = kv.Key.ToLowerInvariant();
      try
      {
        switch (key)
        {
          case "visible": ds.Visible = kv.Value!.GetValue<bool>(); break;
          case "color": ds.Color = ParseColor(kv.Value); break;
          case "layer":
          {
            var layer = kv.Value!.GetValue<string>();
            EnsureLayer(database, transaction, layer, spec["layerColor"], report);
            ds.Layer = layer;
            break;
          }
          case "layercolor": break; // consumed by layer
          case "linetype":
          {
            var lt = kv.Value!.GetValue<string>();
            EnsureLinetype(database, transaction, lt, report);
            ds.Linetype = lt;
            break;
          }
          case "linetypescale": ds.LinetypeScale = kv.Value!.GetValue<double>(); break;
          case "lineweight": ds.Lineweight = ParseLineweight(kv.Value); break;
          case "plotstyle": ds.PlotStyle = kv.Value!.GetValue<string>(); break;
          default:
            report.Warnings.Add($"{path}: unknown display property '{kv.Key}' (visible, color, layer, layerColor, linetype, linetypeScale, lineweight, plotStyle).");
            continue;
        }
        report.Applied.Add($"{path}.{kv.Key}");
      }
      catch (JsonRpcDispatchException) { throw; }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{path}.{kv.Key}: {Civil3DCompatibility.DescribeException(ex)}");
      }
    }
  }

  private static void ApplySettings(object target, JsonObject settings, CivilDocument civilDoc, Database database, Transaction transaction, ApplyReport report, string path, int depth)
  {
    var type = target.GetType();
    foreach (var kv in settings)
    {
      var property = FindProperty(type, kv.Key);
      if (property == null)
      {
        var available = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
          .Where(p => p.PropertyType.IsClass && p.PropertyType.Namespace == StylesNamespace && p.PropertyType != typeof(DisplayStyle))
          .Select(p => CamelCase(p.Name)).ToList();
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"{path}: {type.Name} has no settings object '{kv.Key}'. Available: {string.Join(", ", available)}.");
      }
      object? sub;
      try { sub = property.GetValue(target); }
      catch (Exception ex) { throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{path}.{kv.Key}: {Civil3DCompatibility.DescribeException(ex)}"); }
      if (sub == null || kv.Value is not JsonObject subSpec)
      {
        report.Warnings.Add($"{path}.{kv.Key}: not an object, skipped.");
        continue;
      }

      var aliasKey = CamelCase(property.Name);
      Aliases.TryGetValue(aliasKey, out var aliasMap);
      foreach (var inner in subSpec)
      {
        var innerName = aliasMap != null && aliasMap.TryGetValue(inner.Key, out var mapped) ? mapped : inner.Key;
        if (string.Equals(innerName, "display", StringComparison.OrdinalIgnoreCase) && inner.Value is JsonObject innerDisplay)
        {
          ApplyDisplayViews(sub, innerDisplay, database, transaction, report, $"{path}.{kv.Key}.display");
          continue;
        }
        var innerProperty = FindProperty(sub.GetType(), innerName);
        if (innerProperty != null && inner.Value is JsonObject nested && innerProperty.PropertyType.IsClass && innerProperty.PropertyType.Namespace == StylesNamespace && innerProperty.PropertyType != typeof(DisplayStyle) && depth < 3)
        {
          ApplySettings(sub, new JsonObject { [innerProperty.Name] = nested.DeepClone() }, civilDoc, database, transaction, report, $"{path}.{kv.Key}", depth + 1);
          continue;
        }
        ApplyScalar(sub, innerName, inner.Value, civilDoc, transaction, report, $"{path}.{kv.Key}.{inner.Key}");
      }
    }
  }

  private static void ApplyScalar(object target, string propertyName, JsonNode? valueNode, CivilDocument civilDoc, Transaction transaction, ApplyReport report, string path)
  {
    var type = target.GetType();
    var property = FindProperty(type, propertyName);
    if (property == null)
    {
      var available = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => (p.CanWrite || LabelStyleCommands.IsPropertyWrapper(p.PropertyType)) && !SkippedProperties.Contains(p.Name))
        .Select(p => CamelCase(p.Name)).OrderBy(n => n).ToList();
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"{path}: {type.Name} has no property '{propertyName}'. Writable: {string.Join(", ", available)}.");
    }
    if (LabelStyleCommands.IsPropertyWrapper(property.PropertyType))
    {
      object? wrapper;
      try { wrapper = property.GetValue(target); }
      catch (Exception ex) { throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{path}: {Civil3DCompatibility.DescribeException(ex)}"); }
      if (wrapper == null) throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{path}: property is not available.");
      LabelStyleCommands.WriteWrapper(wrapper, property.Name, valueNode, name => ResolveReference(civilDoc, transaction, property.Name, name), paperSizes: false, path);
      report.Applied.Add(path);
      return;
    }

    if (!property.CanWrite)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{path}: {type.Name}.{property.Name} is read-only.");

    var pt = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
    object? converted;
    try
    {
      if (pt == typeof(ObjectId))
      {
        var refName = valueNode?.GetValue<string?>();
        converted = string.IsNullOrWhiteSpace(refName) ? ObjectId.Null : ResolveReference(civilDoc, transaction, property.Name, refName);
      }
      else if (pt == typeof(Color)) converted = ParseColor(valueNode);
      else if (pt == typeof(LineWeight)) converted = ParseLineweight(valueNode);
      else if (pt.IsEnum)
      {
        var text = valueNode?.GetValue<string>() ?? "";
        try { converted = Enum.Parse(pt, text, ignoreCase: true); }
        catch
        {
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"{path}: '{text}' is not a {pt.Name}. Values: {string.Join(", ", Enum.GetNames(pt))}.");
        }
      }
      else if (pt == typeof(bool)) converted = valueNode!.GetValue<bool>();
      else if (pt == typeof(int)) converted = valueNode!.GetValue<int>();
      else if (pt == typeof(short)) converted = (short)valueNode!.GetValue<int>();
      else if (pt == typeof(long)) converted = valueNode!.GetValue<long>();
      else if (pt == typeof(double)) converted = valueNode!.GetValue<double>();
      else if (pt == typeof(float)) converted = (float)valueNode!.GetValue<double>();
      else if (pt == typeof(string)) converted = valueNode?.GetValue<string?>();
      else
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{path}: {type.Name}.{property.Name} is a {pt.Name}; give it as a settings object, not a scalar.");
    }
    catch (JsonRpcDispatchException) { throw; }
    catch (Exception ex)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{path}: cannot convert '{valueNode?.ToJsonString()}' to {pt.Name}: {ex.Message}");
    }

    if (!Civil3DCompatibility.TrySetPropertyValue(target, property.Name, converted, out var error))
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{path}: Civil 3D rejected {type.Name}.{property.Name} = {valueNode?.ToJsonString()}: {error}");
    report.Applied.Add(path);
  }

  // ---------------------------------------------------------------------------------------------
  // Lookups
  // ---------------------------------------------------------------------------------------------

  private static string RequireFamily(JsonObject? parameters)
  {
    var family = PluginRuntime.GetOptionalString(parameters, "family")
      ?? PluginRuntime.GetOptionalString(parameters, "objectType")
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Missing 'family'. One of: {string.Join(", ", Families.Keys)}, label:<object>/<type>, label_set:<object>.");
    if (LabelStyleCommands.IsAnyLabelFamily(family)) return family.ToLowerInvariant();
    if (!Families.ContainsKey(family))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unknown style family '{family}'. One of: {string.Join(", ", Families.Keys)}, label:<object>/<type> (e.g. label:alignment/major_station), label_set:<alignment|profile|section>.");
    return family.ToLowerInvariant();
  }

  /// <summary>Resolve a style name for a reference property from any collection (object styles, and label styles for *LabelStyle* properties).</summary>
  internal static ObjectId ResolveAnyStyle(CivilDocument civilDoc, Transaction transaction, string propertyName, string name)
    => ResolveReference(civilDoc, transaction, propertyName, name);

  internal static Color ParseColorPublic(JsonNode? node) => ParseColor(node);
  internal static LineWeight ParseLineweightPublic(JsonNode? node) => ParseLineweight(node);
  internal static object LineweightToSpecPublic(LineWeight lw) => LineweightToSpec(lw);

  /// <summary>The style spec: either `spec: {...}` or its keys given directly on the call.</summary>
  private static JsonObject GetSpec(JsonObject? parameters) => GetSpec(parameters, required: true)!;

  private static JsonObject? GetSpec(JsonObject? parameters, bool required)
  {
    if (parameters?["spec"] is JsonObject spec) return spec;
    var inline = new JsonObject();
    foreach (var key in new[] { "description", "display", "settings", "properties", "references", "markers", "surface", "alignment", "profile", "corridor", "feature_line", "marker", "components", "removeComponents", "replaceComponents", "items", "replaceItems", "labels" })
      if (parameters?[key] != null) inline[key] = parameters[key]!.DeepClone();
    if (inline.Count > 0) return inline;
    return required ? new JsonObject() : null;
  }

  private static object? TryGetCollection(CivilDocument civilDoc, string family)
  {
    if (LabelStyleCommands.IsAnyLabelFamily(family)) return LabelStyleCommands.ResolveCollection(civilDoc, family);
    if (!Families.TryGetValue(family, out var propertyName)) return null;
    if (propertyName.Contains('.')) return BandStyleCommands.ResolvePath(civilDoc.Styles, propertyName);
    try { return Civil3DCompatibility.GetPropertyValue(civilDoc.Styles, propertyName); }
    catch { return null; }
  }

  private static object GetCollection(CivilDocument civilDoc, string family)
  {
    return TryGetCollection(civilDoc, family)
      ?? throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Style collection for family '{family}' is not available in this Civil 3D version.");
  }

  private static ObjectId TryFindStyleId(CivilDocument civilDoc, Transaction transaction, string family, string name)
  {
    foreach (var id in CivilObjectUtils.ToObjectIds(GetCollection(civilDoc, family)))
    {
      if (id.IsNull) continue;
      var obj = transaction.GetObject(id, OpenMode.ForRead);
      if (string.Equals(CivilObjectUtils.GetName(obj), name, StringComparison.OrdinalIgnoreCase)) return id;
    }
    return ObjectId.Null;
  }

  private static ObjectId FindStyleId(CivilDocument civilDoc, Transaction transaction, string family, string name)
  {
    var id = TryFindStyleId(civilDoc, transaction, family, name);
    if (!id.IsNull) return id;
    var names = CivilObjectUtils.ToObjectIds(GetCollection(civilDoc, family))
      .Select(i => CivilObjectUtils.GetName(transaction.GetObject(i, OpenMode.ForRead))).Where(n => n != null).Take(40).ToList();
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
      $"{family} style '{name}' was not found. Available: {string.Join(", ", names)}.");
  }

  private static ObjectId InvokeAdd(object collection, string name, string family)
  {
    var result = Civil3DCompatibility.InvokeMatchingOverload(collection, null, "Add", new object?[] { name }, out var error);
    if (error != null)
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Civil 3D could not create {family} style '{name}': {error}");
    if (result is ObjectId id && !id.IsNull) return id;
    throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Civil 3D created {family} style '{name}' but returned no ObjectId ({result?.GetType().Name ?? "null"}).");
  }

  private static ObjectId CopyAsSibling(AcDbObject source, string newName)
  {
    var result = Civil3DCompatibility.InvokeMatchingOverload(source, null, "CopyAsSibling", new object?[] { newName }, out var error);
    if (error != null)
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"CopyAsSibling failed for '{CivilObjectUtils.GetName(source)}': {error}");
    if (result is ObjectId id && !id.IsNull) return id;
    throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", "CopyAsSibling returned no ObjectId.");
  }

  private static ObjectId ResolveReference(CivilDocument civilDoc, Transaction transaction, string propertyName, string name)
  {
    // Which collections can this reference point into? Marker properties → MarkerStyles; legends → TableStyles;
    // otherwise try every object-style collection until the name matches.
    var order = new List<string>();
    if (propertyName.Contains("LabelStyle", StringComparison.OrdinalIgnoreCase) || propertyName.Contains("LabelSet", StringComparison.OrdinalIgnoreCase))
    {
      try { return LabelStyleCommands.FindLabelStyle(civilDoc, transaction, name, null, null); }
      catch (JsonRpcDispatchException) { /* fall through to the object-style collections */ }
    }
    if (propertyName.Contains("Marker", StringComparison.OrdinalIgnoreCase)) order.Add("marker");
    if (propertyName.Contains("Legend", StringComparison.OrdinalIgnoreCase) || propertyName.Contains("Table", StringComparison.OrdinalIgnoreCase)) order.Add("table");
    if (propertyName.Contains("Point", StringComparison.OrdinalIgnoreCase)) order.Add("point");
    order.AddRange(Families.Keys.Where(k => !order.Contains(k)));

    foreach (var family in order)
    {
      var collection = TryGetCollection(civilDoc, family);
      if (collection == null) continue;
      foreach (var id in CivilObjectUtils.ToObjectIds(collection))
      {
        if (id.IsNull) continue;
        var obj = transaction.GetObject(id, OpenMode.ForRead);
        if (string.Equals(CivilObjectUtils.GetName(obj), name, StringComparison.OrdinalIgnoreCase)) return id;
      }
    }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
      $"No style named '{name}' found for reference '{CamelCase(propertyName)}' (searched {string.Join(", ", order.Take(3))} first). Create the marker/table style first.");
  }

  private static AcDbObject ResolveTarget(CivilDocument civilDoc, Database database, Transaction transaction, JsonObject spec)
  {
    var handle = spec["handle"]?.GetValue<string>();
    if (!string.IsNullOrWhiteSpace(handle))
    {
      try
      {
        var h = new Handle(Convert.ToInt64(handle, 16));
        if (database.TryGetObjectId(h, out var id) && !id.IsNull)
          return CivilObjectUtils.GetRequiredObject<AcDbObject>(transaction, id, OpenMode.ForWrite);
      }
      catch (JsonRpcDispatchException) { throw; }
      catch { }
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No object with handle {handle}.");
    }

    var type = (spec["type"]?.GetValue<string>() ?? "").ToLowerInvariant();
    var name = spec["name"]?.GetValue<string>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "apply objects need {handle} or {type, name}.");
    switch (type)
    {
      case "surface": return CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, name, OpenMode.ForWrite);
      case "alignment":
      {
        var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, name);
        alignment.UpgradeOpen();
        return alignment;
      }
      case "profile":
      {
        var alignmentName = spec["alignmentName"]?.GetValue<string>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "profile targets need alignmentName.");
        var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
        return CivilObjectUtils.FindProfileByName(alignment, transaction, name, OpenMode.ForWrite);
      }
      case "corridor": return CivilObjectUtils.FindCorridorByName(civilDoc, transaction, name, OpenMode.ForWrite);
      case "feature_line":
      case "featureline": return FeatureLineCommands.Find(civilDoc, database, transaction, name, null, OpenMode.ForWrite);
      case "profile_view":
      {
        foreach (var id in EnumerateProfileViewIds(database, transaction))
        {
          var pv = transaction.GetObject(id, OpenMode.ForRead);
          if (string.Equals(CivilObjectUtils.GetName(pv), name, StringComparison.OrdinalIgnoreCase)) { pv.UpgradeOpen(); return pv; }
        }
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Profile view '{name}' was not found.");
      }
      default:
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"apply target type '{type}' is not supported; use surface, alignment, profile, corridor, feature_line, profile_view or a handle.");
    }
  }

  /// <summary>StyleId of a Civil 3D entity, read through the typed API (reflection misses it on surfaces).</summary>
  private static ObjectId ReadStyleId(AcDbObject? obj)
  {
    if (obj == null) return ObjectId.Null;
    try
    {
      if (obj is Autodesk.Civil.DatabaseServices.Entity civilEntity) return civilEntity.StyleId;
    }
    catch { }
    return Civil3DCompatibility.GetPropertyValue(obj, "StyleId") is ObjectId sid ? sid : ObjectId.Null;
  }

  /// <summary>styleId → labels of the objects using it (surfaces, alignments, profiles, corridors, feature lines, profile views).</summary>
  private static Dictionary<ObjectId, List<string>> ScanStyleUsage(CivilDocument civilDoc, Transaction transaction)
  {
    var usage = new Dictionary<ObjectId, List<string>>();
    foreach (var (objectId, label) in EnumerateStyledObjects(civilDoc, transaction))
    {
      AcDbObject? obj;
      try { obj = transaction.GetObject(objectId, OpenMode.ForRead); } catch { continue; }
      var styleId = ReadStyleId(obj);
      if (styleId.IsNull) continue;
      if (!usage.TryGetValue(styleId, out var list)) usage[styleId] = list = new List<string>();
      list.Add(label);
    }
    return usage;
  }

  private static IEnumerable<(ObjectId id, string label)> EnumerateStyledObjects(CivilDocument civilDoc, Transaction transaction)
  {
    var results = new List<(ObjectId, string)>();
    void Add(ObjectId id, string kind)
    {
      try { results.Add((id, $"{kind} '{CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead))}'")); }
      catch { }
    }
    try { foreach (ObjectId id in civilDoc.GetSurfaceIds()) Add(id, "surface"); } catch { }
    try
    {
      foreach (ObjectId id in civilDoc.GetAlignmentIds())
      {
        Add(id, "alignment");
        try
        {
          var alignment = transaction.GetObject(id, OpenMode.ForRead) as Alignment;
          if (alignment != null) foreach (ObjectId pid in alignment.GetProfileIds()) Add(pid, "profile");
        }
        catch { }
      }
    }
    catch { }
    try { foreach (ObjectId id in civilDoc.CorridorCollection) Add(id, "corridor"); } catch { }
    try { foreach (ObjectId id in civilDoc.GetSitelessFeatureLineIds()) Add(id, "feature line"); } catch { }
    try
    {
      foreach (ObjectId siteId in civilDoc.GetSiteIds())
      {
        var site = transaction.GetObject(siteId, OpenMode.ForRead) as Site;
        if (site == null) continue;
        try { foreach (ObjectId id in site.GetFeatureLineIds()) Add(id, "feature line"); } catch { }
      }
    }
    catch { }
    try { foreach (var id in EnumerateProfileViewIds(CivilObjectUtils.GetDatabase(civilDoc), transaction)) Add(id, "profile view"); } catch { }
    return results;
  }

  /// <summary>Profile views live in model space (CivilDocument has no id accessor for them).</summary>
  private static IEnumerable<ObjectId> EnumerateProfileViewIds(Database database, Transaction transaction)
  {
    var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
    var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
    foreach (ObjectId objectId in modelSpace)
    {
      if (objectId.ObjectClass?.IsDerivedFrom(Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(ProfileView))) == true)
        yield return objectId;
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Value helpers
  // ---------------------------------------------------------------------------------------------

  private static PropertyInfo? FindProperty(Type type, string name)
  {
    return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
      .FirstOrDefault(p => p.GetIndexParameters().Length == 0
        && (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(CamelCase(p.Name), name, StringComparison.OrdinalIgnoreCase)));
  }

  private static string CamelCase(string name)
  {
    if (string.IsNullOrEmpty(name)) return name;
    if (name.Length > 1 && char.IsUpper(name[0]) && char.IsUpper(name[1]))
    {
      // Acronym prefix (HGPLabelStyleId → hgpLabelStyleId)
      var i = 0;
      while (i < name.Length - 1 && char.IsUpper(name[i + 1])) i++;
      return name[..i].ToLowerInvariant() + name[i..];
    }
    return char.ToLowerInvariant(name[0]) + name[1..];
  }

  private static string? SafeName(Transaction transaction, ObjectId id)
  {
    try { return CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead)); }
    catch { return id.ToString(); }
  }

  private static object ColorToSpec(Color color)
  {
    if (color.IsByLayer) return "ByLayer";
    if (color.IsByBlock) return "ByBlock";
    if (color.ColorMethod == ColorMethod.ByAci) return (int)color.ColorIndex;
    try { return $"{color.Red},{color.Green},{color.Blue}"; } catch { return color.ToString(); }
  }

  private static Color ParseColor(JsonNode? node)
  {
    if (node == null) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "color: give an ACI index 1-255, 'ByLayer', 'ByBlock', 'r,g,b' or '#rrggbb'.");
    if (node is JsonValue v && (v.TryGetValue<int>(out var aci) || (v.TryGetValue<double>(out var d) && (aci = (int)Math.Round(d)) == aci)))
      return aci switch
      {
        0 => Color.FromColorIndex(ColorMethod.ByBlock, 0),
        256 => Color.FromColorIndex(ColorMethod.ByLayer, 256),
        >= 1 and <= 255 => Color.FromColorIndex(ColorMethod.ByAci, (short)aci),
        _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"color {aci}: ACI must be 1-255 (0 = ByBlock, 256 = ByLayer)."),
      };
    var text = node.GetValue<string>().Trim();
    if (text.Equals("ByLayer", StringComparison.OrdinalIgnoreCase)) return Color.FromColorIndex(ColorMethod.ByLayer, 256);
    if (text.Equals("ByBlock", StringComparison.OrdinalIgnoreCase)) return Color.FromColorIndex(ColorMethod.ByBlock, 0);
    if (text.StartsWith('#') && text.Length == 7)
      return Color.FromRgb(Convert.ToByte(text[1..3], 16), Convert.ToByte(text[3..5], 16), Convert.ToByte(text[5..7], 16));
    var parts = text.Split(',');
    if (parts.Length == 3 && parts.All(p => byte.TryParse(p.Trim(), out _)))
      return Color.FromRgb(byte.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]));
    if (int.TryParse(text, out var n)) return ParseColor(JsonValue.Create(n));
    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"color '{text}': give an ACI index 1-255, 'ByLayer', 'ByBlock', 'r,g,b' or '#rrggbb'.");
  }

  private static object LineweightToSpec(LineWeight lw)
  {
    return lw switch
    {
      LineWeight.ByLayer => (object)"ByLayer",
      LineWeight.ByBlock => (object)"ByBlock",
      LineWeight.ByLineWeightDefault => (object)"Default",
      _ => (object)((int)lw / 100.0),
    };
  }

  private static LineWeight ParseLineweight(JsonNode? node)
  {
    if (node is JsonValue v && (v.TryGetValue<double>(out var mm) || (v.TryGetValue<string>(out var s) && double.TryParse(s, out mm))))
    {
      var hundredths = (int)Math.Round(mm * 100);
      var valid = Enum.GetValues<LineWeight>().Select(x => (int)x).Where(x => x >= 0).ToList();
      var nearest = valid.OrderBy(x => Math.Abs(x - hundredths)).First();
      return (LineWeight)nearest;
    }
    var text = node?.GetValue<string>()?.Trim() ?? "";
    if (text.Equals("ByLayer", StringComparison.OrdinalIgnoreCase)) return LineWeight.ByLayer;
    if (text.Equals("ByBlock", StringComparison.OrdinalIgnoreCase)) return LineWeight.ByBlock;
    if (text.Equals("Default", StringComparison.OrdinalIgnoreCase)) return LineWeight.ByLineWeightDefault;
    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"lineweight '{text}': give millimetres (0.13, 0.25, 0.35, 0.5 …), 'ByLayer', 'ByBlock' or 'Default'.");
  }

  private static void EnsureLayer(Database database, Transaction transaction, string layer, JsonNode? layerColor, ApplyReport report)
  {
    if (string.IsNullOrWhiteSpace(layer) || layer == "0") return;
    var layerTable = CivilObjectUtils.GetRequiredObject<LayerTable>(transaction, database.LayerTableId, OpenMode.ForRead);
    if (layerTable.Has(layer))
    {
      // A layer created earlier in this same apply (by another component) still takes the colour.
      if (layerColor != null && report.CreatedLayers.Contains(layer))
      {
        try
        {
          var existing = CivilObjectUtils.GetRequiredObject<LayerTableRecord>(transaction, layerTable[layer], OpenMode.ForWrite);
          existing.Color = ParseColor(layerColor);
          report.Applied.Add($"layer '{layer}' colour set");
        }
        catch { }
      }
      return;
    }
    layerTable.UpgradeOpen();
    var record = new LayerTableRecord { Name = layer };
    if (layerColor != null)
    {
      try { record.Color = ParseColor(layerColor); } catch { }
    }
    layerTable.Add(record);
    transaction.AddNewlyCreatedDBObject(record, true);
    report.CreatedLayers.Add(layer);
    report.Applied.Add($"layer '{layer}' created");
  }

  private static void EnsureLinetype(Database database, Transaction transaction, string linetype, ApplyReport report)
  {
    if (string.IsNullOrWhiteSpace(linetype)) return;
    if (linetype.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) || linetype.Equals("ByBlock", StringComparison.OrdinalIgnoreCase) || linetype.Equals("Continuous", StringComparison.OrdinalIgnoreCase)) return;
    var table = CivilObjectUtils.GetRequiredObject<LinetypeTable>(transaction, database.LinetypeTableId, OpenMode.ForRead);
    if (table.Has(linetype)) return;
    foreach (var file in new[] { "acadiso.lin", "acad.lin" })
    {
      try
      {
        database.LoadLineTypeFile(linetype, file);
        if (table.Has(linetype)) { report.Applied.Add($"linetype '{linetype}' loaded from {file}"); return; }
      }
      catch { }
    }
    report.Warnings.Add($"linetype '{linetype}' is not in the drawing and could not be loaded from acadiso.lin/acad.lin; Civil 3D may reject or fall back to Continuous.");
  }
}
