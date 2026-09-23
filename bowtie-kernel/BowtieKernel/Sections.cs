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
  /// <summary>False when the chain does not end on the ground (a fixed-width section, a wall, a part that finishes with a
  /// bench and a drain whatever the ground does): there is then nothing to carry on past its last point. Set by the solver
  /// when it has a ground surface; true by default, which is the behaviour of a daylight link.</summary>
  public bool Extend = true;
  /// <summary>Level of the last point minus the real daylight surface under it, when the plugin measured it on the surface
  /// itself. Preferred to the ground grid for deciding whether the section ends on the ground.</summary>
  public double? EndGap;
  /// <summary>The section's own hinge (the point its subassembly codes Hinge), when the plugin found one. Otherwise the
  /// hinge is taken as the first link steeper than 1:10, which on a drain section can be a drain wall.</summary>
  public double? HingeAt;

  public double StartOffset => Template[0].Off;
  public double Reach => Template[^1].Off;

  /// <summary>First offset where the chain turns steeper than 1:10 - the hinge of the daylight slope.</summary>
  public double? HingeOffset
  {
    get
    {
      if (HingeAt.HasValue) return HingeAt;
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
    if (!Extend) return off <= Reach + 0.05 ? Template[^1].Dz : null;
    if (Template.Count < 2 || off > Reach + extension) return null;
    var (a0, b0) = Template[^2]; var (a1, b1) = Template[^1];
    var slope = a1 - a0 > 1e-9 ? (b1 - b0) / (a1 - a0) : 0.0;
    return b1 + slope * (off - a1);
  }
}

/// <summary>The sections of one bend, with the design surface they sweep: Z(station, offset), interpolated between stations.
/// Two sections may share a station (the end of one region and the start of the next, where the assembly changes): the
/// order is kept, so the surface just before that station runs to the first of them and the surface after it starts from
/// the second - a step in the design, as Civil 3D builds it, not a blend of two different sections.</summary>
public sealed class SectionSet
{
  public readonly List<SectionSample> Sections;
  public readonly double Extension;
  /// <summary>Where the baseline level comes from. A subset (one side of a bend) keeps the level of the whole set, since
  /// the profile runs on through the bend even where the sections change.</summary>
  private readonly SectionSet? _levels;

  public SectionSet(IEnumerable<SectionSample> sections, double extension = 5.0)
  {
    Sections = sections.Where(s => s.Template.Count >= 2).OrderBy(s => s.Station).ToList();
    if (Sections.Count < 2) throw new ArgumentException("At least two sections with an inside link chain are needed.");
    Extension = extension;
  }

  private SectionSet(List<SectionSample> sections, double extension, SectionSet levels)
  {
    Sections = sections; Extension = extension; _levels = levels;
  }

  /// <summary>The same design surface restricted to some of the sections (one side of a bend); outside their range each
  /// end section is carried on unchanged. The baseline level still comes from every section.</summary>
  public SectionSet Subset(Func<SectionSample, int, bool> keep)
  {
    var list = Sections.Where((s, i) => keep(s, i)).ToList();
    if (list.Count == 0) throw new ArgumentException("The subset has no sections.");
    return new SectionSet(list, Extension, _levels ?? this);
  }

  /// <summary>True when two sections are the same kind of section: they start at the same offset and turn onto the
  /// daylight at the same hinge (within tol). Where they are not, the design changes section between them.</summary>
  public static bool SameKind(SectionSample a, SectionSample b, double tol)
  {
    if (Math.Abs(a.StartOffset - b.StartOffset) > tol) return false;
    var ha = a.HingeOffset; var hb = b.HingeOffset;
    if (ha.HasValue != hb.HasValue) return false;
    return !ha.HasValue || Math.Abs(ha.Value - hb!.Value) <= tol;
  }

