using System.Collections;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Reads horizontal alignment entities for reporting.
///
/// Fixes over the original getAlignment implementation:
/// - entities are returned in station order (collection order is creation order);
/// - length is EndStation - StartStation, which is exact for every entity and group type
///   (the Length getter was returning nothing for lines and arcs);
/// - Civil 3D's own entity type name is kept, so groups such as SpiralCurveSpiral are no
///   longer collapsed into "spiral";
/// - failed property reads are captured as diagnostics instead of silently becoming 0 or "".
///
/// Reads go through the Civil3DCompatibility reflection boundary on purpose: they cannot fail to
/// compile against a Civil 3D version, and the detailed mode doubles as API discovery for the
/// UTNM geometry report.
/// </summary>
internal static class AlignmentGeometryReader
{
  internal sealed record EntityRow(int Index, object Entity, string RawType, double StartStation, double EndStation);

  internal static List<EntityRow> ReadEntities(Alignment alignment)
  {
    var rows = new List<EntityRow>();
    if (TryRead(alignment, "Entities", out _) is not IEnumerable enumerable)
    {
      return rows;
    }

    var index = 0;
    foreach (var entity in enumerable)
    {
      if (entity == null)
      {
        index++;
        continue;
      }

      var rawType = TryRead(entity, "EntityType", out _)?.ToString();
      if (string.IsNullOrWhiteSpace(rawType))
      {
        rawType = entity.GetType().Name;
      }

      var start = ToDouble(TryRead(entity, "StartStation", out _)) ?? 0;
      var end = ToDouble(TryRead(entity, "EndStation", out _)) ?? 0;
      rows.Add(new EntityRow(index, entity, rawType, start, end));
      index++;
    }

    return rows.OrderBy(row => row.StartStation).ThenBy(row => row.Index).ToList();
  }

  internal static string MapEntityType(string rawType)
  {
    var text = rawType.Trim();
    if (text.StartsWith("Alignment", StringComparison.Ordinal) && text.Length > "Alignment".Length)
    {
      text = text.Substring("Alignment".Length);
    }

    return text.ToLowerInvariant() switch
    {
      "line" => "line",
      "arc" => "arc",
      "spiral" => "spiral",
      _ => ToSnakeCase(text),
    };
  }

  internal static Dictionary<string, object?> DescribeEntity(Alignment alignment, EntityRow row, int order, bool detailed)
  {
    var result = new Dictionary<string, object?>
    {
      ["index"] = row.Index,
      ["order"] = order,
      ["type"] = MapEntityType(row.RawType),
      ["entityType"] = row.RawType,
      ["startStation"] = row.StartStation,
      ["endStation"] = row.EndStation,
      ["length"] = row.EndStation - row.StartStation,
    };

    if (!detailed)
    {
      return result;
    }

    var errors = new Dictionary<string, string>();
    var suppressed = 0;
    result["clrType"] = row.Entity.GetType().Name;
    result["start"] = PointAt(alignment, row.StartStation);
    result["end"] = PointAt(alignment, row.EndStation);
    result["properties"] = CleanReport(DumpProperties(row.Entity, errors), errors, ref suppressed);

    var subEntityCount = ToInt(TryRead(row.Entity, "SubEntityCount", out var countError));
    if (countError != null)
    {
      errors["SubEntityCount"] = countError;
    }

    result["subEntityCount"] = subEntityCount;
    var subEntities = new List<object?>();
    for (var i = 0; i < (subEntityCount ?? 0); i++)
    {
      var subEntity = ReadSubEntity(row.Entity, i, out var subError);
      if (subEntity == null)
      {
        errors[$"subEntity[{i}]"] = subError ?? "returned null";
        continue;
      }

      var subErrors = new Dictionary<string, string>();
      var subProperties = CleanReport(DumpProperties(subEntity, subErrors), subErrors, ref suppressed);
      subEntities.Add(new Dictionary<string, object?>
      {
        ["index"] = i,
        ["clrType"] = subEntity.GetType().Name,
        ["properties"] = subProperties,
        ["errors"] = subErrors.Count > 0 ? subErrors : null,
      });
    }

    result["subEntities"] = subEntities;
    if (errors.Count > 0)
    {
      result["errors"] = errors;
    }

    if (suppressed > 0)
    {
      result["suppressedReads"] = suppressed;
    }

    return result;
  }

  // Values that carry no geometry (wrapper plumbing, neighbour links, empty group indices).
  private static readonly HashSet<string> NoiseProperties = new(StringComparer.Ordinal)
  {
    "AutoDelete", "IsDisposed", "UnmanagedObject", "CurveGroupIndex", "CurveGroupSubEntityIndex",
    "EntityBefore", "EntityAfter",
  };

