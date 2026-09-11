using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// UTNM additions for the c3d-alignment-from-ip skill.
///
/// utnmGetAlignmentGeometry (read-only): every entity in station order with exact stations and
/// lengths, start/end coordinates, Civil 3D's entity type, and every readable property of the
/// entity and its sub-entities (spiral in / arc / spiral out for groups).
///
/// This first version is deliberately generic: it reports what the Civil 3D 2026 API actually
/// exposes. Once the live output confirms the property names, the verification step of the skill
/// will pick out radius, direction, spiral lengths and key points from it.
/// </summary>
public static class UtnmAlignmentCommands
{
  public static Task<object?> GetAlignmentGeometryAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, name);
      var rows = AlignmentGeometryReader.ReadEntities(alignment);

      return new Dictionary<string, object?>
      {
        ["name"] = alignment.Name,
        ["handle"] = CivilObjectUtils.GetHandle(alignment),
        ["startStation"] = alignment.StartingStation,
        ["endStation"] = alignment.EndingStation,
        ["length"] = alignment.Length,
        ["entityCount"] = rows.Count,
        ["entities"] = rows
          .Select((row, order) => AlignmentGeometryReader.DescribeEntity(alignment, row, order, detailed: true))
          .ToList(),
      };
    });
  }
}
