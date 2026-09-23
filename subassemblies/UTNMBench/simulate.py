"""
Offline evaluator for a Subassembly Composer flowchart of the UTNMBench family: runs the .pkt's flowchart for one section
over a ground line and returns the points and links it would draw (right side, X = offset outward from the origin).
It knows the activities and positioning types these parts use, nothing more. It is a check of the LOGIC (decisions and
expressions), not of Composer itself: what Composer does with a name or a type still has to be seen in Civil 3D.

    python simulate.py                 # compares UTNMBench_v0.2.pkt with the original over a set of ground lines
"""
import math, re, sys, zipfile
import xml.etree.ElementTree as ET

NS_A = "http://schemas.microsoft.com/netfx/2009/xaml/activities"
NS_S = "clr-namespace:Autodesk.SubassemblyComposer.ActivityLibrary;assembly=Subassembly.ActivityLibrary"
NS_X = "http://schemas.microsoft.com/winfx/2006/xaml"
A, S, X = "{%s}" % NS_A, "{%s}" % NS_S, "{%s}" % NS_X
NAN = float("nan")


class Pt:
    def __init__(self, x=NAN, y=NAN, valid=True):
        self.X, self.Y, self.IsValid = x, y, valid and not (math.isnan(x) or math.isnan(y))

    Offset = property(lambda s: s.X)
    Elevation = property(lambda s: s.Y)


class Target:
    """An offset or elevation target as the flowchart sees it: IsValid, Offset (offset target) / Elevation (elevation target)."""
    def __init__(self, offset=NAN, elevation=NAN, valid=True):
        self.Offset, self.Elevation, self.IsValid = offset, elevation, valid


NO_TARGET = Target(valid=False)


class Enum:
    def __init__(self, v): self.v = v
    def __eq__(self, o): return isinstance(o, Enum) and o.v == self.v
    def __ne__(self, o): return not self.__eq__(o)


def to_python(expr):
    e = expr.strip()
    if e.startswith("[") and e.endswith("]"):
        e = e[1:-1]
    e = re.sub(r"\bIF\(|\bIf\(", "_if(", e)
    e = e.replace("Math.Abs", "abs").replace("Math.Max", "max").replace("Math.Min", "min").replace("Math.Sign", "_sign").replace("Math.Floor", "_floor")
    e = re.sub(r"\bAndAlso\b", " and ", e); e = re.sub(r"\bOrElse\b", " or ", e); e = re.sub(r"\bNot\b", " not ", e)
    e = e.replace("<>", "!=")
    e = re.sub(r"(?<![<>=!])=(?!=)", "==", e)
    return e