  /// <summary>
  /// Removes report noise found in live Civil 3D 2026 output: wrapper plumbing, placeholder values
  /// for objects already reported as sub-entities, and reads that are expected to fail — getters
  /// such as DirectionAtPoint1 or PassThroughPoint2 throw "Invalid Operation" on free/floating
  /// arcs, and EntityBefore/EntityAfter throw at the ends of the alignment.
  /// </summary>
  internal static Dictionary<string, object?> CleanReport(
    Dictionary<string, object?> properties,
    Dictionary<string, string> errors,
    ref int suppressed)
  {
    var cleaned = new Dictionary<string, object?>(StringComparer.Ordinal);
    foreach (var (name, value) in properties)
    {
      if (NoiseProperties.Contains(name)
        || value is string text && (text.Length == 0 || (text.StartsWith("<", StringComparison.Ordinal) && text.EndsWith(">", StringComparison.Ordinal))))
      {
        continue;
      }

      cleaned[name] = value;
    }

    foreach (var name in errors.Keys.ToList())
    {
      var message = errors[name];
      if (NoiseProperties.Contains(name)
        || message.StartsWith("InvalidOperationException", StringComparison.Ordinal)
        || message.StartsWith("EntityNotFoundException", StringComparison.Ordinal))
      {
        errors.Remove(name);
        suppressed++;
      }
    }

    return cleaned;
  }

  internal static (string? Name, string? Error) ReadStyleName(Alignment alignment, Transaction transaction)
    => ReadStyleName(alignment, alignment.StyleId, transaction);

  internal static (string? Name, string? Error) ReadStyleName(object entity, ObjectId styleId, Transaction transaction)
  {
    if (TryRead(entity, "StyleName", out _) is string styleName && !string.IsNullOrWhiteSpace(styleName))
    {
      return (styleName, null);
    }

    if (styleId.IsNull)
    {
      return (null, "no style assigned");
    }

    try
    {
      var style = transaction.GetObject(styleId, OpenMode.ForRead);
      var name = Civil3DCompatibility.TryReadName(style, out var diagnostics);
      return string.IsNullOrWhiteSpace(name) ? (null, diagnostics) : (name, null);
    }
    catch (System.Exception ex)
    {
      return (null, $"style lookup: {Describe(ex)}");
    }
  }

  internal static (List<string> Names, string? Error) ReadDependentCorridors(
    CivilDocument civilDoc,
    Transaction transaction,
    ObjectId alignmentId)
  {
    var names = new List<string>();
    try
    {
      foreach (ObjectId corridorId in civilDoc.CorridorCollection)
      {
        var corridor = CivilObjectUtils.GetRequiredObject<Corridor>(transaction, corridorId, OpenMode.ForRead);
        foreach (Baseline baseline in corridor.Baselines)
        {
          if (TryRead(baseline, "AlignmentId", out _) is ObjectId baselineAlignmentId
            && baselineAlignmentId == alignmentId
            && !names.Contains(corridor.Name))
          {
            names.Add(corridor.Name);
          }
        }
      }

      return (names, null);
    }
    catch (System.Exception ex)
    {
      return (names, Describe(ex));
    }
  }

  internal static List<object> ReadSubEntities(object entity)
  {
    var subEntities = new List<object>();
    var count = ToInt(TryRead(entity, "SubEntityCount", out _)) ?? 0;
    for (var i = 0; i < count; i++)
    {
      if (ReadSubEntity(entity, i, out _) is { } subEntity)
      {
        subEntities.Add(subEntity);
      }
    }

    return subEntities;
  }

  internal static object? TryRead(object? target, string propertyName, out string? error)
    => Civil3DCompatibility.TryReadProperty(target, propertyName, out error);

  private static object? ReadSubEntity(object entity, int index, out string? error)
    => Civil3DCompatibility.TryReadIntIndexer(entity, index, out error);

  private static Dictionary<string, object?> DumpProperties(object target, Dictionary<string, string> errors)
    => Civil3DCompatibility.ReadAllPropertiesForReport(target, errors);

  private static Dictionary<string, object?> PointAt(Alignment alignment, double station)
  {
    try
    {
      var clamped = Math.Max(alignment.StartingStation, Math.Min(station, alignment.EndingStation));
      double x = 0;
      double y = 0;
      alignment.PointLocation(clamped, 0, ref x, ref y);
      return new Dictionary<string, object?> { ["x"] = x, ["y"] = y };
    }
    catch (System.Exception ex)
    {
      return new Dictionary<string, object?> { ["error"] = Describe(ex) };
    }
  }

  private static double? ToDouble(object? value)
  {
    try
    {
      return value == null ? null : Convert.ToDouble(value);
    }
    catch
    {
      return null;
    }
  }

  private static int? ToInt(object? value)
  {
    try
    {
      return value == null ? null : Convert.ToInt32(value);
    }
    catch
    {
      return null;
    }
  }

  private static string ToSnakeCase(string text)
  {
    var builder = new System.Text.StringBuilder(text.Length + 8);
    for (var i = 0; i < text.Length; i++)
    {
      var character = text[i];
      if (char.IsUpper(character) && i > 0 && !char.IsUpper(text[i - 1]))
      {
        builder.Append('_');
      }

      builder.Append(char.ToLowerInvariant(character));
    }

    return builder.ToString();
  }

  private static string Describe(System.Exception ex)
    => Civil3DCompatibility.DescribeException(ex);

}
