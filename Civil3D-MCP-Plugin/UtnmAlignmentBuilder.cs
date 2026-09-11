using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Builds a horizontal alignment from a validated IP spec (skill c3d-alignment-from-ip, spec v0.1).
///
/// Construction (proven on a live Civil 3D 2026 drawing, where a free spiral-curve-spiral sits
/// between two fixed straights):
///   1. Alignment.Create (siteless unless a site is named)
///   2. a fixed line between every pair of consecutive points (BP, IPs, EP)
///   3. at each IP with a curve, a free curve group between the two lines meeting there
///      (simple -> AddFreeCurve by radius; scs -> AddFreeSCS by spiral lengths and radius)
///   4. reference station, description tag
///   5. verification inside the same transaction: the PI implied by each built curve's tangents
///      must land on the spec IP, and radius / spiral lengths must match
///
/// Everything happens in one transaction. A dry run always rolls back. A real run rolls back too if
/// any build step or verification check fails, so a wrong alignment is never committed.
///
/// API calls that are not referenced elsewhere in the plugin go through
/// Civil3DCompatibility.InvokeMatchingOverload, which matches the overloads that exist at runtime
/// and reports them if none fit.
/// </summary>
internal static class UtnmAlignmentBuilder
{
  internal const string TagPrefix = "UTNM:c3d-alignment-from-ip";

  internal static Dictionary<string, object?> Build(
    CivilDocument civilDoc,
    Database database,
    Transaction transaction,
    UtnmAlignmentSpec spec,
    bool dryRun,
    double tolerance)
  {
    var warnings = new List<string>();

    var nameProblem = UtnmNameRules.Problem(spec.Name, ExistingAlignmentNames(civilDoc, transaction));
    if (nameProblem != null)
    {
      throw Invalid(nameProblem.StartsWith("already", StringComparison.Ordinal) ? "CIVIL3D.CONFLICT" : "CIVIL3D.INVALID_INPUT",
        $"Alignment name '{spec.Name}' {nameProblem}");
    }

    var layerId = ResolveLayer(database, transaction, spec.Layer, warnings);
    var siteId = ResolveSite(civilDoc, transaction, spec.Site);
    var styleId = ResolveNamed(transaction, LookupUtils.GetAlignmentStyleId(civilDoc, transaction, spec.Style), spec.Style, "alignment style",
      () => civilDoc.Styles.AlignmentStyles.Cast<ObjectId>());
    var labelSetId = ResolveNamed(transaction, LookupUtils.GetAlignmentLabelSetId(civilDoc, transaction, spec.LabelSet), spec.LabelSet, "alignment label set",
      () => civilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles.Cast<ObjectId>());

    var created = Civil3DCompatibility.InvokeMatchingOverload(
      null, typeof(Alignment), "Create",
      new object?[] { civilDoc, spec.Name, siteId, layerId, styleId, labelSetId },
      out var createError);
    if (created is not ObjectId alignmentId || alignmentId.IsNull)
    {
      throw Invalid("CIVIL3D.API_ERROR", $"Alignment.Create failed: {createError ?? "returned no id"}");
    }

    var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, alignmentId, OpenMode.ForWrite);

    // 2. Fixed lines between consecutive points.
    var lineIds = new List<int>();
    for (var i = 0; i < spec.Points.Count - 1; i++)
    {
      var a = spec.Points[i];
      var b = spec.Points[i + 1];
      object? line;
      try
      {
        line = alignment.Entities.AddFixedLine(new Point3d(a.Easting, a.Northing, 0), new Point3d(b.Easting, b.Northing, 0));
      }
      catch (System.Exception ex)
      {
        throw Invalid("CIVIL3D.API_ERROR", $"{a.Id}->{b.Id}: fixed line rejected: {Civil3DCompatibility.DescribeException(ex)}");
      }

      lineIds.Add(ReadEntityId(line, $"{a.Id}->{b.Id} line"));
    }

