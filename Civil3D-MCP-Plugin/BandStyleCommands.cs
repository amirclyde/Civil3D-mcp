using System.Reflection;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

namespace Civil3DMcpPlugin;

/// <summary>
/// civil3d_style phase 3: profile/section view band styles and band sets.
///
/// Families (StylesRoot.BandStyles.*):
///   band:profile_data | band:horizontal_geometry | band:vertical_geometry | band:superelevation |
///   band:sectional_data | band:pipe_network | band:section_data | band:section_segments
///
/// Band style spec additions (on top of the generic display / settings / properties / references):
///   "properties": { "bandHeight": 15, "textBoxWidth": 80, "textHeight": 4, "offsetFromBand": 0, "text": "FINISHED ROAD LEVEL (m)",
///                   "textLocation": "...", "textBoxPosition": "...", "weedingFactor": 0 }     // sizes in paper mm
///   "labels": { "majorIncrement": { label style spec: properties / components / replaceComponents }, "titleText": {...}, ... }
///     — every *LabelStyleId property of the band style is a label style owned by the band; edit it in place.
///
/// Band set spec: "items": [ { "band": "UTNM Finished Road Level", "location": "bottom", "gap": 0, "majorInterval": 25,
///   "minorInterval": 5, "showLabels": true, "staggerLabel": false, "staggerLineHeight": 0, "weeding": 0,
///   "labelAtStartStation": true, "labelAtEndStation": true } ], "replaceItems": true
/// </summary>
internal static class BandStyleCommands
{
  internal const double PaperScale = 0.001;

  internal static readonly Dictionary<string, string> BandFamilies = new(StringComparer.OrdinalIgnoreCase)
  {
    ["band:profile_data"] = "BandStyles.ProfileViewProfileDataBandStyles",
    ["band:horizontal_geometry"] = "BandStyles.ProfileViewHorizontalGeometryBandStyles",
    ["band:vertical_geometry"] = "BandStyles.ProfileViewVerticalGeometryBandStyles",
    ["band:superelevation"] = "BandStyles.ProfileViewSuperElevationBandStyles",
    ["band:sectional_data"] = "BandStyles.ProfileViewSectionalDataBandStyles",
    ["band:pipe_network"] = "BandStyles.ProfileViewPipeNetworkBandStyles",
    ["band:section_data"] = "BandStyles.SectionViewSectionDataBandStyles",
    ["band:section_segments"] = "BandStyles.SectionViewSegmentsBandStyles",
  };

  /// <summary>
  /// Read a property that a derived band-set item type redeclares (BandStyleId, MajorInterval ... exist on both
  /// BandSetItem and ProfileViewBandSetItem, so a plain GetProperty is ambiguous): most-derived declaration wins.
  /// </summary>
  internal static object? ReadDerived(object target, string name)
  {
    for (var t = target.GetType(); t != null; t = t.BaseType)
    {
      var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
      if (p == null) continue;
      try { return p.GetValue(target); } catch { return null; }
    }
    return null;
  }

  internal static bool IsBandFamily(string family) => family.StartsWith("band:", StringComparison.OrdinalIgnoreCase);

  /// <summary>Walk a dotted property path from StylesRoot ("BandStyles.ProfileViewProfileDataBandStyles").</summary>
  internal static object? ResolvePath(object root, string path)
  {
    object? current = root;
    foreach (var part in path.Split('.'))
    {
      if (current == null) return null;
      try { current = Civil3DCompatibility.GetPropertyValue(current, part); }
      catch { return null; }
    }
    return current;
  }

  /// <summary>Band style properties that are paper sizes (metres in the API, mm in the spec).</summary>
  private static readonly HashSet<string> SizeProperties = new(StringComparer.OrdinalIgnoreCase)
  {
    "bandHeight", "textBoxWidth", "textHeight", "offsetFromBand", "staggerLineHeight", "gap",
    "smallTicksAtTopSize", "smallTicksAtMiddleSize", "smallTicksAtBottomSize",
  };

  private static string LabelKey(string propertyName)
  {
    var name = propertyName.EndsWith("LabelStyleId", StringComparison.Ordinal) ? propertyName[..^"LabelStyleId".Length] : propertyName;
    return name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
  }

  // ---------------------------------------------------------------------------------------------
  // Band styles
  // ---------------------------------------------------------------------------------------------

