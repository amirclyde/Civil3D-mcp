using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DMcpPlugin;

public static class ProfileCommands
{
  public static Task<object?> ListProfilesAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profiles = alignment.GetProfileIds()
        .Cast<ObjectId>()
        .Select(id => CivilObjectUtils.GetRequiredObject<Profile>(transaction, id, OpenMode.ForRead))
        .Select(profile => ToProfileSummary(profile, transaction))
        .ToList();

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profiles"] = profiles,
      };
    });
  }

  public static Task<object?> GetProfileAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForRead);
      var entities = ReadProfileEntities(profile);

      return new Dictionary<string, object?>
      {
        ["name"] = profile.Name,
        ["handle"] = CivilObjectUtils.GetHandle(profile),
        ["type"] = MapProfileType(profile.ProfileType.ToString()),
        ["profileType"] = profile.ProfileType.ToString(),
        ["style"] = AlignmentGeometryReader.ReadStyleName(profile, profile.StyleId, transaction).Name ?? string.Empty,
        ["layer"] = profile.Layer,
        ["startStation"] = profile.StartingStation,
        ["endStation"] = profile.EndingStation,
        ["minElevation"] = GetElevationExtents(profile).Min,
        ["maxElevation"] = GetElevationExtents(profile).Max,
        ["entityCount"] = entities.Count,
        ["entities"] = entities,
        ["pviCount"] = CountPvis(profile),
        ["pvis"] = ReadPvis(profile),
        ["units"] = new Dictionary<string, object?>
        {
          ["horizontal"] = CivilObjectUtils.LinearUnits(database),
          ["vertical"] = CivilObjectUtils.LinearUnits(database),
        },
      };
    });
  }

  public static Task<object?> GetProfileElevationAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var station = PluginRuntime.GetRequiredDouble(parameters, "station");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForRead);
      var elevation = ReadProfileElevation(profile, station);
      var grade = ReadProfileGrade(profile, station);

      return new Dictionary<string, object?>
      {
        ["station"] = station,
        ["elevation"] = elevation,
        ["grade"] = grade,
        ["units"] = CivilObjectUtils.LinearUnits(database),
      };
    });
  }

  public static Task<object?> SampleProfileElevationsAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var interval = PluginRuntime.GetRequiredDouble(parameters, "interval");
    var startStation = PluginRuntime.GetOptionalDouble(parameters, "startStation");
    var endStation = PluginRuntime.GetOptionalDouble(parameters, "endStation");

    return CivilExecution.ReadAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForRead);
      var from = startStation ?? profile.StartingStation;
      var to = endStation ?? profile.EndingStation;
      var samples = new List<Dictionary<string, object?>>();

      for (var station = from; station <= to; station += interval)
      {
        samples.Add(new Dictionary<string, object?>
        {
          ["station"] = station,
          ["elevation"] = ReadProfileElevation(profile, station),
          ["grade"] = ReadProfileGrade(profile, station),
        });
      }

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["startStation"] = from,
        ["endStation"] = to,
        ["interval"] = interval,
        ["samples"] = samples,
        ["units"] = CivilObjectUtils.LinearUnits(database),
      };
    });
  }

  public static Task<object?> CreateProfileFromSurfaceAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");
    var surfaceName = PluginRuntime.GetRequiredString(parameters, "surfaceName");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var surface = CivilObjectUtils.FindSurfaceByName(civilDoc, transaction, surfaceName, OpenMode.ForRead);
      var layerId = LookupUtils.GetLayerId(database, transaction, PluginRuntime.GetOptionalString(parameters, "layer"));
      var styleId = LookupUtils.GetProfileStyleId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "style"));
      var labelSetId = LookupUtils.GetProfileLabelSetId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "labelSet"));
      var profileId = Profile.CreateFromSurface(profileName, alignment.ObjectId, surface.ObjectId, layerId, styleId, labelSetId);
      var profile = CivilObjectUtils.GetRequiredObject<Profile>(transaction, profileId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["created"] = true,
      };
    });
  }

  public static Task<object?> CreateLayoutProfileAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var layerId = LookupUtils.GetLayerId(database, transaction, PluginRuntime.GetOptionalString(parameters, "layer"));
      var styleId = LookupUtils.GetProfileStyleId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "style"));
      var labelSetId = LookupUtils.GetProfileLabelSetId(civilDoc, transaction, PluginRuntime.GetOptionalString(parameters, "labelSet"));
      var profileId = Profile.CreateByLayout(profileName, alignment.ObjectId, layerId, styleId, labelSetId);
      var profile = CivilObjectUtils.GetRequiredObject<Profile>(transaction, profileId, OpenMode.ForRead);

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profile.Name,
        ["created"] = true,
      };
    });
  }

  public static Task<object?> DeleteProfileAsync(JsonObject? parameters)
  {
    var alignmentName = PluginRuntime.GetRequiredString(parameters, "alignmentName");
    var profileName = PluginRuntime.GetRequiredString(parameters, "profileName");

    return CivilExecution.WriteAsync<object?>((doc, civilDoc, database, transaction) =>
    {
      var alignment = CivilObjectUtils.FindAlignmentByName(civilDoc, transaction, alignmentName);
      var profile = CivilObjectUtils.FindProfileByName(alignment, transaction, profileName, OpenMode.ForWrite);
      profile.Erase();

      return new Dictionary<string, object?>
      {
        ["alignmentName"] = alignment.Name,
        ["profileName"] = profileName,
        ["deleted"] = true,
      };
    });
  }

  private static Dictionary<string, object?> ToProfileSummary(Profile profile, Transaction transaction)
  {
    var extents = GetElevationExtents(profile);
    return new Dictionary<string, object?>
    {
      ["name"] = profile.Name,
      ["handle"] = CivilObjectUtils.GetHandle(profile),
      ["type"] = MapProfileType(profile.ProfileType.ToString()),
      ["profileType"] = profile.ProfileType.ToString(),
      ["style"] = AlignmentGeometryReader.ReadStyleName(profile, profile.StyleId, transaction).Name ?? string.Empty,
      ["startStation"] = profile.StartingStation,
      ["endStation"] = profile.EndingStation,
      ["minElevation"] = extents.Min,
      ["maxElevation"] = extents.Max,
    };
  }

  internal static List<Dictionary<string, object?>> ReadProfileEntities(Profile profile)
  {
    var entities = new List<Dictionary<string, object?>>();
    foreach (ProfileEntity entity in profile.Entities)
    {
      var item = new Dictionary<string, object?>
      {
        ["type"] = MapProfileEntityType(entity.EntityType.ToString()),
        ["entityType"] = entity.EntityType.ToString(),
        ["startStation"] = entity.StartStation,
        ["endStation"] = entity.EndStation,
        ["startElevation"] = entity.StartElevation,
        ["endElevation"] = entity.EndElevation,
        ["grade"] = entity is ProfileTangent tangent ? tangent.Grade : null,
        ["length"] = entity.Length,
      };

      switch (entity)
      {
        case ProfileParabolaSymmetric sym:
          item["pviStation"] = sym.PVIStation;
          item["pviElevation"] = sym.PVIElevation;
          item["gradeIn"] = sym.GradeIn;
          item["gradeOut"] = sym.GradeOut;
          item["gradeChange"] = sym.GradeChange;
          item["k"] = sym.K;
          item["curveType"] = sym.CurveType.ToString();
          item["highLowPointStation"] = Try(() => sym.HighLowPointStation);
          item["highLowPointElevation"] = Try(() => sym.HighLowPointElevation);
          item["tangentOffsetAtPvi"] = Try(() => sym.TangentOffsetAtPVI);
          break;
        case ProfileParabolaAsymmetric asym:
          item["pviStation"] = asym.PVIStation;
          item["pviElevation"] = asym.PVIElevation;
          item["gradeIn"] = asym.GradeIn;
          item["gradeOut"] = asym.GradeOut;
          item["gradeChange"] = asym.GradeChange;
          item["k"] = asym.K;
          item["curveType"] = asym.CurveType.ToString();
          item["length1"] = asym.AsymmetricLength1;
          item["length2"] = asym.AsymmetricLength2;
          item["highLowPointStation"] = Try(() => asym.HighLowPointStation);
          item["highLowPointElevation"] = Try(() => asym.HighLowPointElevation);
          break;
        case ProfileCircular circ:
          item["pviStation"] = circ.PVIStation;
          item["pviElevation"] = circ.PVIElevation;
          item["gradeIn"] = circ.GradeIn;
          item["gradeOut"] = circ.GradeOut;
          item["gradeChange"] = circ.GradeChange;
          item["k"] = circ.K;
          item["radius"] = circ.Radius;
          item["curveType"] = circ.CurveType.ToString();
          item["highLowPointStation"] = Try(() => circ.HighLowPointStation);
          item["highLowPointElevation"] = Try(() => circ.HighLowPointElevation);
          break;
      }
      entities.Add(item);
    }

    // Civil 3D returns entities in creation order; report them in station order.
    entities.Sort((x, y) => ((double)x["startStation"]!).CompareTo((double)y["startStation"]!));
    for (var i = 0; i < entities.Count; i++) entities[i]["index"] = i;
    return entities;
  }

  internal static List<Dictionary<string, object?>> ReadPvis(Profile profile)
  {
    var pvis = new List<Dictionary<string, object?>>();
    foreach (ProfilePVI pvi in profile.PVIs) pvis.Add(ReadPvi(pvi));
    pvis.Sort((x, y) => ((double)x["station"]!).CompareTo((double)y["station"]!));
    for (var i = 0; i < pvis.Count; i++) pvis[i]["index"] = i;
    return pvis;
  }

  internal static Dictionary<string, object?> ReadPvi(ProfilePVI pvi)
  {
    var item = new Dictionary<string, object?>
    {
      ["station"] = pvi.RawStation,
      ["elevation"] = pvi.Elevation,
      ["pviType"] = pvi.PVIType.ToString(),
      ["gradeIn"] = Try(() => pvi.GradeIn),
      ["gradeOut"] = Try(() => pvi.GradeOut),
    };
    var curve = pvi.VerticalCurve;
    if (curve != null)
    {
      item["curveEntityType"] = curve.EntityType.ToString();
      item["curveLength"] = curve.Length;
      item["curveStartStation"] = curve.StartStation;
      item["curveEndStation"] = curve.EndStation;
      switch (curve)
      {
        case ProfileParabolaSymmetric sym: item["k"] = sym.K; break;
        case ProfileParabolaAsymmetric asym: item["k"] = asym.K; item["length1"] = asym.AsymmetricLength1; item["length2"] = asym.AsymmetricLength2; break;
        case ProfileCircular circ: item["k"] = circ.K; item["radius"] = circ.Radius; break;
      }
    }
    return item;
  }

  private static double? Try(Func<double> read)
  {
    try { return read(); } catch { return null; }
  }

  private static (double Min, double Max) GetElevationExtents(Profile profile)
  {
    // Civil 3D calculates profile-wide extents, including interior vertical
    // curve extrema and the numeric fallback required for empty layouts.
    return (profile.ElevationMin, profile.ElevationMax);
  }

  private static int CountPvis(Profile profile)
  {
    return profile.PVIs.Count;
  }

  private static double ReadProfileElevation(Profile profile, double station)
  {
    try
    {
      return profile.ElevationAt(station);
    }
    catch (Exception exception)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        $"Could not read elevation on profile '{profile.Name}' at station {station}: {exception.Message}");
    }
  }

  private static double ReadProfileGrade(Profile profile, double station)
  {
    try
    {
      return profile.GradeAt(station);
    }
    catch (Exception exception)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_ERROR",
        $"Could not read grade on profile '{profile.Name}' at station {station}: {exception.Message}");
    }
  }

  private static string MapProfileType(string? value)
  {
    // Civil 3D reports existing-ground profiles sampled from a surface as "EG".
    var text = value?.ToLowerInvariant() ?? string.Empty;
    if (text == "eg" || text.Contains("surface") || text.Contains("existing"))
    {
      return "surface";
    }

    if (text.Contains("super"))
    {
      return "superimposed";
    }

    return "layout";
  }

  private static string MapProfileEntityType(string value)
  {
    var text = value.ToLowerInvariant();
    if (text.Contains("parabola"))
    {
      // "ParabolaSymmetric" contains the substring "asymmetric" ("parabol-a-symmetric"),
      // so strip "parabola" before testing for asymmetry.
      return text.Replace("parabola", string.Empty).Contains("asymmetric") ? "asymmetric_parabola" : "parabola";
    }

    if (text.Contains("circular") || text.Contains("curve"))
    {
      return "circular_curve";
    }

    return "tangent";
  }
}