    // 3. Free curve groups at each IP.
    var groupIds = new Dictionary<string, int>(StringComparer.Ordinal);
    for (var i = 1; i < spec.Points.Count - 1; i++)
    {
      var pi = spec.Points[i];
      object? group = null;
      string? error = null;
      switch (pi.Kind)
      {
        case "none":
          continue;
        case "simple":
          group = Civil3DCompatibility.InvokeMatchingOverload(
            alignment.Entities, null, "AddFreeCurve",
            new object?[] { lineIds[i - 1], lineIds[i], pi.Radius, "Radius", false, "Compound|*" },
            out error);
          break;
        case "scs":
          group = Civil3DCompatibility.InvokeMatchingOverload(
            alignment.Entities, null, "AddFreeSCS",
            new object?[] { lineIds[i - 1], lineIds[i], pi.LsIn, pi.LsOut, "Length", pi.Radius, false, spec.SpiralType },
            out error);
          break;
        default:
          throw Invalid("CIVIL3D.INVALID_INPUT", $"{pi.Id}: curve kind '{pi.Kind}' is not supported in spec v0.1.");
      }

      if (group == null)
      {
        throw Invalid("CIVIL3D.API_ERROR",
          $"{pi.Id}: Civil 3D rejected the {pi.Kind} curve (R={pi.Radius}, Ls in/out={pi.LsIn}/{pi.LsOut}): {error}");
      }

      groupIds[pi.Id] = ReadEntityId(group, $"{pi.Id} curve");
    }

    // 4. Station and tag.
    if (Math.Abs(alignment.StartingStation - spec.StartStation) > 1e-9)
    {
      if (!Civil3DCompatibility.TrySetPropertyValue(alignment, "ReferencePointStation", spec.StartStation, out var stationError))
      {
        warnings.Add($"start station not set: {stationError}");
      }
      else if (Math.Abs(alignment.StartingStation - spec.StartStation) > 1e-6)
      {
        warnings.Add($"start station requested {spec.StartStation} but alignment starts at {alignment.StartingStation}");
      }
    }

    var tag = $"{TagPrefix} | {spec.SourceLabel}";
    if (!Civil3DCompatibility.TrySetPropertyValue(alignment, "Description", tag, out var tagError))
    {
      warnings.Add($"description tag not set: {tagError}");
    }

    // 5. Verify before anything can be committed.
    var verification = Verify(alignment, spec, groupIds, tolerance);
    var passed = verification.All(result => result["ok"] is true);

    var response = new Dictionary<string, object?>
    {
      ["name"] = spec.Name,
      ["handle"] = CivilObjectUtils.GetHandle(alignment),
      ["dryRun"] = dryRun,
      ["committed"] = !dryRun && passed,
      ["verificationPassed"] = passed,
      ["tolerance"] = tolerance,
      ["startStation"] = alignment.StartingStation,
      ["endStation"] = alignment.EndingStation,
      ["length"] = alignment.Length,
      ["style"] = CivilObjectUtils.GetName(transaction.GetObject(alignment.StyleId, OpenMode.ForRead)),
      ["layer"] = alignment.Layer,
      ["ips"] = verification,
      ["entities"] = AlignmentGeometryReader.ReadEntities(alignment)
        .Select((row, order) => AlignmentGeometryReader.DescribeEntity(alignment, row, order, detailed: false))
        .ToList(),
      ["warnings"] = warnings.Count > 0 ? warnings : null,
    };

    if (!dryRun && !passed)
    {
      // Throwing aborts the transaction: nothing is committed.
      var failures = string.Join("; ", verification
        .Where(result => result["ok"] is not true)
        .Select(result => $"{result["id"]}: {string.Join(", ", (List<string>)result["issues"]!)}"));
      throw Invalid("CIVIL3D.CONFLICT", $"Built geometry did not match the spec, so nothing was committed. {failures}");
    }