  internal static void ReadBand(BandStyle band, Dictionary<string, object?> spec, Transaction transaction)
  {
    // sizes → mm
    if (spec["properties"] is Dictionary<string, object?> props)
    {
      foreach (var key in props.Keys.ToList())
        if (SizeProperties.Contains(key) && props[key] is double d) props[key] = Math.Round(d / PaperScale, 4);
    }
    if (spec["settings"] is Dictionary<string, object?> settings)
    {
      foreach (var sub in settings.Values.OfType<Dictionary<string, object?>>())
        foreach (var key in sub.Keys.ToList())
          if (SizeProperties.Contains(key) && sub[key] is double d) sub[key] = Math.Round(d / PaperScale, 4);
    }
    // owned label styles
    var labels = new Dictionary<string, object?>();
    foreach (var property in band.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
    {
      if (!property.Name.EndsWith("LabelStyleId", StringComparison.Ordinal) || property.PropertyType != typeof(ObjectId)) continue;
      ObjectId id;
      try { id = (ObjectId)property.GetValue(band)!; } catch { continue; }
      if (id.IsNull) continue;
      try
      {
        if (transaction.GetObject(id, OpenMode.ForRead) is LabelStyle ls)
        {
          var sub = new Dictionary<string, object?> { ["name"] = ls.Name, ["handle"] = id.Handle.ToString() };
          LabelStyleCommands.ReadLabelStyle(ls, sub, transaction);
          labels[LabelKey(property.Name)] = sub;
        }
      }
      catch (Exception ex) { labels[LabelKey(property.Name)] = new Dictionary<string, object?> { ["error"] = Civil3DCompatibility.DescribeException(ex) }; }
    }
    if (labels.Count > 0) spec["labels"] = labels;
    if (spec["references"] is Dictionary<string, object?> refs)
      foreach (var key in refs.Keys.Where(k => k.EndsWith("LabelStyleId", StringComparison.OrdinalIgnoreCase) || k.EndsWith("LabelStyle", StringComparison.OrdinalIgnoreCase)).ToList())
        refs.Remove(key); // exposed under "labels" instead
    spec["units"] = "sizes in paper mm (bandHeight, textBoxWidth, textHeight, offsetFromBand, tick sizes); labels: label style specs owned by the band";
  }

  /// <summary>Convert the mm sizes of a band spec to metres in place (before the generic apply).</summary>
  internal static void ScaleBandSpec(JsonObject spec)
  {
    if (spec["properties"] is JsonObject props)
      foreach (var key in props.Select(kv => kv.Key).ToList())
        if (SizeProperties.Contains(key) && props[key] is JsonValue v && v.TryGetValue<double>(out var d)) props[key] = d * PaperScale;
    if (spec["settings"] is JsonObject settings)
      foreach (var sub in settings.Select(kv => kv.Value).OfType<JsonObject>())
        foreach (var key in sub.Select(kv => kv.Key).ToList())
          if (SizeProperties.Contains(key) && sub[key] is JsonValue v && v.TryGetValue<double>(out var d)) sub[key] = d * PaperScale;
  }

  internal static void ApplyBandLabels(BandStyle band, JsonObject labels, CivilDocument civilDoc, Transaction transaction, List<string> applied, List<string> warnings)
  {
    var properties = band.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
      .Where(p => p.Name.EndsWith("LabelStyleId", StringComparison.Ordinal) && p.PropertyType == typeof(ObjectId)).ToList();
    foreach (var kv in labels)
    {
      if (kv.Value is not JsonObject labelSpec) continue;
      var property = properties.FirstOrDefault(p => string.Equals(LabelKey(p.Name), kv.Key, StringComparison.OrdinalIgnoreCase))
        ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"labels.{kv.Key}: {band.GetType().Name} has no such label. Available: {string.Join(", ", properties.Select(p => LabelKey(p.Name)))}.");
      var id = (ObjectId)property.GetValue(band)!;
      if (id.IsNull) { warnings.Add($"labels.{kv.Key}: the band has no label style object for it"); continue; }
      if (transaction.GetObject(id, OpenMode.ForWrite) is not LabelStyle ls) { warnings.Add($"labels.{kv.Key}: not a LabelStyle"); continue; }
      var sub = new List<string>();
      LabelStyleCommands.ApplyLabelStyle(ls, labelSpec, civilDoc, transaction, sub, warnings);
      applied.AddRange(sub.Select(s => $"labels.{kv.Key}.{s}"));
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Band sets
  // ---------------------------------------------------------------------------------------------

  private static readonly string[] ItemProperties =
  {
    "Gap", "MajorInterval", "MinorInterval", "ShowLabels", "StaggerLabel", "StaggerLineHeight", "Weeding", "LabelAtStartStation", "LabelAtEndStation",
  };

  internal static void ReadBandSet(BandSetStyle set, Dictionary<string, object?> spec, Transaction transaction)
  {
    var items = new List<Dictionary<string, object?>>();
    foreach (var (getter, location) in new[] { ("GetTopBandSetItems", "top"), ("GetBottomBandSetItems", "bottom") })
    {
      object? collection;
      try { collection = Civil3DCompatibility.InvokeMatchingOverload(set, null, getter, Array.Empty<object?>(), out var err); if (err != null) continue; }
      catch { continue; }
      if (collection is not System.Collections.IEnumerable enumerable) continue;
      var index = 0;
      foreach (var item in enumerable)
      {
        if (item == null) continue;
        var d = new Dictionary<string, object?> { ["index"] = index++, ["location"] = location };
        if (ReadDerived(item, "BandStyleId") is ObjectId bid && !bid.IsNull)
        {
          try { d["band"] = CivilObjectUtils.GetName(transaction.GetObject(bid, OpenMode.ForRead)); } catch { d["band"] = bid.Handle.ToString(); }
        }
        d["bandType"] = Civil3DCompatibility.GetPropertyValue(item, "BandType")?.ToString();
        foreach (var prop in ItemProperties)
        {
          object? v;
          try { v = Civil3DCompatibility.GetPropertyValue(item, prop); } catch { continue; }
          if (v == null) continue;
          var key = char.ToLowerInvariant(prop[0]) + prop[1..];
          if (SizeProperties.Contains(key) && v is double dv) v = Math.Round(dv / PaperScale, 4);
          d[key] = v;
        }
        items.Add(d);
      }
    }
    spec["items"] = items;
    spec["units"] = "gap / staggerLineHeight in paper mm; intervals in metres";
  }

  private static object? NewItemCollection(BandSetStyle set, Type collectionType, string location, List<string> warnings)
  {
    object? locationValue = null;
    try { locationValue = Enum.Parse(typeof(Autodesk.Civil.BandLocationType), location, ignoreCase: true); } catch { }
    foreach (var ctor in collectionType.GetConstructors(BindingFlags.Public | BindingFlags.Instance).OrderBy(c => c.GetParameters().Length))
    {
      var ps = ctor.GetParameters();
      try
      {
        if (ps.Length == 0) return ctor.Invoke(Array.Empty<object>());
        if (ps.Length == 1 && ps[0].ParameterType.IsEnum && locationValue != null) return ctor.Invoke(new[] { locationValue });
        if (ps.Length == 2 && ps[0].ParameterType == typeof(ObjectId) && ps[1].ParameterType.IsEnum && locationValue != null) return ctor.Invoke(new[] { set.ObjectId, locationValue });
        if (ps.Length == 2 && ps[1].ParameterType == typeof(ObjectId) && ps[0].ParameterType.IsEnum && locationValue != null) return ctor.Invoke(new[] { locationValue, set.ObjectId });
      }
      catch (Exception ex) { warnings.Add($"new {collectionType.Name}({string.Join(", ", ps.Select(p => p.ParameterType.Name))}): {Civil3DCompatibility.DescribeException(ex)}"); }
    }
    return null;
  }

  internal static ObjectId FindBandStyle(CivilDocument civilDoc, Transaction transaction, string name, string? family)
  {
    var families = family != null ? new[] { family } : BandFamilies.Keys.ToArray();
    foreach (var fam in families)
    {
      if (!BandFamilies.TryGetValue(fam, out var path)) continue;
      var collection = ResolvePath(civilDoc.Styles, path);
      if (collection == null) continue;
      foreach (var id in CivilObjectUtils.ToObjectIds(collection))
      {
        if (id.IsNull) continue;
        if (string.Equals(CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead)), name, StringComparison.OrdinalIgnoreCase)) return id;
      }
    }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Band style '{name}' not found in {string.Join(", ", families)}. Create it first (family band:profile_data / band:horizontal_geometry / band:vertical_geometry ...).");
  }

