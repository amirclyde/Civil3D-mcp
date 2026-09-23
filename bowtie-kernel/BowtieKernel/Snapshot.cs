using System.Text.Json;
using System.Text.Json.Serialization;

namespace Utnm.BowtieKernel;

/// <summary>
/// The evidence for one bend, as the plugin writes it and the kernel reads it (schema 1). Everything is plain numbers so a
/// snapshot can be saved, diffed, and solved again offline. Sections must be UNCLIPPED; 'clipped' marks ones that are not.
/// </summary>
public sealed class Snapshot
{
  public int Schema { get; set; } = 1;
  public string? Corridor { get; set; }
  public string? Baseline { get; set; }
  public double BendFrom { get; set; }
  public double BendTo { get; set; }
  public BaselineDto Samples { get; set; } = new();
  public List<SectionDto> Sections { get; set; } = new();
  public GroundGridDto? Ground { get; set; }

  public sealed class BaselineDto
  {
    public double[] S { get; set; } = Array.Empty<double>();
    public double[] X { get; set; } = Array.Empty<double>();
    public double[] Y { get; set; } = Array.Empty<double>();
    /// <summary>Tangent direction, radians, counter-clockwise from +X. An angle point is two samples at one station.</summary>
    public double[] Dir { get; set; } = Array.Empty<double>();
  }
  public sealed class SectionDto
  {
    public double Station { get; set; }
    public double Z0 { get; set; }
    public bool Clipped { get; set; }
    /// <summary>[offset towards the inside, elevation relative to Z0], inner to outer, ending at the natural daylight.</summary>
    public List<double[]> Template { get; set; } = new();
  }
  public sealed class GroundGridDto
  {
    public double X0 { get; set; }
    public double Y0 { get; set; }
    public double Cell { get; set; }
    public int Nx { get; set; }
    public int Ny { get; set; }
    /// <summary>Row-major, Ny rows of Nx; null where the surface has no elevation.</summary>
    public double?[] Z { get; set; } = Array.Empty<double?>();
  }

  private static readonly JsonSerializerOptions Json = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
  };
  public static Snapshot Load(string path) => JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Empty snapshot.");
  public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
  public static string ToJson(object value) => JsonSerializer.Serialize(value, Json);

  public SampledBaseline ToBaseline() => new(Samples.S, Samples.X, Samples.Y, Samples.Dir);
  public SectionSet ToSections(double extension = 5.0) => new(Sections.Select(s => new SectionSample
  {
    Station = s.Station, Z0 = s.Z0, Clipped = s.Clipped, Template = s.Template.Select(p => (p[0], p[1])).ToList(),
  }), extension);

  /// <summary>Bilinear ground from the grid; null outside it or next to a hole.</summary>
  public Func<double, double, double?>? ToGround()
  {
    var g = Ground;
    if (g == null || g.Nx < 2 || g.Ny < 2) return null;
    return (x, y) =>
    {
      var fx = (x - g.X0) / g.Cell; var fy = (y - g.Y0) / g.Cell;
      var i = (int)Math.Floor(fx); var j = (int)Math.Floor(fy);
      if (i < 0 || j < 0 || i >= g.Nx - 1 || j >= g.Ny - 1) return null;
      double? At(int a, int b) => g.Z[b * g.Nx + a];
      var (z00, z10, z01, z11) = (At(i, j), At(i + 1, j), At(i, j + 1), At(i + 1, j + 1));
      if (!z00.HasValue || !z10.HasValue || !z01.HasValue || !z11.HasValue) return null;
      var u = fx - i; var v = fy - j;
      return z00.Value * (1 - u) * (1 - v) + z10.Value * u * (1 - v) + z01.Value * (1 - u) * v + z11.Value * u * v;
    };
  }
}

/// <summary>The part of a result worth keeping on file: status, the two clip lines, the meet stations and the per-station table.</summary>
public static class ResultReport
{
  public static object Of(SeamResult r) => new
  {
    status = r.Status.ToString(), reasons = r.Reasons, warnings = r.Warnings,
    side = r.Side.ToString().ToLowerInvariant(), bendType = r.BendType, turnDegrees = Math.Round(r.TurnDegrees, 3),
    apex = new { station = Math.Round(r.ApexStation, 3), x = Math.Round(r.Apex.X, 4), y = Math.Round(r.Apex.Y, 4), z = r.ApexZ.HasValue ? Math.Round(r.ApexZ.Value, 4) : (double?)null, radius = r.Radius.HasValue ? Math.Round(r.Radius.Value, 3) : (double?)null },
    seam = r.Seam.Select(q => new { x = Math.Round(q.X, 4), y = Math.Round(q.Y, 4), z = double.IsNaN(q.Z) ? (double?)null : Math.Round(q.Z, 4), kind = q.Kind }),
    cap = r.Cap.Select(q => new[] { Math.Round(q.X, 4), Math.Round(q.Y, 4) }),
    ground = r.Hit.HasValue ? new { x = Math.Round(r.Hit.Value.X, 4), y = Math.Round(r.Hit.Value.Y, 4), z = Math.Round(r.Hit.Value.Z, 4) } : null,
    meetStations = new[] { r.MeetA, r.MeetB }, meetOffsets = new[] { r.MeetOffsetA, r.MeetOffsetB },
    seamEnd = r.SeamEnd, cutFillChanges = r.CutFillChanges.Select(x => Math.Round(x, 3)),
    clipFrom = r.ClipFrom, clipTo = r.ClipTo, capFrom = r.CapFrom, capTo = r.CapTo,
    checks = new
    {
      linkCrossingsBefore = r.LinkCrossingsBefore, linkCrossingsAfter = r.LinkCrossingsAfter,
      maxClosure = Math.Round(r.MaxClosure, 4), apexMismatch = Math.Round(r.ApexMismatch, 4), apexSpread = Math.Round(r.ApexSpread, 4), levelStep = Math.Round(r.LevelStep, 4), maxLevelAdjust = Math.Round(r.MaxLevelAdjust, 4), endRunOut = Math.Round(r.EndRunOut, 4),
      maxLean = Math.Round(r.MaxLean, 4), everySectionStopsAtItsFirstCrossing = r.Stations.All(x => x.FirstCrossingIsClip),
    },
    stations = r.Stations.Select(x => new
    {
      station = Math.Round(x.Station, 4), role = x.Role, reach = Math.Round(x.Reach, 3),
      clipOffset = x.ClipOffset.HasValue ? Math.Round(x.ClipOffset.Value, 3) : (double?)null,
      clipZ = x.ClipZ.HasValue ? Math.Round(x.ClipZ.Value, 3) : (double?)null,
      closure = x.Closure.HasValue ? Math.Round(x.Closure.Value, 4) : (double?)null,
      targetZ = x.TargetZ.HasValue ? Math.Round(x.TargetZ.Value, 3) : (double?)null,
      levelAdjust = x.LevelAdjust.HasValue ? Math.Round(x.LevelAdjust.Value, 3) : (double?)null,
      ownSlope = x.OwnSlope.HasValue ? Math.Round(x.OwnSlope.Value, 4) : (double?)null,
      adjustedSlope = x.AdjustedSlope.HasValue ? Math.Round(x.AdjustedSlope.Value, 4) : (double?)null,
      crossings = x.Crossings.Select(c => Math.Round(c, 3)),
    }),
  };
}
