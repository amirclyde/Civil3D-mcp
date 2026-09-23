"""
Offline check of UTNM_LaneDaylightClip v0.4 (Spread From Hinge) against v0.3, with the flowchart evaluator
(../UTNMBench/simulate.py). Right side, X = offset from the attachment point.

  1. Spread From Hinge = No behaves exactly as v0.3 (every case, with and without clip targets).
  2. Spread From Hinge = Yes without ClipElev, or without a clip, behaves exactly as v0.3.
  3. Spread From Hinge = Yes with ClipTarget + ClipElev beyond the hinge:
       - the section ends in a Valley point at the clip offset and the ClipElev level,
       - it has the same points (same names, same offsets) as v0.3 up to the clip - no breakpoint moves,
       - every link from the hinge to the clip has its v0.3 grade plus one and the same g,
       - g = (ClipElev - own level at the clip) / (clip - hinge), the level v0.3 would reach on its own slopes.

    python check_spread.py
"""
import itertools, math, os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "UTNMBench"))
import simulate as S

V3 = S.Part(os.path.join(HERE, "UTNM_LaneDaylightClip_v0.3.pkt"))
V4 = S.Part(os.path.join(HERE, "UTNM_LaneDaylightClip.pkt"))
YES, NO = S.Enum(10), S.Enum(11)


def grounds():
    # level ground at many heights (cut and fill, one to four stages), sloping ground, a wavy one
    for h in (-14.0, -9.0, -5.5, -2.5, -0.8, 0.6, 2.5, 5.0, 8.5, 13.0):
        yield f"level {h:+}", (lambda h: lambda x: h)(h)
    for h, c in ((-6.0, 0.12), (4.0, -0.1), (3.0, 0.25), (-3.0, -0.3)):
        yield f"cross-fall {h:+} {c:+}", (lambda h, c: lambda x: h + c * x)(h, c)
    yield "wavy", lambda x: -4.0 + 1.5 * math.sin(x / 3.0)


def run(part, ground, clip=None, elev=None, spread=YES, lane=3.7):
    params = {"LaneWidth": lane, "LaneSlope": -0.02, "SpreadFromHinge": spread,
              "ClipTarget": S.Target(offset=clip) if clip is not None else S.NO_TARGET,
              "ClipElev": S.Target(elevation=elev) if elev is not None else S.NO_TARGET}
    pts, links, env = part.run(ground, params)
    return pts, links


def same(a, b, tol=1e-9):
    (pa, la), (pb, lb) = a, b
    if [p[0] for p in pa] != [p[0] for p in pb] or [l[0] for l in la] != [l[0] for l in lb]: return False
    return all(abs(x[1] - y[1]) <= tol and abs(x[2] - y[2]) <= tol for x, y in zip(pa, pb))


fails = 0; cases = 0; spread_cases = 0; worst_g = 0.0
def fail(msg):
    global fails
    fails += 1
    if fails <= 25: print("FAIL", msg)


for (gname, g), lane in itertools.product(list(grounds()), (3.7, 0.0)):
    base = run(V3, g, lane=lane)
    # the natural reach: where v0.3 ends with no clip
    reach = max(p[1] for p in base[0])
    hinge = next((p[1] for p in base[0] if "Hinge" in p[3]), 0.0)
    clips = [None] + [hinge * f for f in (0.5,)] + [hinge + (reach - hinge) * f for f in (0.05, 0.3, 0.55, 0.8, 0.97)] + [reach + 2.0]
    for clip in clips:
        own3 = run(V3, g, clip, None, lane=lane)
        for dz in (None, -0.45, -0.1, 0.05, 0.3, 0.8):
            cases += 1
            elev = None
            if clip is not None and dz is not None:
                vp = [p for p in own3[0] if "Valley" in p[3]]
                if not vp: continue
                elev = vp[0][2] + dz
            r3 = run(V3, g, clip, elev, lane=lane)
            rno = run(V4, g, clip, elev, spread=NO, lane=lane)
            if not same(r3, rno, 1e-7): fail(f"{gname} lane {lane} clip {clip} dz {dz}: Spread From Hinge = No differs from v0.3")
            ryes = run(V4, g, clip, elev, spread=YES, lane=lane)
            clipped_beyond_hinge = elev is not None and clip is not None and clip > hinge + 0.01 and any("Valley" in p[3] for p in r3[0])
            if not clipped_beyond_hinge:
                if not same(r3, ryes, 1e-7): fail(f"{gname} lane {lane} clip {clip} dz {dz}: Yes without a spread differs from v0.3")
                continue
            spread_cases += 1
            p3, l3 = own3; py, ly = ryes          # against v0.3 on its own slopes (same points, the clip without ClipElev)
            if [p[0] for p in p3] != [p[0] for p in py]: fail(f"{gname} clip {clip} dz {dz}: different points {[p[0] for p in p3]} / {[p[0] for p in py]}"); continue
            if any(abs(a[1] - b[1]) > 1e-7 for a, b in zip(p3, py)): fail(f"{gname} clip {clip} dz {dz}: a breakpoint moved"); continue
            v = [p for p in py if "Valley" in p[3]][0]
            if abs(v[1] - clip) > 1e-7 or abs(v[2] - elev) > 1e-7: fail(f"{gname} clip {clip} dz {dz}: valley at {v[1]:.4f}/{v[2]:.4f}, expected {clip:.4f}/{elev:.4f}")
            own = [p for p in own3[0] if "Valley" in p[3]][0][2]
            gexp = (elev - own) / (clip - hinge)
            worst_g = max(worst_g, abs(gexp))
            byname3 = {p[0]: p for p in p3}; bynamey = {p[0]: p for p in py}
            for (name, a, b, codes) in ly:
                a3, b3, ay, by = byname3[a], byname3[b], bynamey[a], bynamey[b]
                run_ = b3[1] - a3[1]
                if a3[1] < hinge - 1e-9 or run_ < 1e-6: continue
                d = (by[2] - ay[2]) / run_ - (b3[2] - a3[2]) / run_
                if abs(d - gexp) > 1e-6: fail(f"{gname} clip {clip} dz {dz}: link {name} {a}-{b} grade changed by {d:.5f}, expected {gexp:.5f}"); break

print(f"{cases} cases, {spread_cases} with the move spread (largest g {worst_g * 100:.1f}%), {fails} failures")
sys.exit(1 if fails else 0)