  /// <summary>Stations between which the section changes kind (see SameKind), with a short description of what changes.</summary>
  public List<(double From, double To, string What)> Changes(double lo, double hi, double tol)
  {
    var found = new List<(double, double, string)>();
    for (var i = 1; i < Sections.Count; i++)
    {
      var a = Sections[i - 1]; var b = Sections[i];
      if (b.Station < lo || a.Station > hi || SameKind(a, b, tol)) continue;
      var what = new List<string>();
      if (Math.Abs(a.StartOffset - b.StartOffset) > tol) what.Add($"inside links start at {a.StartOffset:0.###} then {b.StartOffset:0.###} m");
      var ha = a.HingeOffset; var hb = b.HingeOffset;
      if (ha.HasValue && hb.HasValue && Math.Abs(ha.Value - hb.Value) > tol) what.Add($"hinge at {ha:0.###} then {hb:0.###} m");
      else if (ha.HasValue != hb.HasValue) what.Add(ha.HasValue ? "a daylight slope, then none" : "no daylight slope, then one");
      found.Add((a.Station, b.Station, string.Join(", ", what)));
    }
    return found;
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
    if (_levels != null) return _levels.BaselineLevel(s);
    var (a, b, w) = Bracket(s);
    return a.Z0 + (b.Z0 - a.Z0) * w;
  }

  /// <summary>The section at a station: the section itself at an applied station, else the two either side interpolated
  /// vertex by vertex when they have the same shape, else the nearer of the two.</summary>
  public SectionSample SampleAt(double s)
  {
    var (a, b, w) = Bracket(s);
    if (w <= 0 || ReferenceEquals(a, b)) return a;
    if (w >= 1) return b;
    if (a.Template.Count == b.Template.Count && a.Template.Count >= 2)
    {
      var mid = new SectionSample { Station = s, Z0 = a.Z0 + (b.Z0 - a.Z0) * w, Extend = a.Extend && b.Extend,
        HingeAt = a.HingeAt.HasValue && b.HingeAt.HasValue ? a.HingeAt + (b.HingeAt - a.HingeAt) * w : null };
      for (var i = 0; i < a.Template.Count; i++)
        mid.Template.Add((a.Template[i].Off + (b.Template[i].Off - a.Template[i].Off) * w, a.Template[i].Dz + (b.Template[i].Dz - a.Template[i].Dz) * w));
      var ok = true;
      for (var i = 1; i < mid.Template.Count; i++) if (mid.Template[i].Off < mid.Template[i - 1].Off - 1e-9) { ok = false; break; }
      if (ok) return mid;
    }
    return w > 0.5 ? b : a;
  }

  /// <summary>Relative elevation of the design at (station, offset). Between two sections with the same number of
  /// vertices the vertices themselves are interpolated (a bench or drain edge moves smoothly from one section to the next,
  /// as the corridor surface joins same-coded points); otherwise the two levels at this offset are blended.</summary>
  public double? Dz(double s, double off)
  {
    var (a, b, w) = Bracket(s);
    var da = a.Dz(off, Extension);
    if (w <= 0 || ReferenceEquals(a, b)) return da;
    if (a.Template.Count == b.Template.Count && a.Template.Count >= 2)
    {
      var mid = new SectionSample { Station = s, Extend = a.Extend && b.Extend };
      for (var i = 0; i < a.Template.Count; i++)
        mid.Template.Add((a.Template[i].Off + (b.Template[i].Off - a.Template[i].Off) * w, a.Template[i].Dz + (b.Template[i].Dz - a.Template[i].Dz) * w));
      var ok = true;
      for (var i = 1; i < mid.Template.Count; i++) if (mid.Template[i].Off < mid.Template[i - 1].Off - 1e-9) { ok = false; break; }
      if (ok) return mid.Dz(off, Extension);
    }
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

  /// <summary>Horizontal run of the link under (station, offset), from the nearer of the two sections next to the station.
  /// The last link of a section that ends on the ground is its daylight link and counts as long, whatever its length.</summary>
  public double LinkRun(double s, double off)
  {
    var (a, b, w) = Bracket(s);
    var src = w > 0.5 ? b : a;
    var t = src.Template;
    for (var i = 1; i < t.Count; i++)
      if (off <= t[i].Off || i == t.Count - 1)
        return i == t.Count - 1 && src.Extend ? double.MaxValue : t[i].Off - t[i - 1].Off;
    return double.MaxValue;
  }

  /// <summary>False where either section next to this station is open ended (see SectionSample.Extend).</summary>
  public bool Extends(double s)
  {
    var (a, b, _) = Bracket(s);
    return a.Extend && b.Extend;
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