  internal static void ApplyBandSet(BandSetStyle set, JsonObject spec, CivilDocument civilDoc, Transaction transaction, List<string> applied, List<string> warnings)
  {
    if (spec["items"] is not JsonArray items) return;
    var replace = spec["replaceItems"]?.GetValue<bool>() ?? true;
    foreach (var (getter, setter, location) in new[] { ("GetTopBandSetItems", "SetTopBandSetItems", "top"), ("GetBottomBandSetItems", "SetBottomBandSetItems", "bottom") })
    {
      var wanted = items.OfType<JsonObject>().Where(i => string.Equals(i["location"]?.GetValue<string>() ?? "bottom", location, StringComparison.OrdinalIgnoreCase)).ToList();
      if (wanted.Count == 0 && !replace) continue;
      var collection = Civil3DCompatibility.InvokeMatchingOverload(set, null, getter, Array.Empty<object?>(), out var err);
      if (err != null || collection == null) { warnings.Add($"{getter}: {err ?? "no collection"}"); continue; }
      var existing = 0;
      try { existing = Convert.ToInt32(Civil3DCompatibility.GetPropertyValue(collection, "Count") ?? 0); } catch { }
      // Setting an empty collection on a side that is already empty throws (NullReference) in C3D 2026 — nothing to do there.
      if (wanted.Count == 0 && existing == 0) continue;
      // The collection handed back by Get*BandSetItems() on a freshly created set is not initialised (Add throws
      // NullReference); build a new ProfileViewBandSetItemCollection / SectionViewBandSetItemCollection instead.
      if (replace)
      {
        var fresh = NewItemCollection(set, collection.GetType(), location, warnings);
        if (fresh != null) { collection = fresh; existing = 0; }
      }
      if (replace && existing > 0)
      {
        var removeErr = (string?)null;
        Civil3DCompatibility.InvokeMatchingOverload(collection, null, "RemoveAll", Array.Empty<object?>(), out removeErr);
        if (removeErr != null)
        {
          // fall back to RemoveAt from the end
          var count = Convert.ToInt32(Civil3DCompatibility.GetPropertyValue(collection, "Count") ?? 0);
          for (var i = count - 1; i >= 0; i--) Civil3DCompatibility.InvokeMatchingOverload(collection, null, "RemoveAt", new object?[] { i }, out _);
        }
      }
      foreach (var itemSpec in wanted)
      {
        var bandName = itemSpec["band"]?.GetValue<string>() ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "band set items need 'band' (band style name).");
        var bandId = FindBandStyle(civilDoc, transaction, bandName, itemSpec["family"]?.GetValue<string>());
        var added = Civil3DCompatibility.InvokeMatchingOverload(collection, null, "Add", new object?[] { bandId }, out var addErr);
        if (addErr != null) throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"band set item '{bandName}': {addErr}");
        object? item = added;
        if (item == null || item is ObjectId)
        {
          var count = Convert.ToInt32(Civil3DCompatibility.GetPropertyValue(collection, "Count") ?? 0);
          item = Civil3DCompatibility.GetIndexedPropertyValue(collection, "Item", count - 1);
        }
        if (item == null) { warnings.Add($"band set item '{bandName}': added but not returned; its options were left at defaults"); continue; }
        foreach (var prop in ItemProperties)
        {
          var key = char.ToLowerInvariant(prop[0]) + prop[1..];
          if (itemSpec[key] is not JsonValue v) continue;
          object? value = v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<double>(out var d) ? d : v.GetValue<string>();
          if (value is double dv && SizeProperties.Contains(key)) value = dv * PaperScale;
          if (!Civil3DCompatibility.TrySetPropertyValue(item, prop, value, out var setErr)) warnings.Add($"band set item '{bandName}'.{key}: {setErr}");
        }
        applied.Add($"items.{location}.{bandName}");
      }
      Civil3DCompatibility.InvokeMatchingOverload(set, null, setter, new object?[] { collection }, out var setCollErr);
      if (setCollErr != null) throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"{setter}: {setCollErr}");
    }
  }
}
