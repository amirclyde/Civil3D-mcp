import math, sys
from simulate import Part, Enum
old, new = Part("UTNMBench-C3D25-DrainConnect.pkt"), Part("UTNMBench_v0.2.pkt")
YES, NO = Enum(10), Enum(11)
P = dict(MaxDaylightHeight=4.0, BenchHeight=4.0)

def grounds():
    out = []
    for h in (-30, -17.3, -9, -4.2, -2, -0.5, -0.1, 0.1, 0.5, 2, 4.2, 9, 17.3, 30):
        for cf in (0.0, 0.03, -0.03, 0.10, -0.10, 0.25, -0.25):
            out.append((f"h={h:+} fall={cf:+.2f}", (lambda h, cf: lambda x: h + cf * x)(h, cf)))
        out.append((f"h={h:+} wavy", (lambda h: lambda x: h + 0.6 * math.sin(x / 7.0) + 0.02 * x)(h)))
    return out

def end_gap(order, g):
    n, x, y, c = order[-1] if "Proposed" not in order[-1][3] else [o for o in order if "Proposed" not in o[3]][-1]
    return y - g(x), n, c

def tbench_gap(order, g):
    c = [o for o in order if "TBenchDrainIn" in o[3]]
    return (c[-1][2] - g(c[-1][1])) if c else float("nan")

rows = []
flat_same = flat_total = nofit_same = nofit_total = 0
worst_old = worst_new = worst_tb_old = worst_tb_new = 0.0
open_old = open_new = 0
for name, g in grounds():
    o_pts, o_links, _ = old.run(g, P)
    n_pts, n_links, env = new.run(g, P)
    f_pts, f_links, _ = new.run(g, dict(P, FitToGround=NO))
    go, gn = end_gap(o_pts, g), end_gap(n_pts, g)
    open_old += abs(go[0]) > 1e-4; open_new += abs(gn[0]) > 1e-4
    worst_old, worst_new = max(worst_old, abs(go[0])), max(worst_new, abs(gn[0]))
    to, tn = tbench_gap(o_pts, g), tbench_gap(n_pts, g)
    if not math.isnan(to): worst_tb_old = max(worst_tb_old, abs(to))
    if not math.isnan(tn): worst_tb_new = max(worst_tb_new, abs(tn))
    od = {p[0]: p for p in o_pts}
    def same(pts):
        nd = {p[0]: p for p in pts}
        return all(k in nd and abs(nd[k][1] - v[1]) < 1e-9 and abs(nd[k][2] - v[2]) < 1e-9 for k, v in od.items())
    if "fall=+0.00" in name:
        flat_total += 1; flat_same += same(n_pts)
        if not same(n_pts): print("FLAT DIFF", name, [k for k in od if k not in {p[0] for p in n_pts}])
    # FitToGround = No: everything but the last point must be the original
    nofit_total += 1
    nd = {p[0]: p for p in f_pts}
    last = o_pts[-1][0]
    ok = all(k in nd and abs(nd[k][1] - v[1]) < 1e-9 and abs(nd[k][2] - v[2]) < 1e-9 for k, v in od.items() if k != last or abs(go[0]) < 1e-4)
    nofit_same += ok
    if not ok: print("NOFIT DIFF", name)
    rows.append((name, len(o_pts), len([p for p in n_pts if "Proposed" not in p[3]]), go[0], gn[0], to, tn, sum("Proposed" in p[3] for p in n_pts)))

print(f"grounds: {len(rows)}")
print(f"even ground: v0.2 identical to the original in {flat_same}/{flat_total}")
print(f"FitToGround=No: identical to the original (last link aside) in {nofit_same}/{nofit_total}")
print(f"sections not ending on the ground: original {open_old}, v0.2 {open_new}  (largest gap {worst_old:.3f} / {worst_new:.6f} m)")
print(f"terminal bench end off the ground, worst: original {worst_tb_old:.3f} m, v0.2 {worst_tb_new:.3f} m")
if "-v" in sys.argv:
    for r in rows: print("%-22s pts %2d/%2d  end gap %8.3f -> %9.5f   tbench gap %7.3f -> %7.3f  proposed %d" % r)
