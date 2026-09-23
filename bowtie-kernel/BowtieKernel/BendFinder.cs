namespace Utnm.BowtieKernel;

public sealed record Bend(double From, double To, Side Side, double TurnDegrees);

/// <summary>Splits a baseline into single bends: a maximal run that turns one way. A reverse curve gives two bends on opposite sides.</summary>
public static class BendFinder
{
  public static List<Bend> Find(SampledBaseline bl, double from, double to, double minTurnDegrees = 3.0, double straightGap = 1.0)
  {
    var bends = new List<Bend>();
    from = Math.Max(from, bl.Start); to = Math.Min(to, bl.End);
    const double h = 0.1;
    int Sign(double s)
    {
      var corner = bl.Corners.FirstOrDefault(c => Math.Abs(c.Station - s) <= h / 2);
      if (corner.Jump != 0) return Math.Sign(corner.Jump);
      var k = bl.Kappa(s, 0.1);
      return Math.Abs(k) < 1e-4 ? 0 : Math.Sign(k);
    }
    double? start = null; double last = from; var sign = 0;
    void Close()
    {
      if (start.HasValue)
      {
        var turn = bl.Turn(start.Value, last) * 180 / Math.PI;
        if (Math.Abs(turn) >= minTurnDegrees) bends.Add(new Bend(start.Value, last, turn > 0 ? Side.Left : Side.Right, turn));
      }
      start = null; sign = 0;
    }
    for (var s = from; s <= to + 1e-9; s += h)
    {
      var sg = Sign(s);
      if (sg == 0) { if (start.HasValue && s - last >= straightGap) Close(); continue; }
      if (start.HasValue && sg != sign) Close();
      if (!start.HasValue) { start = Math.Max(from, s - h); sign = sg; }
      last = Math.Min(to, s + h);
    }
    Close();
    return bends;
  }
}
