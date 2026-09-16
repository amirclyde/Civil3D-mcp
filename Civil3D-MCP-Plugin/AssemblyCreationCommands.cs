using System.Collections;
using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.Runtime;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using SaPoint = Autodesk.Civil.DatabaseServices.Point;
using SaLink = Autodesk.Civil.DatabaseServices.Link;
using SaShape = Autodesk.Civil.DatabaseServices.Shape;

namespace Civil3DMcpPlugin;

/// <summary>
/// Assembly operations implemented with the documented Civil 3D 2026 API.
///
/// Subassemblies come from two sources:
///  - stock (.NET) subassemblies via <c>SubassemblyCollection.ImportStockSubassembly</c>. The class
///    name Civil 3D expects is the fully qualified stock class, e.g. "Subassembly.LinkWidthAndSlope";
///    a bare "LinkWidthAndSlope" is resolved by trying the "Subassembly." prefix.
///  - Subassembly Composer packages via <c>SubassemblyCollection.ImportSACSubassembly</c> when a
///    .pkt path is given (parameter <c>pktFilePath</c>, or <c>subassemblyType</c> ending in .pkt).
///
/// Input parameters are written through the subassembly's ParamsDouble/ParamsLong/ParamsBool/
/// ParamsString collections, matched by key name first (the SAC parameter name / stock property
/// key) and by display name second. A parameter that cannot be matched aborts the transaction so
/// nothing half-configured is left in the drawing.
/// </summary>
public static class AssemblyCreationCommands
{
  private const string StockClassPrefix = "Subassembly.";

  public static Task<object?> ListAssembliesAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var assemblies = civilDoc.AssemblyCollection
        .Select(id => CivilObjectUtils.GetRequiredObject<Assembly>(transaction, id, OpenMode.ForRead))
        .Select(assembly =>
        {
          var summary = ToAssemblySummary(assembly);
          summary["usedByCorridors"] = new List<string>();
          return summary;
        })
        .ToList();

