using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

namespace Civil3DMcpPlugin;

/// <summary>
/// civil3d_style phase 2: label styles and label sets, plus abbreviation settings and label placement.
///
/// Families:
///   label:&lt;object&gt;/&lt;type&gt;   e.g. label:alignment/major_station, label:alignment/point_of_intersection,
///                              label:profile/curve, label:profile/grade_break, label:surface/spot_elevation,
///                              label:general_note (objects without sub-types take no /type)
///   label_set:alignment | label_set:profile | label_set:section
///
/// Label style spec (get emits it, create/edit accept it):
///   { "family": "label:alignment/major_station", "name": "UTNM Major Station",
///     "properties": { "label": { "layer": "C-ROAD-TEXT", "textStyle": "Standard", "visibility": true },
///                     "behavior": { "orientationReference": "Object", "insertOption": "Top" },
///                     "planReadability": { "planReadable": true, "planReadableBias": 110, "flipAnchorsWithText": false },
///                     "leader": { ... }, "draggedState": { ... } },
///     "components": [
///        { "name": "Tick", "type": "Tick", "tick": { "blockName": "AeccTickLine", "blockHeight": 1.0, "alignWithObject": true, "color": "ByLayer" } },
///        { "name": "Station", "type": "Text",
///          "general": { "anchorComponent": "<Feature>", "anchorPoint": "Middle", "visible": true },
///          "text": { "contents": "<[Station Value(Um|FD|P3|RN|AP|Sn|TP|B2|EN|W0|OF)]>", "height": 1.8, "attachment": "BottomCenter", "angle": 90, "xOffset": 0, "yOffset": 0.5 },
///          "border": { "visible": false } } ],
///     "removeComponents": ["Direction Arrow"] }
///
/// Every group property is a Civil 3D Property&lt;T&gt; wrapper; the value is read/written through .Value.
/// Angles are degrees in the spec (radians in the API). Sizes are paper millimetres in the spec
/// (metres in the API, see <see cref="PaperScale"/>).
///
/// Label set spec:
///   { "family": "label_set:alignment", "name": "UTNM Alignment Labels",
///     "items": [ { "style": "UTNM Major Station", "increment": 100 },
///                { "style": "UTNM Geometry Point", "geometryPoints": ["BegOfAlign", "EndOfAlign", "TanCurve", "CurveTan", "TanSpiral", "SpiralCurve", "CurveSpiral", "SpiralTan"] },
///                { "style": "UTNM IP Table" } ] }
///   { "family": "label_set:profile", "items": [ { "style": "UTNM Grade" }, { "style": "UTNM Vertical Curve", "curve": "crest" },
///                { "style": "UTNM Vertical Curve", "curve": "sag" }, { "style": "UTNM Profile End" } ] }
/// </summary>
public static class LabelStyleCommands
{
  private const string StylesNamespace = "Autodesk.Civil.DatabaseServices.Styles";

  /// <summary>Civil 3D stores label sizes (text height, offsets, tick height) in metres of paper; specs use millimetres.</summary>
  private const double PaperScale = 0.001;

  private static readonly HashSet<string> SizeProperties = new(StringComparer.OrdinalIgnoreCase)
  {
    "Height", "XOffset", "YOffset", "MaxWidth", "BlockHeight", "Length", "FixedLength", "StartPointXOffset", "StartPointYOffset",
    "EndPointXOffset", "EndPointYOffset", "Gap", "ArrowheadSize", "TextHeight", "MaxTextWidth", "LengthOrMinimumLength",
  };

  // ---------------------------------------------------------------------------------------------
  // Families
  // ---------------------------------------------------------------------------------------------

  internal static bool IsLabelFamily(string family) => family.StartsWith("label:", StringComparison.OrdinalIgnoreCase);
  internal static bool IsLabelSetFamily(string family) => family.StartsWith("label_set:", StringComparison.OrdinalIgnoreCase);
  internal static bool IsAnyLabelFamily(string family) => IsLabelFamily(family) || IsLabelSetFamily(family);

  private static string Norm(string s) => Regex.Replace(s ?? "", "[^A-Za-z0-9]", "").ToLowerInvariant();

  private static object? Prop(object? target, string name)
  {
    if (target == null) return null;
    var p = target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
      .FirstOrDefault(x => x.GetIndexParameters().Length == 0 && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
    return p?.GetValue(target);
  }

  private static IEnumerable<PropertyInfo> CollectionProps(object node) =>
    node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
      .Where(p => p.GetIndexParameters().Length == 0 && p.Name.EndsWith("LabelStyles", StringComparison.Ordinal));

  private static IEnumerable<PropertyInfo> LabelSetCollectionProps(object node) =>
    node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
      .Where(p => p.GetIndexParameters().Length == 0 && p.Name.EndsWith("LabelSetStyles", StringComparison.Ordinal));

  private static string FamilyKey(string propertyName, string suffix)
  {
    var stem = propertyName[..^suffix.Length];
    return Regex.Replace(stem, "(?<=[a-z0-9])(?=[A-Z])", "_").ToLowerInvariant();
  }

  /// <summary>Resolve a label / label_set family to its style collection.</summary>
  internal static object ResolveCollection(CivilDocument civilDoc, string family)
  {
    if (IsLabelSetFamily(family))
    {
      var objectKey = family["label_set:".Length..];
      var root = civilDoc.Styles.LabelSetStyles;
      var prop = LabelSetCollectionProps(root).FirstOrDefault(p => Norm(p.Name) == Norm(objectKey) + "labelsetstyles");
      if (prop == null)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"Unknown label set family '{family}'. Available: {string.Join(", ", LabelSetCollectionProps(root).Select(p => "label_set:" + FamilyKey(p.Name, "LabelSetStyles")))}.");
      return prop.GetValue(root) ?? throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{prop.Name} is not available.");
    }

    if (!IsLabelFamily(family))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{family}' is not a label family.");

    var rest = family["label:".Length..];
    var slash = rest.IndexOf('/');
    var objectPart = slash < 0 ? rest : rest[..slash];
    var typePart = slash < 0 ? null : rest[(slash + 1)..];

    var labelRoot = civilDoc.Styles.LabelStyles;
    var objectProp = CollectionProps(labelRoot).FirstOrDefault(p => Norm(p.Name) == Norm(objectPart) + "labelstyles");
    if (objectProp == null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"Unknown label object '{objectPart}'. Available: {string.Join(", ", CollectionProps(labelRoot).Select(p => FamilyKey(p.Name, "LabelStyles")))}.");
    var node = objectProp.GetValue(labelRoot) ?? throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{objectProp.Name} is not available.");

    if (node is LabelStyleCollection)
    {
      if (typePart != null)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'label:{objectPart}' has no sub-types; use it without '/{typePart}'.");
      return node;
    }

