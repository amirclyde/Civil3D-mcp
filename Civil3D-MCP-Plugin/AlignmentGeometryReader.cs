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
    result["clrType"] = row.Entity.GetType().Name;
    result["start"] = PointAt(alignment, row.StartStation);
    result["end"] = PointAt(alignment, row.EndStation);
    result["properties"] = DumpProperties(row.Entity, errors);

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
      subEntities.Add(new Dictionary<string, object?>
      {
        ["index"] = i,
        ["clrType"] = subEntity.GetType().Name,
        ["properties"] = DumpProperties(subEntity, subErrors),
        ["errors"] = subErrors.Count > 0 ? subErrors : null,
      });
    }

    result["subEntities"] = subEntities;
    if (errors.Count > 0)
    {
      result["errors"] = errors;
    }

    return result;
  }

  internal static (string? Name, string? Error) ReadStyleName(Alignment alignment, Transaction transaction)
  {
    var styleName = TryRead(alignment, "StyleName", out var styleNameError) as string;
    if (!string.IsNullOrWhiteSpace(styleName))
    {
      return (styleName, null);
    }

    try
    {
      var style = transaction.GetObject(alignment.StyleId, OpenMode.ForRead);
      var name = TryRead(style, "Name", out var nameError) as string;
      if (!string.IsNullOrWhiteSpace(name))
      {
        return (name, null);
      }

      return (null, $"StyleName: {styleNameError ?? "empty"}; {style.GetType().Name}.Name: {nameError ?? "empty"}");
    }
    catch (System.Exception ex)
    {
      return (null, $"StyleName: {styleNameError ?? "empty"}; style lookup: {Describe(ex)}");
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
