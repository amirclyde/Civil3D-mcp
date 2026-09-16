using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Ribbon "Profile" drop-down extras on the documented Civil 3D 2026 API:
///
///   Create Profile from File   -> text file of "station elevation" pairs → Profile.CreateByLayout + PVIs
///   Copy profile               -> Profile.CreateStaticFGFromProfile(name, profileId, layer, style, labelSet)
///   Create Multiple Profile Views -> ProfileView.CreateMultiple(alignmentId, insert, name, bandSetId, styleId, MultipleProfileViewsCreationOptions)
///   Create Surface Profile with offset / station range -> Profile.CreateFromSurface(..., offset, sampleStart, sampleEnd)
/// </summary>
public static class ProfileExtraCommands
{
  // -------------------------------------------------------------------------
  // profileCreateFromFile
  // -------------------------------------------------------------------------

  public static Task<object?> CreateFromFileAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var filePath = PluginRuntime.GetOptionalString(parameters, "filePath");
    var text = PluginRuntime.GetOptionalString(parameters, "text");
    var stationColumn = PluginRuntime.GetOptionalInt(parameters, "stationColumn") ?? 0;
    var elevationColumn = PluginRuntime.GetOptionalInt(parameters, "elevationColumn") ?? 1;
    var skipLines = PluginRuntime.GetOptionalInt(parameters, "skipLines") ?? 0;

    if (string.IsNullOrWhiteSpace(filePath) && string.IsNullOrWhiteSpace(text))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Give filePath (a text file of station/elevation lines) or text (the lines themselves).");

