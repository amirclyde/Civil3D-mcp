using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CivilAssembly = Autodesk.Civil.DatabaseServices.Assembly;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3DMcpPlugin;

/// <summary>
/// Corridor creation and editing with the documented Civil 3D 2026 API (no reflection guesses):
///
///   CorridorCollection.Add(name, baselineName, alignmentId, profileId, regionName, assemblyId)
///   Baseline.BaselineRegions.Add(regionName, assemblyId, startStation, endStation) / RemoveAt(index)
///   BaselineRegion.AppliedAssemblySetting.FrequencyAlong* (assembly frequency)
///   BaselineRegion.GetTargets() -> SubassemblyTargetInfoCollection; SetTargets(collection)
///   SubassemblyTargetInfo.SubassemblyName / LogicalName / DisplayName / TargetType / TargetIds / TargetToOption
///   Corridor.Rebuild()
///
/// Every write either completes fully or throws before the transaction commits.
/// </summary>
public static class CorridorEditingCommands
{
  // -------------------------------------------------------------------------
  // createCorridor
  // -------------------------------------------------------------------------

  public static Task<object?> CreateCorridorAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "name");
    var alignmentName = PluginRuntime.GetOptionalString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetOptionalString(parameters, "profileName");
    var featureLineName = PluginRuntime.GetOptionalString(parameters, "featureLineName");
    var featureLineHandle = PluginRuntime.GetOptionalString(parameters, "featureLineHandle");
    var assemblyName = PluginRuntime.GetRequiredString(parameters, "assemblyName");
    var useFeatureLine = !string.IsNullOrWhiteSpace(featureLineName) || !string.IsNullOrWhiteSpace(featureLineHandle);
    if (!useFeatureLine && (string.IsNullOrWhiteSpace(alignmentName) || string.IsNullOrWhiteSpace(profileName)))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        "Give either alignmentName + profileName (alignment baseline) or featureLineName / featureLineHandle (feature line baseline). No corridor was created.");
    var baselineName = PluginRuntime.GetOptionalString(parameters, "baselineName");
    var regionName = PluginRuntime.GetOptionalString(parameters, "regionName");
    var startStation = PluginRuntime.GetOptionalDouble(parameters, "startStation");
    var endStation = PluginRuntime.GetOptionalDouble(parameters, "endStation");
    var frequency = PluginRuntime.GetOptionalDouble(parameters, "frequency");
    var rebuild = PluginRuntime.GetOptionalBool(parameters, "rebuild") ?? true;

    if (frequency is <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "frequency must be greater than zero.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      if (CorridorExists(civilDoc, transaction, corridorName))
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Corridor '{corridorName}' already exists. No corridor was created.");

      var assemblyId = FindAssemblyId(civilDoc, transaction, assemblyName);
      var region = string.IsNullOrWhiteSpace(regionName) ? $"RG - {assemblyName}" : regionName;

      Alignment? alignment = null;
      Profile? profile = null;
      FeatureLine? featureLine = null;
      string baseline;
      double? start = startStation, end = endStation;

      if (useFeatureLine)
      {
        featureLine = FindFeatureLine(civilDoc, transaction, featureLineName, featureLineHandle);
        baseline = string.IsNullOrWhiteSpace(baselineName)
          ? $"BL - FL {(string.IsNullOrWhiteSpace(featureLine.Name) ? featureLine.Handle.ToString() : featureLine.Name)}"
          : baselineName;
      }
      else
      {
        alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName!);
        profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName!, OpenMode.ForRead);
        start ??= alignment.StartingStation;
        end ??= alignment.EndingStation;
        if (start < alignment.StartingStation - 1e-6 || end > alignment.EndingStation + 1e-6)
          throw new JsonRpcDispatchException(
            "CIVIL3D.INVALID_INPUT",
            $"Region {start:F3}-{end:F3} is outside alignment '{alignment.Name}' ({alignment.StartingStation:F3}-{alignment.EndingStation:F3}). No corridor was created.");
        baseline = string.IsNullOrWhiteSpace(baselineName) ? $"BL - {alignment.Name}" : baselineName;
      }
      if (start.HasValue && end.HasValue && end <= start)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"endStation ({end:F3}) must be greater than startStation ({start:F3}). No corridor was created.");

      ObjectId corridorId;
      try
      {
        corridorId = featureLine != null
          ? civilDoc.CorridorCollection.Add(corridorName, baseline, featureLine.ObjectId, region, assemblyId)
          : civilDoc.CorridorCollection.Add(corridorName, baseline, alignment!.ObjectId, profile!.ObjectId, region, assemblyId);
      }
      catch (Exception ex) when (ex is not JsonRpcDispatchException)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Civil 3D refused to create the corridor: {ex.GetType().Name}: {ex.Message}. No corridor was created.");
      }

      var corridor = CivilObjectUtils.GetRequiredObject<Corridor>(transaction, corridorId, OpenMode.ForWrite);
      var baselineObj = corridor.Baselines[0];
      var regionObj = baselineObj.BaselineRegions[0];

      if (startStation.HasValue) regionObj.StartStation = startStation.Value;
      if (endStation.HasValue) regionObj.EndStation = endStation.Value;
      if (frequency.HasValue)
        ApplyFrequency(regionObj, frequency.Value);

      var rebuildError = rebuild ? TryRebuild(corridor) : null;

      return new Dictionary<string, object?>
      {
        ["name"] = corridor.Name,
        ["handle"] = CivilObjectUtils.GetHandle(corridor),
        ["baselineName"] = baselineObj.Name,
        ["baselineType"] = featureLine != null ? "featureLine" : "alignment",
        ["alignmentName"] = alignment?.Name,
        ["profileName"] = profile?.Name,
        ["featureLineName"] = featureLine?.Name,
        ["featureLineHandle"] = featureLine != null ? CivilObjectUtils.GetHandle(featureLine) : null,
        ["regionName"] = regionObj.Name,
        ["assemblyName"] = assemblyName,
        ["startStation"] = regionObj.StartStation,
        ["endStation"] = regionObj.EndStation,
        ["frequency"] = ReadFrequency(regionObj),
        ["rebuilt"] = rebuild && rebuildError == null,
        ["rebuildError"] = rebuildError,
        ["state"] = corridor.IsOutOfDate ? "out_of_date" : "built",
        ["targets"] = ReadTargets(regionObj, transaction),
        ["created"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // getCorridorTargetMappings
  // -------------------------------------------------------------------------

  public static Task<object?> GetCorridorTargetMappingsAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var regionIndex = PluginRuntime.GetOptionalInt(parameters, "regionIndex");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForRead);
      var baseline = GetBaseline(corridor, baselineIndex);

      var results = new List<Dictionary<string, object?>>();
      var regions = baseline.BaselineRegions;
      for (var ri = 0; ri < regions.Count; ri++)
      {
        if (regionIndex.HasValue && ri != regionIndex.Value) continue;
        var region = regions[ri];
        results.Add(new Dictionary<string, object?>
        {
          ["regionIndex"] = ri,
          ["regionName"] = region.Name,
          ["startStation"] = region.StartStation,
          ["endStation"] = region.EndStation,
          ["frequency"] = ReadFrequency(region),
          ["targets"] = ReadTargets(region, transaction),
        });
      }

      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridorName,
        ["baselineIndex"] = baselineIndex,
        ["regions"] = results,
      };
    });
  }

  // -------------------------------------------------------------------------
  // setCorridorTargetMappings
  // -------------------------------------------------------------------------

  public static Task<object?> SetCorridorTargetMappingsAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var regionIndex = PluginRuntime.GetOptionalInt(parameters, "regionIndex") ?? 0;
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var rebuild = PluginRuntime.GetOptionalBool(parameters, "rebuild") ?? true;
    var targetsNode = PluginRuntime.GetParameter(parameters, "targets") as JsonArray
      ?? throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "targets array is required.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForWrite);
      var baseline = GetBaseline(corridor, baselineIndex);
      if (regionIndex < 0 || regionIndex >= baseline.BaselineRegions.Count)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"Region index {regionIndex} is out of range. Corridor '{corridorName}' baseline {baselineIndex} has {baseline.BaselineRegions.Count} region(s).");

      var region = baseline.BaselineRegions[regionIndex];
      var targetInfos = region.GetTargets();
      var applied = new List<Dictionary<string, object?>>();

      foreach (var targetNode in targetsNode)
      {
        if (targetNode is not JsonObject t) continue;
        var paramName = t["parameterName"]?.GetValue<string>();
        var targetType = t["targetType"]?.GetValue<string>();
        var targetName = t["targetName"]?.GetValue<string>();
        var subassemblyName = t["subassemblyName"]?.GetValue<string>();
        var targetToOption = t["targetToOption"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(paramName) || string.IsNullOrWhiteSpace(targetType) || string.IsNullOrWhiteSpace(targetName))
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Each target needs parameterName, targetType and targetName. Nothing was changed.");

        var targetId = ResolveTargetObjectId(civilDoc, transaction, targetType, targetName)
          ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
            $"Target object '{targetName}' of type '{targetType}' was not found. Nothing was changed.");

        var matches = new List<SubassemblyTargetInfo>();
        for (var i = 0; i < targetInfos.Count; i++)
        {
          var info = targetInfos[i];
          if (!string.IsNullOrWhiteSpace(subassemblyName) &&
              !string.Equals(info.SubassemblyName, subassemblyName, StringComparison.OrdinalIgnoreCase))
            continue;
          if (string.Equals(info.LogicalName, paramName, StringComparison.OrdinalIgnoreCase) ||
              string.Equals(info.DisplayName, paramName, StringComparison.OrdinalIgnoreCase))
            matches.Add(info);
        }

        if (matches.Count == 0)
        {
          var available = new List<string>();
          for (var i = 0; i < targetInfos.Count; i++)
            available.Add($"{targetInfos[i].SubassemblyName}:{targetInfos[i].LogicalName} ({targetInfos[i].TargetType})");
          throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
            $"No subassembly target named '{paramName}'{(subassemblyName != null ? $" on '{subassemblyName}'" : "")} in region '{region.Name}'. " +
            $"Available: {string.Join(", ", available)}. Nothing was changed.");
        }

        foreach (var info in matches)
        {
          ValidateTargetKind(info, targetType);
          info.TargetIds = new ObjectIdCollection { targetId };
          if (!string.IsNullOrWhiteSpace(targetToOption))
          {
            if (!Enum.TryParse<SubassemblyTargetToOption>(targetToOption, ignoreCase: true, out var option))
              throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
                $"targetToOption '{targetToOption}' is not one of {string.Join(", ", Enum.GetNames<SubassemblyTargetToOption>())}.");
            info.TargetToOption = option;
          }
          applied.Add(new Dictionary<string, object?>
          {
            ["subassemblyName"] = info.SubassemblyName,
            ["parameterName"] = info.LogicalName,
            ["targetType"] = info.TargetType.ToString(),
            ["targetName"] = targetName,
          });
        }
      }

      region.SetTargets(targetInfos);
      var rebuildError = rebuild ? TryRebuild(corridor) : null;

      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridorName,
        ["baselineIndex"] = baselineIndex,
        ["regionIndex"] = regionIndex,
        ["targetsApplied"] = applied.Count,
        ["applied"] = applied,
        ["rebuilt"] = rebuild && rebuildError == null,
        ["rebuildError"] = rebuildError,
        ["targets"] = ReadTargets(region, transaction),
      };
    });
  }

  // -------------------------------------------------------------------------
  // addCorridorRegion
  // -------------------------------------------------------------------------

  public static Task<object?> AddCorridorRegionAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var assemblyName = PluginRuntime.GetRequiredString(parameters, "assemblyName");
    var regionName = PluginRuntime.GetOptionalString(parameters, "regionName");
    var startStation = PluginRuntime.GetRequiredDouble(parameters, "startStation");
    var endStation = PluginRuntime.GetRequiredDouble(parameters, "endStation");
    var frequency = PluginRuntime.GetOptionalDouble(parameters, "frequency");
    var rebuild = PluginRuntime.GetOptionalBool(parameters, "rebuild") ?? true;

    if (endStation <= startStation)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "endStation must be greater than startStation.");
    if (frequency is <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "frequency must be greater than zero.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForWrite);
      var baseline = GetBaseline(corridor, baselineIndex);
      var assemblyId = FindAssemblyId(civilDoc, transaction, assemblyName);

      var regions = baseline.BaselineRegions;
      for (var i = 0; i < regions.Count; i++)
      {
        var existing = regions[i];
        if (startStation < existing.EndStation - 1e-6 && endStation > existing.StartStation + 1e-6)
          throw new JsonRpcDispatchException("CIVIL3D.CONFLICT",
            $"Stations {startStation:F3}-{endStation:F3} overlap region '{existing.Name}' ({existing.StartStation:F3}-{existing.EndStation:F3}). Nothing was added.");
      }

      var name = string.IsNullOrWhiteSpace(regionName) ? $"RG - {assemblyName} ({regions.Count + 1})" : regionName;
      BaselineRegion region;
      try
      {
        region = regions.Add(name, assemblyId, startStation, endStation);
      }
      catch (Exception ex) when (ex is not JsonRpcDispatchException)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Civil 3D refused to add the region: {ex.GetType().Name}: {ex.Message}. Nothing was added.");
      }
      if (frequency.HasValue)
        ApplyFrequency(region, frequency.Value);

      var rebuildError = rebuild ? TryRebuild(corridor) : null;

      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridorName,
        ["baselineIndex"] = baselineIndex,
        ["regionIndex"] = regions.Count - 1,
        ["regionName"] = region.Name,
        ["assemblyName"] = assemblyName,
        ["startStation"] = region.StartStation,
        ["endStation"] = region.EndStation,
        ["frequency"] = ReadFrequency(region),
        ["rebuilt"] = rebuild && rebuildError == null,
        ["rebuildError"] = rebuildError,
        ["targets"] = ReadTargets(region, transaction),
      };
    });
  }

  // -------------------------------------------------------------------------
  // setCorridorRegionFrequency
  // -------------------------------------------------------------------------

  public static Task<object?> SetCorridorRegionFrequencyAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var regionIndex = PluginRuntime.GetOptionalInt(parameters, "regionIndex") ?? 0;
    var frequency = PluginRuntime.GetRequiredDouble(parameters, "frequency");
    var rebuild = PluginRuntime.GetOptionalBool(parameters, "rebuild") ?? true;
    if (frequency <= 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "frequency must be greater than zero.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForWrite);
      var baseline = GetBaseline(corridor, baselineIndex);
      if (regionIndex < 0 || regionIndex >= baseline.BaselineRegions.Count)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Region index {regionIndex} is out of range.");
      var region = baseline.BaselineRegions[regionIndex];
      ApplyFrequency(region, frequency);
      var rebuildError = rebuild ? TryRebuild(corridor) : null;
      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridorName,
        ["regionIndex"] = regionIndex,
        ["regionName"] = region.Name,
        ["frequency"] = ReadFrequency(region),
        ["rebuilt"] = rebuild && rebuildError == null,
        ["rebuildError"] = rebuildError,
      };
    });
  }

  // -------------------------------------------------------------------------
  // deleteCorridorRegion
  // -------------------------------------------------------------------------

  public static Task<object?> DeleteCorridorRegionAsync(JsonObject? parameters)
  {
    var corridorName = PluginRuntime.GetRequiredString(parameters, "corridorName");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var regionIndex = PluginRuntime.GetRequiredInt(parameters, "regionIndex");
    var rebuild = PluginRuntime.GetOptionalBool(parameters, "rebuild") ?? true;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, corridorName, OpenMode.ForWrite);
      var baseline = GetBaseline(corridor, baselineIndex);
      if (regionIndex < 0 || regionIndex >= baseline.BaselineRegions.Count)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Region index {regionIndex} is out of range.");

      var region = baseline.BaselineRegions[regionIndex];
      var regionName = region.Name;
      var startStation = region.StartStation;
      var endStation = region.EndStation;

      try
      {
        baseline.BaselineRegions.RemoveAt(regionIndex);
      }
      catch (Exception ex) when (ex is not JsonRpcDispatchException)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Civil 3D refused to remove the region: {ex.GetType().Name}: {ex.Message}. Nothing was deleted.");
      }

      var rebuildError = rebuild ? TryRebuild(corridor) : null;

      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridorName,
        ["baselineIndex"] = baselineIndex,
        ["deletedRegionIndex"] = regionIndex,
        ["deletedRegionName"] = regionName,
        ["deletedStartStation"] = startStation,
        ["deletedEndStation"] = endStation,
        ["remainingRegions"] = baseline.BaselineRegions.Count,
        ["rebuilt"] = rebuild && rebuildError == null,
        ["rebuildError"] = rebuildError,
        ["deleted"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // Helpers
  // -------------------------------------------------------------------------

  // -------------------------------------------------------------------------
  // getCorridorSection: the applied assembly (points / links / shapes) at a station
  // -------------------------------------------------------------------------

  public static Task<object?> GetCorridorSectionAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var station = PluginRuntime.GetRequiredDouble(parameters, "station");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, name, OpenMode.ForRead);
      var baseline = GetBaseline(corridor, baselineIndex);
      var section = ReadAppliedAssembly(corridor, baseline, station);
      section["corridorName"] = corridor.Name;
      section["baselineIndex"] = baselineIndex;
      section["baselineName"] = baseline.Name;
      return section;
    });
  }

  /// <summary>
  /// Reads the corridor's applied assembly at the applied station nearest to <paramref name="station"/>
  /// (assemblies only exist at the region frequency stations). Offsets are relative to the baseline,
  /// elevations absolute; XYZ are world coordinates.
  /// </summary>
  internal static Dictionary<string, object?> ReadAppliedAssembly(Corridor corridor, Baseline baseline, double station)
  {
    var stations = baseline.SortedStations();
    if (stations == null || stations.Length == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_STATE",
        $"Corridor '{corridor.Name}' has no applied stations on baseline '{baseline.Name}' - rebuild it first.");

    var applied = stations.OrderBy(s => Math.Abs(s - station)).First();
    AppliedAssembly assembly;
    try
    {
      assembly = baseline.GetAppliedAssemblyAtStation(applied);
    }
    catch (Exception ex)
    {
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR",
        $"Civil 3D could not return the applied assembly at station {applied:0.###} of '{corridor.Name}': {ex.GetType().Name}: {ex.Message}");
    }

    // Applied geometry is exposed as CalculatedPoint / CalculatedLink / CalculatedShape.
    // StationOffsetElevationToBaseline is (station, offset, elevation); XYZ is the world position.
    var points = new List<Dictionary<string, object?>>();
    var pointKeys = new List<(double Offset, double Elevation)>();
    foreach (CalculatedPoint point in assembly.Points)
    {
      var soe = point.StationOffsetElevationToBaseline;
      var xyz = point.XYZ;
      pointKeys.Add((soe.Y, soe.Z));
      points.Add(new Dictionary<string, object?>
      {
        ["index"] = points.Count,
        ["offset"] = soe.Y,
        ["elevation"] = soe.Z,
        ["x"] = xyz.X,
        ["y"] = xyz.Y,
        ["z"] = xyz.Z,
        ["codes"] = ReadCorridorCodes(point.CorridorCodes),
      });
    }

    int IndexOf(CalculatedPoint p)
    {
      var soe = p.StationOffsetElevationToBaseline;
      for (var i = 0; i < pointKeys.Count; i++)
      {
        if (Math.Abs(pointKeys[i].Offset - soe.Y) < 1e-6 && Math.Abs(pointKeys[i].Elevation - soe.Z) < 1e-6)
          return i;
      }
      return -1;
    }

    var links = new List<Dictionary<string, object?>>();
    var linkKeys = new List<string>();
    foreach (CalculatedLink link in assembly.Links)
    {
      var indices = link.CalculatedPoints.Cast<CalculatedPoint>().Select(IndexOf).ToList();
      linkKeys.Add(string.Join(",", indices));
      links.Add(new Dictionary<string, object?>
      {
        ["index"] = links.Count,
        ["points"] = indices,
        ["codes"] = ReadCorridorCodes(link.CorridorCodes),
      });
    }

    var shapes = new List<Dictionary<string, object?>>();
    foreach (CalculatedShape shape in assembly.Shapes)
    {
      var linkIndices = new List<int>();
      foreach (CalculatedLink link in shape.CalculatedLinks)
      {
        var key = string.Join(",", link.CalculatedPoints.Cast<CalculatedPoint>().Select(IndexOf));
        var reversed = string.Join(",", link.CalculatedPoints.Cast<CalculatedPoint>().Select(IndexOf).Reverse());
        var idx = linkKeys.IndexOf(key);
        if (idx < 0) idx = linkKeys.IndexOf(reversed);
        linkIndices.Add(idx);
      }
      double? area = null;
      try { area = shape.Area; } catch { }
      shapes.Add(new Dictionary<string, object?>
      {
        ["index"] = shapes.Count,
        ["links"] = linkIndices,
        ["codes"] = ReadCorridorCodes(shape.CorridorCodes),
        ["area"] = area,
      });
    }

    return new Dictionary<string, object?>
    {
      ["requestedStation"] = station,
      ["station"] = applied,
      ["stationDelta"] = applied - station,
      ["appliedStationCount"] = stations.Length,
      ["points"] = points,
      ["links"] = links,
      ["shapes"] = shapes,
      ["minElevation"] = points.Count > 0 ? points.Min(p => (double)p["elevation"]!) : null,
      ["maxElevation"] = points.Count > 0 ? points.Max(p => (double)p["elevation"]!) : null,
      ["minOffset"] = points.Count > 0 ? points.Min(p => (double)p["offset"]!) : null,
      ["maxOffset"] = points.Count > 0 ? points.Max(p => (double)p["offset"]!) : null,
    };
  }

  // -------------------------------------------------------------------------
  // exportCorridorFeatureLine: ribbon "Create Alignment / Profile / Feature Line from Corridor"
  // -------------------------------------------------------------------------

  public static Task<object?> ListCorridorFeatureLineCodesAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, name, OpenMode.ForRead);
      var baseline = GetBaseline(corridor, baselineIndex);
      var map = baseline.MainBaselineFeatureLines.FeatureLineCollectionMap;
      var codes = new List<Dictionary<string, object?>>();
      foreach (FeatureLineCollection collection in map)
      {
        var lines = new List<Dictionary<string, object?>>();
        var i = 0;
        foreach (CorridorFeatureLine line in collection)
        {
          var pts = line.FeatureLinePoints;
          lines.Add(new Dictionary<string, object?>
          {
            ["index"] = i++,
            ["pointCount"] = pts?.Count ?? 0,
            ["style"] = line.StyleName,
          });
        }
        codes.Add(new Dictionary<string, object?>
        {
          ["code"] = collection.FeatureLineCodeInfo?.CodeName,
          ["featureLineCount"] = lines.Count,
          ["featureLines"] = lines,
        });
      }
      // CodeName is also available on the map; fill it where FeatureLineCodeInfo was empty.
      var codeNames = map.CodeNames().Cast<string>().ToList();
      for (var i = 0; i < codes.Count && i < codeNames.Count; i++) codes[i]["code"] ??= codeNames[i];
      return new Dictionary<string, object?>
      {
        ["corridorName"] = corridor.Name,
        ["baselineIndex"] = baselineIndex,
        ["baselineName"] = baseline.Name,
        ["codes"] = codes,
      };
    });
  }

  public static Task<object?> ExportCorridorFeatureLineAsync(JsonObject? parameters)
  {
    var name = PluginRuntime.GetRequiredString(parameters, "name");
    var code = PluginRuntime.GetRequiredString(parameters, "code");
    var exportAs = PluginRuntime.GetRequiredString(parameters, "exportAs").ToLowerInvariant();
    var baselineIndex = PluginRuntime.GetOptionalInt(parameters, "baselineIndex") ?? 0;
    var featureLineIndex = PluginRuntime.GetOptionalInt(parameters, "featureLineIndex") ?? 0;
    var outputName = PluginRuntime.GetOptionalString(parameters, "outputName");
    var targetAlignmentName = PluginRuntime.GetOptionalString(parameters, "alignmentName");
    var siteName = PluginRuntime.GetOptionalString(parameters, "siteName");
    var layer = PluginRuntime.GetOptionalString(parameters, "layer");
    var style = PluginRuntime.GetOptionalString(parameters, "style");
    var labelSet = PluginRuntime.GetOptionalString(parameters, "labelSet");
    var dynamic = PluginRuntime.GetOptionalBool(parameters, "dynamic") ?? true;
    var smooth = PluginRuntime.GetOptionalBool(parameters, "smooth") ?? false;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var corridor = CivilObjectUtils.FindCorridorByName(civilDoc, transaction, name, OpenMode.ForRead);
      var baseline = GetBaseline(corridor, baselineIndex);
      var map = baseline.MainBaselineFeatureLines.FeatureLineCollectionMap;

      FeatureLineCollection? collection = null;
      try { collection = map[code]; } catch { }
      if (collection == null || collection.Count == 0)
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
          $"Corridor '{corridor.Name}' baseline '{baseline.Name}' has no feature line with code '{code}'. Codes: {string.Join(", ", map.CodeNames().Cast<string>())}.");
      if (featureLineIndex < 0 || featureLineIndex >= collection.Count)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"Code '{code}' has {collection.Count} feature line(s); featureLineIndex {featureLineIndex} is out of range.");
      var featureLine = collection[featureLineIndex];

      var layerId = LookupUtils.GetLayerId(database, transaction, layer);
      var result = new Dictionary<string, object?>
      {
        ["corridorName"] = corridor.Name,
        ["baselineName"] = baseline.Name,
        ["code"] = code,
        ["featureLineIndex"] = featureLineIndex,
        ["pointCount"] = featureLine.FeatureLinePoints?.Count ?? 0,
        ["exportAs"] = exportAs,
      };

      try
      {
        switch (exportAs)
        {
          case "alignment":
          {
            var alignmentName = outputName ?? $"{corridor.Name} - {code}";
            var siteId = GetOrCreateSiteId(civilDoc, transaction, siteName ?? "Corridor Exports", result);
            var styleId = LookupUtils.GetAlignmentStyleId(civilDoc, transaction, style);
            var labelSetId = LookupUtils.GetAlignmentLabelSetId(civilDoc, transaction, labelSet);
            var id = featureLine.ExportAsAlignment(alignmentName, siteId, layerId, styleId, labelSetId, AlignmentType.Centerline);
            var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, id, OpenMode.ForRead);
            result["alignmentName"] = alignment.Name;
            result["handle"] = CivilObjectUtils.GetHandle(alignment);
            result["startStation"] = alignment.StartingStation;
            result["endStation"] = alignment.EndingStation;
            result["length"] = alignment.Length;
            break;
          }
          case "profile":
          {
            Alignment target;
            if (!string.IsNullOrWhiteSpace(targetAlignmentName))
              target = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, targetAlignmentName);
            else if (!baseline.IsFeatureLineBased() && !baseline.AlignmentId.IsNull)
              target = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, baseline.AlignmentId, OpenMode.ForRead);
            else
              throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "This baseline is feature-line based; pass alignmentName for the profile's parent alignment.");
            var profileName = outputName ?? $"{corridor.Name} - {code}";
            var styleId = LookupUtils.GetProfileStyleId(civilDoc, transaction, style);
            var labelSetId = LookupUtils.GetProfileLabelSetId(civilDoc, transaction, labelSet);
            var id = featureLine.ExportAsProfile(profileName, target.ObjectId, layerId, styleId, labelSetId);
            var profile = CivilObjectUtils.GetRequiredObject<Profile>(transaction, id, OpenMode.ForRead);
            result["profileName"] = profile.Name;
            result["alignmentName"] = target.Name;
            result["handle"] = CivilObjectUtils.GetHandle(profile);
            result["startStation"] = profile.StartingStation;
            result["endStation"] = profile.EndingStation;
            result["minElevation"] = profile.ElevationMin;
            result["maxElevation"] = profile.ElevationMax;
            break;
          }
          case "feature_line":
          {
            var siteId = GetOrCreateSiteId(civilDoc, transaction, siteName ?? "Corridor Exports", result);
            var flName = outputName ?? $"{corridor.Name} - {code}";
            var styleId = LookupUtils.GetFeatureLineStyleId(civilDoc, transaction, style);
            var smoothOption = new GradingSmoothOption(smooth, 1.0, 0.0, 0.05);
            var id = featureLine.ExportAsGradingFeatureLine(siteId, dynamic, flName, layerId, styleId, smoothOption);
            var fl = CivilObjectUtils.GetRequiredObject<FeatureLine>(transaction, id, OpenMode.ForRead);
            result["featureLineName"] = fl.Name;
            result["handle"] = CivilObjectUtils.GetHandle(fl);
            result["dynamic"] = dynamic;
            result["site"] = siteId.IsNull ? null : CivilObjectUtils.GetName(transaction.GetObject(siteId, OpenMode.ForRead));
            result["minElevation"] = fl.MinElevation;
            result["maxElevation"] = fl.MaxElevation;
            result["pointCount"] = fl.PointsCount;
            break;
          }
          case "polyline3d":
          {
            var ids = featureLine.ExportAsPolyline3dCollection();
            var handles = new List<string>();
            foreach (ObjectId id in ids)
            {
              var pl = transaction.GetObject(id, OpenMode.ForWrite);
              if (!string.IsNullOrWhiteSpace(layer) && pl is Autodesk.AutoCAD.DatabaseServices.Entity ent) ent.Layer = layer;
              handles.Add(id.Handle.ToString());
            }
            result["handle"] = handles.FirstOrDefault();
            result["handles"] = handles;
            result["objectType"] = "Polyline3d";
            result["polylineCount"] = handles.Count;
            break;
          }
          default:
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"exportAs '{exportAs}' is not one of alignment, profile, feature_line, polyline3d.");
        }
      }
      catch (JsonRpcDispatchException) { throw; }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR",
          $"Civil 3D could not export '{code}' from '{corridor.Name}' as {exportAs}: {ex.GetType().Name}: {ex.Message}. Nothing was created.");
      }

      result["created"] = true;
      return result;
    });
  }

  /// <summary>Corridor exports to alignment / grading feature line need a real site; create it when missing.</summary>
  private static ObjectId GetOrCreateSiteId(CivilDocument civilDoc, Transaction transaction, string siteName, Dictionary<string, object?> result)
  {
    var siteId = LookupUtils.GetSiteId(civilDoc, transaction, siteName);
    if (!siteId.IsNull) { result["site"] = siteName; return siteId; }
    try
    {
      siteId = Site.Create(civilDoc, siteName);
      result["site"] = siteName;
      result["siteCreated"] = true;
      return siteId;
    }
    catch (Exception ex)
    {
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Site '{siteName}' does not exist and could not be created: {ex.Message}");
    }
  }

  private static List<string> ReadCorridorCodes(CorridorCodeCollection? codes)
  {
    var list = new List<string>();
    if (codes == null) return list;
    try { foreach (var code in codes) list.Add(code?.ToString() ?? string.Empty); } catch { }
    return list;
  }

  private static FeatureLine FindFeatureLine(CivilDocument civilDoc, Transaction transaction, string? name, string? handle)
  {
    var ids = new List<ObjectId>();
    try { foreach (ObjectId id in civilDoc.GetSitelessFeatureLineIds()) ids.Add(id); } catch { }
    try
    {
      foreach (ObjectId siteId in civilDoc.GetSiteIds())
      {
        var site = CivilObjectUtils.GetRequiredObject<Site>(transaction, siteId, OpenMode.ForRead);
        foreach (ObjectId id in site.GetFeatureLineIds()) ids.Add(id);
      }
    }
    catch { }

    var matches = new List<FeatureLine>();
    foreach (var id in ids)
    {
      if (transaction.GetObject(id, OpenMode.ForRead) is not FeatureLine fl) continue;
      if (!string.IsNullOrWhiteSpace(handle) && string.Equals(fl.Handle.ToString(), handle.Trim(), StringComparison.OrdinalIgnoreCase))
        return fl;
      if (!string.IsNullOrWhiteSpace(name) && string.Equals(fl.Name, name, StringComparison.OrdinalIgnoreCase))
        matches.Add(fl);
    }
    if (!string.IsNullOrWhiteSpace(handle))
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No feature line with handle '{handle}' (searched {ids.Count} feature lines). No corridor was created.");
    if (matches.Count == 1) return matches[0];
    if (matches.Count > 1)
      throw new JsonRpcDispatchException("CIVIL3D.CONFLICT",
        $"{matches.Count} feature lines are named '{name}'; pass featureLineHandle instead ({string.Join(", ", matches.Select(m => m.Handle.ToString()))}). No corridor was created.");
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Feature line '{name}' was not found (searched {ids.Count} feature lines, siteless and in sites). No corridor was created.");
  }

  private static bool CorridorExists(CivilDocument civilDoc, Transaction transaction, string name)
  {
    foreach (ObjectId id in civilDoc.CorridorCollection)
    {
      var corridor = CivilObjectUtils.GetRequiredObject<Corridor>(transaction, id, OpenMode.ForRead);
      if (string.Equals(corridor.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
    }
    return false;
  }

  private static ObjectId FindAssemblyId(CivilDocument civilDoc, Transaction transaction, string assemblyName)
  {
    foreach (ObjectId aid in civilDoc.AssemblyCollection)
    {
      var asm = CivilObjectUtils.GetRequiredObject<CivilAssembly>(transaction, aid, OpenMode.ForRead);
      if (string.Equals(asm.Name, assemblyName, StringComparison.OrdinalIgnoreCase))
        return aid;
    }
    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Assembly '{assemblyName}' was not found.");
  }

  private static Baseline GetBaseline(Corridor corridor, int index)
  {
    if (index < 0 || index >= corridor.Baselines.Count)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"Baseline index {index} is out of range. Corridor '{corridor.Name}' has {corridor.Baselines.Count} baseline(s).");
    return corridor.Baselines[index];
  }

  private static void ApplyFrequency(BaselineRegion region, double frequency)
  {
    var setting = region.AppliedAssemblySetting;
    setting.FrequencyAlongTangents = frequency;
    setting.FrequencyAlongCurves = frequency;
    setting.FrequencyAlongSpirals = frequency;
    setting.FrequencyAlongProfileCurves = frequency;
  }

  private static Dictionary<string, object?> ReadFrequency(BaselineRegion region)
  {
    try
    {
      var s = region.AppliedAssemblySetting;
      return new Dictionary<string, object?>
      {
        ["alongTangents"] = s.FrequencyAlongTangents,
        ["alongCurves"] = s.FrequencyAlongCurves,
        ["alongSpirals"] = s.FrequencyAlongSpirals,
        ["alongProfileCurves"] = s.FrequencyAlongProfileCurves,
      };
    }
    catch (Exception ex)
    {
      PluginLog.Debug("Corridor", "AppliedAssemblySetting not readable", ex);
      return new Dictionary<string, object?>();
    }
  }

  private static string? TryRebuild(Corridor corridor)
  {
    try
    {
      corridor.Rebuild();
      return null;
    }
    catch (Exception ex)
    {
      PluginLog.Warn("Corridor", $"Rebuild of '{corridor.Name}' failed", ex);
      return $"{ex.GetType().Name}: {ex.Message}";
    }
  }

  private static List<Dictionary<string, object?>> ReadTargets(BaselineRegion region, Transaction transaction)
  {
    var rows = new List<Dictionary<string, object?>>();
    SubassemblyTargetInfoCollection infos;
    try
    {
      infos = region.GetTargets();
    }
    catch (Exception ex)
    {
      PluginLog.Debug("Corridor", "GetTargets failed", ex);
      return rows;
    }

    for (var i = 0; i < infos.Count; i++)
    {
      var info = infos[i];
      var names = new List<string>();
      try
      {
        foreach (ObjectId id in info.TargetIds)
        {
          if (id.IsNull) continue;
          var name = CivilObjectUtils.GetName(transaction.GetObject(id, OpenMode.ForRead));
          names.Add(name ?? id.Handle.ToString());
        }
      }
      catch { }

      rows.Add(new Dictionary<string, object?>
      {
        ["subassemblyName"] = info.SubassemblyName,
        ["assemblyGroupName"] = info.AssemblyGroupName,
        ["parameterName"] = info.LogicalName,
        ["displayName"] = info.DisplayName,
        ["targetType"] = info.TargetType.ToString(),
        ["targetToOption"] = SafeTargetToOption(info),
        ["targetNames"] = names,
        ["isSet"] = names.Count > 0,
      });
    }
    return rows;
  }

  /// <summary>Civil 3D throws "The count of TargetIds should be greater or equal to 2" when TargetToOption is read on an offset/elevation target that has fewer than two targets assigned.</summary>
  private static string? SafeTargetToOption(SubassemblyTargetInfo info)
  {
    try { return info.TargetToOption.ToString(); } catch { return null; }
  }

  private static void ValidateTargetKind(SubassemblyTargetInfo info, string targetType)
  {
    var kind = targetType.ToLowerInvariant();
    var ok = info.TargetType switch
    {
      SubassemblyLogicalNameType.Surface => kind == "surface",
      SubassemblyLogicalNameType.Offset or SubassemblyLogicalNameType.Alignment => kind == "alignment",
      SubassemblyLogicalNameType.Elevation or SubassemblyLogicalNameType.Profile => kind == "profile",
      _ => false,
    };
    if (!ok)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"Target '{info.SubassemblyName}:{info.LogicalName}' expects a {info.TargetType} target, not '{targetType}'. Nothing was changed.");
  }

  private static ObjectId? ResolveTargetObjectId(CivilDocument civilDoc, Transaction transaction, string targetType, string targetName)
  {
    switch (targetType.ToLowerInvariant())
    {
      case "surface":
        foreach (ObjectId id in civilDoc.GetSurfaceIds())
        {
          var s = CivilObjectUtils.GetRequiredObject<CivilSurface>(transaction, id, OpenMode.ForRead);
          if (string.Equals(s.Name, targetName, StringComparison.OrdinalIgnoreCase)) return id;
        }
        break;
      case "alignment":
        foreach (ObjectId id in civilDoc.GetAlignmentIds())
        {
          var a = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, id, OpenMode.ForRead);
          if (string.Equals(a.Name, targetName, StringComparison.OrdinalIgnoreCase)) return id;
        }
        break;
      case "profile":
        foreach (ObjectId aid in civilDoc.GetAlignmentIds())
        {
          var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, aid, OpenMode.ForRead);
          foreach (ObjectId pid in alignment.GetProfileIds())
          {
            var p = CivilObjectUtils.GetRequiredObject<Profile>(transaction, pid, OpenMode.ForRead);
            if (string.Equals(p.Name, targetName, StringComparison.OrdinalIgnoreCase)) return pid;
          }
        }
        break;
      default:
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"targetType '{targetType}' is not supported; use surface, alignment or profile. Nothing was changed.");
    }
    return null;
  }
}
