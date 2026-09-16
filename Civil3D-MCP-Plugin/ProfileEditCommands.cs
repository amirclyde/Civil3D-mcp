using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;

namespace Civil3DMcpPlugin;

/// <summary>
/// Editing commands for Civil 3D vertical profiles and profile views:
/// add_pvi, delete_pvi, add_curve, set_grade, get_elevation,
/// check_k_values, profile_view_create, profile_view_band_set.
/// </summary>
public static class ProfileEditCommands
{
  // ─── profileAddPvi ────────────────────────────────────────────────────────

  public static Task<object?> AddPviAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var station = PluginRuntime.GetRequiredDouble(parameters, "station");
    var elevation = PluginRuntime.GetRequiredDouble(parameters, "elevation");
    var curveLength = PluginRuntime.GetOptionalDouble(parameters, "curveLength");
    var curveLength1 = PluginRuntime.GetOptionalDouble(parameters, "curveLength1");
    var curveLength2 = PluginRuntime.GetOptionalDouble(parameters, "curveLength2");
    var radius = PluginRuntime.GetOptionalDouble(parameters, "radius");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForWrite);

      if (station < alignment.StartingStation - 1e-6 || station > alignment.EndingStation + 1e-6)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
          $"Station {station:0.###} is outside alignment '{alignment.Name}' ({alignment.StartingStation:0.###} to {alignment.EndingStation:0.###}). No PVI was added.");
      if (FindPviNearStation(profile.PVIs, station, 0.001) != null)
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT",
          $"Profile '{profile.Name}' already has a PVI at station {station:0.###}. Use move_pvi to change it. No PVI was added.");

      string method;
      ProfilePVI pvi;
      try
      {
        if (curveLength1.HasValue || curveLength2.HasValue)
        {
          if (!(curveLength1 > 0) || !(curveLength2 > 0))
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "curveLength1 and curveLength2 must both be positive for an asymmetric parabola.");
          pvi = profile.PVIs.AddPVIAsymParabola(station, elevation, curveLength1.Value, curveLength2.Value);
          method = "AddPVIAsymParabola";
        }
        else if (radius.HasValue)
        {
          if (!(radius > 0)) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "radius must be positive for a circular vertical curve.");
          pvi = profile.PVIs.AddPVIArc(station, elevation, radius.Value);
          method = "AddPVIArc";
        }
        else if (curveLength.HasValue)
        {
          if (!(curveLength > 0)) throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "curveLength must be positive.");
          pvi = profile.PVIs.AddPVISymParabola(station, elevation, curveLength.Value);
          method = "AddPVISymParabola";
        }
        else
        {
          pvi = profile.PVIs.AddPVI(station, elevation);
          method = "AddPVI";
        }
      }
      catch (JsonRpcDispatchException) { throw; }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR",
          $"Civil 3D refused the PVI at {station:0.###}/{elevation:0.###} on '{profile.Name}': {ex.GetType().Name}: {ex.Message}. No PVI was added.");
      }

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["station"] = station,
        ["elevation"] = elevation,
        ["method"] = method,
        ["pvi"] = ProfileCommands.ReadPvi(pvi),
        ["pviCount"] = profile.PVIs.Count,
        ["entities"] = ProfileCommands.ReadProfileEntities(profile),
        ["success"] = true,
      };
    });
  }

  // ─── profileMovePvi ───────────────────────────────────────────────────────

  public static Task<object?> MovePviAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var station = PluginRuntime.GetRequiredDouble(parameters, "station");
    var newStation = PluginRuntime.GetOptionalDouble(parameters, "newStation");
    var newElevation = PluginRuntime.GetOptionalDouble(parameters, "newElevation");

    if (!newStation.HasValue && !newElevation.HasValue)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "move_pvi needs newStation and/or newElevation.");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForWrite);
      var pvi = FindPviNearStation(profile.PVIs, station, PviMatchTolerance)
        ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No PVI within {PviMatchTolerance} m of station {station:0.###} in profile '{profileName}'. PVIs: {PviStations(profile)}.");
      var before = ProfileCommands.ReadPvi(pvi);

      try
      {
        if (newStation.HasValue) pvi.RawStation = newStation.Value;
        if (newElevation.HasValue) pvi.Elevation = newElevation.Value;
      }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR",
          $"Civil 3D refused moving the PVI at {station:0.###}: {ex.GetType().Name}: {ex.Message}. Nothing was changed.");
      }

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["before"] = before,
        ["after"] = ProfileCommands.ReadPvi(pvi),
        ["entities"] = ProfileCommands.ReadProfileEntities(profile),
        ["success"] = true,
      };
    });
  }

  // ─── profileDeletePvi ─────────────────────────────────────────────────────

  public static Task<object?> DeletePviAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var station = PluginRuntime.GetRequiredDouble(parameters, "station");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForWrite);

      var targetPvi = FindPviNearStation(profile.PVIs, station, PviMatchTolerance)
        ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No PVI within {PviMatchTolerance} m of station {station:0.###} in profile '{profileName}'. PVIs: {PviStations(profile)}.");
      var removed = ProfileCommands.ReadPvi(targetPvi);
      profile.PVIs.RemoveAt(targetPvi.RawStation, targetPvi.Elevation);

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["station"] = station,
        ["removed"] = removed,
        ["pviCount"] = profile.PVIs.Count,
        ["entities"] = ProfileCommands.ReadProfileEntities(profile),
        ["success"] = true,
      };
    });
  }

  // ─── profileAddCurve ──────────────────────────────────────────────────────

  public static Task<object?> AddCurveAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var pviStation = PluginRuntime.GetRequiredDouble(parameters, "pviStation");
    var length = PluginRuntime.GetOptionalDouble(parameters, "length");
    var length1 = PluginRuntime.GetOptionalDouble(parameters, "length1");
    var length2 = PluginRuntime.GetOptionalDouble(parameters, "length2");
    var k = PluginRuntime.GetOptionalDouble(parameters, "k");
    var radius = PluginRuntime.GetOptionalDouble(parameters, "radius");
    var curveType = (PluginRuntime.GetOptionalString(parameters, "curveType") ?? "symmetric_parabola").ToLowerInvariant();

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForWrite);
      var targetPvi = FindPviNearStation(profile.PVIs, pviStation, PviMatchTolerance)
        ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No PVI within {PviMatchTolerance} m of station {pviStation:0.###} in profile '{profileName}'. PVIs: {PviStations(profile)}.");

      if (targetPvi.VerticalCurve != null)
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT",
          $"The PVI at {targetPvi.RawStation:0.###} already carries a vertical curve; delete the PVI and add it again with a curve, or move it. Nothing was changed.");

      string method;
      try
      {
        switch (curveType)
        {
          case "symmetric_parabola":
            if (k.HasValue) { profile.Entities.AddFreeSymmetricParabolaByPVIAndK(targetPvi, k.Value); method = "AddFreeSymmetricParabolaByPVIAndK"; }
            else if (length.HasValue) { profile.Entities.AddFreeSymmetricParabolaByPVIAndCurveLength(targetPvi, length.Value); method = "AddFreeSymmetricParabolaByPVIAndCurveLength"; }
            else throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "symmetric_parabola needs length or k.");
            break;
          case "asymmetric_parabola":
            if (!(length1 > 0) || !(length2 > 0))
              throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "asymmetric_parabola needs positive length1 and length2 (tangent lengths before/after the PVI).");
            profile.Entities.AddFreeAsymmetricParabolaByPVIAndLengths(targetPvi, length1.Value, length2.Value);
            method = "AddFreeAsymmetricParabolaByPVIAndLengths";
            break;
          case "circular":
            if (radius.HasValue) { profile.Entities.AddFreeCircularCurveByPVIAndRadius(targetPvi, radius.Value); method = "AddFreeCircularCurveByPVIAndRadius"; }
            else if (length.HasValue) { profile.Entities.AddFreeCircularCurveByPVIAndLength(targetPvi, length.Value); method = "AddFreeCircularCurveByPVIAndLength"; }
            else throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "circular needs radius or length.");
            break;
          default:
            throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Unknown curveType '{curveType}'. Use symmetric_parabola, asymmetric_parabola or circular.");
        }
      }
      catch (JsonRpcDispatchException) { throw; }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR",
          $"Civil 3D refused the {curveType} at PVI {targetPvi.RawStation:0.###} on '{profile.Name}': {ex.GetType().Name}: {ex.Message}. Nothing was changed.");
      }

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["pviStation"] = pviStation,
        ["curveType"] = curveType,
        ["method"] = method,
        ["pvi"] = ProfileCommands.ReadPvi(targetPvi),
        ["entities"] = ProfileCommands.ReadProfileEntities(profile),
        ["success"] = true,
      };
    });
  }

  // ─── profileSetGrade ──────────────────────────────────────────────────────

  /// <summary>
  /// ProfileTangent.Grade is read-only, but ProfilePVI.GradeIn / GradeOut are writable: Civil 3D moves the
  /// neighbouring PVI's elevation to produce the grade. Address the PVI by station.
  /// </summary>
  public static Task<object?> SetGradeAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var pviStation = PluginRuntime.GetOptionalDouble(parameters, "pviStation") ?? PluginRuntime.GetOptionalDouble(parameters, "station");
    var gradeIn = PluginRuntime.GetOptionalDouble(parameters, "gradeIn");
    var gradeOut = PluginRuntime.GetOptionalDouble(parameters, "gradeOut") ?? PluginRuntime.GetOptionalDouble(parameters, "grade");

    if (!pviStation.HasValue)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "set_grade needs pviStation (the PVI whose grade in/out changes).");
    if (!gradeIn.HasValue && !gradeOut.HasValue)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "set_grade needs gradeIn and/or gradeOut (decimal, e.g. -0.005 for -0.5%).");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForWrite);
      var pvi = FindPviNearStation(profile.PVIs, pviStation.Value, PviMatchTolerance)
        ?? throw new JsonRpcDispatchException("CIVIL3D.OBJECT_NOT_FOUND", $"No PVI within {PviMatchTolerance} m of station {pviStation.Value:0.###} in profile '{profileName}'. PVIs: {PviStations(profile)}.");
      var before = ProfileCommands.ReadPvi(pvi);

      try
      {
        if (gradeIn.HasValue) pvi.GradeIn = gradeIn.Value;
        if (gradeOut.HasValue) pvi.GradeOut = gradeOut.Value;
      }
      catch (Exception ex)
      {
        throw new JsonRpcDispatchException("CIVIL3D.API_ERROR",
          $"Civil 3D refused the grade change at PVI {pvi.RawStation:0.###}: {ex.GetType().Name}: {ex.Message}. Nothing was changed.");
      }

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["before"] = before,
        ["after"] = ProfileCommands.ReadPvi(pvi),
        ["pvis"] = ProfileCommands.ReadPvis(profile),
        ["entities"] = ProfileCommands.ReadProfileEntities(profile),
        ["success"] = true,
      };
    });
  }

  // ─── profileGetElevation ──────────────────────────────────────────────────

  /// <summary>
  /// Delegates to the same underlying implementation as
  /// ProfileCommands.GetProfileElevationAsync but is exposed as a
  /// dedicated tool per JFS-10 requirements.
  /// </summary>
  public static Task<object?> GetElevationAsync(JsonObject? parameters)
  {
    return ProfileCommands.GetProfileElevationAsync(parameters);
  }

  // ─── profileCheckKValues ──────────────────────────────────────────────────

  public static Task<object?> CheckKValuesAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var designSpeed = PluginRuntime.GetRequiredDouble(parameters, "designSpeed");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForRead);

      var entities = CivilObjectUtils.GetPropertyValue<object>(profile, "Entities");
      if (entities == null)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.TRANSACTION_FAILED",
          $"Profile '{profileName}' does not expose an Entities collection.");
      }

      // AASHTO minimum K values table (metric km/h → K_sag, K_crest)
      // Source: AASHTO Green Book 2011 Table 3-36 / 3-37
      var kTable = BuildAashtoKTable();
      var (kSagMin, kCrestMin) = LookupKValues(kTable, designSpeed);

      var results = new List<Dictionary<string, object?>>();
      var index = 0;
      foreach (var entity in (System.Collections.IEnumerable)entities)
      {
        var entityType = entity?.GetType().Name ?? string.Empty;
        var isCurve = entityType.ToLowerInvariant().Contains("parabola")
          || entityType.ToLowerInvariant().Contains("curve");
        if (!isCurve)
        {
          index++;
          continue;
        }

        var curveLength = CivilObjectUtils.GetPropertyValue<double?>(entity, "Length") ?? 0;
        var gradeIn = CivilObjectUtils.GetPropertyValue<double?>(entity, "GradeIn")
          ?? CivilObjectUtils.GetPropertyValue<double?>(entity, "StartGrade") ?? 0;
        var gradeOut = CivilObjectUtils.GetPropertyValue<double?>(entity, "GradeOut")
          ?? CivilObjectUtils.GetPropertyValue<double?>(entity, "EndGrade") ?? 0;
        var algebraicDiff = Math.Abs(gradeOut - gradeIn);
        var kValue = algebraicDiff > 1e-10 ? curveLength / algebraicDiff : double.PositiveInfinity;

        var isSag = gradeOut > gradeIn;
        var requiredK = isSag ? kSagMin : kCrestMin;
        var passes = kValue >= requiredK || double.IsPositiveInfinity(kValue);

        results.Add(new Dictionary<string, object?>
        {
          ["entityIndex"] = index,
          ["curveType"] = isSag ? "sag" : "crest",
          ["curveLength"] = curveLength,
          ["gradeIn"] = gradeIn,
          ["gradeOut"] = gradeOut,
          ["algebraicDifference"] = algebraicDiff,
          ["kValue"] = double.IsPositiveInfinity(kValue) ? null : (object?)kValue,
          ["requiredK"] = requiredK,
          ["passes"] = passes,
        });
        index++;
      }

      var allPass = results.All(r => (bool)(r["passes"] ?? false));
      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["designSpeed"] = designSpeed,
        ["kSagMinimum"] = kSagMin,
        ["kCrestMinimum"] = kCrestMin,
        ["curves"] = results,
        ["allPass"] = allPass,
        ["summary"] = allPass
          ? $"All {results.Count} vertical curve(s) meet minimum K values for {designSpeed} design speed."
          : $"{results.Count(r => !(bool)(r["passes"] ?? false))} of {results.Count} curve(s) fail minimum K value requirements.",
      };
    });
  }

  // ─── profileViewCreate ────────────────────────────────────────────────────

  public static Task<object?> ProfileViewCreateAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileViewName = PluginRuntime.GetRequiredString(parameters, "profileViewName");
    var insertX = PluginRuntime.GetRequiredDouble(parameters, "insertX");
    var insertY = PluginRuntime.GetRequiredDouble(parameters, "insertY");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var insertionPoint = new Point3d(insertX, insertY, 0);

      var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(
        transaction, database.BlockTableId, OpenMode.ForRead);
      var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(
        transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

      var styleId = LookupUtils.GetProfileViewStyleId(
        civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "style"));
      var bandSetId = LookupUtils.GetProfileViewBandSetId(
        civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "bandSet"));

      // Civil 3D 2026: ProfileView.Create(alignmentId, insertPosition[, name, bandSetId, styleId]).
      // Band label groups (the text inside profile-data / geometry bands) are only created by Civil 3D
      // when the band set is part of the creation call: ProfileView.Bands.ImportBandSetStyle on an
      // existing view draws the band boxes and ticks but never their labels. So when a band set is
      // given, create with the full overload; otherwise the two-argument overload (drawing default
      // band set) and apply name/style afterwards.
      var explicitStyle = !styleId.IsNull && !string.IsNullOrWhiteSpace(PluginRuntime.GetOptionalString(parameters, "style"));
      ObjectId pvId;
      if (!bandSetId.IsNull)
      {
        // styleId falls back to the first profile view style when no style was named
        pvId = ProfileView.Create(alignment.ObjectId, insertionPoint, profileViewName, bandSetId, styleId);
      }
      else
      {
        pvId = ProfileView.Create(alignment.ObjectId, insertionPoint);
      }
      if (pvId.IsNull)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.TRANSACTION_FAILED",
          $"Civil 3D did not create a profile view for alignment '{alignment.Name}'.");
      }

      var createdView = CivilObjectUtils.GetRequiredObject<ProfileView>(transaction, pvId, OpenMode.ForWrite);
      if (!string.Equals(createdView.Name, profileViewName, StringComparison.Ordinal))
      {
        createdView.Name = profileViewName;
      }
      if (explicitStyle && createdView.StyleId != styleId)
      {
        createdView.StyleId = styleId;
      }

      var profileView = createdView;

      return new Dictionary<string, object?>
      {
        ["profileViewName"] = profileView.Name,
        ["handle"] = CivilObjectUtils.GetHandle(profileView),
        ["alignmentName"] = alignment.Name,
        ["insertX"] = insertX,
        ["insertY"] = insertY,
        ["style"] = AlignmentGeometryReader.ReadStyleName(profileView, profileView.StyleId, transaction).Name,
        ["success"] = true,
      };
    });
  }

  // ─── profileViewBandSet ───────────────────────────────────────────────────

  public static Task<object?> ProfileViewBandSetAsync(JsonObject? parameters)
  {
    var profileViewName = PluginRuntime.GetRequiredString(parameters, "profileViewName");
    var bandSetName = PluginRuntime.GetRequiredString(parameters, "bandSetName");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var profileView = FindProfileViewByName(civilDoc, transaction, profileViewName);
      var writeView = CivilObjectUtils.GetRequiredObject<ProfileView>(
        transaction, profileView.ObjectId, OpenMode.ForWrite);

      var bandSetId = LookupUtils.GetProfileViewBandSetId(civilDoc, transaction, bandSetName);
      var assigned = new List<Dictionary<string, object?>>();
      var warnings = new List<string>();

      // ImportBandSetStyle only draws band boxes/ticks on an existing view: Civil 3D creates the band label
      // groups (the text in the bands) at ProfileView.Create time. rebuild:true instead builds the view's own
      // ProfileViewBandItemCollection (Add(bandType, styleName) per set item, item properties copied from the set),
      // which is the Profile View Properties dialog path and does create label groups.
      var rebuild = parameters?["rebuild"]?.GetValue<bool>() ?? false;
      if (rebuild)
      {
        RebuildBandsFromSet(writeView, bandSetId, parameters?["bands"] as JsonArray, transaction, warnings);
      }
      else
      {
        writeView.Bands.ImportBandSetStyle(bandSetId);
      }

      // Optional data sources: bands: [{ band: "<band style name>", profile1: "<profile>", profile2?: "<profile>" }]
      // Profile-data bands show nothing until Profile1Id is set; match by band style name (first unassigned match wins).
      if (parameters?["bands"] is JsonArray bandSpecs && bandSpecs.Count > 0)
      {
        var alignment = CivilObjectUtils.GetRequiredObject<Alignment>(transaction, writeView.AlignmentId, OpenMode.ForRead);
        ObjectId ProfileId(string? name)
        {
          if (string.IsNullOrWhiteSpace(name)) return ObjectId.Null;
          return CivilObjectUtils.FindProfileByName(alignment, transaction, name, OpenMode.ForRead).ObjectId;
        }
        foreach (var (getter, setter, location) in new[] { ("GetBottomBandItems", "SetBottomBandItems", "bottom"), ("GetTopBandItems", "SetTopBandItems", "top") })
        {
          var items = Civil3DCompatibility.InvokeMatchingOverload(writeView.Bands, null, getter, Array.Empty<object?>(), out var err);
          if (err != null || items is not System.Collections.IEnumerable enumerable) { if (err != null) warnings.Add($"{getter}: {err}"); continue; }
          var used = new HashSet<int>();
          var list = enumerable.Cast<object>().ToList();
          var changed = false;
          // Names of the band styles in this side's items (read once; the getter is redeclared on the derived item type).
          var itemNames = new List<string?>();
          foreach (var item in list)
          {
            string? styleName = null;
            try
            {
              var styleId = BandStyleCommands.ReadDerived(item, "BandStyleId") is ObjectId sid ? sid : ObjectId.Null;
              if (!styleId.IsNull) styleName = CivilObjectUtils.GetName(transaction.GetObject(styleId, OpenMode.ForRead));
            }
            catch (Exception ex) { warnings.Add($"{location} item {itemNames.Count}: BandStyleId: {Civil3DCompatibility.DescribeException(ex)}"); }
            itemNames.Add(styleName);
          }
          foreach (var node in bandSpecs)
          {
            if (node is not JsonObject spec) continue;
            var bandName = spec["band"]?.GetValue<string>();
            var wantedIndex = spec["index"]?.GetValue<int>();
            var wantedLocation = spec["location"]?.GetValue<string>() ?? "bottom";
            if (string.IsNullOrWhiteSpace(bandName) && wantedIndex == null) continue;
            if (wantedIndex != null && !string.Equals(wantedLocation, location, StringComparison.OrdinalIgnoreCase)) continue;
            for (var i = 0; i < list.Count; i++)
            {
              if (used.Contains(i)) continue;
              var item = list[i];
              var styleName = itemNames[i];
              // match by index when given (band names cannot always be read back), otherwise by band style name
              if (wantedIndex != null ? wantedIndex.Value != i : !string.Equals(styleName, bandName, StringComparison.OrdinalIgnoreCase)) continue;
              used.Add(i);
              var entry = new Dictionary<string, object?> { ["location"] = location, ["index"] = i, ["band"] = styleName ?? bandName };
              foreach (var (key, prop) in new[] { ("profile1", "Profile1Id"), ("profile2", "Profile2Id") })
              {
                var profileName = spec[key]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(profileName)) continue;
                var pid = ProfileId(profileName);
                if (Civil3DCompatibility.TrySetPropertyValue(item, prop, pid, out var setErr)) { entry[key] = profileName; changed = true; }
                else warnings.Add($"{bandName}.{key}: {setErr}");
              }
              assigned.Add(entry);
              break;
            }
          }
          if (changed)
          {
            Civil3DCompatibility.InvokeMatchingOverload(writeView.Bands, null, setter, new object?[] { items }, out var setErr2);
            if (setErr2 != null) warnings.Add($"{setter}: {setErr2}");
          }
        }
        foreach (var node in bandSpecs)
          if (node is JsonObject spec && spec["index"] == null && spec["band"]?.GetValue<string>() is string bn && !assigned.Any(a => string.Equals(a["band"]?.ToString(), bn, StringComparison.OrdinalIgnoreCase)))
            warnings.Add($"band '{bn}' is not in the imported band set (or its name could not be read — use index instead)");
      }

      var labelGroups = CountBandLabelGroups(writeView.ObjectId, warnings);
      if (labelGroups == 0)
      {
        warnings.Add("no band label groups exist on this view: band text will not render. Civil 3D only creates them when the band set is part of ProfileView.Create (civil3d_profile view_create … bandSet) or through the view's own band item collection (view_band_set … rebuild:true).");
      }

      return new Dictionary<string, object?>
      {
        ["profileViewName"] = profileView.Name,
        ["bandSetName"] = bandSetName,
        ["rebuild"] = rebuild,
        ["bands"] = assigned,
        ["bandLabelGroups"] = labelGroups,
        ["warnings"] = warnings,
        ["success"] = true,
      };
    });
  }

  /// <summary>Number of band label groups (profile data + horizontal + vertical geometry) attached to a profile view.</summary>
  internal static int CountBandLabelGroups(ObjectId profileViewId, List<string> warnings)
  {
    // The three Get* calls overlap (the base ProfileBandLabelGroup query returns every band group), so dedupe.
    var unique = new HashSet<ObjectId>();
    foreach (var groupType in new[] { typeof(ProfileDataBandLabelGroup), typeof(HorizontalGeometryBandLabelGroup), typeof(VerticalGeometryBandLabelGroup) })
    {
      var ids = Civil3DCompatibility.InvokeMatchingOverload(null, groupType, "GetAvailableLabelGroupIds", new object?[] { profileViewId }, out var err);
      if (err != null) { warnings.Add($"{groupType.Name}.GetAvailableLabelGroupIds: {err}"); continue; }
      if (ids is System.Collections.IEnumerable e)
        foreach (var id in e.Cast<object>())
          if (id is ObjectId oid && !oid.IsNull && !oid.IsErased) unique.Add(oid);
    }
    return unique.Count;
  }

  /// <summary>
  /// Replace the view's band items with a fresh ProfileViewBandItemCollection built from a band set style:
  /// one Add(bandType, bandStyleName) per set item (names from bands[].band by index when the set item's
  /// BandStyleId cannot be read back), then Gap / intervals / ShowLabels / Weeding / stagger / start-end
  /// label flags copied from the set item.
  /// </summary>
  private static void RebuildBandsFromSet(ProfileView view, ObjectId bandSetId, JsonArray? bandSpecs, Transaction transaction, List<string> warnings)
  {
    var set = transaction.GetObject(bandSetId, OpenMode.ForRead);
    var copied = new[] { "Gap", "MajorInterval", "MinorInterval", "ShowLabels", "Weeding", "StaggerLabel", "StaggerLineHeight", "LabelAtStartStation", "LabelAtEndStation" };
    foreach (var (getter, setter, location, loc) in new[]
    {
      ("GetBottomBandSetItems", "SetBottomBandItems", "bottom", BandLocationType.Bottom),
      ("GetTopBandSetItems", "SetTopBandItems", "top", BandLocationType.Top),
    })
    {
      var setItems = Civil3DCompatibility.InvokeMatchingOverload(set, null, getter, Array.Empty<object?>(), out var err);
      if (err != null) { warnings.Add($"{getter}: {err}"); continue; }
      var list = setItems is System.Collections.IEnumerable e ? e.Cast<object>().ToList() : new List<object>();
      if (list.Count == 0) continue;

      var collection = new ProfileViewBandItemCollection(view.ObjectId, loc);
      for (var i = 0; i < list.Count; i++)
      {
        var setItem = list[i];
        var bandType = BandStyleCommands.ReadDerived(setItem, "BandType") is BandType bt ? bt : BandType.ProfileData;
        string? styleName = null;
        try
        {
          if (BandStyleCommands.ReadDerived(setItem, "BandStyleId") is ObjectId sid && !sid.IsNull)
            styleName = CivilObjectUtils.GetName(transaction.GetObject(sid, OpenMode.ForRead));
        }
        catch { /* fall through to the request */ }
        if (string.IsNullOrWhiteSpace(styleName) && bandSpecs != null)
        {
          foreach (var node in bandSpecs)
          {
            if (node is not JsonObject spec) continue;
            var idx = spec["index"]?.GetValue<int>();
            var l = spec["location"]?.GetValue<string>() ?? "bottom";
            if (idx == i && string.Equals(l, location, StringComparison.OrdinalIgnoreCase)) { styleName = spec["band"]?.GetValue<string>(); break; }
          }
        }
        if (string.IsNullOrWhiteSpace(styleName))
        {
          warnings.Add($"{location} item {i}: band style name unknown (BandStyleId unreadable) — pass bands:[{{index:{i}, band:'<style name>'}}]; item skipped");
          continue;
        }
        try
        {
          collection.Add(bandType, styleName);
        }
        catch (Exception ex)
        {
          warnings.Add($"{location} item {i} '{styleName}': Add failed: {Civil3DCompatibility.DescribeException(ex)}");
          continue;
        }
        var added = collection[collection.Count - 1];
        foreach (var prop in copied)
        {
          var value = BandStyleCommands.ReadDerived(setItem, prop);
          if (value == null) continue;
          // Stagger / interval properties throw InvalidOperationException on items where they do not apply
          // (geometry bands, stagger None) — not worth a warning.
          if (!Civil3DCompatibility.TrySetPropertyValue(added, prop, value, out var setErr) && setErr != null
              && !setErr.Contains("not found", StringComparison.OrdinalIgnoreCase)
              && !setErr.Contains("InvalidOperationException", StringComparison.OrdinalIgnoreCase))
            warnings.Add($"{location} item {i} '{styleName}'.{prop}: {setErr}");
        }
      }
      if (collection.Count == 0) continue;
      Civil3DCompatibility.InvokeMatchingOverload(view.Bands, null, setter, new object?[] { collection }, out var setErr2);
      if (setErr2 != null) warnings.Add($"{setter}: {setErr2}");
    }
  }

  // ─── Private helpers ─────────────────────────────────────────────────────

  private static ProfileView FindProfileViewByName(
    Autodesk.Civil.ApplicationServices.CivilDocument civilDoc,
    Transaction transaction,
    string name)
  {
    // Profile views live in model space; enumerate all ProfileView objects
    var database = CivilObjectUtils.GetDatabase(civilDoc);
    var blockTable = CivilObjectUtils.GetRequiredObject<BlockTable>(
      transaction, database.BlockTableId, OpenMode.ForRead);
    var modelSpace = CivilObjectUtils.GetRequiredObject<BlockTableRecord>(
      transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);

    foreach (ObjectId objectId in modelSpace)
    {
      var obj = transaction.GetObject(objectId, OpenMode.ForRead);
      if (obj is ProfileView pv
        && string.Equals(pv.Name, name, StringComparison.OrdinalIgnoreCase))
      {
        return pv;
      }
    }

    throw new JsonRpcDispatchException(
      "CIVIL3D.OBJECT_NOT_FOUND",
      $"Profile view '{name}' was not found in model space.");
  }

  private const double PviMatchTolerance = 0.5;

  private static string PviStations(Profile profile)
  {
    var list = new List<string>();
    foreach (ProfilePVI pvi in profile.PVIs) list.Add(pvi.RawStation.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
    return string.Join(", ", list);
  }

  private static ProfilePVI? FindPviNearStation(ProfilePVICollection pvis, double targetStation, double tolerance)
  {
    var pvi = FindPviNearStation(pvis, targetStation);
    return pvi != null && Math.Abs(pvi.RawStation - targetStation) <= tolerance ? pvi : null;
  }

  private static ProfilePVI? FindPviNearStation(ProfilePVICollection pvis, double targetStation)
  {
    ProfilePVI? closest = null;
    var minDist = double.MaxValue;

    foreach (ProfilePVI pvi in pvis)
    {
      var dist = Math.Abs(pvi.RawStation - targetStation);
      if (dist < minDist)
      {
        minDist = dist;
        closest = pvi;
      }
    }

    return closest;
  }

  /// <summary>
  /// AASHTO minimum K values (metric, km/h).
  /// Returns (K_sag_min, K_crest_min).
  /// Source: AASHTO A Policy on Geometric Design of Highways and Streets, 2011.
  /// </summary>
  private static List<(double speed, double kSag, double kCrest)> BuildAashtoKTable() =>
  [
    (30, 3, 1),
    (40, 7, 2),
    (50, 9, 4),
    (60, 11, 6),
    (70, 14, 10),
    (80, 19, 17),
    (90, 24, 29),
    (100, 30, 44),
    (110, 37, 60),
    (120, 46, 84),
    (130, 57, 114),
  ];

  private static (double kSag, double kCrest) LookupKValues(
    List<(double speed, double kSag, double kCrest)> table,
    double designSpeed)
  {
    // Find exact match first
    var exact = table.FirstOrDefault(t => Math.Abs(t.speed - designSpeed) < 0.5);
    if (exact != default)
    {
      return (exact.kSag, exact.kCrest);
    }

    // Interpolate between nearest values
    var lower = table.LastOrDefault(t => t.speed <= designSpeed);
    var upper = table.FirstOrDefault(t => t.speed > designSpeed);

    if (lower == default)
    {
      return (table[0].kSag, table[0].kCrest);
    }

    if (upper == default)
    {
      return (table[^1].kSag, table[^1].kCrest);
    }

    var ratio = (designSpeed - lower.speed) / (upper.speed - lower.speed);
    return (
      lower.kSag + ratio * (upper.kSag - lower.kSag),
      lower.kCrest + ratio * (upper.kCrest - lower.kCrest));
  }
}