    var lines = !string.IsNullOrWhiteSpace(text)
      ? text!.Split('\n')
      : File.Exists(filePath) ? File.ReadAllLines(filePath!)
      : throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"File not found: {filePath}");

    var pvis = new List<(double Station, double Elevation)>();
    var skipped = new List<string>();
    var lineNo = 0;
    foreach (var raw in lines)
    {
      lineNo++;
      if (lineNo <= skipLines) continue;
      var line = raw.Trim();
      if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;
      var parts = line.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length <= Math.Max(stationColumn, elevationColumn)
        || !double.TryParse(parts[stationColumn], NumberStyles.Float, CultureInfo.InvariantCulture, out var station)
        || !double.TryParse(parts[elevationColumn], NumberStyles.Float, CultureInfo.InvariantCulture, out var elevation))
      {
        skipped.Add($"line {lineNo}: '{line}'");
        continue;
      }
      pvis.Add((station, elevation));
    }
    if (pvis.Count < 2)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"Only {pvis.Count} usable station/elevation pair(s) found. Expected lines like '0+000 12.345' or '150.0,11.9'. Skipped: {string.Join("; ", skipped.Take(5))}");
    pvis.Sort((a, b) => a.Station.CompareTo(b.Station));

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var outside = pvis.Where(p => p.Station < alignment.StartingStation - 1e-6 || p.Station > alignment.EndingStation + 1e-6).ToList();
      if (outside.Count > 0)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"{outside.Count} station(s) fall outside '{alignment.Name}' ({alignment.StartingStation:0.###} to {alignment.EndingStation:0.###}), e.g. {outside[0].Station:0.###}. Nothing was created.");

      var layerId = LookupUtils.GetLayerId(database, transaction, PluginRuntime.GetOptionalString(parameters, "layer"));
      var styleId = LookupUtils.GetProfileStyleId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "style"));
      var labelSetId = LookupUtils.GetProfileLabelSetId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "labelSet"));
      ObjectId profileId;
      try { profileId = Profile.CreateByLayout(profileName, alignment.ObjectId, layerId, styleId, labelSetId); }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Civil 3D could not create profile '{profileName}': {ex.GetType().Name}: {ex.Message}");
      }
      var profile = CivilObjectUtils.GetRequiredObject<Profile>(transaction, profileId, OpenMode.ForWrite);
      var added = 0;
      foreach (var (station, elevation) in pvis)
      {
        try { profile.PVIs.AddPVI(station, elevation); added++; }
        catch (Exception ex) { skipped.Add($"PVI {station:0.###}/{elevation:0.###}: {ex.Message}"); }
      }

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["handle"] = CivilObjectUtils.GetHandle(profile),
        ["source"] = filePath ?? "text",
        ["pviCount"] = added,
        ["skipped"] = skipped,
        ["startStation"] = profile.StartingStation,
        ["endStation"] = profile.EndingStation,
        ["minElevation"] = profile.ElevationMin,
        ["maxElevation"] = profile.ElevationMax,
        ["entities"] = ProfileCommands.ReadProfileEntities(profile),
        ["created"] = true,
      };
    });
  }

  // -------------------------------------------------------------------------
  // profileCopy: static copy of any profile (design or surface) as a layout profile
  // -------------------------------------------------------------------------

  public static Task<object?> CopyAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var newName = PluginRuntime.GetRequiredString(parameters, "newProfileName");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var source = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForRead);
      var layerId = LookupUtils.GetLayerId(database, transaction, PluginRuntime.GetOptionalString(parameters, "layer") ?? source.Layer);
      var styleId = LookupUtils.GetProfileStyleId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "style"));
      var labelSetId = LookupUtils.GetProfileLabelSetId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "labelSet"));
      string method;
      if (TryStaticCopy(newName, source, layerId, styleId, labelSetId, out var id))
      {
        method = "CreateStaticFGFromProfile";
      }
      else
      {
        // CreateStaticFGFromProfile only accepts dynamic (surface / corridor / offset) sources;
        // a layout profile is copied PVI by PVI, including its vertical curves.
        method = "CreateByLayout + PVIs";
        var skipped = new List<string>();
        try { id = Profile.CreateByLayout(newName, alignment.ObjectId, layerId, styleId, labelSetId); }
        catch (Exception ex) { throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Civil 3D could not create '{newName}': {ex.Message}"); }
        var target = CivilObjectUtils.GetRequiredObject<Profile>(transaction, id, OpenMode.ForWrite);
        // Pass 1: every PVI as a grade break (a curve needs both tangents to exist first).
        var curves = new List<(double Station, double Elevation, ProfileEntity Curve)>();
        foreach (ProfilePVI pvi in source.PVIs)
        {
          try { target.PVIs.AddPVI(pvi.RawStation, pvi.Elevation); }
          catch (Exception ex) { skipped.Add($"PVI {pvi.RawStation:0.###}: {ex.Message}"); continue; }
          if (pvi.VerticalCurve != null) curves.Add((pvi.RawStation, pvi.Elevation, pvi.VerticalCurve));
        }
        // Pass 2: the vertical curves, by the same definition as the source.
        foreach (var (station, elevation, curve) in curves)
        {
          try
          {
            var targetPvi = target.PVIs.GetPVIAt(station, elevation);
            switch (curve)
            {
              case ProfileParabolaSymmetric sym: target.Entities.AddFreeSymmetricParabolaByPVIAndCurveLength(targetPvi, sym.Length); break;
              case ProfileParabolaAsymmetric asym: target.Entities.AddFreeAsymmetricParabolaByPVIAndLengths(targetPvi, asym.AsymmetricLength1, asym.AsymmetricLength2); break;
              case ProfileCircular circ: target.Entities.AddFreeCircularCurveByPVIAndRadius(targetPvi, circ.Radius); break;
            }
          }
          catch (Exception ex) { skipped.Add($"curve at {station:0.###}: {ex.Message}"); }
        }
        if (skipped.Count > 0)
          throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Copy of '{profileName}' incomplete: {string.Join("; ", skipped)}");
      }
      var copy = CivilObjectUtils.GetRequiredObject<Profile>(transaction, id, OpenMode.ForRead);
      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["sourceProfileName"] = source.Name,
        ["profileName"] = copy.Name,
        ["handle"] = CivilObjectUtils.GetHandle(copy),
        ["profileType"] = copy.ProfileType.ToString(),
        ["method"] = method,
        ["pviCount"] = copy.PVIs.Count,
        ["startStation"] = copy.StartingStation,
        ["endStation"] = copy.EndingStation,
        ["minElevation"] = copy.ElevationMin,
        ["maxElevation"] = copy.ElevationMax,
        ["created"] = true,
      };
    });
  }

  private static bool TryStaticCopy(string newName, Profile source, ObjectId layerId, ObjectId styleId, ObjectId labelSetId, out ObjectId id)
  {
    try { id = Profile.CreateStaticFGFromProfile(newName, source.ObjectId, layerId, styleId, labelSetId); return !id.IsNull; }
    catch { id = ObjectId.Null; return false; }
  }

  // -------------------------------------------------------------------------
  // profileViewCreateMultiple: ribbon "Create Multiple Profile Views"
  // -------------------------------------------------------------------------

  public static Task<object?> ViewCreateMultipleAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var baseName = PluginRuntime.GetRequiredString(parameters, "profileViewName");
    var insertX = PluginRuntime.GetRequiredDouble(parameters, "insertX");
    var insertY = PluginRuntime.GetRequiredDouble(parameters, "insertY");
    var lengthOfEachView = PluginRuntime.GetRequiredDouble(parameters, "lengthOfEachView");
    var maxInRow = PluginRuntime.GetOptionalInt(parameters, "maxViewsInRow") ?? 3;
    var gapRow = PluginRuntime.GetOptionalDouble(parameters, "gapBetweenViewsInRow") ?? 100;
    var gapColumn = PluginRuntime.GetOptionalDouble(parameters, "gapBetweenViewsInColumn") ?? 100;

    if (lengthOfEachView <= 0) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "lengthOfEachView must be positive.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var styleId = LookupUtils.GetProfileViewStyleId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "style"));
      var bandSetId = LookupUtils.GetProfileViewBandSetId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "bandSet"));
      var options = new MultipleProfileViewsCreationOptions
      {
        LengthOfEachView = lengthOfEachView,
        MaxViewInRowOrColumn = maxInRow,
        GapBetweenViewsInRow = gapRow,
        GapBetweenViewsInColumn = gapColumn,
      };

      ObjectIdCollection ids;
      try
      {
        ids = bandSetId.IsNull || styleId.IsNull
          ? ProfileView.CreateMultiple(alignment.ObjectId, new Point3d(insertX, insertY, 0), options)
          : ProfileView.CreateMultiple(alignment.ObjectId, new Point3d(insertX, insertY, 0), baseName, bandSetId, styleId, options);
      }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Civil 3D could not create the profile views: {ex.GetType().Name}: {ex.Message}");
      }

      var views = new List<Dictionary<string, object?>>();
      var i = 0;
      foreach (ObjectId id in ids)
      {
        var view = CivilObjectUtils.GetRequiredObject<ProfileView>(transaction, id, OpenMode.ForWrite);
        if (bandSetId.IsNull || styleId.IsNull)
        {
          view.Name = ids.Count == 1 ? baseName : $"{baseName} ({i + 1})";
          if (!styleId.IsNull && !string.IsNullOrWhiteSpace(PluginRuntime.GetOptionalString(parameters, "style"))) view.StyleId = styleId;
          if (!bandSetId.IsNull) view.Bands.ImportBandSetStyle(bandSetId);
        }
        views.Add(new Dictionary<string, object?>
        {
          ["index"] = i++,
          ["name"] = view.Name,
          ["handle"] = CivilObjectUtils.GetHandle(view),
          ["startStation"] = view.StationStart,
          ["endStation"] = view.StationEnd,
          ["location"] = TryLocation(view),
        });
      }

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["viewCount"] = views.Count,
        ["lengthOfEachView"] = lengthOfEachView,
        ["views"] = views,
        ["created"] = true,
      };
    });
  }

  private static double[]? TryLocation(ProfileView view)
  {
    try { var p = view.Location; return new[] { p.X, p.Y }; } catch { return null; }
  }

  // -------------------------------------------------------------------------
  // profileCreateFromSurface with offset + sample range (extends create_from_surface)
  // -------------------------------------------------------------------------

  public static Task<object?> CreateFromSurfaceAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var surfaceName = PluginRuntime.GetRequiredString(parameters, "surfaceName");
    var offset = PluginRuntime.GetOptionalDouble(parameters, "offset");
    var sampleStart = PluginRuntime.GetOptionalDouble(parameters, "startStation");
    var sampleEnd = PluginRuntime.GetOptionalDouble(parameters, "endStation");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, surfaceName, OpenMode.ForRead);
      var layerId = LookupUtils.GetLayerId(database, transaction, PluginRuntime.GetOptionalString(parameters, "layer"));
      var styleName = PluginRuntime.GetOptionalString(parameters, "style");
      var styleId = LookupUtils.GetStyleIdPreferring(civilDoc.Styles.ProfileStyles, transaction, styleName, new[] { "Existing Ground Profile", "Existing Ground", "Existing" });
      var labelSetId = LookupUtils.GetProfileLabelSetId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "labelSet"));

      var start = sampleStart ?? alignment.StartingStation;
      var end = sampleEnd ?? alignment.EndingStation;
      if (start < alignment.StartingStation - 1e-6 || end > alignment.EndingStation + 1e-6 || end <= start)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"Sample range {start:0.###}-{end:0.###} must lie within '{alignment.Name}' ({alignment.StartingStation:0.###} to {alignment.EndingStation:0.###}).");

      ObjectId id;
      try
      {
        id = (offset.HasValue || sampleStart.HasValue || sampleEnd.HasValue)
          ? Profile.CreateFromSurface(profileName, alignment.ObjectId, surface.ObjectId, layerId, styleId, labelSetId, offset ?? 0, start, end)
          : Profile.CreateFromSurface(profileName, alignment.ObjectId, surface.ObjectId, layerId, styleId, labelSetId);
      }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR", $"Civil 3D could not sample '{surface.Name}' along '{alignment.Name}': {ex.GetType().Name}: {ex.Message}");
      }
      var profile = CivilObjectUtils.GetRequiredObject<Profile>(transaction, id, OpenMode.ForRead);
      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["surfaceName"] = surface.Name,
        ["profileName"] = profile.Name,
        ["handle"] = CivilObjectUtils.GetHandle(profile),
        ["offset"] = offset ?? 0,
        ["startStation"] = profile.StartingStation,
        ["endStation"] = profile.EndingStation,
        ["minElevation"] = profile.ElevationMin,
        ["maxElevation"] = profile.ElevationMax,
        ["style"] = AlignmentGeometryReader.ReadStyleName(profile, profile.StyleId, transaction).Name,
        ["created"] = true,
      };
    });
  }
}