class Part:
    def __init__(self, path):
        with zipfile.ZipFile(path) as z:
            xaml = [n for n in z.namelist() if n.endswith(".xaml")][0]
            self.root = ET.fromstring(z.read(xaml).decode("utf-8-sig"))
        self.names = {e.get(X + "Name"): e for e in self.root.iter() if e.get(X + "Name")}
        self.defaults = {}
        for k, v in self.root.attrib.items():
            if "Subassembly." in k:
                name = k.split("Subassembly.")[1]
                m = re.search(r"new (?:Slope|Grade)\(([-\d.]+)\)", v)
                me = re.search(r"new EnumType\((-?\d+)", v)
                if m: self.defaults[name] = float(m.group(1))
                elif me: self.defaults[name] = Enum(int(me.group(1)))
                elif re.fullmatch(r"[-\d.]+", v): self.defaults[name] = float(v)

    def run(self, ground, params=None, limit=400.0):
        env = dict(self.defaults); env.update(params or {})
        env.update(_if=lambda c, a, b: a if c else b, _sign=lambda v: (v > 0) - (v < 0), _floor=lambda v: float(math.floor(v)) if math.isfinite(v) else v, Yes=Enum(10), No=Enum(11), abs=abs, max=max, min=min)
        for k, v in list((params or {}).items()): env[k] = v
        pts, links, order = {}, [], []

        def ev(expr):
            try:
                return eval(to_python(expr), {"__builtins__": {}}, env)
            except Exception as ex:
                raise RuntimeError(f"{expr}: {ex}")

        def g(x):
            return ground(x)

        def args(el):
            out = {}
            for c in el:
                if c.tag.endswith(".Arguments"):
                    for a in c:
                        out[re.sub(r"\d+$", "", a.get(X + "Key"))] = a.text
            return out

        def codes(el, suffix):
            for c in el:
                if c.tag.endswith(suffix):
                    return [a.text for a in c.iter(A + "InArgument")]
            return []

        def make_point(el, aux):
            pid, frm, pos = el.get("PointNumber"), el.get("FromPoint"), el.get("Positioning")
            if pid in pts: raise RuntimeError(f"{pid} created twice")
            o = pts[frm] if frm and frm != "{x:Null}" else Pt(0.0, 0.0)
            a = args(el)
            if not o.IsValid: p = Pt(valid=False)
            elif pos == "DeltaXAndDeltaY": p = Pt(o.X + ev(a["DeltaX"]), o.Y + ev(a["DeltaY"]))
            elif pos == "SlopeAndDeltaX": dx = ev(a["DeltaX"]); p = Pt(o.X + dx, o.Y + ev(a["Slope"]) * dx)
            elif pos == "SlopeAndDeltaY":
                sl, dy = ev(a["Slope"]), ev(a["DeltaY"]); p = Pt(o.X + abs(dy / sl), o.Y + dy)
            elif pos == "DeltaXOnSurface":
                x = o.X + ev(a["DeltaX"]); gy = g(x); p = Pt(x, gy) if gy is not None else Pt(valid=False)
            elif pos == "SlopeToSurface":
                sl = ev(a["Slope"]); f = lambda t: None if g(o.X + t) is None else o.Y + sl * t - g(o.X + t)
                p, t, step, prev = Pt(valid=False), 1e-6, 0.01, None
                prev = f(t)
                while t < limit and prev is not None:
                    cur = f(t + step)
                    if cur is None: break
                    if prev == 0 or prev * cur <= 0:
                        lo, hi = t, t + step
                        for _ in range(60):
                            mid = 0.5 * (lo + hi)
                            if f(lo) * f(mid) <= 0: hi = mid
                            else: lo = mid
                        tt = 0.5 * (lo + hi); p = Pt(o.X + tt, o.Y + sl * tt); break
                    prev, t = cur, t + step
            else:
                raise RuntimeError(f"{pid}: positioning {pos}")
            pts[pid] = p; env[pid] = p
            if not aux and p.IsValid:
                order.append((pid, p.X, p.Y, codes(el, ".PointCodes")))
                if el.get("AutoLink") == "True" and frm in pts:
                    links.append((el.get("AutoLinkGeometryName"), frm, pid, codes(el, ".AutoLinkCodes")))

        def activity(el):
            t = el.tag
            if t == S + "CreatePoint": make_point(el, False)
            elif t == S + "CreateAuxPoint": make_point(el, True)
            elif t == S + "CreateLink":
                a, b = el.get("StartPoint"), el.get("EndPoint")
                if pts.get(a) and pts.get(b) and pts[a].IsValid and pts[b].IsValid:
                    links.append((el.get("LinkNumber"), a, b, codes(el, ".LinkCodes")))
            elif t == S + "InternalVariableDefine":
                var = el.find(S + "InternalVariableDefine.Variable")
                val = el.find(S + "InternalVariableDefine.DefaultValue")
                if var is not None and len(var) and val is not None and len(val):
                    env[el.get("VariableName")] = ev(val[0].text)
            elif t == A + "Sequence":
                for c in el:
                    if c.tag.startswith(S) or c.tag in (A + "Sequence", A + "Flowchart"): activity(c)
            elif t == A + "Flowchart":
                sn = el.find(A + "Flowchart.StartNode")
                if sn is not None and len(sn): node(sn[0])
            else:
                raise RuntimeError("activity " + t)

        def node(el):
            while el is not None:
                if el.tag == X + "Reference": el = self.names[el.text]
                if el.tag == A + "FlowStep":
                    for c in el:
                        if c.tag.startswith(S) or c.tag in (A + "Sequence", A + "Flowchart"): activity(c)
                    nx = el.find(A + "FlowStep.Next")
                    el = nx[0] if nx is not None and len(nx) else None
                elif el.tag == A + "FlowDecision":
                    br = el.find(A + ("FlowDecision.True" if ev(el.get("Condition")) else "FlowDecision.False"))
                    el = br[0] if br is not None and len(br) else None
                else:
                    raise RuntimeError("node " + el.tag)

        activity(self.root.find(A + "Flowchart"))
        return order, links, env
