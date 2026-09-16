using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Ribbon "Alignment Creation Tools" and "Create Connected Alignment" on the documented Civil 3D 2026 API:
///
///   Alignment.Create(doc, name, siteId, layerId, styleId, labelSetId)            - empty layout alignment
///   AlignmentEntityCollection.AddFixedLine / AddFloatingLine / AddFreeLine
///   AlignmentEntityCollection.AddFixedCurve / AddFloatingCurve / AddFreeCurve
///   AlignmentEntityCollection.AddFreeSCS / AddFreeSpiral / AddFixedSpiral / AddFloatSpiral
///   AlignmentEntityCollection.Remove(entity)
///   Alignment.CreateConnectedAlignment(name, siteId, layerId, styleId, labelSetId, ConnectedAlignmentParams)
///
/// Entities are addressed by their ORDER along the alignment (0 = first), never by internal id.
/// </summary>
public static class AlignmentLayoutCommands
{
  // -------------------------------------------------------------------------
  // alignmentCreateLayout: an empty alignment to build entity by entity
  // -------------------------------------------------------------------------

  public static Task<object?> CreateLayoutAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var siteName = PluginRuntime.GetOptionalString(parameters, "site");
    var layer = PluginRuntime.GetOptionalString(parameters, "layer");
    var style = PluginRuntime.GetOptionalString(parameters, "style");
    var labelSet = PluginRuntime.GetOptionalString(parameters, "labelSet");
    var startStation = PluginRuntime.GetOptionalDouble(parameters, "startStation");
    var description = PluginRuntime.GetOptionalString(parameters, "description");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      if (AlignmentExists(civilDoc, transaction, name))
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Alignment '{name}' already exists. Nothing was created.");

      var siteId = LookupUtils.GetSiteId(civilDoc, transaction, siteName);
      var layerId = LookupUtils.GetLayerId(database, transaction, layer);
      var styleId = LookupUtils.GetAlignmentStyleId(civilDoc, transaction, style);
      var labelSetId = LookupUtils.GetAlignmentLabelSetId(civilDoc, transaction, labelSet);