      return new Dictionary<string, object?> { ["assemblies"] = assemblies };
    });
  }

  public static Task<object?> GetAssemblyAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var assembly = FindAssemblyByName(civilDoc, transaction, name, OpenMode.ForRead);
      var usedByCorridors = new List<string>();

      foreach (ObjectId corridorId in civilDoc.CorridorCollection)
      {
        var corridor = CivilObjectUtils.GetRequiredObject<Corridor>(transaction, corridorId, OpenMode.ForRead);
        foreach (Baseline baseline in corridor.Baselines)
        {
          foreach (BaselineRegion region in baseline.BaselineRegions)
          {
            if (region.AssemblyId == assembly.ObjectId && !usedByCorridors.Contains(corridor.Name))
              usedByCorridors.Add(corridor.Name);
          }
        }
      }

      return new Dictionary<string, object?>
      {
        ["name"] = assembly.Name,
        ["handle"] = CivilObjectUtils.GetHandle(assembly),
        ["subassemblyCount"] = GetSubassemblyIds(assembly).Count,
        ["style"] = GetStyleName(assembly, transaction) ?? string.Empty,
        ["type"] = assembly.Type.ToString(),
        ["location"] = new Dictionary<string, object?> { ["x"] = assembly.Location.X, ["y"] = assembly.Location.Y },
        ["subassemblies"] = GetSubassemblies(assembly, transaction),
        ["usedByCorridors"] = usedByCorridors,
      };
    });
  }

  public static Task<object?> CreateAssemblyAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var insertX = PluginRuntime.GetRequiredDouble(parameters, "insertX");
    var insertY = PluginRuntime.GetRequiredDouble(parameters, "insertY");
    var description = PluginRuntime.GetOptionalString(parameters, "description") ?? string.Empty;
    var assemblyTypeText = PluginRuntime.GetRequiredString(parameters, "assemblyType");
    if (!Enum.TryParse<AssemblyType>(assemblyTypeText, ignoreCase: true, out var assemblyType))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        $"Invalid assemblyType '{assemblyTypeText}'. Use {string.Join(", ", Enum.GetNames<AssemblyType>())}.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var assemblyId = civilDoc.AssemblyCollection.Add(name, assemblyType, new Point3d(insertX, insertY, 0));
      var assembly = CivilObjectUtils.GetRequiredObject<Assembly>(transaction, assemblyId, OpenMode.ForWrite);
      assembly.Description = description;

      return new Dictionary<string, object?>
      {
        ["name"] = assembly.Name,
        ["handle"] = CivilObjectUtils.GetHandle(assembly),
        ["insertX"] = insertX,
        ["insertY"] = insertY,
        ["assemblyType"] = assembly.Type.ToString(),
        ["created"] = true,
      };
    });
  }

  public static Task<object?> CreateSubassemblyAsync(JsonObject? parameters)
  {
    var assemblyName = PluginRuntime.GetRequiredString(parameters, "assemblyName");
    var subassemblyType = PluginRuntime.GetRequiredString(parameters, "subassemblyType").Trim();
    var pktFilePath = PluginRuntime.GetOptionalString(parameters, "pktFilePath");
    var requestedName = PluginRuntime.GetOptionalString(parameters, "subassemblyName");
    var side = PluginRuntime.GetOptionalString(parameters, "side");
    if (!string.IsNullOrWhiteSpace(side) &&
        !new[] { "Left", "Right", "Both", "None" }.Contains(side, StringComparer.OrdinalIgnoreCase))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "side must be Left, Right, Both or None (omit it for a subassembly that has no side).");
    }

    var isPackage = !string.IsNullOrWhiteSpace(pktFilePath)
      || subassemblyType.EndsWith(".pkt", StringComparison.OrdinalIgnoreCase);
    var packagePath = isPackage ? (string.IsNullOrWhiteSpace(pktFilePath) ? subassemblyType : pktFilePath!.Trim()) : null;
    if (packagePath != null && !File.Exists(packagePath))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        $"Subassembly Composer package '{packagePath}' was not found. Nothing was added.");
    }

    var subParams = ReadParameters(parameters?["parameters"] as JsonObject);

    // Attachment to a point on an existing subassembly (Assembly.AddSubassembly(id, pointHookTo)),
    // e.g. a DaylightBench on the drain's DrainTopOut_L. Accepted as top-level fields or, so the
    // existing tool schema can carry them, as reserved keys inside "parameters".
    var attachToSubassembly = PluginRuntime.GetOptionalString(parameters, "attachToSubassembly");
    var attachToPointCode = PluginRuntime.GetOptionalString(parameters, "attachToPointCode");
    var attachToPointIndex = PluginRuntime.GetOptionalInt(parameters, "attachToPointIndex");
    if (subParams.TryGetValue("attachToSubassembly", out var a1)) { attachToSubassembly ??= a1?.ToString(); subParams.Remove("attachToSubassembly"); }
    if (subParams.TryGetValue("attachToPointCode", out var a2)) { attachToPointCode ??= a2?.ToString(); subParams.Remove("attachToPointCode"); }
    if (subParams.TryGetValue("attachToPointIndex", out var a3)) { attachToPointIndex ??= a3 is double dIdx ? (int)dIdx : int.TryParse(a3?.ToString(), out var pIdx) ? pIdx : null; subParams.Remove("attachToPointIndex"); }
    if ((attachToPointCode != null || attachToPointIndex != null) && string.IsNullOrWhiteSpace(attachToSubassembly))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "attachToPointCode/attachToPointIndex need attachToSubassembly (the name of the subassembly in this assembly to hook onto).");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var assembly = FindAssemblyByName(civilDoc, transaction, assemblyName, OpenMode.ForWrite);
      var requestedSides = ResolveRequestedSides(side);

      Subassembly? hookHost = null;
      if (!string.IsNullOrWhiteSpace(attachToSubassembly))
      {
        hookHost = GetSubassemblyIds(assembly)
          .Select(id => CivilObjectUtils.GetRequiredObject<Subassembly>(transaction, id, OpenMode.ForRead))
          .FirstOrDefault(sa => string.Equals(sa.Name, attachToSubassembly, StringComparison.OrdinalIgnoreCase));
        if (hookHost == null)
        {
          var names = GetSubassemblyIds(assembly).Select(id => CivilObjectUtils.GetRequiredObject<Subassembly>(transaction, id, OpenMode.ForRead).Name);
          throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Assembly '{assemblyName}' has no subassembly named '{attachToSubassembly}'. Subassemblies: {string.Join(", ", names)}.");
        }
      }
      var created = new List<Dictionary<string, object?>>();
      var warnings = new List<string>();
      string? resolvedClassName = null;

      foreach (var requestedSide in requestedSides)
      {
        var baseName = requestedName
          ?? (packagePath != null ? Path.GetFileNameWithoutExtension(packagePath) : StripStockPrefix(subassemblyType));
        var subassemblyName = requestedSides.Count > 1 && requestedSide != null
          ? $"{baseName} - {requestedSide}"
          : baseName;
        subassemblyName = MakeUniqueSubassemblyName(civilDoc, subassemblyName);

        ObjectId subassemblyId;
        string importedVia;
        if (packagePath != null)
        {
          subassemblyId = ImportPackage(civilDoc, subassemblyName, packagePath, assembly.Location);
          importedVia = "pkt";
        }
        else
        {
          subassemblyId = ImportStock(civilDoc, subassemblyName, subassemblyType, assembly.Location, out resolvedClassName);
          importedVia = "stock";
        }

        string? hookedTo = null;
        if (hookHost != null)
        {
          // For side Both, hook the Left copy to the *_L point and the Right copy to the *_R point
          // when the code is given with an _L/_R suffix or without one (DrainTopOut -> DrainTopOut_L / _R).
          var code = attachToPointCode;
          if (code != null && requestedSides.Count > 1 && requestedSide != null && !code.EndsWith("_L", StringComparison.OrdinalIgnoreCase) && !code.EndsWith("_R", StringComparison.OrdinalIgnoreCase))
            code = code + (requestedSide.Equals("Left", StringComparison.OrdinalIgnoreCase) ? "_L" : "_R");
          var hookPoint = FindHookPoint(hookHost, code, attachToPointIndex, out hookedTo);
          assembly.AddSubassembly(subassemblyId, hookPoint);
        }
        else
        {
          assembly.AddSubassembly(subassemblyId);
        }
        var subassembly = CivilObjectUtils.GetRequiredObject<Subassembly>(transaction, subassemblyId, OpenMode.ForWrite);

        if (requestedSide != null)
        {
          if (subassembly.HasSide)
          {
            subassembly.Side = requestedSide.Equals("Left", StringComparison.OrdinalIgnoreCase)
              ? SubassemblySideType.Left
              : SubassemblySideType.Right;
          }
          else
          {
            warnings.Add($"'{subassemblyName}' has no Side parameter; side '{requestedSide}' was ignored.");
          }
        }

        var (applied, missing, available) = ApplySubassemblyParameters(subassembly, subParams);
        if (missing.Count > 0)
        {
          throw new JsonRpcDispatchException(
            "CIVIL3D.INVALID_INPUT",
            $"Subassembly '{subassemblyName}' has no input parameter named: {string.Join(", ", missing)}. " +
            $"Available parameters: {string.Join(", ", available)}. Nothing was added.");
        }

        var summary = ToSubassemblySummary(subassembly);
        summary["importedVia"] = importedVia;
        summary["appliedParameters"] = applied;
        if (hookedTo != null) summary["hookedTo"] = hookedTo;
        created.Add(summary);
      }

      return new Dictionary<string, object?>
      {
        ["assemblyName"] = assemblyName,
        ["subassemblyType"] = subassemblyType,
        ["resolvedClassName"] = resolvedClassName,
        ["pktFilePath"] = packagePath,
        ["subassemblies"] = created,
        ["warnings"] = warnings,
        ["added"] = true,
      };
    });
  }

  public static Task<object?> EditAssemblyAsync(JsonObject? parameters)
  {
    var assemblyName = PluginRuntime.GetRequiredString(parameters, "assemblyName");
    var subassemblyName = PluginRuntime.GetOptionalString(parameters, "subassemblyName");
    var deleteSubassembly = PluginRuntime.GetOptionalBool(parameters, "delete") ?? false;
    var editParameters = ReadParameters(parameters?["parameters"] as JsonObject);

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var openMode = deleteSubassembly || editParameters.Count > 0 ? OpenMode.ForWrite : OpenMode.ForRead;
      var assembly = FindAssemblyByName(civilDoc, transaction, assemblyName, openMode);
      var subassemblyIds = GetSubassemblyIds(assembly);

      if (string.IsNullOrWhiteSpace(subassemblyName))
      {
        var subassemblies = subassemblyIds
          .Select(id => CivilObjectUtils.GetRequiredObject<Subassembly>(transaction, id, OpenMode.ForRead))
          .Select(ToSubassemblySummary)
          .ToList();
        return new Dictionary<string, object?>
        {
          ["assemblyName"] = assemblyName,
          ["subassemblyCount"] = subassemblies.Count,
          ["subassemblies"] = subassemblies,
        };
      }

      var targetId = subassemblyIds.FirstOrDefault(id =>
      {
        var candidate = CivilObjectUtils.GetRequiredObject<Subassembly>(transaction, id, OpenMode.ForRead);
        return string.Equals(candidate.Name, subassemblyName, StringComparison.OrdinalIgnoreCase);
      });
      if (targetId.IsNull)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Subassembly '{subassemblyName}' not found in assembly '{assemblyName}'.");

      var target = CivilObjectUtils.GetRequiredObject<Subassembly>(transaction, targetId, OpenMode.ForWrite);
      if (deleteSubassembly)
      {
        target.Erase();
        return new Dictionary<string, object?>
        {
          ["assemblyName"] = assemblyName,
          ["subassemblyName"] = subassemblyName,
          ["deleted"] = true,
        };
      }

      var (applied, missing, available) = ApplySubassemblyParameters(target, editParameters);
      if (missing.Count > 0)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.INVALID_INPUT",
          $"Subassembly parameters were not found: {string.Join(", ", missing)}. Available: {string.Join(", ", available)}. The transaction was not committed.");
      }

      var summary = ToSubassemblySummary(target);
      summary["updatedParameters"] = applied;
      summary["updated"] = applied.Count > 0;
      summary["assemblyName"] = assemblyName;
      return summary;
    });
  }

  // ---------------------------------------------------------------- import helpers

  private static List<string?> ResolveRequestedSides(string? side)
  {
    if (string.IsNullOrWhiteSpace(side) || side.Equals("None", StringComparison.OrdinalIgnoreCase))
      return new List<string?> { null };
    if (side.Equals("Both", StringComparison.OrdinalIgnoreCase))
      return new List<string?> { "Left", "Right" };
    return new List<string?> { side.Equals("Left", StringComparison.OrdinalIgnoreCase) ? "Left" : "Right" };
  }

  private static string StripStockPrefix(string className)
  {
    return className.StartsWith(StockClassPrefix, StringComparison.OrdinalIgnoreCase)
      ? className.Substring(StockClassPrefix.Length)
      : className;
  }

  private static ObjectId ImportPackage(CivilDocument civilDoc, string subassemblyName, string packagePath, Point3d location)
  {
    try
    {
      return civilDoc.SubassemblyCollection.ImportSACSubassembly(subassemblyName, packagePath, location);
    }
    catch (Exception ex) when (ex is not JsonRpcDispatchException)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        $"Civil 3D could not import the Subassembly Composer package '{packagePath}': {ex.GetType().Name}: {ex.Message}. " +
        "Check that the .pkt opens in Subassembly Composer 2026 without errors. Nothing was added.");
    }
  }

  private static ObjectId ImportStock(
    CivilDocument civilDoc,
    string subassemblyName,
    string className,
    Point3d location,
    out string resolvedClassName)
  {
    var candidates = new List<string> { className };
    if (!className.StartsWith(StockClassPrefix, StringComparison.OrdinalIgnoreCase))
      candidates.Add(StockClassPrefix + className);

    var failures = new List<string>();
    foreach (var candidate in candidates)
    {
      try
      {
        var id = civilDoc.SubassemblyCollection.ImportStockSubassembly(subassemblyName, candidate, location);
        resolvedClassName = candidate;
        return id;
      }
      catch (Exception ex) when (ex is not JsonRpcDispatchException)
      {
        failures.Add($"{candidate}: {ex.GetType().Name}: {ex.Message}");
      }
    }

    throw new JsonRpcDispatchException(
      "CIVIL3D.INVALID_INPUT",
      $"Stock subassembly '{className}' was not found in the Civil 3D catalog (tried {string.Join(" | ", failures)}). " +
      "Use the stock class name such as LinkWidthAndSlope, BasicLane or DaylightBench; for a Subassembly Composer " +
      "subassembly pass pktFilePath. Nothing was added.");
  }

  private static string MakeUniqueSubassemblyName(CivilDocument civilDoc, string requested)
  {
    var name = requested;
    var suffix = 1;
    while (SubassemblyNameExists(civilDoc, name))
    {
      suffix++;
      name = $"{requested} ({suffix})";
    }
    return name;
  }

  private static bool SubassemblyNameExists(CivilDocument civilDoc, string name)
  {
    try
    {
      var ids = civilDoc.SubassemblyCollection.GetSubassemblyIdsByName(name);
      return ids != null && ids.Count > 0;
    }
    catch
    {
      return false;
    }
  }

  // ---------------------------------------------------------------- parameters

  private static SaPoint FindHookPoint(Subassembly host, string? code, int? index, out string description)
  {
    var points = host.Points;
    if (index.HasValue)
    {
      if (index.Value < 0 || index.Value >= points.Count)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"attachToPointIndex {index.Value} is out of range for '{host.Name}' (0-{points.Count - 1}).");
      var pt = points[index.Value];
      description = $"{host.Name} point {index.Value} [{string.Join(",", ReadCodes(pt.Codes))}]";
      return pt;
    }
    if (string.IsNullOrWhiteSpace(code))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give attachToPointCode or attachToPointIndex.");
    var available = new List<string>();
    for (var i = 0; i < points.Count; i++)
    {
      var codes = ReadCodes(points[i].Codes);
      available.AddRange(codes);
      if (codes.Any(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase)))
      {
        description = $"{host.Name} point {i} [{string.Join(",", codes)}]";
        return points[i];
      }
    }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"'{host.Name}' has no point with code '{code}'. Point codes: {string.Join(", ", available.Distinct())}.");
  }

  private static Dictionary<string, object?> ReadParameters(JsonObject? values)
  {
    var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    if (values == null) return result;
    foreach (var pair in values)
    {
      if (pair.Value is JsonValue jsonValue)
      {
        if (jsonValue.TryGetValue<double>(out var d)) result[pair.Key] = d;
        else if (jsonValue.TryGetValue<bool>(out var b)) result[pair.Key] = b;
        else if (jsonValue.TryGetValue<string>(out var s)) result[pair.Key] = s;
        else result[pair.Key] = jsonValue.ToString();
      }
      else
      {
        result[pair.Key] = pair.Value?.ToJsonString();
      }
    }
    return result;
  }

  private static (List<string> applied, List<string> missing, List<string> available) ApplySubassemblyParameters(
    Subassembly subassembly,
    IReadOnlyDictionary<string, object?> parameters)
  {
    var applied = new List<string>();
    var missing = new List<string>();
    foreach (var pair in parameters)
    {
      if (TrySetParameter(subassembly, pair.Key, pair.Value))
        applied.Add(pair.Key);
      else
        missing.Add(pair.Key);
    }
    return (applied, missing, GetAvailableParameterNames(subassembly));
  }

  private static bool TrySetParameter(Subassembly subassembly, string key, object? value)
  {
    if (value == null) return false;

    if (TrySetDouble(subassembly, key, value)) return true;
    if (TrySetLong(subassembly, key, value)) return true;
    if (TrySetBool(subassembly, key, value)) return true;
    if (TrySetString(subassembly, key, value)) return true;

    // Last resort: a plain .NET property on the subassembly object (e.g. Side, CodeSetStyleName).
    return Civil3DCompatibility.TrySetProperty(subassembly, key, value);
  }

  private static bool TrySetDouble(Subassembly subassembly, string key, object value)
  {
    if (!TryToDouble(value, out var d)) return false;
    var param = FindParam<ParamDouble>(subassembly.ParamsDouble, key, () => subassembly.ParamsDouble[key]);
    if (param == null) return false;
    param.Value = d;
    return true;
  }

  private static bool TrySetLong(Subassembly subassembly, string key, object value)
  {
    if (!TryToDouble(value, out var d) || Math.Abs(d - Math.Round(d)) > 1e-9) return false;
    var param = FindParam<ParamLong>(subassembly.ParamsLong, key, () => subassembly.ParamsLong[key]);
    if (param == null) return false;
    param.Value = (int)Math.Round(d);
    return true;
  }

  private static bool TrySetBool(Subassembly subassembly, string key, object value)
  {
    bool b;
    if (value is bool flag) b = flag;
    else if (value is string text && bool.TryParse(text, out var parsed)) b = parsed;
    else return false;
    var param = FindParam<ParamBool>(subassembly.ParamsBool, key, () => subassembly.ParamsBool[key]);
    if (param == null) return false;
    param.Value = b;
    return true;
  }

  private static bool TrySetString(Subassembly subassembly, string key, object value)
  {
    var text = value is string s ? s : Convert.ToString(value, CultureInfo.InvariantCulture);
    if (text == null) return false;
    var param = FindParam<ParamString>(subassembly.ParamsString, key, () => subassembly.ParamsString[key]);
    if (param == null) return false;
    param.Value = text;
    return true;
  }

  private static bool TryToDouble(object value, out double result)
  {
    switch (value)
    {
      case double d: result = d; return true;
      case float f: result = f; return true;
      case int i: result = i; return true;
      case long l: result = l; return true;
      case decimal m: result = (double)m; return true;
      case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
        result = parsed; return true;
      default:
        result = 0; return false;
    }
  }

  /// <summary>
  /// Finds a parameter by key name (indexer) first, then by display name (enumeration).
  /// </summary>
  private static T? FindParam<T>(IEnumerable? collection, string key, Func<T?> byKey) where T : class
  {
    try
    {
      var direct = byKey();
      if (direct != null) return direct;
    }
    catch
    {
      // Missing key: fall through to the display-name scan.
    }

    if (collection == null) return null;
    foreach (var item in collection)
    {
      if (item is not T typed) continue;
      var displayName = Civil3DCompatibility.GetPropertyValue(typed, "DisplayName")?.ToString();
      if (displayName != null && string.Equals(displayName, key, StringComparison.OrdinalIgnoreCase))
        return typed;
    }
    return null;
  }

  private static List<string> GetAvailableParameterNames(Subassembly subassembly)
  {
    var names = new List<string>();
    void Collect(IEnumerable? collection, string kind)
    {
      if (collection == null) return;
      foreach (var item in collection)
      {
        var displayName = Civil3DCompatibility.GetPropertyValue(item, "DisplayName")?.ToString();
        if (!string.IsNullOrWhiteSpace(displayName)) names.Add($"{displayName} ({kind})");
      }
    }
    try { Collect(subassembly.ParamsDouble, "double"); } catch { }
    try { Collect(subassembly.ParamsLong, "long"); } catch { }
    try { Collect(subassembly.ParamsBool, "bool"); } catch { }
    try { Collect(subassembly.ParamsString, "string"); } catch { }
    return names;
  }

  private static Dictionary<string, object?> ReadParameterValues(Subassembly subassembly)
  {
    var values = new Dictionary<string, object?>();
    void Collect(IEnumerable? collection)
    {
      if (collection == null) return;
      foreach (var item in collection)
      {
        var displayName = Civil3DCompatibility.GetPropertyValue(item, "DisplayName")?.ToString();
        if (string.IsNullOrWhiteSpace(displayName)) continue;
        values[displayName] = Civil3DCompatibility.GetPropertyValue(item, "Value");
      }
    }
    try { Collect(subassembly.ParamsDouble); } catch { }
    try { Collect(subassembly.ParamsLong); } catch { }
    try { Collect(subassembly.ParamsBool); } catch { }
    try { Collect(subassembly.ParamsString); } catch { }
    return values;
  }

  // ---------------------------------------------------------------- lookups and summaries

  private static Assembly FindAssemblyByName(
    CivilDocument civilDocument,
    Transaction transaction,
    string name,
    OpenMode openMode)
  {
    foreach (ObjectId id in civilDocument.AssemblyCollection)
    {
      var assembly = CivilObjectUtils.GetRequiredObject<Assembly>(transaction, id, OpenMode.ForRead);
      if (string.Equals(assembly.Name, name, StringComparison.OrdinalIgnoreCase))
      {
        return openMode == OpenMode.ForWrite
          ? CivilObjectUtils.GetRequiredObject<Assembly>(transaction, id, OpenMode.ForWrite)
          : assembly;
      }
    }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Assembly '{name}' was not found in the drawing.");
  }

  private static List<ObjectId> GetSubassemblyIds(Assembly assembly)
  {
    return assembly.Groups
      .SelectMany(group => group.GetSubassemblyIds().Cast<ObjectId>())
      .Where(id => !id.IsNull)
      .Distinct()
      .ToList();
  }

  private static string? GetStyleName(Assembly assembly, Transaction transaction)
  {
    if (assembly.StyleId.IsNull) return null;
    return CivilObjectUtils.GetName(transaction.GetObject(assembly.StyleId, OpenMode.ForRead));
  }

  private static List<Dictionary<string, object?>> GetSubassemblies(Assembly assembly, Transaction transaction)
  {
    return GetSubassemblyIds(assembly)
      .Select(id => CivilObjectUtils.GetRequiredObject<Subassembly>(transaction, id, OpenMode.ForRead))
      .Select(ToSubassemblySummary)
      .ToList();
  }

  private static List<string> ReadCodes(IEnumerable? codes)
  {
    var list = new List<string>();
    if (codes == null) return list;
    foreach (var code in codes)
    {
      var text = code?.ToString();
      if (!string.IsNullOrWhiteSpace(text)) list.Add(text);
    }
    return list;
  }

  private static List<Dictionary<string, object?>> ReadPoints(Subassembly subassembly)
  {
    var rows = new List<Dictionary<string, object?>>();
    try
    {
      foreach (SaPoint point in subassembly.Points)
      {
        rows.Add(new Dictionary<string, object?>
        {
          ["index"] = point.Index,
          ["offset"] = point.Offset,
          ["elevation"] = point.Elevation,
          ["codes"] = ReadCodes(point.Codes),
        });
      }
    }
    catch (Exception ex)
    {
      PluginLog.Debug("Assembly", "Subassembly points not readable", ex);
    }
    return rows;
  }

  private static List<Dictionary<string, object?>> ReadLinks(Subassembly subassembly)
  {
    var rows = new List<Dictionary<string, object?>>();
    try
    {
      foreach (SaLink link in subassembly.Links)
      {
        var pointIndices = new List<int>();
        try
        {
          foreach (SaPoint point in link.Points) pointIndices.Add(point.Index);
        }
        catch { }
        rows.Add(new Dictionary<string, object?>
        {
          ["index"] = link.Index,
          ["points"] = pointIndices,
          ["codes"] = ReadCodes(link.Codes),
        });
      }
    }
    catch (Exception ex)
    {
      PluginLog.Debug("Assembly", "Subassembly links not readable", ex);
    }
    return rows;
  }

  private static List<Dictionary<string, object?>> ReadShapes(Subassembly subassembly)
  {
    var rows = new List<Dictionary<string, object?>>();
    try
    {
      foreach (SaShape shape in subassembly.Shapes)
      {
        var linkIndices = new List<int>();
        try
        {
          foreach (SaLink link in shape.Links) linkIndices.Add(link.Index);
        }
        catch { }
        rows.Add(new Dictionary<string, object?>
        {
          ["index"] = shape.Index,
          ["links"] = linkIndices,
          ["codes"] = ReadCodes(shape.Codes),
        });
      }
    }
    catch (Exception ex)
    {
      PluginLog.Debug("Assembly", "Subassembly shapes not readable", ex);
    }
    return rows;
  }

  private static Dictionary<string, object?> ToSubassemblySummary(Subassembly subassembly)
  {
    string sideText = "none";
    try
    {
      if (subassembly.HasSide) sideText = subassembly.Side.ToString().ToLowerInvariant();
    }
    catch { }

    string? origin = null;
    try
    {
      var o = subassembly.Origin;
      origin = $"{o.X.ToString("0.###", CultureInfo.InvariantCulture)},{o.Y.ToString("0.###", CultureInfo.InvariantCulture)}";
    }
    catch { }

    bool fromComposer = false;
    try { fromComposer = subassembly.IsFromSubassemblyComposer; } catch { }

    return new Dictionary<string, object?>
    {
      ["name"] = subassembly.Name ?? subassembly.Handle.ToString(),
      ["handle"] = CivilObjectUtils.GetHandle(subassembly),
      ["type"] = subassembly.GetType().Name,
      ["className"] = subassembly.GetType().Name,
      ["side"] = sideText,
      ["isFromSubassemblyComposer"] = fromComposer,
      ["origin"] = origin,
      ["parameters"] = ReadParameterValues(subassembly),
      ["points"] = ReadPoints(subassembly),
      ["links"] = ReadLinks(subassembly),
      ["shapes"] = ReadShapes(subassembly),
    };
  }

  private static Dictionary<string, object?> ToAssemblySummary(Assembly assembly)
  {
    return new Dictionary<string, object?>
    {
      ["name"] = assembly.Name,
      ["handle"] = CivilObjectUtils.GetHandle(assembly),
      ["subassemblyCount"] = GetSubassemblyIds(assembly).Count,
      ["type"] = assembly.Type.ToString(),
    };
  }
}
