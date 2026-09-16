using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// Editing commands for Civil 3D horizontal alignments:
/// add_tangent, add_curve, add_spiral, delete_entity,
/// set_station_equation, get_station_offset,
/// offset_create, widen_transition.
/// </summary>
public static class AlignmentEditCommands
{
  // ─── alignmentAddTangent ──────────────────────────────────────────────────

  public static Task<object?> AddTangentAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var startX = PluginRuntime.GetRequiredDouble(parameters, "startX");
    var startY = PluginRuntime.GetRequiredDouble(parameters, "startY");
    var endX = PluginRuntime.GetRequiredDouble(parameters, "endX");
    var endY = PluginRuntime.GetRequiredDouble(parameters, "endY");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var writeAlignment = CivilObjectUtils.GetRequiredObject<Alignment>(
        transaction, alignment.ObjectId, OpenMode.ForWrite);

      var entities = writeAlignment.Entities;
      entities.AddFixedLine(new Point3d(startX, startY, 0), new Point3d(endX, endY, 0));

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["operation"] = "add_tangent",
        ["entityIndex"] = entities.Count - 1,
        ["startX"] = startX,
        ["startY"] = startY,
        ["endX"] = endX,
        ["endY"] = endY,
        ["success"] = true,
      };
    });
  }

  // ─── alignmentAddCurve ────────────────────────────────────────────────────

  public static Task<object?> AddCurveAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var passThroughX = PluginRuntime.GetRequiredDouble(parameters, "passThroughX");
    var passThroughY = PluginRuntime.GetRequiredDouble(parameters, "passThroughY");
    var radius = PluginRuntime.GetRequiredDouble(parameters, "radius");

    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      $"Cannot add the requested radius-{radius} curve at ({passThroughX}, {passThroughY}): Civil 3D 2026 requires an explicit clockwise direction or additional geometry. " +
      "The current tool schema does not provide enough information, so no curve was created.");
  }

  // ─── alignmentAddSpiral ───────────────────────────────────────────────────

  public static Task<object?> AddSpiralAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var spiralType = PluginRuntime.GetOptionalString(parameters, "spiralType") ?? "clothoid";
    var startX = PluginRuntime.GetRequiredDouble(parameters, "startX");
    var startY = PluginRuntime.GetRequiredDouble(parameters, "startY");
    var startRadius = PluginRuntime.GetRequiredDouble(parameters, "startRadius");
    var endRadius = PluginRuntime.GetRequiredDouble(parameters, "endRadius");
    var length = PluginRuntime.GetRequiredDouble(parameters, "length");

    throw new JsonRpcDispatchException(
      "CIVIL3D.API_ERROR",
      $"Cannot add {spiralType} spiral '{alignmentName}' from ({startX}, {startY}) with radii {startRadius}/{endRadius} and length {length}: " +
      "the Civil 3D 2026 AddFixedSpiral overloads require a previous entity id and additional geometric constraints not present in this tool schema. No spiral was created.");
  }

  // ─── alignmentDeleteEntity ────────────────────────────────────────────────

  public static Task<object?> DeleteEntityAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var entityIndex = (int)(PluginRuntime.GetRequiredDouble(parameters, "entityIndex"));

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var writeAlignment = CivilObjectUtils.GetRequiredObject<Alignment>(
        transaction, alignment.ObjectId, OpenMode.ForWrite);

      var entities = writeAlignment.Entities;
      var count = entities.Count;
      if (entityIndex < 0 || entityIndex >= count)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.INVALID_INPUT",
          $"Entity index {entityIndex} is out of range (alignment has {count} entities).");
      }

      entities.RemoveAt(entityIndex);

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["operation"] = "delete_entity",
        ["deletedEntityIndex"] = entityIndex,
        ["success"] = true,
      };
    });
  }

  // ─── alignmentSetStationEquation ─────────────────────────────────────────

  public static Task<object?> SetStationEquationAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var rawStation = PluginRuntime.GetRequiredDouble(parameters, "rawStation");
    var nominalStation = PluginRuntime.GetRequiredDouble(parameters, "nominalStation");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var writeAlignment = CivilObjectUtils.GetRequiredObject<Alignment>(
        transaction, alignment.ObjectId, OpenMode.ForWrite);

      var equationType = nominalStation >= rawStation
        ? StationEquationType.Increasing
        : StationEquationType.Decreasing;
      writeAlignment.StationEquations.Add(rawStation, nominalStation, equationType);

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["rawStation"] = rawStation,
        ["nominalStation"] = nominalStation,
        ["equationType"] = equationType.ToString(),
        ["success"] = true,
      };
    });
  }

  // ─── alignmentGetStationOffset ────────────────────────────────────────────

  public static Task<object?> GetStationOffsetAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var x = PluginRuntime.GetRequiredDouble(parameters, "x");
    var y = PluginRuntime.GetRequiredDouble(parameters, "y");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      double station = 0;
      double offset = 0;
      alignment.StationOffset(x, y, ref station, ref offset);

      return new Dictionary<string, object?>
      {
        ["station"] = station,
        ["offset"] = offset,
        ["distanceFromAlignment"] = Math.Abs(offset),
        ["units"] = CivilObjectUtils.LinearUnits(database),
      };
    });
  }

  // ─── alignmentOffsetCreate ────────────────────────────────────────────────
  // Ribbon "Create Offset Alignment": Alignment.CreateOffsetAlignment(name, parentId, offset, styleId[, start, end])
  // (+ optional "Create offset profile" by cross slope: Profile.CreateOffsetProfileBySlope).
  // Offset sign follows Civil 3D: positive = right of the parent, negative = left.

  public static Task<object?> OffsetCreateAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var offsetName = PluginRuntime.GetRequiredString(parameters, "offsetName");
    var offset = PluginRuntime.GetRequiredDouble(parameters, "offset");
    var startStation = PluginRuntime.GetOptionalDouble(parameters, "startStation");
    var endStation = PluginRuntime.GetOptionalDouble(parameters, "endStation");
    var styleName = PluginRuntime.GetOptionalString(parameters, "style");
    var layerName = PluginRuntime.GetOptionalString(parameters, "layer");
    var labelSet = PluginRuntime.GetOptionalString(parameters, "labelSet");
    var profileName = PluginRuntime.GetOptionalString(parameters, "offsetProfileName");
    var slope = PluginRuntime.GetOptionalDouble(parameters, "offsetProfileSlope");
    var profileStyle = PluginRuntime.GetOptionalString(parameters, "offsetProfileStyle");
    var parentProfileName = PluginRuntime.GetOptionalString(parameters, "parentProfileName");

    if (Math.Abs(offset) < 1e-9)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "offset must be non-zero (positive = right of the parent alignment, negative = left).");
    if ((startStation.HasValue) != (endStation.HasValue))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "startStation and endStation must be given together (or both omitted for the full length).");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var parent = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      if (AlignmentExists(civilDoc, transaction, offsetName))
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Alignment '{offsetName}' already exists. Nothing was created.");
      if (startStation.HasValue && (startStation.Value < parent.StartingStation - 1e-6 || endStation!.Value > parent.EndingStation + 1e-6 || endStation.Value <= startStation.Value))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"Station range {startStation:0.###}-{endStation:0.###} must lie within '{parent.Name}' ({parent.StartingStation:0.###} to {parent.EndingStation:0.###}) with end > start.");

      var styleId = LookupUtils.GetAlignmentStyleId(civilDoc, transaction, styleName);
      ObjectId offsetId;
      try
      {
        offsetId = startStation.HasValue
          ? Alignment.CreateOffsetAlignment(offsetName, parent.ObjectId, offset, styleId, startStation.Value, endStation!.Value)
          : Alignment.CreateOffsetAlignment(offsetName, parent.ObjectId, offset, styleId);
      }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR",
          $"Civil 3D could not create the offset alignment: {ex.GetType().Name}: {ex.Message}. Nothing was created.");
      }

      var offsetAlignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, offsetId, OpenMode.ForWrite);
      if (!string.IsNullOrWhiteSpace(layerName))
      {
        var layerId = LookupUtils.GetLayerId(database, transaction, layerName);
        if (!layerId.IsNull) offsetAlignment.LayerId = layerId;
      }
      if (!string.IsNullOrWhiteSpace(labelSet))
      {
        try { offsetAlignment.ImportLabelSet(labelSet); } catch (Exception ex) { PluginLog.Warn("AlignmentEdit", $"Label set '{labelSet}' not applied: {ex.Message}"); }
      }

      var result = new Dictionary<string, object?>
      {
        ["baseAlignmentName"] = parent.Name,
        ["offsetName"] = offsetAlignment.Name,
        ["offset"] = offset,
        ["side"] = offset > 0 ? "right" : "left",
        ["handle"] = CivilObjectUtils.GetHandle(offsetAlignment),
        ["startStation"] = offsetAlignment.StartingStation,
        ["endStation"] = offsetAlignment.EndingStation,
        ["length"] = offsetAlignment.Length,
        ["isOffsetAlignment"] = offsetAlignment.IsOffsetAlignment,
        ["style"] = AlignmentGeometryReader.ReadStyleName(offsetAlignment, transaction).Name,
        ["success"] = true,
      };

      if (!string.IsNullOrWhiteSpace(profileName) || slope.HasValue)
      {
        if (!slope.HasValue)
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "offsetProfileSlope (decimal cross slope, e.g. -0.025) is required with offsetProfileName. The offset alignment was created.");
        var pName = profileName ?? $"{offsetAlignment.Name} - Profile";
        var pStyle = LookupUtils.GetProfileStyleId(civilDoc, transaction, profileStyle);
        var parentProfile = FindParentProfile(civilDoc, transaction, parent, parentProfileName);
        try
        {
          // Instance method on the PARENT profile: the offset profile follows it at the given cross slope.
          var pId = parentProfile.CreateOffsetProfileBySlope(pName, offsetId, pStyle, slope.Value);
          var profile = CivilObjectUtils.GetRequiredObject<Profile>(transaction, pId, OpenMode.ForRead);
          result["offsetProfile"] = new Dictionary<string, object?>
          {
            ["name"] = profile.Name,
            ["handle"] = CivilObjectUtils.GetHandle(profile),
            ["parentProfileName"] = parentProfile.Name,
            ["slope"] = slope.Value,
            ["startStation"] = profile.StartingStation,
            ["endStation"] = profile.EndingStation,
            ["minElevation"] = profile.ElevationMin,
            ["maxElevation"] = profile.ElevationMax,
          };
        }
        catch (Exception ex)
        {
          result["offsetProfileError"] = $"{ex.GetType().Name}: {ex.Message} (the parent alignment needs a design profile for an offset profile by slope)";
        }
      }

      return result;
    });
  }

  // ─── alignmentWidenTransition ─────────────────────────────────────────────
  // Ribbon "Create Widening": on an offset alignment, OffsetAlignmentInfo.AddWidening(start, end, offsetDistance)
  // creates a widening region with entry/exit transitions. Pass either the offset alignment itself
  // (alignmentName) or the parent + side/offsetName to locate it. Optionally creates the offset alignment first.

  public static Task<object?> WidenTransitionAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var startStation = PluginRuntime.GetRequiredDouble(parameters, "startStation");
    var endStation = PluginRuntime.GetRequiredDouble(parameters, "endStation");
    var wideningOffset = PluginRuntime.GetOptionalDouble(parameters, "wideningOffset") ?? PluginRuntime.GetOptionalDouble(parameters, "endOffset");
    var offsetName = PluginRuntime.GetOptionalString(parameters, "offsetName");
    var baseOffset = PluginRuntime.GetOptionalDouble(parameters, "startOffset");
    var side = PluginRuntime.GetOptionalString(parameters, "side");
    var styleName = PluginRuntime.GetOptionalString(parameters, "style");

    if (!wideningOffset.HasValue)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "wideningOffset (the offset from the parent inside the widened region, signed: + right / - left) is required.");
    if (endStation <= startStation)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "endStation must be greater than startStation.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var named = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      Alignment offsetAlignment;
      Alignment parent;

      if (named.IsOffsetAlignment)
      {
        offsetAlignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, named.ObjectId, OpenMode.ForWrite);
        parent = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, named.OffsetAlignmentInfo.ParentAlignmentId, OpenMode.ForRead);
      }
      else
      {
        parent = named;
        Alignment? existing = null;
        if (!string.IsNullOrWhiteSpace(offsetName))
        {
          try { existing = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, offsetName); } catch { }
          if (existing != null && !existing.IsOffsetAlignment)
            throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"'{offsetName}' exists but is not an offset alignment.");
        }
        if (existing == null)
        {
          if (!baseOffset.HasValue)
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
              $"'{parent.Name}' is a parent alignment. Give startOffset (the normal offset, signed) to create the offset alignment first, or pass an existing offset alignment as alignmentName.");
          var name = offsetName ?? $"{parent.Name} - Offset {(baseOffset.Value > 0 ? "R" : "L")} {Math.Abs(baseOffset.Value):0.###}";
          var styleId = LookupUtils.GetAlignmentStyleId(civilDoc, transaction, styleName);
          var id = Alignment.CreateOffsetAlignment(name, parent.ObjectId, baseOffset.Value, styleId);
          existing = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, id, OpenMode.ForWrite);
        }
        offsetAlignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, existing.ObjectId, OpenMode.ForWrite);
      }

      var info = offsetAlignment.OffsetAlignmentInfo;
      var nominal = info.NominalOffset;
      if (Math.Sign(wideningOffset.Value) != Math.Sign(nominal) && Math.Abs(nominal) > 1e-9)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"wideningOffset {wideningOffset.Value:0.###} is on the opposite side of the offset alignment's nominal offset {nominal:0.###}. Nothing was changed.");
      if (startStation < parent.StartingStation - 1e-6 || endStation > parent.EndingStation + 1e-6)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"Widening {startStation:0.###}-{endStation:0.###} must lie within '{parent.Name}' ({parent.StartingStation:0.###} to {parent.EndingStation:0.###}).");

      try
      {
        info.AddWidening(startStation, endStation, wideningOffset.Value);
      }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR",
          $"Civil 3D refused the widening on '{offsetAlignment.Name}': {ex.GetType().Name}: {ex.Message}. Nothing was changed.");
      }

      return new Dictionary<string, object?>
      {
        ["baseAlignmentName"] = parent.Name,
        ["offsetName"] = offsetAlignment.Name,
        ["nominalOffset"] = nominal,
        ["widening"] = new Dictionary<string, object?>
        {
          ["startStation"] = startStation,
          ["endStation"] = endStation,
          ["offset"] = wideningOffset.Value,
          ["increasedWidth"] = Math.Abs(wideningOffset.Value) - Math.Abs(nominal),
        },
        ["handle"] = CivilObjectUtils.GetHandle(offsetAlignment),
        ["note"] = "Use offset_info to read the widening regions and transitions.",
        ["success"] = true,
      };
    });
  }

  // ─── alignmentOffsetInfo: nominal offset, regions and transitions of an offset alignment ──

  public static Task<object?> OffsetInfoAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    // Opened for write: OffsetAlignmentInfo.Regions is only readable from a write-opened alignment.
    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var found = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, found.ObjectId, OpenMode.ForWrite);
      if (!alignment.IsOffsetAlignment)
      {
        var children = new List<string>();
        try { foreach (ObjectId id in alignment.GetChildOffsetAlignmentIds()) if (transaction.GetObject(id, OpenMode.ForRead) is Alignment c) children.Add(c.Name); } catch { }
        return new Dictionary<string, object?>
        {
          ["alignmentName"] = alignment.Name,
          ["isOffsetAlignment"] = false,
          ["childOffsetAlignments"] = children,
        };
      }
      var info = alignment.OffsetAlignmentInfo;
      var parent = transaction.GetObject(info.ParentAlignmentId, OpenMode.ForRead) as Alignment;
      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["isOffsetAlignment"] = true,
        ["parentAlignmentName"] = parent?.Name,
        ["nominalOffset"] = info.NominalOffset,
        ["side"] = info.Side.ToString(),
        ["lockMode"] = info.LockMode.ToString(),
        ["updateMode"] = info.UpdateMode.ToString(),
        ["startStation"] = alignment.StartingStation,
        ["endStation"] = alignment.EndingStation,
        ["regions"] = ReadOffsetRegions(info),
      };
    });
  }

  private static Profile FindParentProfile(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Transaction transaction, Alignment parent, string? parentProfileName)
  {
    if (!string.IsNullOrWhiteSpace(parentProfileName))
      return CivilObjectUtils.FindProfileByName(parent, transaction, parentProfileName, OpenMode.ForRead);

    var candidates = new List<Profile>();
    foreach (ObjectId id in parent.GetProfileIds())
    {
      if (transaction.GetObject(id, OpenMode.ForRead) is Profile pr) candidates.Add(pr);
    }
    var design = candidates.Where(pr => pr.ProfileType != ProfileType.EG).ToList();
    if (design.Count == 1) return design[0];
    if (design.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"'{parent.Name}' has no design (layout) profile to offset from; give parentProfileName or create one first. Profiles: {string.Join(", ", candidates.Select(c => c.Name))}.");
    throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
      $"'{parent.Name}' has several design profiles ({string.Join(", ", design.Select(c => c.Name))}); give parentProfileName.");
  }

  private static List<Dictionary<string, object?>> ReadOffsetRegions(OffsetAlignmentInfo info)
  {
    var list = new List<Dictionary<string, object?>>();
    AlignmentRegionCollection regions;
    try { regions = info.Regions; }
    catch (Exception ex)
    {
      list.Add(new Dictionary<string, object?> { ["error"] = $"Regions unavailable: {ex.GetType().Name}: {ex.Message}" });
      return list;
    }
    for (var i = 0; i < regions.Count; i++)
    {
      var item = new Dictionary<string, object?> { ["index"] = i };
      try
      {
        var region = regions[i];
        item["type"] = R(() => region.RegionType.ToString());
        item["startStation"] = R(() => (object)region.StartStation);
        item["endStation"] = R(() => (object)region.EndStation);
        item["length"] = R(() => (object)region.Length);
        item["offset"] = R(() => (object)region.Offset);
        item["increasedWidth"] = R(() => (object)region.IncreasedWidth);
        item["entryTransition"] = R(() => region.EntryTransition?.TransitionType.ToString());
        item["exitTransition"] = R(() => region.ExitTransition?.TransitionType.ToString());
      }
      catch (Exception ex) { item["error"] = ex.Message; }
      list.Add(item);
    }
    return list;
  }

  private static object? R(Func<object?> read)
  {
    try { return read(); } catch { return null; }
  }

  private static bool AlignmentExists(Autodesk.Civil.ApplicationServices.CivilDocument civilDoc, Transaction transaction, string name)
  {
    foreach (ObjectId id in civilDoc.GetAlignmentIds())
    {
      var a = transaction.GetObject(id, OpenMode.ForRead) as Alignment;
      if (a != null && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
    }
    return false;
  }
}