      ObjectId id;
      try { id = Alignment.Create(civilDoc, name, siteId, layerId, styleId, labelSetId); }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Civil 3D could not create alignment '{name}': {ex.GetType().Name}: {ex.Message}");
      }

      var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, id, OpenMode.ForWrite);
      if (!string.IsNullOrWhiteSpace(description)) alignment.Description = description;
      if (startStation.HasValue) alignment.ReferencePointStation = startStation.Value;

      return new Dictionary<string, object?>
      {
        ["name"] = alignment.Name,
        ["handle"] = CivilObjectUtils.GetHandle(alignment),
        ["site"] = siteId.IsNull ? null : siteName,
        ["style"] = AlignmentGeometryReader.ReadStyleName(alignment, transaction).Name,
        ["entityCount"] = alignment.Entities.Count,
        ["created"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // alignmentEntityAdd: one action, `kind` selects the layout tool
  // -------------------------------------------------------------------------

  public static Task<object?> EntityAddAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var kind = PluginRuntime.GetRequiredString(parameters, "kind").ToLowerInvariant();

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var found = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, found.ObjectId, OpenMode.ForWrite);
      var entities = alignment.Entities;
      var countBefore = entities.Count;

      int PrevId() => ResolveEntityId(entities, parameters, "previousEntityIndex", "previousEntityId");
      int NextId() => ResolveEntityId(entities, parameters, "nextEntityIndex", "nextEntityId");
      bool HasPrev() => PluginRuntime.GetOptionalInt(parameters, "previousEntityIndex").HasValue || PluginRuntime.GetOptionalInt(parameters, "previousEntityId").HasValue;
      bool HasNext() => PluginRuntime.GetOptionalInt(parameters, "nextEntityIndex").HasValue || PluginRuntime.GetOptionalInt(parameters, "nextEntityId").HasValue;
      Point3d Pt(string prefix) => new(RequiredDouble(parameters, prefix + "X"), RequiredDouble(parameters, prefix + "Y"), 0);
      bool Clockwise() => PluginRuntime.GetOptionalBool(parameters, "isClockwise") ?? true;
      bool Over180() => PluginRuntime.GetOptionalBool(parameters, "isGreaterThan180") ?? false;
      var spiralDefinition = ParseSpiralType(PluginRuntime.GetOptionalString(parameters, "spiralType"));
      var curveType = string.Equals(PluginRuntime.GetOptionalString(parameters, "curveType"), "reverse", StringComparison.OrdinalIgnoreCase) ? CurveType.Reverse : CurveType.Compound;

      string method;
      try
      {
        switch (kind)
        {
          // ---- lines ---------------------------------------------------------
          case "fixed_line":
            if (HasPrev()) { entities.AddFixedLine(PrevId(), Pt("start"), Pt("end")); method = "AddFixedLine(prev, start, end)"; }
            else { entities.AddFixedLine(Pt("start"), Pt("end")); method = "AddFixedLine(start, end)"; }
            break;
          case "fixed_line_by_length":
            entities.AddFixedLine(PrevId(), RequiredDouble(parameters, "length")); method = "AddFixedLine(prev, distance)";
            break;
          case "floating_line":
            if (HasPrev())
            {
              if (PluginRuntime.GetOptionalDouble(parameters, "length") is double len) { entities.AddFloatingLine(PrevId(), len); method = "AddFloatingLine(prev, length)"; }
              else { entities.AddFloatingLine(PrevId(), Pt("passThrough")); method = "AddFloatingLine(prev, passThrough)"; }
            }
            else if (HasNext())
            {
              if (PluginRuntime.GetOptionalDouble(parameters, "length") is double len) { entities.AddFloatingLine(len, NextId()); method = "AddFloatingLine(length, next)"; }
              else { entities.AddFloatingLine(Pt("passThrough"), NextId()); method = "AddFloatingLine(passThrough, next)"; }
            }
            else throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "floating_line needs previousEntityIndex or nextEntityIndex, plus length or passThroughX/Y.");
            break;
          case "free_line":
            entities.AddFreeLine(PrevId(), NextId()); method = "AddFreeLine(prev, next)";
            break;

          // ---- curves --------------------------------------------------------
          case "fixed_curve":
            if (HasPrev() && parameters?["middleX"] != null)
            {
              entities.AddFixedCurve(PrevId(), Pt("start"), Pt("middle"), Pt("end")); method = "AddFixedCurve(prev, start, middle, end)";
            }
            else if (parameters?["centerX"] != null)
            {
              if (parameters?["passThroughX"] != null) { entities.AddFixedCurve(Pt("center"), Pt("passThrough"), Clockwise()); method = "AddFixedCurve(center, passThrough, cw)"; }
              else { entities.AddFixedCurve(Pt("center"), RequiredDouble(parameters, "radius"), Clockwise()); method = "AddFixedCurve(center, radius, cw)"; }
            }
            else
            {
              entities.AddFixedCurve(Pt("start"), Pt("end"), RequiredDouble(parameters, "radius"), Clockwise()); method = "AddFixedCurve(pt1, pt2, radius, cw)";
            }
            break;
          case "floating_curve":
            if (HasPrev())
            {
              if (parameters?["passThroughX"] != null)
              {
                if (PluginRuntime.GetOptionalDouble(parameters, "radius") is double r)
                { entities.AddFloatingCurve(PrevId(), Pt("passThrough"), r, Over180(), curveType); method = "AddFloatingCurve(prev, passThrough, radius, >180, curveType)"; }
                else { entities.AddFloatingCurve(PrevId(), Pt("passThrough")); method = "AddFloatingCurve(prev, passThrough)"; }
              }
              else
              {
                var (value, type) = CurveParam(parameters);
                entities.AddFloatingCurve(PrevId(), RequiredDouble(parameters, "radius"), value, type, Clockwise()); method = $"AddFloatingCurve(prev, radius, {type}={value}, cw)";
              }
            }
            else if (HasNext())
            {
              if (parameters?["passThroughX"] != null)
              {
                if (PluginRuntime.GetOptionalDouble(parameters, "radius") is double r)
                { entities.AddFloatingCurve(Pt("passThrough"), r, Over180(), curveType, NextId()); method = "AddFloatingCurve(passThrough, radius, >180, curveType, next)"; }
                else { entities.AddFloatingCurve(Pt("passThrough"), NextId()); method = "AddFloatingCurve(passThrough, next)"; }
              }
              else
              {
                var (value, type) = CurveParam(parameters);
                entities.AddFloatingCurve(RequiredDouble(parameters, "radius"), value, type, Clockwise(), NextId()); method = $"AddFloatingCurve(radius, {type}={value}, cw, next)";
              }
            }
            else throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "floating_curve needs previousEntityIndex or nextEntityIndex.");
            break;
          case "free_curve":
            if (parameters?["passThroughX"] != null)
            { entities.AddFreeCurve(PrevId(), NextId(), Pt("passThrough")); method = "AddFreeCurve(prev, next, passThrough)"; }
            else
            {
              var (value, type) = CurveParam(parameters);
              entities.AddFreeCurve(PrevId(), NextId(), value, type, Over180(), curveType); method = $"AddFreeCurve(prev, next, {type}={value}, >180, {curveType})";
            }
            break;

          // ---- spirals -------------------------------------------------------
          case "free_scs":
          {
            var (p1, p2, spType) = SpiralParams(parameters);
            entities.AddFreeSCS(PrevId(), NextId(), p1, p2, spType, RequiredDouble(parameters, "radius"), Over180(), spiralDefinition);
            method = $"AddFreeSCS(prev, next, {spType} {p1}/{p2}, radius, >180, {spiralDefinition})";
            break;
          }
          case "free_spiral":
          {
            var (p1, _, spType) = SpiralParams(parameters, requireSecond: false);
            var scType = string.Equals(PluginRuntime.GetOptionalString(parameters, "spiralCurveType"), "out", StringComparison.OrdinalIgnoreCase) ? SpiralCurveType.OutCurve : SpiralCurveType.InCurve;
            entities.AddFreeSpiral(PrevId(), NextId(), p1, spType, scType, spiralDefinition);
            method = $"AddFreeSpiral(prev, next, {spType} {p1}, {scType}, {spiralDefinition})";
            break;
          }
          case "fixed_spiral":
            entities.AddFixedSpiral(PrevId(), RequiredDouble(parameters, "startRadius"), RequiredDouble(parameters, "endRadius"), RequiredDouble(parameters, "length"), spiralDefinition);
            method = "AddFixedSpiral(prev, startRadius, endRadius, length, spiralType)";
            break;
          case "floating_spiral":
            if (HasPrev()) { entities.AddFloatSpiral(PrevId(), RequiredDouble(parameters, "radius"), RequiredDouble(parameters, "length"), Clockwise(), spiralDefinition); method = "AddFloatSpiral(prev, radius, length, cw, spiralType)"; }
            else { entities.AddFloatSpiral(RequiredDouble(parameters, "radius"), RequiredDouble(parameters, "length"), NextId(), Clockwise(), spiralDefinition); method = "AddFloatSpiral(radius, length, next, cw, spiralType)"; }
            break;
          case "free_sts":
            entities.AddFreeSTS(PrevId(), NextId(), RequiredDouble(parameters, "length"), spiralDefinition); method = "AddFreeSTS(prev, next, tangentLength, spiralType)";
            break;
          default:
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
              $"Unknown kind '{kind}'. Use fixed_line, fixed_line_by_length, floating_line, free_line, fixed_curve, floating_curve, free_curve, free_scs, free_spiral, fixed_spiral, floating_spiral, free_sts.");
        }
      }
      catch (JsonRpcDispatchException) { throw; }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR",
          $"Civil 3D refused {kind} on '{alignment.Name}': {ex.GetType().Name}: {ex.Message}. Nothing was added.");
      }

      var summary = ReadEntities(alignment);
      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["kind"] = kind,
        ["method"] = method,
        ["entityCountBefore"] = countBefore,
        ["entityCount"] = entities.Count,
        ["startStation"] = alignment.StartingStation,
        ["endStation"] = alignment.EndingStation,
        ["length"] = alignment.Length,
        ["entities"] = summary,
        ["success"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // alignmentEntityDelete: by order along the alignment
  // -------------------------------------------------------------------------

  public static Task<object?> EntityDeleteAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var entityIndex = RequiredInt(parameters, "entityIndex");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var found = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, found.ObjectId, OpenMode.ForWrite);
      var entities = alignment.Entities;
      if (entityIndex < 0 || entityIndex >= entities.Count)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"entityIndex {entityIndex} is out of range; '{alignment.Name}' has {entities.Count} entities.");

      AlignmentEntity entity;
      try { entity = entities.GetEntityByOrder(entityIndex); }
      catch { entity = entities[entityIndex]; }
      var removed = DescribeEntity(entity, entityIndex);
      try { entities.Remove(entity); }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Civil 3D refused removing entity {entityIndex}: {ex.GetType().Name}: {ex.Message}. Nothing was changed.");
      }

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["removed"] = removed,
        ["entityCount"] = entities.Count,
        ["entities"] = ReadEntities(alignment),
        ["success"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // alignmentCreateConnected: ribbon "Create Connected Alignment"
  // -------------------------------------------------------------------------

  public static Task<object?> CreateConnectedAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var inName = PluginRuntime.GetRequiredString(parameters, "incomingAlignmentName");
    var inStation = PluginRuntime.GetRequiredDouble(parameters, "incomingStation");
    var outName = PluginRuntime.GetRequiredString(parameters, "outgoingAlignmentName");
    var outStation = PluginRuntime.GetRequiredDouble(parameters, "outgoingStation");
    var offsetIn = PluginRuntime.GetOptionalDouble(parameters, "offsetIn") ?? 0;
    var offsetOut = PluginRuntime.GetOptionalDouble(parameters, "offsetOut") ?? 0;
    var radius = PluginRuntime.GetRequiredDouble(parameters, "radius");
    var spiralIn = PluginRuntime.GetOptionalDouble(parameters, "spiralInLength");
    var spiralOut = PluginRuntime.GetOptionalDouble(parameters, "spiralOutLength");
    var overlapIn = PluginRuntime.GetOptionalDouble(parameters, "overlapIn") ?? 0;
    var overlapOut = PluginRuntime.GetOptionalDouble(parameters, "overlapOut") ?? 0;
    var greaterThan180 = PluginRuntime.GetOptionalBool(parameters, "isGreaterThan180") ?? false;
    var siteName = PluginRuntime.GetOptionalString(parameters, "site");
    var layer = PluginRuntime.GetOptionalString(parameters, "layer");
    var style = PluginRuntime.GetOptionalString(parameters, "style");
    var labelSet = PluginRuntime.GetOptionalString(parameters, "labelSet");
    var spiralDefinition = ParseSpiralType(PluginRuntime.GetOptionalString(parameters, "spiralType"));

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      if (AlignmentExists(civilDoc, transaction, name))
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Alignment '{name}' already exists. Nothing was created.");
      var incoming = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, inName);
      var outgoing = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, outName);
      CheckStation(incoming, inStation, "incomingStation");
      CheckStation(outgoing, outStation, "outgoingStation");

      var p = new ConnectedAlignmentParams
      {
        IncomingParentAlignmentId = incoming.ObjectId,
        IncomingParentAlignmentStation = inStation,
        OffsetIn = offsetIn,
        ConnectionOverlapLengthIn = overlapIn,
        OutgoingParentAlignmentId = outgoing.ObjectId,
        OutgoingParentAlignmentStation = outStation,
        OffsetOut = offsetOut,
        ConnectionOverlapLengthOut = overlapOut,
        CurveRadius = radius,
        GreaterThan180 = greaterThan180,
        SpiralDefinition = spiralDefinition,
      };
      var hasSpirals = spiralIn.HasValue || spiralOut.HasValue;
      if (hasSpirals)
      {
        p.SpiralInLength = spiralIn ?? 0;
        p.SpiralOutLength = spiralOut ?? 0;
      }
      TrySetCurveGroupType(p, hasSpirals);

      var siteId = LookupUtils.GetSiteId(civilDoc, transaction, siteName);
      var layerId = LookupUtils.GetLayerId(database, transaction, layer);
      var styleId = LookupUtils.GetAlignmentStyleId(civilDoc, transaction, style);
      var labelSetId = LookupUtils.GetAlignmentLabelSetId(civilDoc, transaction, labelSet);

      ObjectId id;
      try { id = Alignment.CreateConnectedAlignment(name, siteId, layerId, styleId, labelSetId, p); }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR",
          $"Civil 3D could not create the connected alignment: {ex.GetType().Name}: {ex.Message}. Check that the radius and spiral lengths fit between the two connection points.");
      }
      var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, id, OpenMode.ForRead);
      return new Dictionary<string, object?>
      {
        ["name"] = alignment.Name,
        ["handle"] = CivilObjectUtils.GetHandle(alignment),
        ["incomingAlignmentName"] = incoming.Name,
        ["outgoingAlignmentName"] = outgoing.Name,
        ["curveGroupType"] = p.CurveGroupType.ToString(),
        ["startStation"] = alignment.StartingStation,
        ["endStation"] = alignment.EndingStation,
        ["length"] = alignment.Length,
        ["entities"] = ReadEntities(alignment),
        ["created"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // helpers
  // -------------------------------------------------------------------------

  private static void TrySetCurveGroupType(ConnectedAlignmentParams p, bool withSpirals)
  {
    p.CurveGroupType = withSpirals ? CurbReturnCurveGroupType.SCS : CurbReturnCurveGroupType.Arc;
  }

  private static void CheckStation(Alignment alignment, double station, string label)
  {
    if (station < alignment.StartingStation - 1e-6 || station > alignment.EndingStation + 1e-6)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"{label} {station:0.###} is outside '{alignment.Name}' ({alignment.StartingStation:0.###} to {alignment.EndingStation:0.###}).");
  }

  private static (double value, CurveParamType type) CurveParam(JsonObject? parameters)
  {
    var typeName = (PluginRuntime.GetOptionalString(parameters, "curveParamType") ?? "curve_length").ToLowerInvariant();
    var value = PluginRuntime.GetOptionalDouble(parameters, "curveParamValue")
      ?? PluginRuntime.GetOptionalDouble(parameters, "length")
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give curveParamValue (with curveParamType) or length.");
    var type = typeName switch
    {
      "curve_length" or "length" => CurveParamType.CurveLength,
      "tangent_length" => CurveParamType.TangentLength,
      "chord_length" => CurveParamType.ChordLength,
      "curve_angle" or "angle" => CurveParamType.CurveAngle,
      "external" or "external_dist" => CurveParamType.ExternalDist,
      "middle_ordinate" => CurveParamType.MiddleOrdinate,
      "degree_of_curve" => CurveParamType.DegreeOfCurve,
      "radius" => CurveParamType.Radius,
      _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unknown curveParamType '{typeName}'."),
    };
    return (value, type);
  }

  private static (double p1, double p2, SpiralParamType type) SpiralParams(JsonObject? parameters, bool requireSecond = true)
  {
    var lenIn = PluginRuntime.GetOptionalDouble(parameters, "spiralInLength");
    var lenOut = PluginRuntime.GetOptionalDouble(parameters, "spiralOutLength");
    var aIn = PluginRuntime.GetOptionalDouble(parameters, "spiralInA");
    var aOut = PluginRuntime.GetOptionalDouble(parameters, "spiralOutA");
    if (lenIn.HasValue || lenOut.HasValue)
    {
      if (requireSecond && !(lenIn.HasValue && lenOut.HasValue))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give both spiralInLength and spiralOutLength.");
      return (lenIn ?? lenOut ?? 0, lenOut ?? lenIn ?? 0, SpiralParamType.Length);
    }
    if (aIn.HasValue || aOut.HasValue)
    {
      if (requireSecond && !(aIn.HasValue && aOut.HasValue))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give both spiralInA and spiralOutA.");
      return (aIn ?? aOut ?? 0, aOut ?? aIn ?? 0, SpiralParamType.AValue);
    }
    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Spiral parameters missing: spiralInLength/spiralOutLength or spiralInA/spiralOutA.");
  }

  private static SpiralType ParseSpiralType(string? name)
  {
    if (string.IsNullOrWhiteSpace(name)) return SpiralType.Clothoid;
    if (Enum.TryParse<SpiralType>(name, ignoreCase: true, out var value)) return value;
    return name.ToLowerInvariant() switch
    {
      "cubic" or "cubic_parabola" => SpiralType.CubicParabola,
      "biquadratic" => SpiralType.BiQuadratic,
      "bloss" => SpiralType.Bloss,
      "sinusoidal" => SpiralType.Sinusoidal,
      _ => throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unknown spiralType '{name}'."),
    };
  }

  /// <summary>
  /// Entity reference: `...Index` is the 0-based order along the alignment (connected entities only);
  /// when that fails (a not-yet-connected fixed entity) the same number is tried as a collection index;
  /// `...Id` addresses the Civil 3D entity id directly (as reported in `entities[].entityId`).
  /// </summary>
  private static int ResolveEntityId(AlignmentEntityCollection entities, JsonObject? parameters, string indexKey, string idKey)
  {
    var id = PluginRuntime.GetOptionalInt(parameters, idKey);
    if (id.HasValue)
    {
      try { return entities.EntityAtId(id.Value).EntityId; }
      catch { throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{idKey} {id.Value} is not an entity of this alignment. Ids: {string.Join(", ", AllEntityIds(entities))}."); }
    }
    var index = PluginRuntime.GetOptionalInt(parameters, indexKey)
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{indexKey} (or {idKey}) is required.");
    if (index < 0 || index >= entities.Count)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{indexKey} {index} is out of range; the alignment has {entities.Count} entities.");
    try { return entities.GetEntityByOrder(index).EntityId; } catch { }
    try { return entities[index].EntityId; } catch { }
    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{indexKey} {index} could not be resolved; use {idKey} with one of: {string.Join(", ", AllEntityIds(entities))}.");
  }

  private static List<int> AllEntityIds(AlignmentEntityCollection entities)
  {
    var ids = new List<int>();
    for (var i = 0; i < entities.Count; i++) { try { ids.Add(entities[i].EntityId); } catch { } }
    return ids;
  }

  private static int RequiredInt(JsonObject? parameters, string name)
    => PluginRuntime.GetOptionalInt(parameters, name) ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{name} is required.");

  private static double RequiredDouble(JsonObject? parameters, string name)
    => PluginRuntime.GetOptionalDouble(parameters, name) ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{name} is required.");

  internal static List<Dictionary<string, object?>> ReadEntities(Alignment alignment)
  {
    var list = new List<Dictionary<string, object?>>();
    var entities = alignment.Entities;
    var seen = new HashSet<int>();
    var ordered = new List<AlignmentEntity>();
    for (var i = 0; i < entities.Count; i++)
    {
      try { var e = entities.GetEntityByOrder(i); ordered.Add(e); seen.Add(e.EntityId); } catch { break; }
    }
    var connectedCount = ordered.Count;
    // Fixed entities that are not yet joined to the chain are still in the collection; list them after the chain.
    for (var i = 0; i < entities.Count; i++)
    {
      try { var e = entities[i]; if (seen.Add(e.EntityId)) ordered.Add(e); } catch { }
    }
    for (var i = 0; i < ordered.Count; i++)
    {
      var entity = ordered[i];
      var item = DescribeEntity(entity, i);
      item["connected"] = i < connectedCount;
      switch (entity)
      {
        case AlignmentArc arc:
          item["radius"] = Try(() => (object)arc.Radius);
          item["isClockwise"] = Try(() => (object)arc.Clockwise);
          item["delta"] = Try(() => (object)arc.Delta);
          item["chordLength"] = Try(() => (object)arc.ChordLength);
          break;
        case AlignmentLine line:
          item["direction"] = Try(() => (object)line.Direction);
          break;
        case AlignmentSpiral spiral:
          item["radiusIn"] = Try(() => (object)spiral.RadiusIn);
          item["radiusOut"] = Try(() => (object)spiral.RadiusOut);
          item["a"] = Try(() => (object)spiral.A);
          item["spiralType"] = Try(() => spiral.SpiralDefinition.ToString());
          break;
      }
      list.Add(item);
    }
    return list;
  }

  private static Dictionary<string, object?> DescribeEntity(AlignmentEntity entity, int order)
  {
    var item = new Dictionary<string, object?>
    {
      ["index"] = order,
      ["entityId"] = entity.EntityId,
      ["type"] = Try(() => entity.EntityType.ToString()),
      ["constraint"] = Try(() => entity.Constraint1.ToString()),
      ["subEntityCount"] = Try(() => (object)entity.SubEntityCount),
    };
    // Station/length reads throw ("Retrieve attribute failed") on entities not yet joined to the chain.
    if (entity is AlignmentCurve curve)
    {
      item["startStation"] = Try(() => (object)curve.StartStation);
      item["endStation"] = Try(() => (object)curve.EndStation);
      item["length"] = Try(() => (object)curve.Length);
      item["startPoint"] = Try(() => (object)new[] { curve.StartPoint.X, curve.StartPoint.Y });
      item["endPoint"] = Try(() => (object)new[] { curve.EndPoint.X, curve.EndPoint.Y });
    }
    else if (entity.SubEntityCount > 0)
    {
      item["startStation"] = Try(() => (object)entity[0].StartStation);
      item["endStation"] = Try(() => (object)entity[entity.SubEntityCount - 1].EndStation);
      item["length"] = Try(() =>
      {
        double length = 0;
        for (var s = 0; s < entity.SubEntityCount; s++) length += entity[s].Length;
        return (object)length;
      });
    }
    return item;
  }

  private static object? Try(Func<object?> read)
  {
    try { return read(); } catch { return null; }
  }

  private static bool AlignmentExists(CivilDocument civilDoc, Transaction transaction, string name)
  {
    foreach (ObjectId id in civilDoc.GetAlignmentIds())
    {
      if (transaction.GetObject(id, OpenMode.ForRead) is Alignment a && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
    }
    return false;
  }
}
