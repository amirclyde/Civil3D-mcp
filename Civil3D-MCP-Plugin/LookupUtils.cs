using System.Collections;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

public static class LookupUtils
{
  public static ObjectId GetLayerId(Database database, Transaction transaction, string? layerName)
  {
    if (string.IsNullOrWhiteSpace(layerName))
    {
      return database.Clayer;
    }

    var layerTable = CivilObjectUtils.GetRequiredObject<LayerTable>(transaction, database.LayerTableId, OpenMode.ForRead);
    if (layerTable.Has(layerName))
    {
      return layerTable[layerName];
    }

    return database.Clayer;
  }

  public static ObjectId GetSiteId(CivilDocument civilDoc, Transaction transaction, string? siteName)
  {
    if (string.IsNullOrWhiteSpace(siteName))
    {
      return ObjectId.Null;
    }

    foreach (ObjectId objectId in civilDoc.GetSiteIds())
    {
      var site = CivilObjectUtils.GetRequiredObject<Site>(transaction, objectId, OpenMode.ForRead);
      if (string.Equals(site.Name, siteName, StringComparison.OrdinalIgnoreCase))
      {
        return objectId;
      }
    }

    return ObjectId.Null;
  }

  public static ObjectId GetAlignmentStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.AlignmentStyles, transaction, styleName);
  }

  public static ObjectId GetProfileStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.ProfileStyles, transaction, styleName, PreferredDesignProfileStyles);
  }

  /// <summary>Default profile styles tried, in order, when the caller names none (a layout profile is a design line, not EG).</summary>
  private static readonly string[] PreferredDesignProfileStyles = { "Design Profile", "Proposed", "Finished Ground", "Layout", "Basic" };

  public static ObjectId GetFeatureLineStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.FeatureLineStyles, transaction, styleName);
  }

  public static ObjectId GetSurfaceStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.SurfaceStyles, transaction, styleName);
  }

  public static ObjectId GetAlignmentLabelSetId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles, transaction, styleName);
  }

  public static ObjectId GetProfileLabelSetId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.LabelSetStyles.ProfileLabelSetStyles, transaction, styleName);
  }

  public static ObjectId GetProfileViewStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    var styles = CivilObjectUtils.GetPropertyValue<object>(civilDoc.Styles, "ProfileViewStyles");
    return styles != null
      ? GetStyleId(styles, transaction, styleName)
      : ObjectId.Null;
  }

  public static ObjectId GetProfileViewBandSetId(CivilDocument civilDoc, Transaction transaction, string? bandSetName)
  {
    if (string.IsNullOrWhiteSpace(bandSetName))
    {
      return ObjectId.Null;
    }

    // Band sets live on StylesRoot (civilDoc.Styles.ProfileViewBandSetStyles), not under LabelSetStyles.
    var bandSetStyles = (object?)CivilObjectUtils.GetPropertyValue<object>(civilDoc.Styles, "ProfileViewBandSetStyles")
      ?? CivilObjectUtils.GetPropertyValue<object>(civilDoc.Styles.LabelSetStyles, "ProfileViewBandSetStyles");
    if (bandSetStyles == null) return ObjectId.Null;
    var id = GetStyleId(bandSetStyles, transaction, bandSetName);
    if (id.IsNull) throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Profile view band set '{bandSetName}' was not found.");
    return id;
  }

  public static ObjectId GetParcelStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.ParcelStyles, transaction, styleName);
  }

  public static ObjectId GetParcelAreaLabelStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.LabelStyles.ParcelLabelStyles.AreaLabelStyles, transaction, styleName);
  }

  public static ObjectId GetSectionViewStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return GetStyleId(civilDoc.Styles.SectionViewStyles, transaction, styleName);
  }

  public static ObjectId GetSectionViewBandSetId(CivilDocument civilDoc, Transaction transaction, string? bandSetName)
  {
    return string.IsNullOrWhiteSpace(bandSetName)
      ? ObjectId.Null
      : GetStyleId(civilDoc.Styles.SectionViewBandSetStyles, transaction, bandSetName);
  }

  public static ObjectId GetGroupPlotStyleId(CivilDocument civilDoc, Transaction transaction, string? styleName)
  {
    return string.IsNullOrWhiteSpace(styleName)
      ? ObjectId.Null
      : GetStyleId(civilDoc.Styles.GroupPlotStyles, transaction, styleName);
  }

  /// <summary>Style by name, or the first of <paramref name="preferredDefaults"/> that exists, or the first style.</summary>
  public static ObjectId GetStyleIdPreferring(object collection, Transaction transaction, string? styleName, string[] preferredDefaults)
  {
    return GetStyleId(collection, transaction, styleName, preferredDefaults);
  }

  public static string? GetFirstStyleName(object? collection, Transaction transaction)
  {
    foreach (var objectId in EnumerateObjectIds(collection))
    {
      if (objectId == ObjectId.Null)
      {
        continue;
      }

      var style = transaction.GetObject(objectId, OpenMode.ForRead);
      return CivilObjectUtils.GetName(style);
    }

    return null;
  }

  private static ObjectId GetStyleId(object collection, Transaction transaction, string? styleName, string[]? preferredDefaults = null)
  {
    if (string.IsNullOrWhiteSpace(styleName) && preferredDefaults != null)
    {
      foreach (var preferred in preferredDefaults)
      {
        ObjectId preferredId;
        try { preferredId = GetStyleId(collection, transaction, preferred); }
        catch (JsonRpcDispatchException) { continue; }
        if (!preferredId.IsNull)
        {
          var candidate = transaction.GetObject(preferredId, OpenMode.ForRead);
          if (string.Equals(CivilObjectUtils.GetName(candidate), preferred, StringComparison.OrdinalIgnoreCase))
          {
            return preferredId;
          }
        }
      }
    }

    var fallback = ObjectId.Null;
    var available = new List<string>();

    foreach (var objectId in EnumerateObjectIds(collection))
    {
      if (objectId == ObjectId.Null)
      {
        continue;
      }

      if (fallback == ObjectId.Null)
      {
        fallback = objectId;
      }

      if (string.IsNullOrWhiteSpace(styleName))
      {
        continue;
      }

      var style = transaction.GetObject(objectId, OpenMode.ForRead);
      var name = CivilObjectUtils.GetName(style);
      if (string.Equals(name, styleName, StringComparison.OrdinalIgnoreCase))
      {
        return objectId;
      }
      if (name != null && available.Count < 40) available.Add(name);
    }

    if (!string.IsNullOrWhiteSpace(styleName))
    {
      // A named style that does not exist is an error, never a silent substitution:
      // the previous behaviour handed back the first style in the collection, so a typo
      // produced "Basic" where "UTNM Drain" was asked for and nobody noticed.
      var collectionName = collection?.GetType().Name.Replace("Collection", "") ?? "style";
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
        $"{collectionName} '{styleName}' was not found in this drawing. Available: {(available.Count > 0 ? string.Join(", ", available) : "(none)")}. Create it with civil3d_style create, or omit the style to use the drawing default.");
    }

    return fallback;
  }

  private static IEnumerable<ObjectId> EnumerateObjectIds(object? collection)
  {
    if (collection is ObjectIdCollection objectIds)
    {
      foreach (ObjectId objectId in objectIds)
      {
        yield return objectId;
      }

      yield break;
    }

    if (collection is IEnumerable enumerable)
    {
      foreach (var item in enumerable)
      {
        if (item is ObjectId objectId)
        {
          yield return objectId;
        }
      }
    }
  }
}