    return response;
  }

  private static List<Dictionary<string, object?>> Verify(
    Alignment alignment,
    UtnmAlignmentSpec spec,
    IReadOnlyDictionary<string, int> groupIds,
    double tolerance)
  {
    var rows = AlignmentGeometryReader.ReadEntities(alignment);
    var byEntityId = new Dictionary<int, AlignmentGeometryReader.EntityRow>();
    foreach (var row in rows)
    {
      if (AlignmentGeometryReader.TryRead(row.Entity, "EntityId", out _) is int entityId)
      {
        byEntityId[entityId] = row;
      }
    }

    var results = new List<Dictionary<string, object?>>();
    foreach (var pi in spec.Points.Skip(1).Take(spec.Points.Count - 2).Where(point => point.Kind != "none"))
    {
      var issues = new List<string>();
      var result = new Dictionary<string, object?> { ["id"] = pi.Id, ["kind"] = pi.Kind };
      results.Add(result);

      if (!groupIds.TryGetValue(pi.Id, out var groupId) || !byEntityId.TryGetValue(groupId, out var group))
      {
        issues.Add("built curve not found");
        result["ok"] = false;
        result["issues"] = issues;
        continue;
      }

      var subEntities = AlignmentGeometryReader.ReadSubEntities(group.Entity);
      if (subEntities.Count == 0)
      {
        issues.Add("curve has no readable sub-entities");
        result["ok"] = false;
        result["issues"] = issues;
        continue;
      }

      var first = subEntities[0];
      var last = subEntities[^1];
      var arc = subEntities.FirstOrDefault(sub => Equals(AlignmentGeometryReader.TryRead(sub, "SubEntityType", out _)?.ToString(), "Arc"));

      var tsPoint = ReadXY(first, "StartPoint");
      var stPoint = ReadXY(last, "EndPoint");
      var directionIn = ToDouble(AlignmentGeometryReader.TryRead(first, "StartDirection", out _));
      var directionOut = ToDouble(AlignmentGeometryReader.TryRead(last, "EndDirection", out _));
      var radius = ToDouble(AlignmentGeometryReader.TryRead(arc, "Radius", out _));
      var clockwise = AlignmentGeometryReader.TryRead(arc, "Clockwise", out _) as bool?;
      var arcLength = ToDouble(AlignmentGeometryReader.TryRead(arc, "Length", out _));
      double? lsIn = IsSpiral(first) ? ToDouble(AlignmentGeometryReader.TryRead(first, "Length", out _)) : 0;
      double? lsOut = IsSpiral(last) ? ToDouble(AlignmentGeometryReader.TryRead(last, "Length", out _)) : 0;

      result["ts"] = group.StartStation;
      result["st"] = group.EndStation;
      result["length"] = group.EndStation - group.StartStation;
      result["radius"] = radius;
      result["lsIn"] = lsIn;
      result["lsOut"] = lsOut;
      result["arcLength"] = arcLength;
      result["direction"] = clockwise == null ? null : clockwise.Value ? "R" : "L";

      if (tsPoint is { } ts && stPoint is { } st && directionIn is { } din && directionOut is { } dout)
      {
        // Directions are azimuths from grid north, clockwise, in radians (confirmed live).
        var u = (x: Math.Sin(din), y: Math.Cos(din));
        var v = (x: Math.Sin(dout), y: Math.Cos(dout));
        var determinant = u.x * -v.y - u.y * -v.x;
        if (Math.Abs(determinant) > 1e-12)
        {
          var bx = st.x - ts.x;
          var by = st.y - ts.y;
          var t1 = (bx * -v.y - by * -v.x) / determinant;
          var t2 = -(u.x * by - u.y * bx) / determinant;
          var piBuilt = (x: ts.x + t1 * u.x, y: ts.y + t1 * u.y);
          var piDelta = Math.Sqrt(Math.Pow(piBuilt.x - pi.Easting, 2) + Math.Pow(piBuilt.y - pi.Northing, 2));
          result["t1"] = t1;
          result["t2"] = t2;
          result["piBuilt"] = new Dictionary<string, object?> { ["x"] = piBuilt.x, ["y"] = piBuilt.y };
          result["piDelta"] = piDelta;
          if (piDelta > tolerance)
          {
            issues.Add($"PI implied by built tangents is {piDelta:F4} m from the spec IP");
          }
        }
        else
        {
          issues.Add("tangents are parallel; PI could not be checked");
        }
      }
      else
      {
        issues.Add("start/end point or direction could not be read");
      }

      if (radius is null || Math.Abs(radius.Value - pi.Radius) > Math.Max(tolerance, pi.Radius * 1e-9))
      {
        issues.Add($"radius {radius?.ToString("F4") ?? "unreadable"} != spec {pi.Radius}");
      }

      if (lsIn is null || Math.Abs(lsIn.Value - pi.LsIn) > tolerance)
      {
        issues.Add($"spiral in {lsIn?.ToString("F4") ?? "unreadable"} != spec {pi.LsIn}");
      }

      if (lsOut is null || Math.Abs(lsOut.Value - pi.LsOut) > tolerance)
      {
        issues.Add($"spiral out {lsOut?.ToString("F4") ?? "unreadable"} != spec {pi.LsOut}");
      }

      result["ok"] = issues.Count == 0;
      result["issues"] = issues;
    }

    return results;
  }

  private static bool IsSpiral(object subEntity)
    => Equals(AlignmentGeometryReader.TryRead(subEntity, "SubEntityType", out _)?.ToString(), "Spiral");

  private static (double x, double y)? ReadXY(object? source, string propertyName)
  {
    var point = AlignmentGeometryReader.TryRead(source, propertyName, out _);
    var x = ToDouble(AlignmentGeometryReader.TryRead(point, "X", out _));
    var y = ToDouble(AlignmentGeometryReader.TryRead(point, "Y", out _));
    return x is { } px && y is { } py ? (px, py) : null;
  }

  private static double? ToDouble(object? value)
  {
    try
    {
      return value is double or float or int or long ? Convert.ToDouble(value) : null;
    }
    catch
    {
      return null;
    }
  }

  private static int ReadEntityId(object? entity, string what)
  {
    if (AlignmentGeometryReader.TryRead(entity, "EntityId", out var error) is int id)
    {
      return id;
    }

    throw Invalid("CIVIL3D.API_ERROR", $"{what}: could not read EntityId ({error ?? "no entity returned"})");
  }

  internal static List<string> ExistingAlignmentNames(CivilDocument civilDoc, Transaction transaction)
    => civilDoc.GetAlignmentIds()
      .Cast<ObjectId>()
      .Select(id => CivilObjectUtils.GetRequiredObject<Alignment>(transaction, id, OpenMode.ForRead).Name)
      .ToList();

  // The user picks the layer; it must be a usable layer of this drawing, never a silent fallback.
  private static ObjectId ResolveLayer(Database database, Transaction transaction, string? layerName, List<string> warnings)
  {
    if (string.IsNullOrWhiteSpace(layerName))
    {
      throw Invalid("CIVIL3D.INVALID_INPUT", "A layer must be selected.");
    }

    var layerTable = CivilObjectUtils.GetRequiredObject<LayerTable>(transaction, database.LayerTableId, OpenMode.ForRead);
    if (!layerTable.Has(layerName))
    {
      throw Invalid("CIVIL3D.INVALID_INPUT", $"Layer '{layerName}' does not exist in this drawing.");
    }

    var layerId = layerTable[layerName];
    var layer = CivilObjectUtils.GetRequiredObject<LayerTableRecord>(transaction, layerId, OpenMode.ForRead);
    var state = UtnmBuildOptions.ReadLayerState(layer);
    if (state.IsDependent)
    {
      throw Invalid("CIVIL3D.INVALID_INPUT", $"Layer '{layerName}' belongs to an external reference and cannot hold new objects.");
    }

    if (state.IsFrozen)
    {
      throw Invalid("CIVIL3D.INVALID_INPUT", $"Layer '{layerName}' is frozen; the alignment would be invisible. Thaw it or pick another layer.");
    }

    if (state.IsLocked)
    {
      throw Invalid("CIVIL3D.INVALID_INPUT", $"Layer '{layerName}' is locked. Unlock it or pick another layer.");
    }

    if (state.IsOff)
    {
      warnings.Add($"layer '{layerName}' is turned off, so the alignment will not be visible until it is turned on");
    }

    return layerId;
  }

  private static ObjectId ResolveSite(CivilDocument civilDoc, Transaction transaction, string? siteName)
  {
    if (string.IsNullOrWhiteSpace(siteName))
    {
      return ObjectId.Null;
    }

    var siteId = LookupUtils.GetSiteId(civilDoc, transaction, siteName);
    if (siteId.IsNull)
    {
      throw Invalid("CIVIL3D.INVALID_INPUT", $"Site '{siteName}' does not exist in this drawing.");
    }

    return siteId;
  }

  // LookupUtils falls back to the first style when a name is not found; for a builder that would
  // silently apply the wrong company style, so confirm the resolved name.
  private static ObjectId ResolveNamed(
    Transaction transaction,
    ObjectId resolvedId,
    string? requestedName,
    string what,
    Func<IEnumerable<ObjectId>>? available)
  {
    if (string.IsNullOrWhiteSpace(requestedName))
    {
      throw Invalid("CIVIL3D.INVALID_INPUT", $"An {what} must be selected.");
    }

    var resolvedName = resolvedId.IsNull ? null : CivilObjectUtils.GetName(transaction.GetObject(resolvedId, OpenMode.ForRead));
    if (string.Equals(resolvedName, requestedName, StringComparison.OrdinalIgnoreCase))
    {
      return resolvedId;
    }

    var names = available == null
      ? string.Empty
      : " Available: " + string.Join(", ", available()
        .Select(id => CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead)))
        .Where(name => !string.IsNullOrWhiteSpace(name)));
    throw Invalid("CIVIL3D.INVALID_INPUT", $"{char.ToUpperInvariant(what[0])}{what[1..]} '{requestedName}' was not found.{names}");
  }

  private static JsonRpcDispatchException Invalid(string code, string message)
    => new(code, message.EndsWith("committed.", StringComparison.Ordinal) ? message : $"{message} No alignment was created.");
}