    var typeProps = CollectionProps(node).ToList();
    if (typePart == null)
    {
      // Nodes with a single unnamed collection (PointLabelStyles.LabelStyles, ViewFrame, SampleLine, Structure)
      var single = typeProps.FirstOrDefault(p => p.Name == "LabelStyles");
      if (single != null) return single.GetValue(node) ?? throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{objectProp.Name}.LabelStyles is not available.");
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"'label:{objectPart}' needs a type: {string.Join(", ", typeProps.Select(p => "label:" + objectPart + "/" + FamilyKey(p.Name, "LabelStyles")))}.");
    }
    var typeProp = typeProps.FirstOrDefault(p => Norm(p.Name) == Norm(typePart) + "labelstyles");
    if (typeProp == null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"Unknown label type '{typePart}' for '{objectPart}'. Available: {string.Join(", ", typeProps.Select(p => FamilyKey(p.Name, "LabelStyles")))}.");
    return typeProp.GetValue(node) ?? throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{typeProp.Name} is not available.");
  }

  /// <summary>Every label family in the drawing with its style count (for `list` without a family).</summary>
  internal static List<Dictionary<string, object?>> ListFamilies(CivilDocument civilDoc)
  {
    var result = new List<Dictionary<string, object?>>();
    var labelRoot = civilDoc.Styles.LabelStyles;
    foreach (var objectProp in CollectionProps(labelRoot))
    {
      object? node;
      try { node = objectProp.GetValue(labelRoot); } catch { continue; }
      if (node == null) continue;
      var objectKey = FamilyKey(objectProp.Name, "LabelStyles");
      if (node is LabelStyleCollection)
      {
        result.Add(new() { ["family"] = "label:" + objectKey, ["collection"] = objectProp.Name, ["count"] = SafeCount(node) });
        continue;
      }
      foreach (var typeProp in CollectionProps(node))
      {
        object? coll;
        try { coll = typeProp.GetValue(node); } catch { continue; }
        if (coll == null) continue;
        result.Add(new()
        {
          ["family"] = typeProp.Name == "LabelStyles" ? "label:" + objectKey : "label:" + objectKey + "/" + FamilyKey(typeProp.Name, "LabelStyles"),
          ["collection"] = objectProp.Name + "." + typeProp.Name,
          ["count"] = SafeCount(coll),
        });
      }
    }
    var setRoot = civilDoc.Styles.LabelSetStyles;
    foreach (var p in LabelSetCollectionProps(setRoot))
    {
      object? coll;
      try { coll = p.GetValue(setRoot); } catch { continue; }
      if (coll == null) continue;
      result.Add(new() { ["family"] = "label_set:" + FamilyKey(p.Name, "LabelSetStyles"), ["collection"] = p.Name, ["count"] = SafeCount(coll) });
    }
    return result;
  }

  private static int SafeCount(object collection)
  {
    try { return CivilObjectUtils.ToObjectIds(collection).Count(); } catch { return -1; }
  }

  /// <summary>Find a label style by name in any collection under the given object (alignment, profile ...) or anywhere.</summary>
  internal static ObjectId FindLabelStyle(CivilDocument civilDoc, Transaction transaction, string name, string? objectKey, string? typeKey)
  {
    var labelRoot = civilDoc.Styles.LabelStyles;
    var searched = new List<string>();
    foreach (var objectProp in CollectionProps(labelRoot))
    {
      if (objectKey != null && Norm(objectProp.Name) != Norm(objectKey) + "labelstyles") continue;
      object? node;
      try { node = objectProp.GetValue(labelRoot); } catch { continue; }
      if (node == null) continue;
      IEnumerable<(string key, object coll)> collections = node is LabelStyleCollection
        ? new[] { (objectProp.Name, node) }
        : CollectionProps(node).Select(p => (p.Name, p.GetValue(node)!)).Where(t => t.Item2 != null);
      foreach (var (key, coll) in collections)
      {
        if (typeKey != null && Norm(key) != Norm(typeKey) + "labelstyles") continue;
        searched.Add(key);
        foreach (var id in CivilObjectUtils.ToObjectIds(coll))
        {
          if (id.IsNull) continue;
          var obj = transaction.GetObject(id, OpenMode.ForRead);
          if (string.Equals(CivilObjectUtils.GetName(obj), name, StringComparison.OrdinalIgnoreCase)) return id;
          // Child styles (label style children) are reachable through GetDescendantIds
          if (obj is LabelStyle ls)
          {
            try
            {
              foreach (ObjectId childId in ls.GetDescendantIds())
              {
                var child = transaction.GetObject(childId, OpenMode.ForRead);
                if (string.Equals(CivilObjectUtils.GetName(child), name, StringComparison.OrdinalIgnoreCase)) return childId;
              }
            }
            catch { }
          }
        }
      }
    }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
      $"Label style '{name}' was not found{(objectKey != null ? " under " + objectKey : "")}{(typeKey != null ? "/" + typeKey : "")} (searched {searched.Count} collection(s)).");
  }

  // ---------------------------------------------------------------------------------------------
  // Property<T> wrappers
  // ---------------------------------------------------------------------------------------------

  internal static bool IsPropertyWrapper(Type type)
  {
    for (var t = type; t != null && t != typeof(object); t = t.BaseType)
    {
      if (t.Namespace == "Autodesk.Civil" && t.Name.StartsWith("Property", StringComparison.Ordinal)) return true;
    }
    return false;
  }

  /// <summary>The most-derived 'Value' property (PropertyEnum&lt;T&gt; hides the base int Value, so a plain GetProperty is ambiguous).</summary>
  internal static PropertyInfo? ValueProperty(Type type)
  {
    for (var cur = type; cur != null && cur != typeof(object); cur = cur.BaseType)
    {
      var p = cur.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
      if (p != null) return p;
    }
    return null;
  }

  private static PropertyInfo? ValueProperty(object wrapper) => ValueProperty(wrapper.GetType());

  private static bool IsAngle(object wrapper, string propertyName) =>
    wrapper.GetType().Name == "PropertyAngle"
    || propertyName.EndsWith("Angle", StringComparison.OrdinalIgnoreCase)
    || string.Equals(propertyName, "PlanReadableBias", StringComparison.OrdinalIgnoreCase);

  /// <summary>Enum behind an int-valued anchor wrapper, by property name (AnchorPoint / StartPointAnchorPoint → AnchorPointType, AnchorLocation → AnchorLocationType).</summary>
  private static Type? AnchorEnumFor(string propertyName) => null; // *AnchorPoint ints are native codes; write anchors through *AnchorLocation (typed enums)

  /// <summary>Read a Property&lt;T&gt; wrapper into a JSON-friendly value.</summary>
  internal static object? ReadWrapper(object wrapper, string propertyName, Transaction transaction, bool paperSizes, Func<ObjectId, string?>? idToName)
  {
    var vp = ValueProperty(wrapper);
    if (vp == null) return null;
    object? value;
    try { value = vp.GetValue(wrapper); } catch { return null; }
    if (value == null) return null;
    switch (value)
    {
      case ObjectId oid:
        return oid.IsNull ? null : (idToName?.Invoke(oid) ?? SafeName(transaction, oid));
      case Autodesk.AutoCAD.Colors.Color color:
        return color.IsByLayer ? "ByLayer" : color.IsByBlock ? "ByBlock" : color.IsByAci ? color.ColorIndex : $"{color.Red},{color.Green},{color.Blue}";
      case LineWeight lw:
        return StyleCommands.LineweightToSpecPublic(lw);
      case Enum e:
        return e.ToString();
      case int n when AnchorEnumFor(propertyName) is Type enumType && Enum.IsDefined(enumType, n):
        return Enum.GetName(enumType, n);
      case double d:
        if (IsAngle(wrapper, propertyName)) return Math.Round(d * 180.0 / Math.PI, 6);
        if (paperSizes && SizeProperties.Contains(propertyName)) return Math.Round(d / PaperScale, 6);
        return d;
      case bool or int or string or short or long or float:
        return value;
      default:
        return value.ToString();
    }
  }

  /// <summary>Write a JSON value into a Property&lt;T&gt; wrapper.</summary>
  internal static void WriteWrapper(object wrapper, string propertyName, JsonNode? node, Func<string, ObjectId>? nameToId, bool paperSizes, string path)
  {
    var vp = ValueProperty(wrapper) ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{path}: not writable.");
    var vt = Nullable.GetUnderlyingType(vp.PropertyType) ?? vp.PropertyType;
    object? converted;
    try
    {
      if (vt == typeof(ObjectId))
      {
        var text = node?.GetValue<string?>();
        converted = string.IsNullOrWhiteSpace(text) || text == "<Feature>" || text == "<None>"
          ? ObjectId.Null
          : (nameToId ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{path}: cannot resolve '{text}'."))(text);
      }
      else if (vt == typeof(Autodesk.AutoCAD.Colors.Color)) converted = StyleCommands.ParseColorPublic(node);
      else if (vt == typeof(LineWeight)) converted = StyleCommands.ParseLineweightPublic(node);
      else if (vt.IsEnum)
      {
        var text = node?.GetValue<string>() ?? "";
        try { converted = Enum.Parse(vt, text, ignoreCase: true); }
        catch { throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{path}: '{text}' is not a {vt.Name}. Values: {string.Join(", ", Enum.GetNames(vt))}."); }
      }
      else if (vt == typeof(bool)) converted = node!.GetValue<bool>();
      else if (vt == typeof(int))
      {
        // Some wrappers (PropertyAnchorPoint ...) expose an int whose meaning is an Autodesk.Civil enum.
        var enumType = AnchorEnumFor(propertyName);
        if (enumType != null && node is JsonValue jv && jv.TryGetValue<string>(out var enumText))
        {
          try { converted = (int)Enum.Parse(enumType, enumText, ignoreCase: true); }
          catch { throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{path}: '{enumText}' is not a {enumType.Name}. Values: {string.Join(", ", Enum.GetNames(enumType))}."); }
        }
        else converted = node!.GetValue<int>();
      }
      else if (vt == typeof(uint)) converted = (uint)node!.GetValue<int>();
      else if (vt == typeof(double))
      {
        var d = node!.GetValue<double>();
        if (IsAngle(wrapper, propertyName)) d = d * Math.PI / 180.0;
        else if (paperSizes && SizeProperties.Contains(propertyName)) d *= PaperScale;
        converted = d;
      }
      else if (vt == typeof(string))
      {
        var text = node?.GetValue<string?>() ?? "";
        converted = text is "<Feature>" or "<None>" ? "" : text;
      }
      else throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{path}: unsupported value type {vt.Name}.");
    }
    catch (JsonRpcDispatchException) { throw; }
    catch (Exception ex)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{path}: cannot convert '{node?.ToJsonString()}' to {vt.Name}: {ex.Message}");
    }

    try { vp.SetValue(wrapper, converted); }
    catch (Exception ex)
    {
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{path}: Civil 3D rejected {node?.ToJsonString()}: {Civil3DCompatibility.DescribeException(ex)}");
    }
  }

  private static string? SafeName(Transaction transaction, ObjectId id)
  {
    try { return CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead)); } catch { return id.ToString(); }
  }

  // ---------------------------------------------------------------------------------------------
  // Label style: read
  // ---------------------------------------------------------------------------------------------

  private static readonly string[] PropertyGroups = { "Label", "Behavior", "PlanReadability", "Leader", "DraggedStateComponents" };

  private static string GroupKey(string apiName) => apiName switch
  {
    "DraggedStateComponents" => "draggedState",
    _ => char.ToLowerInvariant(apiName[0]) + apiName[1..],
  };

  internal static void ReadLabelStyle(LabelStyle style, Dictionary<string, object?> spec, Transaction transaction)
  {
    spec.Remove("properties");
    spec.Remove("settings");
    spec.Remove("references");

    // Property groups
    var props = new Dictionary<string, object?>();
    var labelProps = Prop(style, "Properties");
    if (labelProps != null)
    {
      foreach (var groupName in PropertyGroups)
      {
        var group = Prop(labelProps, groupName);
        if (group == null) continue;
        props[GroupKey(groupName)] = ReadGroup(group, transaction, null);
      }
    }
    spec["properties"] = props;

    // Components in draw order
    var componentNames = new Dictionary<ObjectId, string>();
    var components = new List<Dictionary<string, object?>>();
    foreach (var componentId in ComponentIds(style))
    {
      var component = transaction.GetObject(componentId, OpenMode.ForRead);
      componentNames[componentId] = CivilObjectUtils.GetName(component) ?? componentId.ToString();
    }
    foreach (var componentId in ComponentIds(style))
    {
      var component = transaction.GetObject(componentId, OpenMode.ForRead);
      var entry = new Dictionary<string, object?>
      {
        ["name"] = componentNames[componentId],
        ["type"] = ComponentTypeName(component),
      };
      foreach (var group in component.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
      {
        if (group.GetIndexParameters().Length != 0) continue;
        if (!group.PropertyType.IsClass || group.PropertyType.Namespace != StylesNamespace) continue;
        object? g;
        try { g = group.GetValue(component); } catch { continue; }
        if (g == null) continue;
        entry[char.ToLowerInvariant(group.Name[0]) + group.Name[1..]] = ReadGroup(g, transaction, id => componentNames.TryGetValue(id, out var n) ? n : null);
      }
      components.Add(entry);
    }
    spec["components"] = components;

    try
    {
      var children = new List<string?>();
      foreach (ObjectId childId in style.GetDescendantIds()) children.Add(SafeName(transaction, childId));
      if (children.Count > 0) spec["children"] = children;
    }
    catch { }
    spec["units"] = "sizes in paper mm, angles in degrees";
  }

  private static Dictionary<string, object?> ReadGroup(object group, Transaction transaction, Func<ObjectId, string?>? idToName)
  {
    var result = new Dictionary<string, object?>();
    foreach (var p in group.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
    {
      if (p.GetIndexParameters().Length != 0 || !p.CanRead) continue;
      object? v;
      try { v = p.GetValue(group); } catch { continue; }
      if (v == null) continue;
      var key = char.ToLowerInvariant(p.Name[0]) + p.Name[1..];
      if (IsPropertyWrapper(v.GetType()))
      {
        var read = ReadWrapper(v, p.Name, transaction, paperSizes: true, idToName);
        var vt = ValueProperty(v.GetType())?.PropertyType;
        if (vt == typeof(ObjectId) && read == null) read = "<Feature>";
        if (vt == typeof(string) && p.Name.EndsWith("AnchorComponent", StringComparison.OrdinalIgnoreCase) && read is "" ) read = "<Feature>";
        result[key] = read;
      }
      else if (v is Enum e) result[key] = e.ToString();
      else if (v is bool or int or double or string) result[key] = v;
    }
    return result;
  }

  private static IEnumerable<ObjectId> ComponentIds(LabelStyle style)
  {
    var ids = new List<ObjectId>();
    try
    {
      var order = style.GetComponentsDrawOrder();
      if (order != null) foreach (ObjectId id in order) if (!id.IsNull) ids.Add(id);
    }
    catch { }
    if (ids.Count > 0) return ids;
    foreach (LabelStyleComponentType t in Enum.GetValues(typeof(LabelStyleComponentType)))
    {
      try
      {
        var coll = style.GetComponents(t);
        if (coll != null) foreach (ObjectId id in coll) if (!id.IsNull && !ids.Contains(id)) ids.Add(id);
      }
      catch { }
    }
    return ids;
  }

  private static string ComponentTypeName(AcDbObject component) => component switch
  {
    LabelStyleTextForEachComponent => "TextForEach",
    LabelStyleReferenceTextComponent => "ReferenceText",
    LabelStyleTextComponent => "Text",
    LabelStyleLineComponent => "Line",
    LabelStyleBlockComponent => "Block",
    LabelStyleTickComponent => "Tick",
    LabelStyleDirectionArrowComponent => "DirectionArrow",
    _ => component.GetType().Name,
  };

  // ---------------------------------------------------------------------------------------------
  // Label style: apply
  // ---------------------------------------------------------------------------------------------

  internal static void ApplyLabelStyle(LabelStyle style, JsonObject spec, CivilDocument civilDoc, Transaction transaction,
    List<string> applied, List<string> warnings)
  {
    // 1. property groups
    if (spec["properties"] is JsonObject groups)
    {
      var labelProps = Prop(style, "Properties") ?? throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", "LabelStyle.Properties is not available.");
      foreach (var kv in groups)
      {
        if (kv.Value is not JsonObject groupSpec) continue;
        var apiGroup = PropertyGroups.FirstOrDefault(g => Norm(GroupKey(g)) == Norm(kv.Key) || Norm(g) == Norm(kv.Key))
          ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"properties.{kv.Key}: unknown group. Use label, behavior, planReadability, leader, draggedState.");
        var group = Prop(labelProps, apiGroup) ?? throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"properties.{kv.Key} is not available.");
        ApplyGroup(group, groupSpec, civilDoc, transaction, null, applied, $"properties.{kv.Key}");
      }
    }

    // 2. remove components
    if (spec["removeComponents"] is JsonArray removals)
    {
      foreach (var node in removals)
      {
        var cname = node?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(cname)) continue;
        try { style.RemoveComponent(cname); applied.Add($"removeComponents.{cname}"); }
        catch (Exception ex) { warnings.Add($"removeComponents.{cname}: {Civil3DCompatibility.DescribeException(ex)}"); }
      }
    }

    // 3. components (created when missing, in the given order so anchors can reference earlier ones)
    if (spec["components"] is JsonArray components)
    {
      // A brand-new label style carries Civil 3D's default components; drop the ones the spec doesn't name
      // (replaceComponents: true, the default for create; edit keeps them unless asked).
      if (spec["replaceComponents"]?.GetValue<bool>() ?? false)
      {
        var keep = components.OfType<JsonObject>().Select(c => c["name"]?.GetValue<string>() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var existingId in ComponentIds(style).ToList())
        {
          var existingName = CivilObjectUtils.GetName(transaction.GetObject(existingId, OpenMode.ForRead)) ?? "";
          if (keep.Contains(existingName)) continue;
          try { style.RemoveComponent(existingName); applied.Add($"components: removed default '{existingName}'"); }
          catch { /* some defaults (e.g. 'Table Tag' on segment labels) are fixed by Civil 3D; leave them */ }
        }
      }
      var index = 0;
      foreach (var node in components)
      {
        var path = $"components[{index++}]";
        if (node is not JsonObject componentSpec) continue;
        var name = componentSpec["name"]?.GetValue<string>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{path}: name is required.");
        var typeText = componentSpec["type"]?.GetValue<string>();
        var componentId = FindComponent(style, transaction, name);
        if (componentId.IsNull)
        {
          if (string.IsNullOrWhiteSpace(typeText))
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{path}: component '{name}' does not exist; give type (Text, Line, Block, Tick, ReferenceText, DirectionArrow, TextForEach) to create it.");
          componentId = AddComponent(style, name, typeText, componentSpec, path);
          applied.Add($"{path}.{name} (created {typeText})");
        }
        var component = transaction.GetObject(componentId, OpenMode.ForWrite);

        foreach (var kv in componentSpec)
        {
          if (kv.Key is "name" or "type" or "selectedType") continue;
          if (kv.Value is not JsonObject groupSpec)
          {
            warnings.Add($"{path}.{kv.Key}: expected an object (general/text/border/line/tick/block/directionArrow).");
            continue;
          }
          var groupProp = component.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.GetIndexParameters().Length == 0 && Norm(p.Name) == Norm(kv.Key));
          if (groupProp == null)
          {
            var available = component.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
              .Where(p => p.PropertyType.IsClass && p.PropertyType.Namespace == StylesNamespace).Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..]);
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{path}.{kv.Key}: {ComponentTypeName(component)} components have {string.Join(", ", available)}.");
          }
          var group = groupProp.GetValue(component) ?? throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{path}.{kv.Key} is not available.");
          ApplyGroup(group, groupSpec, civilDoc, transaction, style, applied, $"{path}.{kv.Key}");
        }
      }
    }
  }

  private static ObjectId FindComponent(LabelStyle style, Transaction transaction, string name)
  {
    foreach (var id in ComponentIds(style))
    {
      var c = transaction.GetObject(id, OpenMode.ForRead);
      if (string.Equals(CivilObjectUtils.GetName(c), name, StringComparison.OrdinalIgnoreCase)) return id;
    }
    return ObjectId.Null;
  }

  private static ObjectId AddComponent(LabelStyle style, string name, string typeText, JsonObject componentSpec, string path)
  {
    string? error;
    object? result;
    var norm = Norm(typeText);
    if (norm == "referencetext")
    {
      var selected = componentSpec["selectedType"]?.GetValue<string>() ?? "Alignment";
      result = Civil3DCompatibility.InvokeMatchingOverload(style, null, "AddReferenceTextComponent", new object?[] { name, selected }, out error);
    }
    else if (norm == "textforeach")
    {
      var selected = componentSpec["selectedType"]?.GetValue<string>() ?? "CurveOrSpiral";
      result = Civil3DCompatibility.InvokeMatchingOverload(style, null, "AddTextForEachComponent", new object?[] { name, selected }, out error);
    }
    else
    {
      if (!Enum.TryParse<LabelStyleComponentType>(typeText, ignoreCase: true, out var ct))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{path}: type '{typeText}' is not one of {string.Join(", ", Enum.GetNames(typeof(LabelStyleComponentType)))}.");
      try { result = style.AddComponent(name, ct); error = null; }
      catch (Exception ex) { result = null; error = Civil3DCompatibility.DescribeException(ex); }
    }
    if (error != null) throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{path}: Civil 3D could not add {typeText} component '{name}': {error}");
    if (result is ObjectId id && !id.IsNull) return id;
    throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{path}: AddComponent returned no ObjectId.");
  }

  private static void ApplyGroup(object group, JsonObject groupSpec, CivilDocument civilDoc, Transaction transaction, LabelStyle? owner,
    List<string> applied, string path)
  {
    foreach (var kv in groupSpec)
    {
      var prop = group.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .FirstOrDefault(p => p.GetIndexParameters().Length == 0 && Norm(p.Name) == Norm(kv.Key));
      if (prop == null)
      {
        var available = group.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
          .Where(p => p.GetIndexParameters().Length == 0).Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..]).OrderBy(n => n);
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{path}: no property '{kv.Key}'. Available: {string.Join(", ", available)}.");
      }
      var wrapper = prop.GetValue(group);
      if (wrapper != null && IsPropertyWrapper(wrapper.GetType()))
      {
        Func<string, ObjectId> resolver = text =>
        {
          // anchor components are other components of the same label style; anything else is a style name
          if (owner != null)
          {
            var cid = FindComponent(owner, transaction, text);
            if (!cid.IsNull) return cid;
          }
          return StyleCommands.ResolveAnyStyle(civilDoc, transaction, prop.Name, text);
        };
        WriteWrapper(wrapper, prop.Name, kv.Value, resolver, paperSizes: true, $"{path}.{kv.Key}");
        applied.Add($"{path}.{kv.Key}");
      }
      else if (prop.CanWrite)
      {
        if (!Civil3DCompatibility.TrySetPropertyValue(group, prop.Name, ConvertPlain(prop.PropertyType, kv.Value), out var error))
          throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{path}.{kv.Key}: {error}");
        applied.Add($"{path}.{kv.Key}");
      }
      else throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{path}.{kv.Key} is read-only.");
    }
  }

  private static object? ConvertPlain(Type type, JsonNode? node)
  {
    var t = Nullable.GetUnderlyingType(type) ?? type;
    if (node == null) return null;
    if (t.IsEnum) return node.GetValue<string>();
    if (t == typeof(bool)) return node.GetValue<bool>();
    if (t == typeof(int)) return node.GetValue<int>();
    if (t == typeof(double)) return node.GetValue<double>();
    if (t == typeof(string)) return node.GetValue<string>();
    return node.GetValue<string>();
  }

  // ---------------------------------------------------------------------------------------------
  // Label sets
  // ---------------------------------------------------------------------------------------------

  internal static void ReadLabelSet(BaseLabelSetStyle set, Dictionary<string, object?> spec, Transaction transaction)
  {
    spec.Remove("properties");
    spec.Remove("settings");
    spec.Remove("references");
    var items = new List<Dictionary<string, object?>>();
    var index = 0;
    foreach (var item in EnumerateItems(set))
    {
      if (item == null) continue;
      var entry = new Dictionary<string, object?> { ["index"] = index++ };
      entry["style"] = CivilObjectUtils.GetStringProperty(item, "LabelStyleName");
      entry["type"] = Civil3DCompatibility.GetPropertyValue(item, "LabelStyleType")?.ToString();
      foreach (var p in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
      {
        if (p.GetIndexParameters().Length != 0 || p.Name is "LabelStyleId" or "LabelStyleName" or "LabelStyleType") continue;
        object? v;
        try { v = p.GetValue(item); } catch { continue; }
        if (v == null) continue;
        var key = char.ToLowerInvariant(p.Name[0]) + p.Name[1..];
        if (IsPropertyWrapper(v.GetType())) entry[key] = ReadWrapper(v, p.Name, transaction, paperSizes: false, null);
        else if (v is Enum e) entry[key] = e.ToString();
        else if (v is bool or int or double or string) entry[key] = v;
      }
      foreach (var (method, key) in new[] { ("GetLabeledAlignmentGeometryPoints", "geometryPoints"), ("GetLabeledProfileGeometryPoints", "profileGeometryPoints"), ("GetLabeledSuperelevationTransitionPoints", "superelevationPoints") })
      {
        var selected = ReadSelector(item, method);
        if (selected != null) entry[key] = selected;
      }
      items.Add(entry);
    }
    spec["items"] = items;
  }

  private static IEnumerable<object?> EnumerateItems(BaseLabelSetStyle set)
  {
    if (set is IEnumerable enumerable)
    {
      var list = new List<object?>();
      try { foreach (var item in enumerable) list.Add(item); return list; }
      catch { }
    }
    var count = Civil3DCompatibility.GetPropertyValue(set, "Count") is int c ? c : 0;
    var byIndex = new List<object?>();
    for (var i = 0; i < count; i++)
    {
      try { byIndex.Add(Civil3DCompatibility.GetIndexedPropertyValue(set, "Item", i)); } catch { }
    }
    return byIndex;
  }

  private static List<string>? ReadSelector(object item, string getter)
  {
    var m = item.GetType().GetMethod(getter, BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
    if (m == null) return null;
    object? selector;
    try { selector = m.Invoke(item, null); } catch { return null; }
    if (selector == null) return null;
    var indexer = selector.GetType().GetProperties().FirstOrDefault(p => p.GetIndexParameters().Length == 1 && p.GetIndexParameters()[0].ParameterType.IsEnum);
    if (indexer == null) return null;
    var enumType = indexer.GetIndexParameters()[0].ParameterType;
    var result = new List<string>();
    foreach (var value in Enum.GetValues(enumType))
    {
      object? option;
      try { option = indexer.GetValue(selector, new[] { value }); } catch { continue; }
      if (option == null) continue;
      object? selected = option is bool ? option : Civil3DCompatibility.GetPropertyValue(option, "Selected");
      if (selected != null && IsPropertyWrapper(selected.GetType())) selected = ReadWrapperOrValue(selected);
      if (selected is bool b && b) result.Add(value.ToString()!);
    }
    return result;
  }

  private static void WriteSelector(object item, string getter, string setter, JsonArray names, string path)
  {
    var gm = item.GetType().GetMethod(getter, BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{path}: this item type has no {getter}.");
    var selector = gm.Invoke(item, null) ?? throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{path}: {getter} returned null.");
    var indexer = selector.GetType().GetProperties().FirstOrDefault(p => p.GetIndexParameters().Length == 1 && p.GetIndexParameters()[0].ParameterType.IsEnum)
      ?? throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{path}: selector has no enum indexer.");
    var enumType = indexer.GetIndexParameters()[0].ParameterType;
    var wanted = new HashSet<string>(names.Select(n => Norm(n?.GetValue<string>() ?? "")));
    var known = Enum.GetNames(enumType).Select(Norm).ToHashSet();
    var unknown = wanted.Where(w => !known.Contains(w)).ToList();
    if (unknown.Count > 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{path}: unknown point type(s) {string.Join(", ", unknown)}. Values: {string.Join(", ", Enum.GetNames(enumType))}.");
    foreach (var value in Enum.GetValues(enumType))
    {
      object? option;
      try { option = indexer.GetValue(selector, new[] { value }); } catch { continue; }
      if (option == null) continue;
      var want = wanted.Contains(Norm(value.ToString()!));
      if (option is bool)
      {
        // GeometryPointSelector<T>.this[T] is the selection flag itself
        try { indexer.SetValue(selector, want, new[] { value }); continue; }
        catch (Exception ex) { throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{path}: cannot set {value}: {Civil3DCompatibility.DescribeException(ex)}"); }
      }
      var current = Civil3DCompatibility.GetPropertyValue(option, "Selected");
      if (current != null && IsPropertyWrapper(current.GetType()))
      {
        ValueProperty(current.GetType())?.SetValue(current, want);
        continue;
      }
      if (Civil3DCompatibility.TrySetPropertyValue(option, "Selected", want, out var selError)) continue;
      // C++/CLI setters sometimes only show up as methods
      var selectedSetter = option.GetType().GetMethod("set_Selected", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
      if (selectedSetter != null)
      {
        try { selectedSetter.Invoke(option, new object[] { want }); continue; }
        catch (Exception ex) { selError = Civil3DCompatibility.DescribeException(ex); }
      }
      var members = string.Join(", ", option.GetType().GetMembers(BindingFlags.Public | BindingFlags.Instance).Select(m => m.Name).Distinct());
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{path}: cannot set {value} on {option.GetType().FullName}: {selError}. Members: {members}");
    }
    var sm = item.GetType().GetMethod(setter, BindingFlags.Public | BindingFlags.Instance)
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{path}: this item type has no {setter}.");
    try { sm.Invoke(item, new[] { selector }); }
    catch (Exception ex) { throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{path}: {setter} failed: {Civil3DCompatibility.DescribeException(ex)}"); }
  }

  internal static void ApplyLabelSet(BaseLabelSetStyle set, string family, JsonObject spec, CivilDocument civilDoc, Transaction transaction,
    List<string> applied, List<string> warnings)
  {
    if (spec["items"] is not JsonArray items) return;
    var objectKey = family["label_set:".Length..];
    var replace = spec["replaceItems"]?.GetValue<bool>() ?? true;

    if (replace)
    {
      var count = Civil3DCompatibility.GetPropertyValue(set, "Count") is int c ? c : 0;
      for (var i = count - 1; i >= 0; i--)
      {
        try { set.RemoveAt(i); } catch (Exception ex) { warnings.Add($"items: could not remove existing item {i}: {Civil3DCompatibility.DescribeException(ex)}"); }
      }
    }

    var index = 0;
    foreach (var node in items)
    {
      var path = $"items[{index++}]";
      if (node is not JsonObject itemSpec) continue;
      var styleName = itemSpec["style"]?.GetValue<string>() ?? itemSpec["labelStyle"]?.GetValue<string>()
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{path}: style is required.");
      var typeKey = itemSpec["labelType"]?.GetValue<string>();
      var styleId = FindLabelStyle(civilDoc, transaction, styleName, objectKey, typeKey);

      var curve = itemSpec["curve"]?.GetValue<string>()?.ToLowerInvariant();
      string? error;
      object? result;
      if (set is ProfileLabelSetStyle && curve is "crest" or "sag")
      {
        result = Civil3DCompatibility.InvokeMatchingOverload(set, null, curve == "crest" ? "AddCrestCurve" : "AddSagCurve", new object?[] { styleId }, out error);
      }
      else
      {
        result = Civil3DCompatibility.InvokeMatchingOverload(set, null, "Add", new object?[] { styleId }, out error);
      }
      if (error != null) throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{path}: Civil 3D could not add '{styleName}': {error}");

      // Find the item just added: Add returns the index (int) on most versions; otherwise take the last one.
      var count = Civil3DCompatibility.GetPropertyValue(set, "Count") is int c2 ? c2 : 0;
      var itemIndex = result is int ri && ri >= 0 && ri < count ? ri : count - 1;
      var item = Civil3DCompatibility.GetIndexedPropertyValue(set, "Item", itemIndex)
        ?? throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{path}: item {itemIndex} not readable after Add.");
      applied.Add($"{path}.style={styleName}");

      foreach (var kv in itemSpec)
      {
        if (kv.Key is "style" or "labelStyle" or "labelType" or "curve" or "type" or "index") continue;
        switch (kv.Key)
        {
          case "geometryPoints":
            WriteSelector(item, "GetLabeledAlignmentGeometryPoints", "SetLabeledAlignmentGeometryPoints", kv.Value as JsonArray ?? new JsonArray(), $"{path}.geometryPoints");
            applied.Add($"{path}.geometryPoints");
            continue;
          case "profileGeometryPoints":
            WriteSelector(item, "GetLabeledProfileGeometryPoints", "SetLabeledProfileGeometryPoints", kv.Value as JsonArray ?? new JsonArray(), $"{path}.profileGeometryPoints");
            applied.Add($"{path}.profileGeometryPoints");
            continue;
          case "superelevationPoints":
            WriteSelector(item, "GetLabeledSuperelevationTransitionPoints", "SetLabeledSuperelevationTransitionPoints", kv.Value as JsonArray ?? new JsonArray(), $"{path}.superelevationPoints");
            applied.Add($"{path}.superelevationPoints");
            continue;
        }
        var prop = item.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
          .FirstOrDefault(p => p.GetIndexParameters().Length == 0 && Norm(p.Name) == Norm(kv.Key));
        if (prop == null)
        {
          var available = item.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite).Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..]);
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{path}.{kv.Key}: unknown item property. Available: {string.Join(", ", available)}, geometryPoints, profileGeometryPoints.");
        }
        var current = prop.GetValue(item);
        if (current != null && IsPropertyWrapper(current.GetType()))
          WriteWrapper(current, prop.Name, kv.Value, null, paperSizes: false, $"{path}.{kv.Key}");
        else if (!Civil3DCompatibility.TrySetPropertyValue(item, prop.Name, ConvertPlain(prop.PropertyType, kv.Value), out var setError))
          throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{path}.{kv.Key}: {setError}");
        applied.Add($"{path}.{kv.Key}");
      }
    }
  }

  /// <summary>
  /// Apply a label set to an alignment (Alignment.ImportLabelSet) or a profile (one label group per set item in every
  /// profile view that shows the profile — the API has no Profile.ImportLabelSet).
  /// </summary>
  private static readonly (Type type, string labelType)[] ProfileGroupTypes =
  {
    (typeof(ProfileLineLabelGroup), "ProfileLine"),
    (typeof(ProfileCrestCurveLabelGroup), "ProfileCrestCurve"),
    (typeof(ProfileSagCurveLabelGroup), "ProfileSagCurve"),
    (typeof(ProfilePVILabelGroup), "ProfileGradeBreaks"),
    (typeof(ProfileHorizontalGeometryPointLabelGroup), "ProfileHorizontalGeometryPoint"),
    (typeof(ProfileStationLabelGroup), "ProfileMajorStation"),
    (typeof(ProfileMinorStationLabelGroup), "ProfileMinorStation"),
  };

  /// <summary>Label groups of one class that belong to a profile in a profile view (API lookup, then a model-space scan).</summary>
  private static List<ObjectId> FindProfileLabelGroups(Type groupType, ProfileView pv, Profile profile, BlockTableRecord modelSpace, Transaction transaction, List<string>? diagnostics = null)
  {
    var ids = new List<ObjectId>();
    try
    {
      var groupIds = Civil3DCompatibility.InvokeMatchingOverload(null, groupType, "GetAvailableLabelGroupIds", new object?[] { pv.ObjectId, profile.ObjectId }, out var err);
      if (err != null && err.Contains("includeDerived", StringComparison.OrdinalIgnoreCase))
        groupIds = Civil3DCompatibility.InvokeMatchingOverload(null, groupType, "GetAvailableLabelGroupIds", new object?[] { pv.ObjectId, profile.ObjectId, true }, out err);
      if (err != null) diagnostics?.Add($"{groupType.Name}: GetAvailableLabelGroupIds: {err}");
      else ids.AddRange(CivilObjectUtils.ToObjectIds(groupIds));
    }
    catch (Exception ex) { diagnostics?.Add($"{groupType.Name}: {Civil3DCompatibility.DescribeException(ex)}"); }
    if (ids.Count == 0)
    {
      var rx = Autodesk.AutoCAD.Runtime.RXObject.GetClass(groupType);
      foreach (ObjectId id in modelSpace)
      {
        if (id.IsErased || id.ObjectClass?.IsDerivedFrom(rx) != true) continue;
        try
        {
          var lg = transaction.GetObject(id, OpenMode.ForRead) as LabelGroup;
          if (lg == null) continue;
          var featureId = Civil3DCompatibility.GetPropertyValue(lg, "FeatureId") is ObjectId f ? f : ObjectId.Null;
          var viewId = Civil3DCompatibility.GetPropertyValue(lg, "ViewId") is ObjectId v ? v : ObjectId.Null;
          if ((featureId.IsNull || featureId == profile.ObjectId) && (viewId.IsNull || viewId == pv.ObjectId)) ids.Add(id);
        }
        catch { }
      }
      if (ids.Count > 0) diagnostics?.Add($"{groupType.Name}: model-space scan found {ids.Count} group(s)");
    }
    return ids.Where(id => !id.IsErased).Distinct().ToList();
  }

  internal static Dictionary<string, object?> ApplyLabelSetTo(AcDbObject target, ObjectId labelSetId, Database database, Transaction transaction, bool replaceGroups = true, double dimensionAnchorScale = 1.0)
  {
    var result = new Dictionary<string, object?> { ["type"] = target.GetType().Name, ["name"] = CivilObjectUtils.GetName(target), ["handle"] = CivilObjectUtils.GetHandle(target) };
    if (target is Alignment)
    {
      Civil3DCompatibility.InvokeMatchingOverload(target, null, "ImportLabelSet", new object?[] { labelSetId }, out var error);
      if (error != null) throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"ImportLabelSet failed on alignment '{CivilObjectUtils.GetName(target)}': {error}");
      result["imported"] = true;
      return result;
    }
    if (target is Profile profile)
    {
      var set = transaction.GetObject(labelSetId, OpenMode.ForRead) as BaseLabelSetStyle
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "The style is not a label set.");
      var views = new List<ProfileView>();
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
      foreach (ObjectId id in modelSpace)
      {
        if (id.ObjectClass?.IsDerivedFrom(Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(ProfileView))) != true) continue;
        if (transaction.GetObject(id, OpenMode.ForRead) is ProfileView pv && pv.AlignmentId == profile.AlignmentId) views.Add(pv);
      }
      if (views.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No profile view shows alignment '{SafeName(transaction, profile.AlignmentId)}'. Create the profile view first; profile labels live in views.");

      var groups = new List<Dictionary<string, object?>>();
      var errors = new List<string>();
      var removed = new List<string>();
      foreach (var pv in views)
      {
        ObjectId majorGroupId = ObjectId.Null;
        var items = EnumerateItems(set).Where(i => i != null).ToList();
        if (replaceGroups)
        {
          // Re-applying a set must not stack a second group of the same type on the first one.
          var typesInSet = items.Select(i => Civil3DCompatibility.GetPropertyValue(i!, "LabelStyleType")?.ToString() ?? "").ToHashSet();
          foreach (var (groupType, labelType) in ProfileGroupTypes)
          {
            if (!typesInSet.Contains(labelType)) continue;
            foreach (var gid in FindProfileLabelGroups(groupType, pv, profile, modelSpace, transaction))
            {
              try
              {
                var old = transaction.GetObject(gid, OpenMode.ForWrite);
                old.Erase();
                removed.Add($"{pv.Name}/{labelType}:{gid.Handle}");
              }
              catch (Exception ex) { errors.Add($"{pv.Name} / {labelType}: could not remove group {gid.Handle}: {Civil3DCompatibility.DescribeException(ex)}"); }
            }
          }
        }
        foreach (var item in items)
        {
          if (item == null) continue;
          var styleId = Civil3DCompatibility.GetPropertyValue(item, "LabelStyleId") is ObjectId sid ? sid : ObjectId.Null;
          var typeText = Civil3DCompatibility.GetPropertyValue(item, "LabelStyleType")?.ToString() ?? "";
          var increment = Civil3DCompatibility.GetPropertyValue(item, "Increment") is object inc ? ReadWrapperOrValue(inc) : null;
          if (styleId.IsNull) continue;
          object? gid = null;
          string? error = null;
          try
          {
            switch (typeText)
            {
              case "ProfileLine": gid = Civil3DCompatibility.InvokeMatchingOverload(null, typeof(ProfileLineLabelGroup), "Create", new object?[] { pv.ObjectId, profile.ObjectId, styleId }, out error); break;
              case "ProfileCrestCurve": gid = Civil3DCompatibility.InvokeMatchingOverload(null, typeof(ProfileCrestCurveLabelGroup), "Create", new object?[] { pv.ObjectId, profile.ObjectId, styleId }, out error); break;
              case "ProfileSagCurve": gid = Civil3DCompatibility.InvokeMatchingOverload(null, typeof(ProfileSagCurveLabelGroup), "Create", new object?[] { pv.ObjectId, profile.ObjectId, styleId }, out error); break;
              case "ProfileGradeBreaks": gid = Civil3DCompatibility.InvokeMatchingOverload(null, typeof(ProfilePVILabelGroup), "Create", new object?[] { pv.ObjectId, profile.ObjectId, styleId }, out error); break;
              case "ProfileHorizontalGeometryPoint": gid = Civil3DCompatibility.InvokeMatchingOverload(null, typeof(ProfileHorizontalGeometryPointLabelGroup), "Create", new object?[] { pv.ObjectId, profile.ObjectId, styleId }, out error); break;
              case "ProfileMajorStation":
                gid = Civil3DCompatibility.InvokeMatchingOverload(null, typeof(ProfileStationLabelGroup), "CreateMajor", new object?[] { pv.ObjectId, profile.ObjectId, styleId, increment ?? 100.0 }, out error);
                if (gid is ObjectId mg) majorGroupId = mg;
                break;
              case "ProfileMinorStation":
                if (majorGroupId.IsNull) { error = "minor stations need a major station item before them"; break; }
                gid = Civil3DCompatibility.InvokeMatchingOverload(null, typeof(ProfileMinorStationLabelGroup), "Create", new object?[] { styleId, majorGroupId, increment ?? 20.0 }, out error);
                break;
              default: error = $"label type {typeText} is not placed by this command"; break;
            }
          }
          catch (Exception ex) { error = Civil3DCompatibility.DescribeException(ex); }
          if (error != null) { errors.Add($"{pv.Name} / {typeText}: {error}"); continue; }
          var groupEntry = new Dictionary<string, object?> { ["view"] = pv.Name, ["type"] = typeText, ["style"] = SafeName(transaction, styleId), ["group"] = gid is ObjectId g ? g.Handle.ToString() : null };
          // The set item's dimension anchor / weeding / stagger are not carried by Create(); copy them onto the group.
          if (gid is ObjectId groupId && !groupId.IsNull)
          {
            try
            {
              var group = transaction.GetObject(groupId, OpenMode.ForWrite);
              var anchorOption = ReadWrapperOrValue(Civil3DCompatibility.GetPropertyValue(item, "DimensionAnchorOption") ?? "")?.ToString();
              var anchorValue = ReadWrapperOrValue(Civil3DCompatibility.GetPropertyValue(item, "DimensionAnchorValue") ?? 0.0);
              var applied = new List<string>();
              if (!string.IsNullOrEmpty(anchorOption))
              {
                // item enum DimensionAnchorType {FixedElevation, DistanceAbove, DistanceBelow, GraphViewTop, GraphViewBottom}
                // ↔ group enum DimensionAnchorOptionType {Elevation, Above, Below, Default, ViewTop, ViewBottom}
                var mapped = anchorOption switch { "FixedElevation" => "Elevation", "DistanceAbove" => "Above", "DistanceBelow" => "Below", "GraphViewTop" => "ViewTop", "GraphViewBottom" => "ViewBottom", _ => anchorOption };
                if (Civil3DCompatibility.TrySetPropertyValue(group, "DefaultDimensionAnchorOption", $"{mapped}|{anchorOption}", out _)) applied.Add("dimensionAnchorOption=" + mapped);
              }
              if (anchorValue is double av)
              {
                var scaled = av * dimensionAnchorScale;
                if (Civil3DCompatibility.TrySetPropertyValue(group, "DefaultDimensionAnchorValue", scaled, out var avError)) applied.Add("dimensionAnchorValue=" + scaled);
                else if (avError != null) applied.Add("dimensionAnchorValue: " + avError);
                var readBack = Civil3DCompatibility.GetPropertyValue(group, "DefaultDimensionAnchorValue");
                if (readBack != null) groupEntry["dimensionAnchorValueReadBack"] = ReadWrapperOrValue(readBack);
              }
              foreach (var (itemProp, groupProp) in new[] { ("Weeding", "Weeding"), ("StaggerLineHeight1", "StaggerLineHeight1"), ("StaggerLineHeight2", "StaggerLineHeight2") })
              {
                var v = ReadWrapperOrValue(Civil3DCompatibility.GetPropertyValue(item, itemProp) ?? 0.0);
                if (v is double d && Civil3DCompatibility.TrySetPropertyValue(group, groupProp, d, out _)) applied.Add(groupProp.ToLowerInvariant() + "=" + d);
              }
              groupEntry["applied"] = applied;
            }
            catch (Exception ex) { groupEntry["warning"] = Civil3DCompatibility.DescribeException(ex); }
          }
          groups.Add(groupEntry);
        }
      }
      result["groups"] = groups;
      result["removedGroups"] = removed;
      result["errors"] = errors;
      return result;
    }
    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Label sets apply to alignments and profiles, not {target.GetType().Name}.");
  }

  private static object? ReadWrapperOrValue(object value)
  {
    if (IsPropertyWrapper(value.GetType()))
    {
      try { return ValueProperty(value.GetType())?.GetValue(value); } catch { return null; }
    }
    return value;
  }

  // ---------------------------------------------------------------------------------------------
  // Abbreviations (Drawing Settings → Abbreviations)
  // ---------------------------------------------------------------------------------------------

  public static Task<object?> AbbreviationsAsync(JsonObject? parameters)
  {
    var alignment = parameters?["alignment"] as JsonObject;
    var alignmentEntity = parameters?["alignmentEntity"] as JsonObject;
    var profile = parameters?["profile"] as JsonObject;
    var write = alignment != null || alignmentEntity != null || profile != null;

    Func<CivilDocument, Database, Transaction, object?> body = (civilDoc, database, transaction) =>
    {
      var settings = Prop(Prop(civilDoc.Settings, "DrawingSettings"), "AbbreviationsSettings")
        ?? throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", "AbbreviationsSettings is not available.");
      var applied = new List<string>();
      var result = new Dictionary<string, object?>();

      void Handle(string key, string nodeName, string getter, string setter, JsonObject? changes)
      {
        var node = Prop(settings, nodeName);
        if (node == null) return;
        var gm = node.GetType().GetMethod(getter, BindingFlags.Public | BindingFlags.Instance);
        var sm = node.GetType().GetMethod(setter, BindingFlags.Public | BindingFlags.Instance);
        if (gm == null) return;
        var enumType = gm.GetParameters()[0].ParameterType;
        if (changes != null && sm != null)
        {
          foreach (var kv in changes)
          {
            if (!Enum.TryParse(enumType, kv.Key, ignoreCase: true, out var ev))
              throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{key}.{kv.Key}: not a {enumType.Name}. Values: {string.Join(", ", Enum.GetNames(enumType))}.");
            try { sm.Invoke(node, new object?[] { ev, kv.Value?.GetValue<string>() ?? "" }); }
            catch (Exception ex) { throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{key}.{kv.Key}: {Civil3DCompatibility.DescribeException(ex)}"); }
            applied.Add($"{key}.{kv.Key}");
          }
        }
        var values = new Dictionary<string, object?>();
        foreach (var ev in Enum.GetValues(enumType))
        {
          try { values[ev.ToString()!] = gm.Invoke(node, new[] { ev })?.ToString(); } catch { }
        }
        result[key] = values;
      }

      Handle("alignment", "AlignmentGeoPointText", "GetAlignmentAbbreviation", "SetAlignmentAbbreviation", alignment);
      Handle("alignmentEntity", "AlignmentGeoPointEntityData", "GetAlignmentAbbreviation", "SetAlignmentAbbreviation", alignmentEntity);
      Handle("profile", "Profile", "GetProfileAbbreviation", "SetProfileAbbreviation", profile);
      result["applied"] = applied;
      return result;
    };

    return write
      ? CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) => body(civilDoc, database, transaction))
      : CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) => body(civilDoc, database, transaction));
  }

  // ---------------------------------------------------------------------------------------------
  // Label placement: alignment segment / PI labels and profile label numbering
  // ---------------------------------------------------------------------------------------------

  /// <summary>
  /// placeLabels {kind: "alignment_segments", alignmentName, tangentStyle?, curveStyle?, spiralStyle?, piStyle?,
  ///              piNumberComponent?: "IP Number", piNumberFormat?: "I.P. {n}", piNumbers?: [2,3,...] | piStartNumber?: 2}
  /// placeLabels {kind: "profile_numbers", alignmentName, profileName, profileViewName?, numberComponent: "VIP Number",
  ///              numberFormat: "VIP NO.  {n}", numbers?: [1,2,...] | startNumber?: 1}
  /// </summary>
  public static Task<object?> PlaceLabelsAsync(JsonObject? parameters)
  {
    var kind = PluginRuntime.GetRequiredString(parameters, "kind").ToLowerInvariant();
    return kind switch
    {
      "alignment_segments" => PlaceAlignmentSegmentLabels(parameters),
      "profile_numbers" => NumberProfileLabels(parameters),
      "alignment_stations" => PlaceAlignmentStationLabels(parameters),
      "alignment_ip_tables" => PlaceAlignmentIpTables(parameters),
      "inspect" => InspectLabels(parameters),
      _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "placeLabels kind must be alignment_segments, alignment_stations, profile_numbers or inspect."),
    };
  }

  private static Dictionary<string, object?> DescribeLabel(object label, Transaction transaction, ProfileView? pv = null)
  {
    var entry = new Dictionary<string, object?>();
    if (label is AcDbObject dbo) entry["handle"] = dbo.Handle.ToString();
    entry["type"] = label.GetType().Name;
    try
    {
      var styleId = Civil3DCompatibility.GetPropertyValue(label, "StyleId") is ObjectId s ? s : ObjectId.Null;
      if (!styleId.IsNull) entry["style"] = SafeName(transaction, styleId);
    }
    catch { }
    foreach (var prop in new[] { "Dragged", "Pinned", "Flipped", "Reversed", "LeaderVisibility" })
    {
      try { var v = Civil3DCompatibility.GetPropertyValue(label, prop); if (v != null) entry[char.ToLowerInvariant(prop[0]) + prop[1..]] = ReadWrapperOrValue(v); } catch { }
    }
    try
    {
      if (Civil3DCompatibility.GetPropertyValue(label, "LabelLocation") is Point3d loc)
      {
        entry["location"] = new[] { Math.Round(loc.X, 3), Math.Round(loc.Y, 3) };
        if (pv != null)
        {
          double station = 0, elevation = 0;
          try { pv.FindStationAndElevationAtXY(loc.X, loc.Y, ref station, ref elevation); entry["station"] = Math.Round(station, 3); entry["elevation"] = Math.Round(elevation, 3); } catch { }
        }
      }
    }
    catch { }
    foreach (var prop in new[] { "DimensionAnchorOption", "DimensionAnchorValue" })
    {
      try { var v = Civil3DCompatibility.GetPropertyValue(label, prop); if (v != null) entry[char.ToLowerInvariant(prop[0]) + prop[1..]] = ReadWrapperOrValue(v)?.ToString(); } catch { }
    }
    try
    {
      if (Civil3DCompatibility.GetPropertyValue(label, "AnchorInfo") is object ai)
      {
        var loc = ai.GetType().GetField("Location")?.GetValue(ai) ?? ai.GetType().GetProperty("Location")?.GetValue(ai);
        if (loc is Point3d p) entry["anchor"] = new[] { Math.Round(p.X, 3), Math.Round(p.Y, 3) };
      }
    }
    catch { }
    try
    {
      if (label is Label l)
      {
        var overrides = new List<string>();
        foreach (ObjectId cid in l.GetTextComponentIds())
        {
          try { if (l.IsTextComponentOverriden(cid)) overrides.Add($"{SafeName(transaction, cid)}={l.GetTextComponentOverride(cid)}"); } catch { }
        }
        if (overrides.Count > 0) entry["textOverrides"] = overrides;
      }
      else if (label is LabelGroupSubEntity se)
      {
        var overrides = new List<string>();
        foreach (ObjectId cid in se.GetTextComponentIds())
        {
          try { if (se.IsTextComponentOverriden(cid)) overrides.Add($"{SafeName(transaction, cid)}={se.GetTextComponentOverride(cid)}"); } catch { }
        }
        if (overrides.Count > 0) entry["textOverrides"] = overrides;
      }
    }
    catch { }
    return entry;
  }

  private static Dictionary<string, object?> DescribeGroup(LabelGroup group, Transaction transaction, ProfileView? pv = null)
  {
    var entry = new Dictionary<string, object?> { ["handle"] = group.Handle.ToString(), ["type"] = group.GetType().Name };
    try { entry["style"] = SafeName(transaction, group.StyleId); } catch { }
    foreach (var prop in new[] { "DefaultDimensionAnchorOption", "DefaultDimensionAnchorValue", "Increment", "RangeStart", "RangeEnd", "Weeding", "StaggerLineHeight1", "StaggerLineHeight2" })
    {
      try { var v = Civil3DCompatibility.GetPropertyValue(group, prop); if (v != null) entry[char.ToLowerInvariant(prop[0]) + prop[1..]] = ReadWrapperOrValue(v) is double d ? d : ReadWrapperOrValue(v)?.ToString(); } catch { }
    }
    try
    {
      // Geometry point groups: which point types are switched on.
      var options = Civil3DCompatibility.InvokeMatchingOverload(group, null, "GetGeometryPointsOptions", Array.Empty<object?>(), out _);
      if (options != null)
      {
        var on = new List<string>();
        var indexer = options.GetType().GetProperties().FirstOrDefault(p => p.GetIndexParameters().Length == 1);
        if (indexer != null)
        {
          var enumType = indexer.GetIndexParameters()[0].ParameterType;
          foreach (var name in Enum.GetNames(enumType))
          {
            try { if (indexer.GetValue(options, new[] { Enum.Parse(enumType, name) }) is bool b && b) on.Add(name); } catch { }
          }
        }
        entry["geometryPoints"] = on;
      }
    }
    catch { }
    int count;
    try { count = Convert.ToInt32(Civil3DCompatibility.GetPropertyValue(group, "SubEntityCount") ?? 0); } catch { count = 0; }
    entry["count"] = count;
    var subs = new List<Dictionary<string, object?>>();
    for (var i = 0; i < count && i < 200; i++)
    {
      try
      {
        var sub = group.GetAt((uint)i);
        if (sub == null) continue;
        var s = DescribeLabel(sub, transaction, pv);
        s["index"] = i;
        subs.Add(s);
      }
      catch (Exception ex) { subs.Add(new Dictionary<string, object?> { ["index"] = i, ["error"] = Civil3DCompatibility.DescribeException(ex) }); }
    }
    entry["labels"] = subs;
    return entry;
  }

  // ---------------------------------------------------------------------------------------------
  // IP tables (engineer's I.P. n / N / E / A / Ac / T / Es / L.C.)
  // ---------------------------------------------------------------------------------------------

  private static string FormatDms(double degrees)
  {
    degrees %= 360.0; if (degrees < 0) degrees += 360.0;
    var d = (int)Math.Floor(degrees);
    var m = (int)Math.Floor((degrees - d) * 60.0);
    var sec = ((degrees - d) * 60.0 - m) * 60.0;
    if (Math.Round(sec, 1) >= 60.0) { sec = 0; m++; }
    if (m >= 60) { m = 0; d = (d + 1) % 360; }
    return $"{d} {m:00} {sec:00.0}";
  }

  private static double AzimuthDeg(Point2d from, Point2d to)
  {
    var a = Math.Atan2(to.X - from.X, to.Y - from.Y) * 180.0 / Math.PI; // clockwise from north
    return a < 0 ? a + 360.0 : a;
  }

  private static Point2d ToPoint2d(object? value) => value switch
  {
    Point2d p => p,
    Point3d p3 => new Point2d(p3.X, p3.Y),
    _ => throw new InvalidOperationException("not a point"),
  };

  /// <summary>Intersection of the line through a with direction (azimuth ai) and the line through b with azimuth ao.</summary>
  private static Point2d? IntersectAzimuths(Point2d a, double aiDeg, Point2d b, double aoDeg)
  {
    var ai = aiDeg * Math.PI / 180.0; var ao = aoDeg * Math.PI / 180.0;
    var d1 = new Vector2d(Math.Sin(ai), Math.Cos(ai));
    var d2 = new Vector2d(Math.Sin(ao), Math.Cos(ao));
    var den = d1.X * d2.Y - d1.Y * d2.X;
    if (Math.Abs(den) < 1e-12) return null;
    var t = ((b.X - a.X) * d2.Y - (b.Y - a.Y) * d2.X) / den;
    return new Point2d(a.X + d1.X * t, a.Y + d1.Y * t);
  }

  /// <summary>Start/End station of any alignment entity (AlignmentCurve exposes them; compound entities via their sub-entities).</summary>
  private static double EntityStation(AlignmentEntity e, string which)
  {
    try
    {
      var v = Civil3DCompatibility.GetPropertyValue(e, which);
      if (v is double d) return d;
      if (v != null) return Convert.ToDouble(v);
    }
    catch { }
    if (e.SubEntityCount > 0)
    {
      var sub = which == "StartStation" ? e[0] : e[e.SubEntityCount - 1];
      return which == "StartStation" ? sub.StartStation : sub.EndStation;
    }
    return 0;
  }

  private sealed class IpData
  {
    public int Index;
    public string Kind = "";
    public Point2d Pi;
    public double AzIn, AzOut, Deflection, T, Es, Lc;
    public AlignmentEntity? Entity;
  }

  /// <summary>
  /// Geometry of every IP in station order: the start point, one per curve-type entity (arc, SCS ...), the end point.
  /// A/T/Es/L.C. are for the whole curve group (TS→PI, PI→arc middle, TS→ST), which is what the engineer's table lists.
  /// </summary>
  private static List<IpData> ComputeIps(Alignment alignment, List<string> notes)
  {
    var entities = alignment.Entities;
    var ordered = new List<AlignmentEntity>();
    for (var i = 0; i < entities.Count; i++) ordered.Add(entities[i]);
    ordered.Sort((x, y) => EntityStation(x, "StartStation").CompareTo(EntityStation(y, "StartStation")));

    object FirstPart(AlignmentEntity e) => e.SubEntityCount > 0 ? (object)e[0] : e;
    object LastPart(AlignmentEntity e) => e.SubEntityCount > 0 ? (object)e[e.SubEntityCount - 1] : e;
    Point2d StartOf(AlignmentEntity e) => ToPoint2d(Civil3DCompatibility.GetPropertyValue(FirstPart(e), "StartPoint"));
    Point2d EndOf(AlignmentEntity e) => ToPoint2d(Civil3DCompatibility.GetPropertyValue(LastPart(e), "EndPoint"));
    double DirDeg(object sub, string prop)
    {
      var v = Civil3DCompatibility.GetPropertyValue(sub, prop);
      var rad = Convert.ToDouble(v is IConvertible ? v : 0.0);
      var az = 90.0 - rad * 180.0 / Math.PI; // AutoCAD radians CCW from east → azimuth
      az %= 360.0; if (az < 0) az += 360.0; return az;
    }

    var ips = new List<IpData>();
    var first = ordered[0]; var last = ordered[^1];
    var startPt = StartOf(first); var endPt = EndOf(last);
    double firstAz = first.EntityType == AlignmentEntityType.Line ? AzimuthDeg(StartOf(first), EndOf(first)) : DirDeg(FirstPart(first), "StartDirection");
    double lastAz = last.EntityType == AlignmentEntityType.Line ? AzimuthDeg(StartOf(last), EndOf(last)) : DirDeg(LastPart(last), "EndDirection");
    ips.Add(new IpData { Kind = "start", Pi = startPt, AzIn = firstAz, AzOut = firstAz });

    for (var i = 0; i < ordered.Count; i++)
    {
      var e = ordered[i];
      if (e.EntityType == AlignmentEntityType.Line) continue;
      var ip = new IpData { Kind = e.EntityType.ToString(), Entity = e };
      try
      {
        var prev = i > 0 ? ordered[i - 1] : null;
        var next = i + 1 < ordered.Count ? ordered[i + 1] : null;
        var ts = StartOf(e); var st = EndOf(e);
        var subIn = FirstPart(e);
        var subOut = LastPart(e);
        ip.AzIn = prev is { EntityType: AlignmentEntityType.Line } ? AzimuthDeg(StartOf(prev), EndOf(prev)) : DirDeg(subIn, "StartDirection");
        ip.AzOut = next is { EntityType: AlignmentEntityType.Line } ? AzimuthDeg(StartOf(next), EndOf(next)) : DirDeg(subOut, "EndDirection");
        var pi = IntersectAzimuths(ts, ip.AzIn, st, ip.AzOut) ?? new Point2d((ts.X + st.X) / 2, (ts.Y + st.Y) / 2);
        ip.Pi = pi;
        var defl = ip.AzOut - ip.AzIn; while (defl > 180) defl -= 360; while (defl <= -180) defl += 360;
        ip.Deflection = defl;
        ip.T = pi.GetDistanceTo(ts);
        ip.Lc = st.GetDistanceTo(ts);
        // external secant: PI to the middle of the circular arc (first arc sub-entity)
        object? arc = null;
        if (e.SubEntityCount > 0) { for (var k = 0; k < e.SubEntityCount; k++) if (e[k] is AlignmentSubEntityArc) { arc = e[k]; break; } }
        else if (e is AlignmentArc) arc = e;
        if (arc != null)
        {
          var c = ToPoint2d(Civil3DCompatibility.GetPropertyValue(arc, "CenterPoint"));
          var a0 = ToPoint2d(Civil3DCompatibility.GetPropertyValue(arc, "StartPoint"));
          var a1 = ToPoint2d(Civil3DCompatibility.GetPropertyValue(arc, "EndPoint"));
          var r = Convert.ToDouble(Civil3DCompatibility.GetPropertyValue(arc, "Radius") ?? 0.0);
          var bis = new Vector2d((a0.X - c.X) + (a1.X - c.X), (a0.Y - c.Y) + (a1.Y - c.Y));
          if (bis.Length > 1e-9 && r > 0)
          {
            bis = bis.GetNormal();
            var mid = new Point2d(c.X + bis.X * r, c.Y + bis.Y * r);
            ip.Es = pi.GetDistanceTo(mid);
          }
        }
      }
      catch (Exception ex) { notes.Add($"entity {i} ({e.EntityType}): {Civil3DCompatibility.DescribeException(ex)}"); }
      ips.Add(ip);
    }
    ips.Add(new IpData { Kind = "end", Pi = endPt, AzIn = lastAz, AzOut = lastAz });
    for (var i = 0; i < ips.Count; i++) ips[i].Index = i + 1;
    return ips;
  }

  private static string FillIpFormat(string format, IpData ip, int number)
  {
    var a = ip.Kind is "start" or "end" ? 0.0 : (ip.Deflection >= 0 ? ip.Deflection : 360.0 + ip.Deflection);
    return format
      .Replace("{n}", number.ToString())
      .Replace("{N}", ip.Pi.Y.ToString("0.000"))
      .Replace("{E}", ip.Pi.X.ToString("0.000"))
      .Replace("{A}", FormatDms(a))
      .Replace("{Ac}", FormatDms(ip.AzOut))
      .Replace("{T}", ip.T.ToString("0.000"))
      .Replace("{Es}", ip.Es.ToString("0.000"))
      .Replace("{LC}", ip.Lc.ToString("0.000"));
  }

  /// <summary>
  /// kind "alignment_ip_tables": the engineer's IP tables — an indexed PI label on every curve-type entity and a
  /// station-offset label at the start and end, numbered 1..N in station order, with the number, N/E and the
  /// A / Ac / T / Es / L.C. block written as text overrides computed from the alignment geometry
  /// (A = deflection, left turns as 360-Δ; Ac = azimuth of the ahead tangent; T = TS→PI; Es = PI→arc middle; L.C. = TS→ST).
  /// Parameters: alignmentName, piStyle, endStyle (label:alignment/station_offset), numbers? [..], startNumber? (1),
  ///   numberComponent ("IP Number"), coordsComponent ("IP Coords"), dataComponent ("Curve Data"),
  ///   numberFormat ("I.P. {n}"), coordsFormat ("N   {N}\PE   {E}"), dataFormat ("A   = {A}\PAc  = {Ac}\PT   = {T}\PEs  = {Es}\PL.C. = {LC}"),
  ///   replace (true: erase the alignment's existing PI and station-offset labels first), markerStyle.
  /// </summary>
  private static Task<object?> PlaceAlignmentIpTables(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var piStyle = PluginRuntime.GetOptionalString(parameters, "piStyle");
    var endStyle = PluginRuntime.GetOptionalString(parameters, "endStyle");
    var markerStyle = PluginRuntime.GetOptionalString(parameters, "markerStyle");
    var numbers = (parameters?["numbers"] as JsonArray)?.Select(n => n?.GetValue<int>() ?? 0).ToList();
    var startNumber = PluginRuntime.GetOptionalInt(parameters, "startNumber") ?? 1;
    var numberComponent = PluginRuntime.GetOptionalString(parameters, "numberComponent") ?? "IP Number";
    var coordsComponent = PluginRuntime.GetOptionalString(parameters, "coordsComponent") ?? "IP Coords";
    var dataComponent = PluginRuntime.GetOptionalString(parameters, "dataComponent") ?? "Curve Data";
    var numberFormat = PluginRuntime.GetOptionalString(parameters, "numberFormat") ?? "I.P. {n}";
    var coordsFormat = PluginRuntime.GetOptionalString(parameters, "coordsFormat") ?? "N   {N}\\PE   {E}";
    var dataFormat = PluginRuntime.GetOptionalString(parameters, "dataFormat") ?? "A   = {A}\\PAc  = {Ac}\\PT   = {T}\\PEs  = {Es}\\PL.C. = {LC}";
    var replace = parameters?["replace"]?.GetValue<bool>() ?? true;
    var overrideCoords = parameters?["overrideCoords"]?.GetValue<bool>() ?? false; // PI labels have live <[PI Northing]>; the end labels always get coords

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      alignment.UpgradeOpen();
      var piId = piStyle == null ? ObjectId.Null : FindLabelStyle(civilDoc, transaction, piStyle, "alignment", "point_of_intersection");
      var endId = endStyle == null ? ObjectId.Null : FindLabelStyle(civilDoc, transaction, endStyle, "alignment", "station_offset");
      var piLabelStyle = piId.IsNull ? null : transaction.GetObject(piId, OpenMode.ForRead) as LabelStyle;
      var endLabelStyle = endId.IsNull ? null : transaction.GetObject(endId, OpenMode.ForRead) as LabelStyle;
      var notes = new List<string>();
      var errors = new List<string>();
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);

      var removed = new List<string>();
      if (replace)
      {
        foreach (ObjectId id in modelSpace)
        {
          if (id.IsErased || id.ObjectClass?.Name?.StartsWith("AeccDb", StringComparison.Ordinal) != true) continue;
          AcDbObject? obj;
          try { obj = transaction.GetObject(id, OpenMode.ForRead); } catch { continue; }
          if (obj is not (AlignmentIndexedPILabel or AlignmentPILabel or StationOffsetLabel)) continue;
          ObjectId featureId;
          try { featureId = Civil3DCompatibility.GetPropertyValue(obj, "FeatureId") is ObjectId f ? f : ObjectId.Null; } catch { featureId = ObjectId.Null; }
          if (featureId != alignment.ObjectId) continue;
          try { obj.UpgradeOpen(); obj.Erase(); removed.Add($"{obj.GetType().Name}:{id.Handle}"); }
          catch (Exception ex) { errors.Add($"remove {id.Handle}: {Civil3DCompatibility.DescribeException(ex)}"); }
        }
      }

      ObjectId markerId = ObjectId.Null;
      try
      {
        var markers = civilDoc.Styles.MarkerStyles;
        if (markerStyle != null) markerId = markers[markerStyle];
        else if (markers.Contains("_No Markers")) markerId = markers["_No Markers"];
        else foreach (ObjectId m in markers) { markerId = m; break; }
      }
      catch (Exception ex) { notes.Add($"marker style: {Civil3DCompatibility.DescribeException(ex)}"); }

      var ips = ComputeIps(alignment, notes);
      var placed = new List<Dictionary<string, object?>>();
      var entities = alignment.Entities;
      for (var k = 0; k < ips.Count; k++)
      {
        var ip = ips[k];
        var number = numbers != null && k < numbers.Count ? numbers[k] : startNumber + k;
        var entry = new Dictionary<string, object?>
        {
          ["number"] = number, ["kind"] = ip.Kind, ["pi"] = new[] { Math.Round(ip.Pi.X, 3), Math.Round(ip.Pi.Y, 3) },
          ["A"] = FormatDms(ip.Kind is "start" or "end" ? 0 : (ip.Deflection >= 0 ? ip.Deflection : 360 + ip.Deflection)),
          ["Ac"] = FormatDms(ip.AzOut), ["T"] = Math.Round(ip.T, 3), ["Es"] = Math.Round(ip.Es, 3), ["LC"] = Math.Round(ip.Lc, 3),
        };
        try
        {
          object? label = null;
          ObjectId labelId = ObjectId.Null;
          LabelStyle? style = null;
          if (ip.Kind is "start" or "end")
          {
            if (endId.IsNull) { placed.Add(entry); continue; }
            var location = ip.Pi;
            foreach (var args in new[] { new object?[] { alignment.ObjectId, endId, markerId, location }, new object?[] { alignment.ObjectId, endId, location } })
            {
              if (args.Length == 4 && markerId.IsNull) continue;
              var r = Civil3DCompatibility.InvokeMatchingOverload(null, typeof(StationOffsetLabel), "Create", args, out var err);
              if (err != null) { notes.Add($"{ip.Kind}: {err}"); continue; }
              if (r is ObjectId id && !id.IsNull) { labelId = id; break; }
            }
            style = endLabelStyle;
          }
          else
          {
            if (piId.IsNull || ip.Entity == null) { placed.Add(entry); continue; }
            var attempts = new List<string>();
            AlignmentEntity? before = null;
            for (var i = 0; i < entities.Count; i++) { var e = entities[i]; if (e.EntityType == AlignmentEntityType.Line && Math.Abs(EntityStation(e, "EndStation") - EntityStation(ip.Entity, "StartStation")) < 0.001) { before = e; break; } }
            foreach (var labelType in new[] { typeof(AlignmentIndexedPILabel), typeof(AlignmentPILabel) })
            {
              foreach (var candidate in new object?[] { ip.Entity, before })
              {
                if (candidate == null) continue;
                try { labelId = InvokeCreate(labelType, candidate, piId); }
                catch (Exception ex) { attempts.Add($"{labelType.Name}: {Civil3DCompatibility.DescribeException(ex)}"); }
                if (!labelId.IsNull) break;
              }
              if (!labelId.IsNull) break;
            }
            if (labelId.IsNull) errors.Add($"IP {number}: {string.Join(" | ", attempts)}");
            style = piLabelStyle;
          }
          if (labelId.IsNull) { placed.Add(entry); continue; }
          entry["label"] = labelId.Handle.ToString();
          label = transaction.GetObject(labelId, OpenMode.ForWrite) as Label;
          if (label is Label lbl && style != null)
          {
            if (lbl.Dragged) { try { lbl.ResetLocation(); } catch { } }
            var applied = new List<string>();
            void Override(string component, string text, bool required)
            {
              var cid = FindComponent(style, transaction, component);
              if (cid.IsNull) { if (required) errors.Add($"IP {number}: style '{style.Name}' has no component '{component}'"); return; }
              try { lbl.SetTextComponentOverride(cid, text); applied.Add(component); }
              catch (Exception ex) { errors.Add($"IP {number} / {component}: {Civil3DCompatibility.DescribeException(ex)}"); }
            }
            Override(numberComponent, FillIpFormat(numberFormat, ip, number), true);
            if (ip.Kind is "start" or "end" || overrideCoords) Override(coordsComponent, FillIpFormat(coordsFormat, ip, number), false);
            Override(dataComponent, FillIpFormat(dataFormat, ip, number), true);
            entry["overrides"] = applied;
          }
        }
        catch (Exception ex) { errors.Add($"IP {number}: {Civil3DCompatibility.DescribeException(ex)}"); }
        placed.Add(entry);
      }
      RequestRegen(doc);
      return new Dictionary<string, object?>
      {
        ["alignment"] = alignmentName,
        ["ips"] = placed,
        ["removed"] = removed,
        ["notes"] = notes,
        ["errors"] = errors,
      };
    });
  }

  /// <summary>
  /// kind "inspect": read-only dump of the labels on an alignment (groups + free labels) and, when profileName is
  /// given, the profile's label groups in each profile view — style, location, station, dragged/pinned state,
  /// dimension anchors, text overrides, geometry-point options. Lets the placement be checked without a screen.
  /// </summary>
  private static Task<object?> InspectLabels(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetOptionalString(parameters, "profileName");
    var profileViewName = PluginRuntime.GetOptionalString(parameters, "profileViewName");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
      var result = new Dictionary<string, object?> { ["alignment"] = alignmentName };
      try
      {
        var scale = database.Cannoscale;
        result["annotationScale"] = new Dictionary<string, object?> { ["name"] = scale.Name, ["paperUnits"] = scale.PaperUnits, ["drawingUnits"] = scale.DrawingUnits, ["scale"] = scale.Scale };
      }
      catch { }

      var groups = new List<Dictionary<string, object?>>();
      var labels = new List<Dictionary<string, object?>>();
      foreach (ObjectId id in modelSpace)
      {
        if (id.IsErased || id.ObjectClass?.Name?.StartsWith("AeccDb", StringComparison.Ordinal) != true) continue;
        AcDbObject? obj;
        try { obj = transaction.GetObject(id, OpenMode.ForRead); } catch { continue; }
        if (obj is not LabelBase) continue;
        ObjectId featureId;
        try { featureId = Civil3DCompatibility.GetPropertyValue(obj, "FeatureId") is ObjectId f ? f : ObjectId.Null; } catch { featureId = ObjectId.Null; }
        if (featureId != alignment.ObjectId) continue;
        if (obj is LabelGroup lg) groups.Add(DescribeGroup(lg, transaction));
        else if (obj is Label l) labels.Add(DescribeLabel(l, transaction));
      }
      result["alignmentGroups"] = groups;
      result["alignmentLabels"] = labels;
      result["startStation"] = alignment.StartingStation;
      result["endStation"] = alignment.EndingStation;

      if (profileName != null)
      {
        var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForRead);
        var views = new List<Dictionary<string, object?>>();
        foreach (ObjectId id in modelSpace)
        {
          if (id.ObjectClass?.IsDerivedFrom(Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(ProfileView))) != true) continue;
          var pv = transaction.GetObject(id, OpenMode.ForRead) as ProfileView;
          if (pv == null || pv.AlignmentId != alignment.ObjectId) continue;
          if (profileViewName != null && !string.Equals(pv.Name, profileViewName, StringComparison.OrdinalIgnoreCase)) continue;
          var viewEntry = new Dictionary<string, object?> { ["view"] = pv.Name, ["location"] = new[] { Math.Round(pv.Location.X, 3), Math.Round(pv.Location.Y, 3) } };
          var pvis = new List<Dictionary<string, object?>>();
          foreach (ProfilePVI pvi in profile.PVIs)
          {
            double x = 0, y = 0;
            try { pv.FindXYAtStationAndElevation(pvi.RawStation, pvi.Elevation, ref x, ref y); } catch { }
            pvis.Add(new Dictionary<string, object?> { ["station"] = Math.Round(pvi.RawStation, 3), ["elevation"] = Math.Round(pvi.Elevation, 3), ["xy"] = new[] { Math.Round(x, 3), Math.Round(y, 3) } });
          }
          viewEntry["pvis"] = pvis;
          var diagnostics = new List<string>();
          var pgroups = new List<Dictionary<string, object?>>();
          foreach (var (groupType, _) in ProfileGroupTypes)
          {
            foreach (var gid in FindProfileLabelGroups(groupType, pv, profile, modelSpace, transaction, diagnostics))
            {
              if (transaction.GetObject(gid, OpenMode.ForRead) is LabelGroup lg) pgroups.Add(DescribeGroup(lg, transaction, pv));
            }
          }
          viewEntry["groups"] = pgroups;
          viewEntry["diagnostics"] = diagnostics;
          views.Add(viewEntry);
        }
        result["profile"] = profileName;
        result["profileViews"] = views;
      }
      return result;
    });
  }

  /// <summary>
  /// kind "alignment_stations": station-offset labels at given stations (default: the alignment start and end)
  /// with a label:alignment/station_offset style — the engineer's start/end chainage labels.
  /// </summary>
  private static Task<object?> PlaceAlignmentStationLabels(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var styleName = PluginRuntime.GetRequiredString(parameters, "stationStyle");
    var markerStyle = PluginRuntime.GetOptionalString(parameters, "markerStyle");
    var stations = (parameters?["stations"] as JsonArray)?.Select(n => n?.GetValue<double>() ?? 0).ToList();
    var offset = parameters?["offset"]?.GetValue<double>() ?? 0.0;
    // overrides: [{ station, components: { "IP Number": "I.P. 1", "Curve Data": "A = ..." } }] — text component overrides per station
    var overrides = new List<(double station, Dictionary<string, string> texts)>();
    if (parameters?["overrides"] is JsonArray overrideArray)
    {
      foreach (var node in overrideArray)
      {
        if (node is not JsonObject o) continue;
        var st = o["station"]?.GetValue<double>() ?? double.NaN;
        var texts = new Dictionary<string, string>();
        if (o["components"] is JsonObject comps)
          foreach (var kv in comps) if (kv.Value != null) texts[kv.Key] = kv.Value.GetValue<string>();
        if (!double.IsNaN(st)) overrides.Add((st, texts));
      }
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      alignment.UpgradeOpen();
      var styleId = FindLabelStyle(civilDoc, transaction, styleName, "alignment", "station_offset");
      var labelStyle = transaction.GetObject(styleId, OpenMode.ForRead) as LabelStyle;
      ObjectId markerId = ObjectId.Null;
      try
      {
        var markers = civilDoc.Styles.MarkerStyles;
        if (markerStyle != null) markerId = markers[markerStyle];
        else if (markers.Contains("_No Markers")) markerId = markers["_No Markers"];
        else foreach (ObjectId m in markers) { markerId = m; break; }
      }
      catch (Exception ex) when (markerStyle != null) { throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Marker style '{markerStyle}': {Civil3DCompatibility.DescribeException(ex)}"); }
      stations ??= new List<double> { alignment.StartingStation, alignment.EndingStation };

      var created = new List<Dictionary<string, object?>>();
      var errors = new List<string>();
      foreach (var station in stations)
      {
        var attempts = new List<string>();
        ObjectId labelId = ObjectId.Null;
        // StationOffsetLabel.Create(alignmentId, labelStyleId, markerStyleId, Point2d location): the label point comes from the station.
        double px = 0, py = 0;
        try { alignment.PointLocation(station, offset, ref px, ref py); }
        catch (Exception ex) { errors.Add($"station {station}: PointLocation: {Civil3DCompatibility.DescribeException(ex)}"); continue; }
        var location = new Point2d(px, py);
        foreach (var args in new[]
        {
          new object?[] { alignment.ObjectId, styleId, markerId, location },
          new object?[] { alignment.ObjectId, styleId, location },
          new object?[] { alignment.ObjectId, station, offset, styleId, markerId },
        })
        {
          if (args.Length != 3 && markerId.IsNull) continue;
          try
          {
            var r = Civil3DCompatibility.InvokeMatchingOverload(null, typeof(StationOffsetLabel), "Create", args, out var err);
            if (err != null) { attempts.Add(err); continue; }
            if (r is ObjectId id && !id.IsNull) { labelId = id; break; }
          }
          catch (Exception ex) { attempts.Add(Civil3DCompatibility.DescribeException(ex)); }
        }
        if (labelId.IsNull) { errors.Add($"station {station}: {string.Join(" | ", attempts)}"); continue; }
        var entry = new Dictionary<string, object?> { ["station"] = Math.Round(station, 3), ["label"] = labelId.Handle.ToString() };
        try
        {
          if (transaction.GetObject(labelId, OpenMode.ForWrite) is Label label)
          {
            if (!styleId.IsNull && label.StyleId != styleId) { try { label.StyleId = styleId; } catch { } }
            entry["dragged"] = label.Dragged;
            if (label.Dragged) { label.ResetLocation(); entry["resetLocation"] = true; entry["draggedAfterReset"] = label.Dragged; }
            var ov = overrides.FirstOrDefault(o => Math.Abs(o.station - station) < 0.01);
            if (ov.texts != null && labelStyle != null)
            {
              var applied = new List<string>();
              foreach (var (componentName, text) in ov.texts)
              {
                var cid = FindComponent(labelStyle, transaction, componentName);
                if (cid.IsNull) { errors.Add($"station {station}: style '{styleName}' has no component '{componentName}'"); continue; }
                try { label.SetTextComponentOverride(cid, text); applied.Add(componentName); }
                catch (Exception ex) { errors.Add($"station {station} / {componentName}: {Civil3DCompatibility.DescribeException(ex)}"); }
              }
              entry["overrides"] = applied;
            }
          }
        }
        catch (Exception ex) { entry["warning"] = Civil3DCompatibility.DescribeException(ex); }
        created.Add(entry);
      }
      RequestRegen(doc);
      return new Dictionary<string, object?> { ["alignment"] = alignmentName, ["style"] = styleName, ["labels"] = created, ["errors"] = errors };
    });
  }

  private static Task<object?> PlaceAlignmentSegmentLabels(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var tangentStyle = PluginRuntime.GetOptionalString(parameters, "tangentStyle");
    var curveStyle = PluginRuntime.GetOptionalString(parameters, "curveStyle");
    var spiralStyle = PluginRuntime.GetOptionalString(parameters, "spiralStyle");
    var piStyle = PluginRuntime.GetOptionalString(parameters, "piStyle");
    var piComponent = PluginRuntime.GetOptionalString(parameters, "piNumberComponent") ?? "IP Number";
    var piFormat = PluginRuntime.GetOptionalString(parameters, "piNumberFormat") ?? "I.P. {n}";
    var piNumbers = (parameters?["piNumbers"] as JsonArray)?.Select(n => n?.GetValue<int>() ?? 0).ToList();
    var piStartGiven = PluginRuntime.GetOptionalInt(parameters, "piStartNumber");
    var piStart = piStartGiven ?? 2;
    // <[Alignment PI Index]> numbers PI labels by itself; only override the number component when the caller supplies numbers.
    var overrideNumbers = piNumbers != null || piStartGiven != null;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      alignment.UpgradeOpen();
      var tangentId = tangentStyle == null ? ObjectId.Null : FindLabelStyle(civilDoc, transaction, tangentStyle, "alignment", "line");
      var curveId = curveStyle == null ? ObjectId.Null : FindLabelStyle(civilDoc, transaction, curveStyle, "alignment", "curve");
      var spiralId = spiralStyle == null ? ObjectId.Null : FindLabelStyle(civilDoc, transaction, spiralStyle, "alignment", "spiral");
      var piId = piStyle == null ? ObjectId.Null : FindLabelStyle(civilDoc, transaction, piStyle, "alignment", "point_of_intersection");

      var created = new List<Dictionary<string, object?>>();
      var errors = new List<string>();
      var notes = new List<string>();
      _createTransaction = transaction;
      _createNotes = notes;
      var piIndex = 0;
      ObjectId piComponentId = ObjectId.Null;
      if (!piId.IsNull)
      {
        var piLabelStyle = transaction.GetObject(piId, OpenMode.ForRead) as LabelStyle;
        if (piLabelStyle != null) piComponentId = FindComponent(piLabelStyle, transaction, piComponent);
      }

      var entities = alignment.Entities;
      for (var i = 0; i < entities.Count; i++)
      {
        var entity = entities[i];
        var entityType = entity.EntityType.ToString();
        var entry = new Dictionary<string, object?> { ["index"] = i, ["entityType"] = entityType, ["entityId"] = entity.EntityId };
        try
        {
          ObjectId labelId = ObjectId.Null;
          switch (entity.EntityType)
          {
            case AlignmentEntityType.Line when !tangentId.IsNull:
              labelId = InvokeCreate(typeof(AlignmentTangentLabel), entity, tangentId);
              break;
            case AlignmentEntityType.Arc when !curveId.IsNull:
              labelId = InvokeCreate(typeof(AlignmentCurveLabel), entity, curveId);
              break;
            case AlignmentEntityType.Spiral when !spiralId.IsNull:
              labelId = InvokeCreate(typeof(AlignmentSpiralLabel), entity, spiralId);
              break;
            case AlignmentEntityType.Line:
            case AlignmentEntityType.Arc:
            case AlignmentEntityType.Spiral:
              break; // style for this simple type not requested
            default:
            {
              // Compound entities (SCS, spiral-line, ...): label each sub-entity.
              var subLabels = new List<string>();
              for (var s = 0; s < entity.SubEntityCount; s++)
              {
                var sub = entity[s];
                ObjectId subId = ObjectId.Null;
                try
                {
                  if (sub is AlignmentSubEntityArc && !curveId.IsNull) subId = InvokeCreate(typeof(AlignmentCurveLabel), sub, curveId);
                  else if (sub is AlignmentSubEntitySpiral && !spiralId.IsNull) subId = InvokeCreate(typeof(AlignmentSpiralLabel), sub, spiralId);
                  else if (sub is AlignmentSubEntityLine && !tangentId.IsNull) subId = InvokeCreate(typeof(AlignmentTangentLabel), sub, tangentId);
                }
                catch (Exception ex) { errors.Add($"entity {i} sub {s}: {Civil3DCompatibility.DescribeException(ex)}"); }
                if (!subId.IsNull) subLabels.Add(subId.Handle.ToString());
              }
              entry["subLabels"] = subLabels;
              break;
            }
          }
          if (!labelId.IsNull) entry["label"] = labelId.Handle.ToString();

          // PI labels: one per curve-type entity (arc / SCS ...), numbered in station order.
          if (!piId.IsNull && entity.EntityType != AlignmentEntityType.Line)
          {
            ObjectId piLabelId = ObjectId.Null;
            var attempts = new List<string>();
            // Civil 3D anchors PI labels to the tangent coming into the PI; the curve overloads are fussier.
            var before = i > 0 ? entities[i - 1] : null;
            // Point-of-Intersection styles (the ones with <[Alignment PI Index]>) belong to AlignmentIndexedPILabel;
            // AlignmentPILabel is the older Tangent Intersection label. Try indexed first, then the legacy class.
            foreach (var labelType in new[] { typeof(AlignmentIndexedPILabel), typeof(AlignmentPILabel) })
            {
              foreach (var candidate in new object?[] { entity, before is { EntityType: AlignmentEntityType.Line } ? before : null })
              {
                if (candidate == null) continue;
                try { piLabelId = InvokeCreate(labelType, candidate, piId); }
                catch (Exception ex) { attempts.Add($"{labelType.Name}: {Civil3DCompatibility.DescribeException(ex)}"); }
                if (!piLabelId.IsNull) break;
              }
              if (!piLabelId.IsNull) break;
            }
            if (piLabelId.IsNull) errors.Add($"entity {i} PI label: {string.Join(" | ", attempts)}");
            if (!piLabelId.IsNull)
            {
              var number = piNumbers != null && piIndex < piNumbers.Count ? piNumbers[piIndex] : piStart + piIndex;
              piIndex++;
              entry["piLabel"] = piLabelId.Handle.ToString();
              entry["piNumber"] = number;
              if (!piComponentId.IsNull && overrideNumbers)
              {
                var label = transaction.GetObject(piLabelId, OpenMode.ForWrite) as Label;
                try { label?.SetTextComponentOverride(piComponentId, piFormat.Replace("{n}", number.ToString())); entry["piNumberApplied"] = true; }
                catch (Exception ex) { errors.Add($"entity {i} PI number override: {Civil3DCompatibility.DescribeException(ex)}"); }
              }
              else entry["piNumberApplied"] = false;
            }
          }
        }
        catch (Exception ex) { errors.Add($"entity {i} ({entityType}): {Civil3DCompatibility.DescribeException(ex)}"); }
        created.Add(entry);
      }
      _createTransaction = null;
      _createNotes = null;
      RequestRegen(doc);

      return new Dictionary<string, object?>
      {
        ["alignment"] = alignmentName,
        ["entities"] = created,
        ["piNumberComponentFound"] = !piComponentId.IsNull,
        ["notes"] = notes,
        ["errors"] = errors,
      };
    });
  }

  /// <summary>Labels drawn inside the API transaction keep their provisional graphics until the next regen; ask for one.</summary>
  private static void RequestRegen(Autodesk.AutoCAD.ApplicationServices.Document doc)
  {
    try { doc.SendStringToExecute("_.REGEN ", true, false, false); } catch { }
  }

  [ThreadStatic] private static Transaction? _createTransaction;
  [ThreadStatic] private static List<string>? _createNotes;

  private static ObjectId InvokeCreate(Type labelType, object entity, ObjectId styleId)
  {
    var result = Civil3DCompatibility.InvokeMatchingOverload(null, labelType, "Create", new object?[] { entity, styleId }, out var error);
    if (error != null) throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{labelType.Name}.Create: {error}");
    var id = result is ObjectId oid ? oid : ObjectId.Null;
    // Labels created through the API come up in the dragged state (stacked, centred text) until they are
    // moved by hand; reset the location so the composed style shows straight away.
    if (!id.IsNull && _createTransaction != null)
    {
      try
      {
        if (_createTransaction.GetObject(id, OpenMode.ForWrite) is Label label && label.Dragged)
        {
          label.ResetLocation();
          _createNotes?.Add($"{id.Handle}: dragged → reset ({(label.Dragged ? "still dragged" : "ok")})");
        }
      }
      catch (Exception ex) { _createNotes?.Add($"{id.Handle}: reset failed: {Civil3DCompatibility.DescribeException(ex)}"); }
    }
    return id;
  }

  private static Task<object?> NumberProfileLabels(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var profileViewName = PluginRuntime.GetOptionalString(parameters, "profileViewName");
    var component = PluginRuntime.GetOptionalString(parameters, "numberComponent") ?? "VIP Number";
    var format = PluginRuntime.GetOptionalString(parameters, "numberFormat") ?? "VIP NO.  {n}";
    var numbers = (parameters?["numbers"] as JsonArray)?.Select(n => n?.GetValue<int>() ?? 0).ToList();
    var start = PluginRuntime.GetOptionalInt(parameters, "startNumber") ?? 1;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForRead);

      // profile views showing this alignment
      var views = new List<ProfileView>();
      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
      foreach (ObjectId id in modelSpace)
      {
        if (id.ObjectClass?.IsDerivedFrom(Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(ProfileView))) != true) continue;
        var pv = transaction.GetObject(id, OpenMode.ForRead) as ProfileView;
        if (pv == null || pv.AlignmentId != alignment.ObjectId) continue;
        if (profileViewName != null && !string.Equals(pv.Name, profileViewName, StringComparison.OrdinalIgnoreCase)) continue;
        views.Add(pv);
      }
      if (views.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No profile view of alignment '{alignmentName}' found; profile labels live in a profile view.");

      // PVI stations in order → numbers
      var pviStations = new List<double>();
      foreach (ProfilePVI pvi in profile.PVIs) pviStations.Add(pvi.RawStation);
      pviStations.Sort();
      int NumberFor(double station)
      {
        var best = 0; var bestDist = double.MaxValue;
        for (var i = 0; i < pviStations.Count; i++)
        {
          var d = Math.Abs(pviStations[i] - station);
          if (d < bestDist) { bestDist = d; best = i; }
        }
        return numbers != null && best < numbers.Count ? numbers[best] : start + best;
      }

      var report = new List<Dictionary<string, object?>>();
      var errors = new List<string>();
      var diagnostics = new List<string>();
      foreach (var pv in views)
      {
        foreach (var (groupType, kind) in new[] { (typeof(ProfilePVILabelGroup), "pvi"), (typeof(ProfileCrestCurveLabelGroup), "crest"), (typeof(ProfileSagCurveLabelGroup), "sag") })
        {
          var ids = new List<ObjectId>();
          try
          {
            var groupIds = Civil3DCompatibility.InvokeMatchingOverload(null, groupType, "GetAvailableLabelGroupIds", new object?[] { pv.ObjectId, profile.ObjectId }, out var err);
            if (err != null) diagnostics.Add($"{kind}: GetAvailableLabelGroupIds: {err}");
            else ids.AddRange(CivilObjectUtils.ToObjectIds(groupIds));
          }
          catch (Exception ex) { diagnostics.Add($"{kind}: {Civil3DCompatibility.DescribeException(ex)}"); }
          if (ids.Count == 0)
          {
            // Fallback: label groups are model-space entities; match by class + feature + view.
            var rx = Autodesk.AutoCAD.Runtime.RXObject.GetClass(groupType);
            foreach (ObjectId id in modelSpace)
            {
              if (id.ObjectClass?.IsDerivedFrom(rx) != true) continue;
              try
              {
                var lg = transaction.GetObject(id, OpenMode.ForRead) as LabelGroup;
                if (lg == null) continue;
                var featureId = Civil3DCompatibility.GetPropertyValue(lg, "FeatureId") is ObjectId f ? f : ObjectId.Null;
                var viewId = Civil3DCompatibility.GetPropertyValue(lg, "ViewId") is ObjectId v ? v : ObjectId.Null;
                if ((featureId.IsNull || featureId == profile.ObjectId) && (viewId.IsNull || viewId == pv.ObjectId)) ids.Add(id);
              }
              catch { }
            }
            diagnostics.Add($"{kind}: model-space scan found {ids.Count} group(s)");
          }
          else diagnostics.Add($"{kind}: {ids.Count} group(s)");
          foreach (var gid in ids)
          {
            var group = transaction.GetObject(gid, OpenMode.ForWrite) as LabelGroup;
            if (group == null) continue;
            var styleId = group.StyleId;
            var style = transaction.GetObject(styleId, OpenMode.ForRead) as LabelStyle;
            var componentId = style == null ? ObjectId.Null : FindComponent(style, transaction, component);
            if (componentId.IsNull)
            {
              errors.Add($"{kind} group {gid.Handle}: label style '{style?.Name}' has no component '{component}'.");
              continue;
            }
            var count = Civil3DCompatibility.GetPropertyValue(group, "SubEntityCount") is int c ? c : -1;
            if (count < 0) { try { count = Convert.ToInt32(Civil3DCompatibility.GetPropertyValue(group, "SubEntityCount") ?? 0); } catch { count = 0; } }
            diagnostics.Add($"{kind} group {gid.Handle}: {count} sub-entities, style '{style?.Name}'");
            for (var i = 0; i < count; i++)
            {
              try
              {
                var sub = group.GetAt((uint)i);
                if (sub == null) continue;
                var location = sub.LabelLocation;
                double station = 0, elevation = 0;
                pv.FindStationAndElevationAtXY(location.X, location.Y, ref station, ref elevation);
                var n = NumberFor(station);
                sub.SetTextComponentOverride(componentId, format.Replace("{n}", n.ToString()));
                var entry = new Dictionary<string, object?> { ["view"] = pv.Name, ["group"] = kind, ["index"] = i, ["station"] = Math.Round(station, 3), ["number"] = n };
                // Grade-break labels at the profile ends: PBT at the start, PAT at the end (component 'Point Type' when present).
                if (kind == "pvi" && style != null && pviStations.Count > 0)
                {
                  var typeComponent = FindComponent(style, transaction, "Point Type");
                  if (!typeComponent.IsNull)
                  {
                    string? pointType = Math.Abs(station - pviStations[0]) < 0.01 ? "PBT" : Math.Abs(station - pviStations[^1]) < 0.01 ? "PAT" : null;
                    if (pointType != null) { sub.SetTextComponentOverride(typeComponent, pointType); entry["pointType"] = pointType; }
                  }
                }
                report.Add(entry);
              }
              catch (Exception ex) { errors.Add($"{kind} group {gid.Handle} label {i}: {Civil3DCompatibility.DescribeException(ex)}"); }
            }
          }
        }
      }
      return new Dictionary<string, object?>
      {
        ["alignment"] = alignmentName,
        ["profile"] = profileName,
        ["pviStations"] = pviStations,
        ["numbered"] = report,
        ["errors"] = errors,
        ["diagnostics"] = diagnostics,
      };
    });
  }
}
