using System.Text.Json.Nodes;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using System.Linq;
using System.Globalization;
using System.Text;

namespace Civil3DMcpPlugin;

public static class SectionCommands
{
  public static Task<object?> ListSampleLineGroupsAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var sampleLineGroups = new List<Dictionary<string, object?>>();

      foreach (ObjectId groupId in alignment.GetSampleLineGroupIds())
      {
        var group = CivilObjectUtils.GetRequiredObject<SampleLineGroup>(transaction, groupId, OpenMode.ForRead);
        var stations = new List<double>();
        foreach (ObjectId sampleLineId in group.GetSampleLineIds())
        {
          var sampleLine = CivilObjectUtils.GetRequiredObject<SampleLine>(transaction, sampleLineId, OpenMode.ForRead);
          stations.Add(sampleLine.Station);
        }
        stations.Sort();

        sampleLineGroups.Add(new Dictionary<string, object?>
        {
          ["name"] = group.Name,
          ["handle"] = CivilObjectUtils.GetHandle(group),
          ["sampleLineCount"] = stations.Count,
          ["stations"] = stations,
          ["sectionSources"] = ReadSectionSources(group, transaction),
          ["sectionViewGroupCount"] = group.SectionViewGroups.Count,
        });
      }

      return new Dictionary<string, object?>
      {
        ["sampleLineGroups"] = sampleLineGroups,
      };
    });
  }

  public static Task<object?> CreateSampleLinesAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var groupName = PluginRuntime.GetRequiredString(parameters, "groupName");
    var leftWidth = PluginRuntime.GetRequiredDouble(parameters, "leftWidth");
    var rightWidth = PluginRuntime.GetRequiredDouble(parameters, "rightWidth");
    var interval = PluginRuntime.GetOptionalDouble(parameters, "interval");
    var stationsNode = PluginRuntime.GetParameter(parameters, "stations") as JsonArray;

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      foreach (ObjectId existingId in alignment.GetSampleLineGroupIds())
      {
        var existing = CivilObjectUtils.GetRequiredObject<SampleLineGroup>(transaction, existingId, OpenMode.ForRead);
        if (string.Equals(existing.Name, groupName, StringComparison.OrdinalIgnoreCase))
          throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"Sample line group '{groupName}' already exists on alignment '{alignment.Name}'. Nothing was created.");
      }
      if (leftWidth <= 0 || rightWidth <= 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "leftWidth and rightWidth must be positive swath widths. Nothing was created.");
      var groupId = SampleLineGroup.Create(groupName, alignment.ObjectId);
      var group = CivilObjectUtils.GetRequiredObject<SampleLineGroup>(transaction, groupId, OpenMode.ForWrite);
      var sectionSources = group.GetSectionSources();
      var requestedSurfaces = (PluginRuntime.GetParameter(parameters, "surfaces") as JsonArray)?.Select(node => node?.GetValue<string>()).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

      foreach (SectionSource source in sectionSources)
      {
        var sourceObject = transaction.GetObject(source.SourceId, OpenMode.ForRead);
        var sourceName = CivilObjectUtils.GetName(sourceObject);
        source.IsSampled = requestedSurfaces.Count == 0 || (sourceName != null && requestedSurfaces.Contains(sourceName));
      }

      var stations = new List<double>();
      if (stationsNode != null && stationsNode.Count > 0)
      {
        stations.AddRange(stationsNode.Select(node => node?.GetValue<double>() ?? 0));
      }
      else if (interval.HasValue && interval.Value > 0)
      {
        var count = (int)Math.Floor((alignment.EndingStation - alignment.StartingStation) / interval.Value + 1e-9);
        for (var i = 0; i <= count; i++)
          stations.Add(Math.Round(alignment.StartingStation + i * interval.Value, 6));
        if (alignment.EndingStation - stations[^1] > 1e-6)
          stations.Add(alignment.EndingStation);
      }
      else
      {
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "createSampleLines requires either stations or interval.");
      }

      var createdStations = new List<double>();
      var skipped = new List<double>();
      foreach (var station in stations.Distinct().OrderBy(value => value))
      {
        if (station < alignment.StartingStation - 1e-6 || station > alignment.EndingStation + 1e-6)
        {
          skipped.Add(station);
          continue;
        }
        double x1 = 0;
        double y1 = 0;
        double x2 = 0;
        double y2 = 0;
        alignment.PointLocation(station, -leftWidth, ref x1, ref y1);
        alignment.PointLocation(station, rightWidth, ref x2, ref y2);
        var points = new Point2dCollection
        {
          new(x1, y1),
          new(x2, y2),
        };
        // Civil 3D wants sample line names unique across the drawing, not only inside the group: a second group at the same stations failed with "Sample line name should not duplicate"
        SampleLine.Create($"{alignment.Name} {station:0+000.00}", groupId, points);
        createdStations.Add(station);
      }

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["sampleLineGroupName"] = group.Name,
        ["created"] = createdStations.Count,
        ["stations"] = createdStations,
        ["skippedOutsideAlignment"] = skipped,
        ["sectionSources"] = ReadSectionSources(group, transaction),
      };
    });
  }

  public static Task<object?> GetSectionDataAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var sampleLineGroupName = PluginRuntime.GetRequiredString(parameters, "sampleLineGroupName");
    var station = PluginRuntime.GetRequiredDouble(parameters, "station");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var group = FindSampleLineGroup(alignment, transaction, sampleLineGroupName);

      var sampleLines = group.GetSampleLineIds()
        .Cast<ObjectId>()
        .Select(id => CivilObjectUtils.GetRequiredObject<SampleLine>(transaction, id, OpenMode.ForRead))
        .ToList();
      var sampleLine = sampleLines.FirstOrDefault(line => Math.Abs(line.Station - station) < 0.001);
      if (sampleLine == null)
      {
        var available = string.Join(", ", sampleLines.Select(l => l.Station.ToString("0.###", CultureInfo.InvariantCulture)).OrderBy(x => x));
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No sample line at station {station:0.###} in group '{group.Name}'. Stations: {available}.");
      }

      var sections = new List<Dictionary<string, object?>>();
      foreach (ObjectId sectionId in sampleLine.GetSectionIds())
      {
        var section = CivilObjectUtils.GetRequiredObject<Autodesk.Civil.DatabaseServices.Section>(transaction, sectionId, OpenMode.ForRead);
        var points = new List<Dictionary<string, object?>>();
        foreach (SectionPoint point in section.SectionPoints)
        {
          points.Add(new Dictionary<string, object?>
          {
            ["offset"] = point.Location.X,
            ["elevation"] = point.Location.Y,
          });
        }
        var entry = new Dictionary<string, object?>
        {
          ["source"] = section.SourceName,
          ["sourceType"] = section.SourceType.ToString(),
          ["sourceHandle"] = section.SourceId.IsNull ? null : section.SourceId.Handle.ToString(),
          ["pointCount"] = points.Count,
          ["minElevation"] = points.Count > 0 ? points.Min(p => (double)p["elevation"]!) : null,
          ["maxElevation"] = points.Count > 0 ? points.Max(p => (double)p["elevation"]!) : null,
          ["points"] = points,
        };

        // Corridor sections carry no SectionPoints through the API; read the applied assembly instead.
        if (points.Count == 0 && !section.SourceId.IsNull && transaction.GetObject(section.SourceId, OpenMode.ForRead) is Corridor corridor)
        {
          Baseline? baseline = null;
          foreach (Baseline candidate in corridor.Baselines)
          {
            if (candidate.AlignmentId == alignment.ObjectId) { baseline = candidate; break; }
          }
          baseline ??= corridor.Baselines.Count > 0 ? corridor.Baselines[0] : null;
          if (baseline != null)
          {
            try
            {
              var applied = CorridorEditingCommands.ReadAppliedAssembly(corridor, baseline, sampleLine.Station);
              entry["appliedAssembly"] = applied;
              entry["points"] = applied["points"];
              entry["pointCount"] = ((List<Dictionary<string, object?>>)applied["points"]!).Count;
              entry["minElevation"] = applied["minElevation"];
              entry["maxElevation"] = applied["maxElevation"];
              entry["appliedStation"] = applied["station"];
            }
            catch (JsonRpcDispatchException ex)
            {
              entry["appliedAssemblyError"] = ex.Message;
            }
          }
        }
        sections.Add(entry);
      }

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["sampleLineGroupName"] = group.Name,
        ["sampleLineName"] = sampleLine.Name,
        ["station"] = sampleLine.Station,
        ["sections"] = sections,
        ["surfaces"] = sections,
        ["units"] = new Dictionary<string, object?>
        {
          ["horizontal"] = CivilObjectUtils.LinearUnits(database),
          ["vertical"] = CivilObjectUtils.LinearUnits(database),
        },
      };
    });
  }

  // -------------------------------------------------------------------------
  // createSectionViews
  // -------------------------------------------------------------------------

  public static Task<object?> CreateSectionViewsAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var sampleLineGroupName = PluginRuntime.GetRequiredString(parameters, "sampleLineGroupName");
    var insertionX = PluginRuntime.GetRequiredDouble(parameters, "insertionX");
    var insertionY = PluginRuntime.GetRequiredDouble(parameters, "insertionY");
    var style = PluginRuntime.GetOptionalString(parameters, "style");
    var bandSetStyle = PluginRuntime.GetOptionalString(parameters, "bandSetStyle");
    var leftOffset = PluginRuntime.GetOptionalDouble(parameters, "leftOffset");
    var rightOffset = PluginRuntime.GetOptionalDouble(parameters, "rightOffset");
    var stationStart = PluginRuntime.GetOptionalDouble(parameters, "stationStart");
    var stationEnd = PluginRuntime.GetOptionalDouble(parameters, "stationEnd");
    var rows = PluginRuntime.GetOptionalInt(parameters, "rows");
    var gapBetweenViews = PluginRuntime.GetOptionalDouble(parameters, "gapBetweenViews");

    if (rows.HasValue || gapBetweenViews.HasValue)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        "Civil 3D 2026 SectionViewGroup draft placement uses drawing settings; per-call rows and gapBetweenViews are not exposed by the .NET API.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var group = FindSampleLineGroup(alignment, transaction, sampleLineGroupName);
      var styleId = LookupUtils.GetSectionViewStyleId(civilDoc, transaction, style);
      var bandSetId = LookupUtils.GetSectionViewBandSetId(civilDoc, transaction, bandSetStyle);
      var insertionPoint = new Point3d(insertionX, insertionY, 0);

      var createdGroup = CreateSectionViewGroup(
        alignment,
        group,
        insertionPoint,
        leftOffset,
        rightOffset,
        stationStart,
        stationEnd,
        null,
        null);
      var createdViews = OpenSectionViews(createdGroup, transaction, OpenMode.ForWrite).ToList();
      ApplySectionViewStyles(createdViews, styleId, bandSetId, applyToAll: true);

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["sampleLineGroupName"] = group.Name,
        ["created"] = createdViews.Count,
        ["layoutSource"] = "Civil 3D SectionViewGroup draft placement settings",
        ["insertionPoint"] = new Dictionary<string, object?>
        {
          ["x"] = insertionPoint.X,
          ["y"] = insertionPoint.Y,
        },
      };
    });
  }

  // -------------------------------------------------------------------------
  // createSectionViewAtStation — one SectionView from one sample line
  // -------------------------------------------------------------------------

  public static Task<object?> CreateSectionViewAtStationAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var sampleLineGroupName = PluginRuntime.GetRequiredString(parameters, "sampleLineGroupName");
    var station = PluginRuntime.GetRequiredDouble(parameters, "station");
    var insertionX = PluginRuntime.GetRequiredDouble(parameters, "insertionX");
    var insertionY = PluginRuntime.GetRequiredDouble(parameters, "insertionY");
    var viewName = PluginRuntime.GetOptionalString(parameters, "viewName");
    var style = PluginRuntime.GetOptionalString(parameters, "style");
    var bandSetStyle = PluginRuntime.GetOptionalString(parameters, "bandSetStyle");
    var tolerance = PluginRuntime.GetOptionalDouble(parameters, "tolerance") ?? 0.5;
    var leftOffset = PluginRuntime.GetOptionalDouble(parameters, "leftOffset");
    var rightOffset = PluginRuntime.GetOptionalDouble(parameters, "rightOffset");
    var elevationMin = PluginRuntime.GetOptionalDouble(parameters, "elevationMin");
    var elevationMax = PluginRuntime.GetOptionalDouble(parameters, "elevationMax");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var group = FindSampleLineGroup(alignment, transaction, sampleLineGroupName);
      var sampleLineIds = group.GetSampleLineIds(station, tolerance);
      if (sampleLineIds.Count == 0)
      {
        var stations = group.GetSampleLineIds().Cast<ObjectId>()
          .Select(id => CivilObjectUtils.GetRequiredObject<SampleLine>(transaction, id, OpenMode.ForRead).Station)
          .OrderBy(v => v).Select(v => v.ToString("F3", CultureInfo.InvariantCulture));
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND",
          $"No sample line within {tolerance} of station {station:F3} in group '{group.Name}'. Sample line stations: {string.Join(", ", stations)}.");
      }
      var sampleLine = CivilObjectUtils.GetRequiredObject<SampleLine>(transaction, sampleLineIds[0], OpenMode.ForRead);
      var name = string.IsNullOrWhiteSpace(viewName) ? $"{alignment.Name} {sampleLine.Station:F2}" : viewName!;
      var styleId = LookupUtils.GetSectionViewStyleId(civilDoc, transaction, style);
      var bandSetId = LookupUtils.GetSectionViewBandSetId(civilDoc, transaction, bandSetStyle);

      ObjectId viewId;
      try
      {
        viewId = SectionView.Create(name, sampleLine.ObjectId, new Point3d(insertionX, insertionY, 0));
      }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"SectionView.Create failed at station {sampleLine.Station:F3}: {ex.GetType().Name}: {ex.Message}. Nothing was created.");
      }
      var view = CivilObjectUtils.GetRequiredObject<SectionView>(transaction, viewId, OpenMode.ForWrite);
      if (!styleId.IsNull) view.StyleId = styleId;
      if (!bandSetId.IsNull) view.Bands.ImportBandSetStyle(bandSetId);
      if (leftOffset.HasValue && rightOffset.HasValue)
      {
        view.IsOffsetRangeAutomatic = false;
        view.OffsetLeft = leftOffset.Value;
        view.OffsetRight = rightOffset.Value;
      }
      if (elevationMin.HasValue && elevationMax.HasValue)
      {
        view.IsElevationRangeAutomatic = false;
        view.ElevationMin = elevationMin.Value;
        view.ElevationMax = elevationMax.Value;
      }

      var summary = MapSectionViewSummary(view, group, alignment);
      summary["station"] = sampleLine.Station;
      summary["sampleLineName"] = sampleLine.Name;
      summary["insertionPoint"] = new Dictionary<string, object?> { ["x"] = insertionX, ["y"] = insertionY };
      summary["offsetRange"] = new Dictionary<string, object?> { ["automatic"] = view.IsOffsetRangeAutomatic, ["left"] = view.OffsetLeft, ["right"] = view.OffsetRight };
      summary["elevationRange"] = new Dictionary<string, object?> { ["automatic"] = view.IsElevationRangeAutomatic, ["min"] = view.ElevationMin, ["max"] = view.ElevationMax };
      summary["created"] = true;
      return summary;
    });
  }

  // -------------------------------------------------------------------------
  // listSectionViews
  // -------------------------------------------------------------------------

  public static Task<object?> ListSectionViewsAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetOptionalString(parameters, "alignmentName");
    var sampleLineGroupName = PluginRuntime.GetOptionalString(parameters, "sampleLineGroupName");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      List<Dictionary<string, object?>> result = new();
      var alignments = new List<Alignment>();
      if (string.IsNullOrWhiteSpace(alignmentName))
      {
        alignments.AddRange(
          civilDoc.GetAlignmentIds()
            .Cast<ObjectId>()
            .Select(id => CivilObjectUtils.GetRequiredObject<Alignment>(transaction, id, OpenMode.ForRead)));
      }
      else
      {
        alignments.Add(CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName));
      }

      foreach (var alignment in alignments)
      {
        foreach (var group in ListSampleLineGroups(alignment, transaction, sampleLineGroupName))
        {
          foreach (var view in EnumerateSectionViews(group, transaction))
          {
            result.Add(MapSectionViewSummary(view, group, alignment));
          }
        }
      }

      if (result.Count == 0)
      {
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", "No section views found for the requested filters.");
      }

      return new Dictionary<string, object?>
      {
        ["sectionViews"] = result,
        ["count"] = result.Count,
      };
    });
  }

  // -------------------------------------------------------------------------
  // updateSectionViewStyles
  // -------------------------------------------------------------------------

  public static Task<object?> UpdateSectionViewStylesAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var sampleLineGroupName = PluginRuntime.GetRequiredString(parameters, "sampleLineGroupName");
    var style = PluginRuntime.GetOptionalString(parameters, "style");
    var bandSetStyle = PluginRuntime.GetOptionalString(parameters, "bandSetStyle");
    var applyToAll = PluginRuntime.GetOptionalBool(parameters, "applyToAll") ?? true;

    if (style == null && bandSetStyle == null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "updateSectionViewStyles requires 'style' or 'bandSetStyle'.");
    }

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var group = FindSampleLineGroup(alignment, transaction, sampleLineGroupName);
      var styleId = string.IsNullOrWhiteSpace(style) ? ObjectId.Null : LookupUtils.GetSectionViewStyleId(civilDoc, transaction, style);
      var bandSetId = LookupUtils.GetSectionViewBandSetId(civilDoc, transaction, bandSetStyle);

      var sectionViews = EnumerateSectionViews(group, transaction).ToList();
      if (sectionViews.Count == 0)
      {
        throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No section views exist for sample line group '{sampleLineGroupName}'.");
      }

      var styleUpdated = ApplySectionViewStyles(sectionViews, styleId, bandSetId, applyToAll);

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["sampleLineGroupName"] = group.Name,
        ["updated"] = styleUpdated,
      };
    });
  }

  // -------------------------------------------------------------------------
  // createSectionViewGroup
  // -------------------------------------------------------------------------

  public static Task<object?> CreateSectionViewGroupAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var sampleLineGroupName = PluginRuntime.GetRequiredString(parameters, "sampleLineGroupName");
    var insertionX = PluginRuntime.GetRequiredDouble(parameters, "insertionX");
    var insertionY = PluginRuntime.GetRequiredDouble(parameters, "insertionY");
    var style = PluginRuntime.GetOptionalString(parameters, "style");
    var bandSetStyle = PluginRuntime.GetOptionalString(parameters, "bandSetStyle");
    var plotStyle = PluginRuntime.GetOptionalString(parameters, "plotStyle");
    var leftOffset = PluginRuntime.GetOptionalDouble(parameters, "leftOffset");
    var rightOffset = PluginRuntime.GetOptionalDouble(parameters, "rightOffset");
    var stationStart = PluginRuntime.GetOptionalDouble(parameters, "stationStart");
    var stationEnd = PluginRuntime.GetOptionalDouble(parameters, "stationEnd");
    var templatePath = PluginRuntime.GetOptionalString(parameters, "templatePath");
    var layoutName = PluginRuntime.GetOptionalString(parameters, "layoutName");
    var rows = PluginRuntime.GetOptionalInt(parameters, "rows");
    var columns = PluginRuntime.GetOptionalInt(parameters, "columns");
    var gapX = PluginRuntime.GetOptionalDouble(parameters, "gapX");
    var gapY = PluginRuntime.GetOptionalDouble(parameters, "gapY");

    if (rows.HasValue || columns.HasValue || gapX.HasValue || gapY.HasValue)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        "Civil 3D 2026 SectionViewGroup draft placement uses drawing settings; per-call rows, columns, gapX, and gapY are not exposed by the .NET API.");
    }
    var production = !string.IsNullOrWhiteSpace(templatePath) || !string.IsNullOrWhiteSpace(layoutName);
    if (production && (string.IsNullOrWhiteSpace(templatePath) || string.IsNullOrWhiteSpace(layoutName)))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Production placement needs both templatePath and layoutName. Nothing was created.");
    if (production && !File.Exists(templatePath))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Sheet template '{templatePath}' was not found. Nothing was created.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var group = FindSampleLineGroup(alignment, transaction, sampleLineGroupName);
      var styleId = LookupUtils.GetSectionViewStyleId(civilDoc, transaction, style);
      var bandSetId = LookupUtils.GetSectionViewBandSetId(civilDoc, transaction, bandSetStyle);
      var plotStyleId = LookupUtils.GetGroupPlotStyleId(civilDoc, transaction, plotStyle);
      var insertionPoint = new Point3d(insertionX, insertionY, 0);

      var createdGroup = CreateSectionViewGroup(
        alignment,
        group,
        insertionPoint,
        leftOffset,
        rightOffset,
        stationStart,
        stationEnd,
        production ? templatePath : null,
        production ? layoutName : null);
      if (plotStyleId != ObjectId.Null)
      {
        createdGroup.PlotStyleId = plotStyleId;
      }
      var createdViews = OpenSectionViews(createdGroup, transaction, OpenMode.ForWrite).ToList();
      ApplySectionViewStyles(createdViews, styleId, bandSetId, applyToAll: true);

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["sampleLineGroupName"] = group.Name,
        ["sectionViewGroupName"] = createdGroup.Name,
        ["created"] = createdViews.Count,
        ["placement"] = production ? "production" : "draft",
        ["layoutSource"] = production ? $"{templatePath} / {layoutName}" : "Civil 3D SectionViewGroup draft placement settings",
        ["stationStart"] = stationStart ?? alignment.StartingStation,
        ["stationEnd"] = stationEnd ?? alignment.EndingStation,
        ["insertionPoint"] = new Dictionary<string, object?>
        {
          ["x"] = insertionPoint.X,
          ["y"] = insertionPoint.Y,
        },
        ["sectionViews"] = createdViews.Select(view => MapSectionViewSummary(view, group, alignment)).ToList(),
      };
    });
  }

  // -------------------------------------------------------------------------
  // exportSectionData
  // -------------------------------------------------------------------------

  public static Task<object?> ExportSectionDataAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var sampleLineGroupName = PluginRuntime.GetRequiredString(parameters, "sampleLineGroupName");
    var outputPath = PluginRuntime.GetRequiredString(parameters, "outputPath");
    var includeElevations = PluginRuntime.GetOptionalBool(parameters, "includeElevations") ?? true;
    var includeMaterials = PluginRuntime.GetOptionalBool(parameters, "includeMaterials") ?? false;
    var stationStart = PluginRuntime.GetOptionalDouble(parameters, "stationStart");
    var stationEnd = PluginRuntime.GetOptionalDouble(parameters, "stationEnd");

    var overwrite = PluginRuntime.GetOptionalBool(parameters, "overwrite") ?? false;
    if (includeMaterials)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        "Section material quantities are not exposed by this export path. Set includeMaterials=false; no file was written.");
    }

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var group = FindSampleLineGroup(alignment, transaction, sampleLineGroupName);
      var csv = new StringBuilder("Station,Source,Offset");
      if (includeElevations) csv.Append(",Elevation");
      csv.AppendLine();
      var rowsWritten = 0;

      foreach (ObjectId sampleLineId in group.GetSampleLineIds())
      {
        var sampleLine = CivilObjectUtils.GetRequiredObject<SampleLine>(transaction, sampleLineId, OpenMode.ForRead);
        if (stationStart.HasValue && sampleLine.Station < stationStart.Value) continue;
        if (stationEnd.HasValue && sampleLine.Station > stationEnd.Value) continue;

        foreach (ObjectId sectionId in sampleLine.GetSectionIds())
        {
          var section = CivilObjectUtils.GetRequiredObject<Autodesk.Civil.DatabaseServices.Section>(transaction, sectionId, OpenMode.ForRead);
          foreach (SectionPoint point in section.SectionPoints)
          {
            csv.Append(sampleLine.Station.ToString("G17", CultureInfo.InvariantCulture))
              .Append(',').Append(EscapeCsv(section.SourceName))
              .Append(',').Append(point.Location.X.ToString("G17", CultureInfo.InvariantCulture));
            if (includeElevations)
              csv.Append(',').Append(point.Location.Y.ToString("G17", CultureInfo.InvariantCulture));
            csv.AppendLine();
            rowsWritten++;
          }
        }
      }

      var canonicalPath = FileBoundary.WriteAllTextAtomic(
        outputPath, csv.ToString(), Encoding.UTF8, overwrite, ".csv");
      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["sampleLineGroupName"] = group.Name,
        ["outputPath"] = canonicalPath,
        ["rowsWritten"] = rowsWritten,
        ["includeElevations"] = includeElevations,
        ["units"] = CivilObjectUtils.LinearUnits(database),
      };
    });
  }

  private static string EscapeCsv(string value)
  {
    if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
      return $"\"{value.Replace("\"", "\"\"")}\"";
    return value;
  }

  private static List<Dictionary<string, object?>> ReadSectionSources(SampleLineGroup group, Transaction transaction)
  {
    var rows = new List<Dictionary<string, object?>>();
    try
    {
      // SampleLineGroup.GetSectionSources() writes to the group internally; on a group opened for
      // read Civil 3D 2026.2 does not throw but aborts AutoCAD ("INTERNAL ERROR: !dbobji.cpp@8703:
      // eNotOpenForWrite"). Upgrade the open first (read-only callers run in an aborted transaction).
      if (!group.IsWriteEnabled) group.UpgradeOpen();
      foreach (SectionSource source in group.GetSectionSources())
      {
        string? name = null;
        try { name = CivilObjectUtils.GetName(transaction.GetObject(source.SourceId, OpenMode.ForRead)); } catch { }
        rows.Add(new Dictionary<string, object?>
        {
          ["name"] = name,
          ["type"] = source.SourceType.ToString(),
          ["isSampled"] = source.IsSampled,
        });
      }
    }
    catch (Exception ex)
    {
      PluginLog.Debug("Section", "section sources not readable", ex);
    }
    return rows;
  }

  private static SampleLineGroup FindSampleLineGroup(Alignment alignment, Transaction transaction, string sampleLineGroupName)
  {
    var groupIds = alignment.GetSampleLineGroupIds();
    if (groupIds.Count == 0)
    {
      throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No sample line groups exist for alignment '{alignment.Name}'.");
    }

    foreach (ObjectId groupId in groupIds)
    {
      var group = CivilObjectUtils.GetRequiredObject<SampleLineGroup>(transaction, groupId, OpenMode.ForRead);
      if (string.Equals(group.Name, sampleLineGroupName, StringComparison.OrdinalIgnoreCase))
      {
        return group;
      }
    }

    throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"Sample line group '{sampleLineGroupName}' was not found.");
  }

  private static IEnumerable<SampleLineGroup> ListSampleLineGroups(Alignment alignment, Transaction transaction, string? sampleLineGroupName = null)
  {
    var groupIds = alignment.GetSampleLineGroupIds();
    if (groupIds.Count == 0)
    {
      return Enumerable.Empty<SampleLineGroup>();
    }

    var result = new List<SampleLineGroup>();
    foreach (ObjectId groupId in groupIds)
    {
      var group = CivilObjectUtils.GetRequiredObject<SampleLineGroup>(transaction, groupId, OpenMode.ForRead);
      if (string.IsNullOrWhiteSpace(sampleLineGroupName) || string.Equals(group.Name, sampleLineGroupName, StringComparison.OrdinalIgnoreCase))
      {
        result.Add(group);
      }
    }

    return result;
  }

  private static IEnumerable<SectionView> EnumerateSectionViews(SampleLineGroup group, Transaction transaction)
  {
    foreach (SectionViewGroup sectionViewGroup in group.SectionViewGroups)
    {
      foreach (var sectionView in OpenSectionViews(sectionViewGroup, transaction, OpenMode.ForRead))
      {
        yield return sectionView;
      }
    }
  }

  private static IEnumerable<SectionView> OpenSectionViews(SectionViewGroup group, Transaction transaction, OpenMode openMode)
  {
    foreach (ObjectId viewId in group.GetSectionViewIds())
    {
      if (viewId != ObjectId.Null)
      {
        yield return CivilObjectUtils.GetRequiredObject<SectionView>(transaction, viewId, openMode);
      }
    }
  }

  private static SectionViewGroup CreateSectionViewGroup(
    Alignment alignment,
    SampleLineGroup group,
    Point3d insertionPoint,
    double? leftOffset,
    double? rightOffset,
    double? stationStart,
    double? stationEnd,
    string? templatePath,
    string? layoutName)
  {
    if (leftOffset.HasValue != rightOffset.HasValue)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "leftOffset and rightOffset must be supplied together.");
    }
    if (leftOffset.HasValue && leftOffset.Value >= rightOffset!.Value)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "leftOffset must be less than rightOffset for Civil 3D section-view ranges.");
    }

    using var rangeOptions = new SectionViewGroupCreationRangeOptions(group.ObjectId);
    if (leftOffset.HasValue)
    {
      rangeOptions.SetOffsetRange(leftOffset.Value, rightOffset!.Value);
    }
    var placementOptions = new SectionViewGroupCreationPlacementOptions();
    if (!string.IsNullOrWhiteSpace(templatePath))
      placementOptions.UseProductionPlacement(templatePath, layoutName!);
    else
      placementOptions.UseDraftPlacement();
    var start = stationStart ?? alignment.StartingStation;
    var end = stationEnd ?? alignment.EndingStation;
    if (end <= start)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"stationEnd ({end:F3}) must be greater than stationStart ({start:F3}).");
    try
    {
      return group.SectionViewGroups.Add(insertionPoint, start, end, rangeOptions, placementOptions);
    }
    catch (Exception ex) when (ex is not JsonRpcDispatchException)
    {
      throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Civil 3D refused to create the section view group: {ex.GetType().Name}: {ex.Message}. Nothing was created.");
    }
  }

  private static int ApplySectionViewStyles(
    IReadOnlyList<SectionView> sectionViews,
    ObjectId styleId,
    ObjectId bandSetStyleId,
    bool applyToAll)
  {
    int updated = 0;
    var stylesToProcess = applyToAll ? sectionViews : sectionViews.Take(1);
    foreach (var view in stylesToProcess)
    {
      if (!view.IsWriteEnabled)
      {
        view.UpgradeOpen();
      }

      var changed = false;
      if (styleId != ObjectId.Null)
      {
        view.StyleId = styleId;
        changed = true;
      }

      if (bandSetStyleId != ObjectId.Null)
      {
        view.Bands.ImportBandSetStyle(bandSetStyleId);
        changed = true;
      }

      if (changed)
      {
        updated++;
      }
    }

    return updated;
  }

  private static Dictionary<string, object?> MapSectionViewSummary(SectionView view, SampleLineGroup group, Alignment alignment)
  {
    return new Dictionary<string, object?>
    {
      ["alignmentName"] = alignment.Name,
      ["sampleLineGroupName"] = group.Name,
      ["name"] = view.Name,
      ["handle"] = CivilObjectUtils.GetHandle(view),
      ["style"] = view.StyleName,
    };
  }

}