/// <summary>Spec v0.1 as sent by the skill. The Node side validates the same shape first.</summary>
internal sealed class UtnmAlignmentSpec
{
  public string Name { get; private init; } = "";
  public string? Layer { get; private init; }
  public string? Style { get; private init; }
  public string? LabelSet { get; private init; }
  public string? Site { get; private init; }
  public double StartStation { get; private init; }
  public string SpiralType { get; private init; } = "Clothoid";
  public string SourceLabel { get; private init; } = "";
  public List<UtnmSpecPoint> Points { get; } = new();

  public static UtnmAlignmentSpec Parse(JsonObject node)
  {
    if (Text(node["spec_version"]) != "0.1") Fail("spec_version must be '0.1'.");

    var status = Text(node["validation"]?["status"]);
    var acknowledged = Bool(node["validation"]?["user_acknowledged_warnings"]) ?? false;
    if (status == "fail") Fail("Spec validation status is 'fail'.");
    if (status == "warn" && !acknowledged) Fail("Spec has warnings the user has not acknowledged.");
    if (status is not ("pass" or "warn")) Fail("Spec validation status must be 'pass' or 'warn'.");

    if (node["alignment"] is not JsonObject alignment) throw Error("Spec is missing 'alignment'.");
    var spiralType = Text(alignment["spiral_type"]) ?? "Clothoid";
    if (!string.Equals(spiralType, "Clothoid", StringComparison.OrdinalIgnoreCase)) Fail("Only Clothoid spirals are supported in spec v0.1.");

    foreach (var required in new[] { "layer", "style", "label_set" })
    {
      if (string.IsNullOrWhiteSpace(Text(alignment[required])))
      {
        Fail($"alignment.{required} is required: the user must select it from the drawing's options.");
      }
    }

    var source = node["source"] as JsonObject;
    var spec = new UtnmAlignmentSpec
    {
      Name = Text(alignment["name"]) is { Length: > 0 } name ? name : throw Error("alignment.name is required."),
      Layer = Text(alignment["layer"]),
      Style = Text(alignment["style"]),
      LabelSet = Text(alignment["label_set"]),
      Site = Text(alignment["site"]),
      StartStation = Number(alignment["start_station"]) ?? 0,
      SpiralType = "Clothoid",
      SourceLabel = string.Join(" / ", new[] { Text(source?["file"]), Text(source?["sheet"]) }.Where(part => !string.IsNullOrWhiteSpace(part))),
    };

    if (node["points"] is not JsonArray points || points.Count < 2) throw Error("Spec needs at least a start and an end point.");
    foreach (var item in points)
    {
      var curve = item?["curve"] as JsonObject;
      spec.Points.Add(new UtnmSpecPoint(
        Text(item?["id"]) ?? "?",
        Text(item?["role"]) ?? "",
        Number(item?["easting"]) ?? double.NaN,
        Number(item?["northing"]) ?? double.NaN,
        Text(curve?["kind"]) ?? "none",
        Number(curve?["radius"]) ?? 0,
        Number(curve?["ls_in"]) ?? 0,
        Number(curve?["ls_out"]) ?? 0));
    }

    if (spec.Points[0].Role != "start" || spec.Points[^1].Role != "end") Fail("First point must be role 'start' and last must be role 'end'.");
    for (var i = 0; i < spec.Points.Count; i++)
    {
      var point = spec.Points[i];
      if (double.IsNaN(point.Easting) || double.IsNaN(point.Northing)) Fail($"{point.Id}: easting and northing are required.");
      var interior = i > 0 && i < spec.Points.Count - 1;
      if (!interior && point.Kind != "none") Fail($"{point.Id}: start and end points cannot carry a curve.");
      if (interior && point.Kind == "simple" && point.Radius <= 0) Fail($"{point.Id}: simple curve needs a positive radius.");
      if (interior && point.Kind == "scs" && (point.Radius <= 0 || point.LsIn <= 0 || point.LsOut <= 0))
        Fail($"{point.Id}: scs needs a positive radius and both spiral lengths (blank spirals must be sent as kind 'simple').");
      if (interior && point.Kind is not ("none" or "simple" or "scs")) Fail($"{point.Id}: curve kind '{point.Kind}' is not supported in spec v0.1.");
    }

    return spec;
  }

