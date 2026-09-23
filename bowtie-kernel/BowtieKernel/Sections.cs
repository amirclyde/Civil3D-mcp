namespace Utnm.BowtieKernel;

/// <summary>
/// One UNCLIPPED inside section: the connected chain of inside links from the first inside point out to the natural
/// daylight, as (offset from the baseline towards the inside, elevation relative to the baseline level Z0).
/// A section that is currently clipped must not be used: its chain stops at the clip, not at the ground.
/// </summary>
public sealed class SectionSample
{
  public double Station;
  public double Z0;
  public List<(double Off, double Dz)> Template = new();
  public bool Clipped;

  public double StartOffset => Template[0].Off;
  public double Reach => Template[^1].Off;

  /// <summary>First offset where the chain turns steeper than 1:10 - the hinge of the daylight slope.</summary>
  public double? HingeOffset
  {
    get
    {
      for (var i = 1; i < Template.Count; i++)
      {
        var run = Template[i].Off - Template[i - 1].Off;
        if (run > 1e-9 && Math.Abs((Template[i].Dz - Template[i - 1].Dz) / run) > 0.1) return Template[i - 1].Off;
      }
      return null;
    }
  }

  /// <summary>Relative elevation at an offset. Inside the first point: that point's level. Past the daylight: the last
  /// slope carried on for at most 'extension' metres (the two sides only meet exactly at the ground, so the march has to
  /// be able to look a little past it).</summary>
  public double? Dz(double off, double extension)
  {
    if (Template.Count == 0) return null;
    if (off <= Template[0].Off) return Template[0].Dz;
    for (var i = 1; i < Template.Count; i++)
    {
      var (o0, z0) = Template[i - 1]; var (o1, z1) = Template[i];
      if (off <= o1) return o1 - o0 > 1e-9 ? z0 + (z1 - z0) * (off - o0) / (o1 - o0) : z1;
    }
    if (Template.Count < 2 || off > Reach + extension) return null;
    var (a0, b0) = Template[^2]; var (a1, b1) = Template[^1];
    var slope = a1 - a0 > 1e-9 ? (b1 - b0) / (a1 - a0) : 0.0;
    return b1 + slope * (off - a1);
  }
}

/// <summary>The sections of one bend, with the design surface they sweep: Z(station, offset), interpolated between stations.</summary>
public sealed class SectionSet
{
  public readonly List<SectionSample> Sections;
  public readonly double Extension;

  public SectionSet(IEnumerable<SectionSample> sections, double extension = 5.0)
  {
    Sections = sections.Where(s => s.Template.Count >= 2).OrderBy(s => s.Station).ToList();
    if (Sections.Count < 2) throw new ArgumentException("At least two sections with an inside link chain are needed.");
    Extension = extension;
  }

  public double First => Sections[0].Station;
  public double Last => Sections[^1].Station;
  public double MaxReach => Sections.Max(s => s.Reach);

  private (SectionSample A, SectionSample B, double W) Bracket(double s)
  {
    if (s <= First) return (Sections[0], Sections[0], 0);
    if (s >= Last) return (Sections[^1], Sections[^1], 0);
    var lo = 0; var hi = Sections.Count - 1;
    while (hi - lo > 1) { var mid = (lo + hi) / 2; if (Sections[mid].Station <= s) lo = mid; else hi = mid; }
    var a = Sections[lo]; var b = Sections[hi];
    return (a, b, b.Station - a.Station < 1e-12 ? 0 : (s - a.Station) / (b.Station - a.Station));
  }

  public double BaselineLevel(double s)
  {
    var (a, b, w) = Bracket(s);
    return a.Z0 + (b.Z0 - a.Z0) * w;
  }

  /// <summary>Relative elevation of the design at (station, offset).</summary>
  public double? Dz(double s, double off)
  {
    var (a, b, w) = Bracket(s);
    var da = a.Dz(off, Extension);
    if (w <= 0 || ReferenceEquals(a, b)) return da;
    var db = b.Dz(off, Extension);
    return da.HasValue && db.HasValue ? da.Value + (db.Value - da.Value) * w : da ?? db;
  }

  public double? Z(double s, double off)
  {
    var dz = Dz(s, off);
    return dz.HasValue ? BaselineLevel(s) + dz.Value : null;
  }

  public double? Slope(double s, double off)
  {
    const double e = 0.005;
    var a = Dz(s, Math.Max(0, off - e)); var b = Dz(s, off + e);
    return a.HasValue && b.HasValue ? (b.Value - a.Value) / (off + e - Math.Max(0, off - e)) : null;
  }

  public double Reach(double s)
  {
    var (a, b, w) = Bracket(s);
    return a.Reach + (b.Reach - a.Reach) * w;
  }

  public double Hinge(double s)
  {
    var (a, b, w) = Bracket(s);
    var ha = a.HingeOffset ?? a.StartOffset; var hb = b.HingeOffset ?? b.StartOffset;
    return ha + (hb - ha) * w;
  }

  /// <summary>Offset of the last template vertex before 'off' at this station: the start of the link that a clip at 'off' cuts.</summary>
  public double LastVertexBefore(double s, double off)
  {
    var (a, b, w) = Bracket(s);
    var src = w > 0.5 ? b : a;
    var best = src.StartOffset;
    foreach (var (o, _) in src.Template) if (o < off - 0.05 && o > best) best = o;
    return best;
  }

  public double StartOffset(double s)
  {
    var (a, b, w) = Bracket(s);
    return a.StartOffset + (b.StartOffset - a.StartOffset) * w;
  }
}