  private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

  private static double? Number(JsonNode? node)
    => node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

  private static bool? Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

  private static JsonRpcDispatchException Error(string message) => new("CIVIL3D.INVALID_INPUT", message + " No alignment was created.");

  private static void Fail(string message) => throw Error(message);
}

internal sealed record UtnmSpecPoint(string Id, string Role, double Easting, double Northing, string Kind, double Radius, double LsIn, double LsOut);

/// <summary>
/// Rules for a "proper" alignment name. Mirrored in utnmAlignmentDomain.ts and returned by
/// build_options so the in-chat form validates exactly the same way.
/// </summary>
internal static class UtnmNameRules
{
  public const int MaxLength = 100;
  public const string InvalidCharacters = "<>/\\\":;?*|=`";
  private static readonly Regex DefaultNamePattern = new(@"^alignment\s*-\s*\(\d+\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

  /// <summary>Returns null when the name is acceptable, otherwise the reason (a sentence fragment).</summary>
  public static string? Problem(string? name, IEnumerable<string> existingNames)
  {
    if (string.IsNullOrWhiteSpace(name))
    {
      return "is required.";
    }

    if (name != name.Trim())
    {
      return "must not start or end with spaces.";
    }

    if (name.Length > MaxLength)
    {
      return $"is longer than {MaxLength} characters.";
    }

    var invalid = name.Where(character => InvalidCharacters.Contains(character) || char.IsControl(character)).Distinct().ToArray();
    if (invalid.Length > 0)
    {
      return $"contains characters that are not allowed: {string.Join(" ", invalid.Select(character => char.IsControl(character) ? "(control)" : character.ToString()))}";
    }

    if (DefaultNamePattern.IsMatch(name))
    {
      return "is a Civil 3D default placeholder; enter a proper descriptive name.";
    }

    if (existingNames.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
    {
      return "already exists in this drawing (names are compared without regard to case).";
    }

    return null;
  }
}

/// <summary>Choices offered to the user before a build, read from the open drawing.</summary>
internal static class UtnmBuildOptions
{
  internal readonly record struct LayerState(bool IsOff, bool IsFrozen, bool IsLocked, bool IsDependent);

  internal static LayerState ReadLayerState(LayerTableRecord layer)
    => new(
      AlignmentGeometryReader.TryRead(layer, "IsOff", out _) is true,
      layer.IsFrozen,
      AlignmentGeometryReader.TryRead(layer, "IsLocked", out _) is true,
      AlignmentGeometryReader.TryRead(layer, "IsDependent", out _) is true);

  internal static Dictionary<string, object?> Read(Document doc, CivilDocument civilDoc, Database database, Transaction transaction)
  {
    var currentLayerName = CivilObjectUtils.GetRequiredObject<LayerTableRecord>(transaction, database.Clayer, OpenMode.ForRead).Name;
    var layers = new List<Dictionary<string, object?>>();
    var layerTable = CivilObjectUtils.GetRequiredObject<LayerTable>(transaction, database.LayerTableId, OpenMode.ForRead);
    foreach (ObjectId layerId in layerTable)
    {
      if (transaction.GetObject(layerId, OpenMode.ForRead) is not LayerTableRecord layer)
      {
        continue;
      }

      var state = ReadLayerState(layer);
      var blockedReason = state.IsDependent ? "external reference layer" : state.IsFrozen ? "frozen" : state.IsLocked ? "locked" : null;
      layers.Add(new Dictionary<string, object?>
      {
        ["name"] = layer.Name,
        ["usable"] = blockedReason == null,
        ["reason"] = blockedReason,
        ["isOff"] = state.IsOff,
        ["isCurrent"] = string.Equals(layer.Name, currentLayerName, StringComparison.OrdinalIgnoreCase),
      });
    }

    return new Dictionary<string, object?>
    {
      ["drawing"] = CivilObjectUtils.GetStringProperty(doc, "Name"),
      ["currentLayer"] = currentLayerName,
      ["layers"] = layers.OrderBy(layer => (string)layer["name"]!, StringComparer.OrdinalIgnoreCase).ToList(),
      ["alignmentStyles"] = Names(transaction, civilDoc.Styles.AlignmentStyles.Cast<ObjectId>()),
      ["alignmentLabelSets"] = Names(transaction, civilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles.Cast<ObjectId>()),
      ["sites"] = civilDoc.GetSiteIds()
        .Cast<ObjectId>()
        .Select(id => CivilObjectUtils.GetRequiredObject<Site>(transaction, id, OpenMode.ForRead).Name)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToList(),
      ["existingAlignmentNames"] = UtnmAlignmentBuilder.ExistingAlignmentNames(civilDoc, transaction),
      ["nameRules"] = new Dictionary<string, object?>
      {
        ["maxLength"] = UtnmNameRules.MaxLength,
        ["invalidCharacters"] = UtnmNameRules.InvalidCharacters,
        ["rejectDefaultPlaceholder"] = "Alignment - (n)",
        ["unique"] = true,
      },
    };
  }

  private static List<string> Names(Transaction transaction, IEnumerable<ObjectId> ids)
    => ids
      .Select(id => CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead)))
      .Where(name => !string.IsNullOrWhiteSpace(name))
      .Select(name => name!)
      .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
      .ToList();
}
