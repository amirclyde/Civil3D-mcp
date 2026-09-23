"""
Generate UTNMBench v0.2 (UTNMBench_v0.2.pkt) from the original UTNMBench (UTNMBench-C3D25-DrainConnect.pkt).

The original package is NOT re-written by hand. Its flowchart is read, every activity is kept VERBATIM (same point and link
numbers, codes, expressions), and only the changes listed here are made. With FitToGround = No and CascadeDrain = No the
part draws what the original draws, plus the repairs of group A.

A. Repairs (always)
  A1  "Partial slope + terminal bench" decisions (7) tested the FILL width of the terminal bench in cut and the CUT width in
      fill; the geometry right behind them uses the right one. The test now agrees with the geometry.
  A2  The dead decision behind "BuildBench1" used Math.Abs on the bench rises, the live ones use them signed. Signed is right
      (a bench that falls back towards the slope gives height back), so that one is now signed too.
  A3  Three variables that nothing reads (BenchDecision, TerminalBenchWidth, TerminalBenchHeight; two of them were not even
      bound to a variable) are removed.
  A4  P290 had no From point; it starts at P233 like its siblings.
  A5  The last daylight link always went the way the ground was above/below P1 (SlopeDirectionCorrection). Where the terminal
      drain ends on the other side of the ground the link pointed away from it, found nothing, and the section ended in the
      air. It now goes up or down, whichever way the ground is from the end of the terminal drain. Where that is the other
      way than SlopeDirectionCorrection a marker point coded  Proposed  is put on the daylight point.
  A6  Codes: every last link is  Top, Daylight  and every last point  Daylight ; terminal bench links are  Top  (two were
      Top, Drain); the uncoded terminal drain of the last ending is coded like its twenty siblings; slope link L238 had no
      code (now Top). Where the terminal drain already ends on the ground (within 1 mm) no link is attempted and a marker
      point coded Daylight is put there, so that every section carries a Daylight point.

B. FitToGround (Yes / No, default Yes)
      The original takes the height of every stage from the ground straight above (below) the point where the stage starts.
      The terminal bench is then built to that level, 6 to 60 m further out, where the ground is somewhere else. With
      FitToGround the height is taken where the terminal bench would really end: five passes (build the stage for the
      height so far, read the ground at the end of the terminal bench, correct; secant steps from the third pass on). On even ground the result is the original to
      the last digit. Where the fitted height differs from the original's by more than 10 mm, a marker point coded  Proposed
      is put at the start of the terminal drain: this is the part proposing something the given parameters did not say.

C. CascadeDrain (Yes / No, default No)
      The berm drains run along the benches. Where the engineer adds a cascade drain down the slope, switch this on for that
      region: links coded  CascadeDrain  join the drains down the slope, bench drain to bench drain to the terminal drain
      (the toe drain, lowest in fill); in cut the chain also runs from bench 1 down to P1, where the roadside drain of the
      full assembly takes the water. The original drew only the last piece (last bench to terminal drain), always, uncoded.

Usage:  python make_pkt.py [out_dir] [source_pkt]
"""
import os, re, sys, uuid, zipfile
sys.setrecursionlimit(20000)
import xml.etree.ElementTree as ET

NAME = "UTNMBench"
FILE = "UTNMBench_v0.2"
VERSION = "0.2"
HERE = os.path.dirname(os.path.abspath(__file__))
OUT_DIR = sys.argv[1] if len(sys.argv) > 1 else HERE
SOURCE = sys.argv[2] if len(sys.argv) > 2 else os.path.join(HERE, "UTNMBench-C3D25-DrainConnect.pkt")
GUID = uuid.uuid4().hex
DESCRIPTION = ("Bench and berm drain with terminal bench drain. v0.2: terminal bench fitted to the ground where it really ends "
               "(FitToGround), last daylight link always reaches the ground, optional cascade drain chain (CascadeDrain), "
               "what the part proposes beyond the given parameters is marked with point code Proposed.")

NS_A = "http://schemas.microsoft.com/netfx/2009/xaml/activities"
NS_S = "clr-namespace:Autodesk.SubassemblyComposer.ActivityLibrary;assembly=Subassembly.ActivityLibrary"
NS_X = "http://schemas.microsoft.com/winfx/2006/xaml"
A, S, X = "{%s}" % NS_A, "{%s}" % NS_S, "{%s}" % NS_X

YESNO_PARAMS = [  # name, display, description, default (Yes = 10, No = 11: Composer's own enum values)
    ("FitToGround", "Fit To Ground", "Yes: the height of the last slope is taken where the terminal bench really ends. No: from the ground straight above the start of the stage, as the original part does.", "Yes"),
    ("CascadeDrain", "Cascade Drain", "Yes: draw the cascade drain links (code CascadeDrain) joining the bench drains down the slope to the lowest drain.", "No"),
]
YESNO_VALUE = {"Yes": 10, "No": 11}
STAGE_START = ["P1", "P147", "P185", "P206", "P227", "P233", "P264"]      # AP1..AP7 sit on the ground above / below these
FIT_VAR = ["FitHA", "FitHB", "FitHC", "FitHD", "FitHE", "FitHF", "FitHG"]
OLD_VAR = ["RawHA", "RawHB", "RawHC", "RawHD", "RawHE", "RawHF", "RawHG"]
BENCH_DRAIN = ["P145", "P183", "P204", "P225", "P231"]                     # outer bottom point of berm drain 1..5
REMOVED_VARS = ["BenchDecision", "TerminalBenchWidth", "TerminalBenchHeight"]
DOUBLE_PARAMS = [("ProposedTolerance", "Proposed Tolerance", "A section is marked Proposed where fitting to the ground changes the height of its last slope by more than this.", 0.05)]


def esc(s):
    return s.replace("&", "&amp;").replace('"', "&quot;").replace("<", "&lt;").replace(">", "&gt;")


# ------------------------------------------------------------------------------------------------ read the source
def read_source():
    with zipfile.ZipFile(SOURCE) as z:
        names = z.namelist()
        stem = [n for n in names if n.endswith(".xaml")][0][:-5]
        files = {n: z.read(n).decode("utf-8-sig") for n in names}
    return stem, files


class Step:
    def __init__(self, kind, aid, el, nxt=None):
        self.kind, self.aid, self.el, self.next = kind, aid, el, nxt     # kind: point | aux | link | var


class Decision:
    def __init__(self, condition, true, false, true_label="True", false_label="False"):
        self.condition, self.true, self.false, self.true_label, self.false_label = condition, true, false, true_label, false_label


class Raw:
    """A generated activity (text) followed by next."""
    def __init__(self, build, nxt=None):
        self.build, self.next = build, nxt


def labels(el):
    out = {}
    for d in el.iter("{clr-namespace:System.Collections.Generic;assembly=mscorlib}Dictionary"):
        for c in d:
            out[c.get(X + "Key")] = c.text
        break
    return out


def to_ir(root):
    names = {e.get(X + "Name"): e for e in root.iter() if e.get(X + "Name")}
    used = set()

    def activity(el, cont):
        t = el.tag
        kinds = {S + "CreatePoint": "point", S + "CreateAuxPoint": "aux", S + "CreateLink": "link", S + "InternalVariableDefine": "var"}
        if t in kinds:
            return Step(kinds[t], el.get("ActivityId"), el, cont)
        if t == A + "Sequence":
            node = cont
            for c in reversed([c for c in el if c.tag.startswith(S) or c.tag in (A + "Sequence", A + "Flowchart")]):
                node = activity(c, node)
            return node
        if t == A + "Flowchart":
            sn = el.find(A + "Flowchart.StartNode")
            return node_ir(list(sn)[0], cont) if sn is not None and len(sn) else cont
        raise ValueError("unknown activity " + t)

    def node_ir(el, cont):
        if el.tag == X + "Reference":
            el = names[el.text]
        n = el.get(X + "Name")
        if n in used:
            raise ValueError(f"flow node {n} is reached twice")
        if n:
            used.add(n)
        if el.tag == A + "FlowStep":
            nx = el.find(A + "FlowStep.Next")
            after = node_ir(list(nx)[0], cont) if nx is not None and len(nx) else cont
            acts = [c for c in el if c.tag.startswith(S) or c.tag in (A + "Sequence", A + "Flowchart")]
            return activity(acts[0], after) if acts else after
        if el.tag == A + "FlowDecision":
            lb = labels(el)
            tb, fb = el.find(A + "FlowDecision.True"), el.find(A + "FlowDecision.False")
            cond = el.get("Condition")
            return Decision(cond[1:-1] if cond.startswith("[") else cond,
                            node_ir(list(tb)[0], cont) if tb is not None and len(tb) else cont,
                            node_ir(list(fb)[0], cont) if fb is not None and len(fb) else cont,
                            lb.get("TrueLabel") or "True", lb.get("FalseLabel") or "False")
        raise ValueError("unknown flow node " + el.tag)

    main = root.find(A + "Flowchart")
    return node_ir(list(main.find(A + "Flowchart.StartNode"))[0], None)


def activity_text(xaml):
    out = {}
    for tag in ("CreatePoint", "CreateAuxPoint", "CreateLink", "InternalVariableDefine"):
        for m in re.finditer(rf"<asa2:{tag} [^>]*?ActivityId=\"(\d+)\".*?</asa2:{tag}>", xaml, re.S):
            out[m.group(1)] = m.group(0)
    return out


def code_list(el, suffix):
    for c in el:
        if c.tag.endswith(suffix):
            return [a.text for a in c.iter(A + "InArgument")]
    return []


# ------------------------------------------------------------------------------------------------ generated activities
COMMON = ('Geometry="[Geometry]" sap:VirtualizedContainerService.HintSize="200,22" ShowErrors="True" '
          'SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]"')
VIEWSTATE = ('<sap:WorkflowViewStateService.ViewState><scg:Dictionary x:TypeArguments="x:String, x:Object">'
             '<x:Boolean x:Key="IsExpanded">True</x:Boolean></scg:Dictionary></sap:WorkflowViewStateService.ViewState>')


class Ids:
    activity = 2000

    @classmethod
    def act(cls):
        cls.activity += 1
        return cls.activity


def codes_xml(tag, codes):
    items = "".join(f'<InArgument x:TypeArguments="x:String">{esc(c)}</InArgument>' for c in codes)
    return f'<{tag}><scg:List x:TypeArguments="InArgument(x:String)" Capacity="4">{items}</scg:List></{tag}>'


def gen_point(pid, positioning, frm, codes, aux=False, **kw):
    tag = "asa2:CreateAuxPoint" if aux else "asa2:CreatePoint"
    if positioning == "DeltaXAndDeltaY":
        rows = [("x:Double", "DeltaX1", kw["dx"]), ("x:Double", "DeltaY1", kw["dy"])]
    elif positioning == "DeltaXOnSurface":
        rows = [("x:Double", "DeltaX4", kw.get("dx", "0")), ("asw:SurfaceTarget", "SurfaceTarget4", "[SurfaceTarget]"),
                ("asw:OffsetTarget", "OffsetTarget4", None), ("x:Double", "DeltaYForLayout4", kw.get("layout_dy", "0"))]
    else:
        raise ValueError(positioning)
    body = "".join(f'<InArgument x:TypeArguments="{t}" x:Key="{k}" />' if v is None else
                   f'<InArgument x:TypeArguments="{t}" x:Key="{k}">{esc(v)}</InArgument>' for t, k, v in rows)
    attrs = (f'FromPoint="{frm}" ' if frm else 'FromPoint="{x:Null}" ') + 'AutoLinkCodes="{x:Null}" AutoLinkGeometryName="{x:Null}" AutoLink="False" '
    if aux or not codes:
        attrs += 'PointCodes="{x:Null}" '
    out = f'<{tag}.Arguments>{body}</{tag}.Arguments>'
    if codes and not aux:
        out += codes_xml(f"{tag}.PointCodes", codes)
    common = COMMON.replace('ShowErrors="True"', 'ShowErrors="False"') if aux else COMMON
    return (f'<{tag} {attrs}ActivityId="{Ids.act()}" ApplyAOR="False" DisplayName="{pid}" {common} '
            f'PointNumber="{pid}" Positioning="{positioning}" Side="[Side]">{out}{VIEWSTATE}</{tag}>')


def gen_link(lid, a, b, codes):
    return (f'<asa2:CreateLink ActivityId="{Ids.act()}" ApplyAOR="False" DisplayName="{lid}" EndPoint="{b}" {COMMON} '
            f'IsEnabled="True" LinkNumber="{lid}" StartPoint="{a}">' + codes_xml("asa2:CreateLink.LinkCodes", codes)
            + VIEWSTATE + '</asa2:CreateLink>')


def gen_var(name, expr):
    return (f'<asa2:InternalVariableDefine ActivityId="{Ids.act()}" DisplayName="{name} &lt;Double&gt;" '
            'sap:VirtualizedContainerService.HintSize="200,22" OldVariableName="" ShowErrors="True" '
            'SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]" '
            f'VariableName="{name}" VariableType="Double"><asa2:InternalVariableDefine.DefaultValue>'
            f'<InArgument x:TypeArguments="x:Double">[{esc(expr)}]</InArgument></asa2:InternalVariableDefine.DefaultValue>'
            f'<asa2:InternalVariableDefine.Variable><OutArgument x:TypeArguments="x:Double">[{name}]</OutArgument>'
            f'</asa2:InternalVariableDefine.Variable>{VIEWSTATE}</asa2:InternalVariableDefine>')


# ------------------------------------------------------------------------------------------------ edits of source activities
def set_codes(text, prop, codes):
    """Replace the code list 'prop' (PointCodes | AutoLinkCodes | LinkCodes) of one activity's XML."""
    tag = re.match(r"<(asa2:\w+)", text).group(1)
    text = text.replace(f' {prop}="{{x:Null}}"', "", 1)
    text = re.sub(rf"<{tag}\.{prop}>.*?</{tag}\.{prop}>\s*", "", text, flags=re.S)
    at = text.index("<sap:WorkflowViewStateService.ViewState>")
    return text[:at] + codes_xml(f"{tag}.{prop}", codes) + text[at:]


ABS_RE = re.compile(r"Math\.Abs\(AP(\d)\.Y ?- ?(P\d+)\.Y\)")


def stage_of(m):
    k = int(m.group(1)) - 1
    assert STAGE_START[k] == m.group(2), m.group(0)
    return k


def use_fit(s):
    return ABS_RE.sub(lambda m: FIT_VAR[stage_of(m)], s)


# ------------------------------------------------------------------------------------------------ the fit
PASSES = int(os.environ.get('BENCH_PASSES', '5'))
LET = 'abcdefghij'
PASS_VARS = [f"Fit{q}{'ABCDEFG'[k]}{c}" for k in range(7) for c in LET[:PASSES] + "z" for q in ("Y", "F")]


def fit_steps(k, nxt):
    """Aux points and variables of stage k, in front of 'nxt'.

    y = height of the last slope. For a given y the stage is laid out (slope y at DaylightSlope, terminal bench) and the
    ground is read at the end of the terminal bench: F(y) = how far the ground there is beyond that end, in the direction the
    slope runs. The height wanted is the y with F(y) = 0. Five passes: the original's height, one plain correction, three
    secant steps (exact on even ground after the second pass; needed where the ground has a kink or is steeper than the slope)."""
    p, ap, st = STAGE_START[k], f"AP{k + 1}", "ABCDEFG"[k]
    sg = f"If({ap}.Y>{p}.Y,1.0,-1.0)"
    w = f"If({ap}.Y>{p}.Y,TBenchCutWidth,TBenchFillWidth)"
    tb = f"Math.Abs(TBenchGrade*{w})"
    raw = f"Math.Abs({ap}.Y-{p}.Y)"
    top = "(Math.Max(BenchHeight,MaxDaylightHeight)+1.0)"
    clamp = lambda e: f"Math.Min({top},Math.Max(0.0,{e}))"
    Y = lambda i: f"FitY{st}{LET[i]}"
    F = lambda i: f"FitF{st}{LET[i]}"
    chain = [gen_var(OLD_VAR[k], raw)]
    for i in range(PASSES):
        if i == 0:
            y = clamp(f"{raw}-{tb}")
        elif i == 1:
            y = clamp(f"{Y(0)}+{F(0)}")
        else:
            d = f"({F(i - 1)}-{F(i - 2)})"
            y = clamp(f"If(Math.Abs({d})>0.000001,{Y(i - 1)}-{F(i - 1)}*({Y(i - 1)}-{Y(i - 2)})/If(Math.Abs({d})>0.000001,{d},1.0),{Y(i - 1)}+{F(i - 1)})")
        r, g = f"AP{100 + 20 * k + 2 * i}", f"AP{101 + 20 * k + 2 * i}"
        chain.append(gen_var(Y(i), y))
        chain.append(gen_point(r, "DeltaXAndDeltaY", p, [], aux=True, dx=f"[{Y(i)}/DaylightSlope+{w}]", dy=f"[{sg}*({Y(i)}+{tb})]"))
        # in layout mode the "ground" is level with the start of the stage, as it is for the original's own AP points
        chain.append(gen_point(g, "DeltaXOnSurface", r, [], aux=True, layout_dy=f"[{p}.Y-{r}.Y]"))
        chain.append(gen_var(F(i), f"If({g}.IsValid,{sg}*({g}.Y-{r}.Y),0.0)"))
    # the pass that came closest decides (the last one, unless the ground sent a secant step astray)
    least = f"Math.Abs({F(0)})"
    for i in range(1, PASSES):
        least = f"Math.Min({least},Math.Abs({F(i)}))"
    best_y, best_f = Y(0), F(0)
    for i in range(1, PASSES):
        best_y = f"If(Math.Abs({F(i)})<={least},{Y(i)},{best_y})"
        best_f = f"If(Math.Abs({F(i)})<={least},{F(i)},{best_f})"
    chain.append(gen_var(f"FitY{st}z", best_y))
    chain.append(gen_var(f"FitF{st}z", best_f))
    # at either limit of y the remainder is carried over: below 0 it selects the endings without a last slope, above the
    # limit it says "a full bench fits here"; in between the height found stands as it is
    at_limit = f"(FitY{st}z<=0.000001 OrElse FitY{st}z>={top}-0.000001)"
    chain.append(gen_var(FIT_VAR[k], f"If(FitToGround=Yes,Math.Max(0.0,FitY{st}z+{tb}+If({at_limit},FitF{st}z,0.0)),{raw})"))
    node = nxt
    for text in reversed(chain):
        node = Raw(text, node)
    return node


# ------------------------------------------------------------------------------------------------ the transformation
STATS = {"endings": 0, "cascade_links": 0, "fit_stages": 0, "cond_swapped": 0}


def transform(ir, texts):
    seq = {"ending": 0, "casc": 320}

    def cascade(link_text, cond, nxt):
        STATS["cascade_links"] += 1
        return Decision(cond, Raw(link_text, nxt), nxt, "Cascade drain", "None")

    def new_cascade(a, b, cond, nxt):
        seq["casc"] += 1
        return cascade(gen_link(f"L{seq['casc']}", a, b, ["CascadeDrain"]), cond, nxt)

    def ending(node, stage, tdrain):
        """node = the last SlopeToSurface point of a branch (with or without an explicit link step behind it)."""
        e = seq["ending"]; seq["ending"] += 1; STATS["endings"] += 1
        el = node.el
        pid, frm = el.get("PointNumber"), el.get("FromPoint")
        ap = f"AP{300 + e}"
        way = f"If({ap}.IsValid,If({ap}.Y&gt;={frm}.Y,1,-1),SlopeDirectionCorrection)"
        t = texts[node.aid]
        assert t.count("[DaylightSlope*SlopeDirectionCorrection]") == 1, pid
        t = t.replace("[DaylightSlope*SlopeDirectionCorrection]", f"[DaylightSlope*{way}]")
        t = set_codes(t, "PointCodes", ["Daylight"])
        tail = node.next
        if el.get("AutoLink") == "True":
            t = set_codes(t, "AutoLinkCodes", ["Top", "Daylight"])
            assert tail is None, pid
        else:
            assert isinstance(tail, Step) and tail.kind == "link" and tail.next is None, pid
            texts[tail.aid] = set_codes(texts[tail.aid], "LinkCodes", ["Top", "Daylight"])
        texts[node.aid] = t
        # markers: the fit moved the terminal bench; the last link goes the other way than the original would have sent it
        flipped = Decision(f"{ap}.IsValid AndAlso {pid}.IsValid AndAlso If({ap}.Y>={frm}.Y,1,-1)<>SlopeDirectionCorrection",
                           Raw(gen_point(f"P{330 + e}", "DeltaXAndDeltaY", pid, ["Proposed"], dx="0", dy="0")), None, "Proposed: link reversed", "As given")
        moved = Decision(f"Math.Abs({FIT_VAR[stage]}-{OLD_VAR[stage]})>ProposedTolerance",
                         Raw(gen_point(f"P{300 + e}", "DeltaXAndDeltaY", tdrain, ["Proposed"], dx="0", dy="0"), flipped), flipped,
                         "Proposed: fitted", "As given")
        if tail is None:
            node.next = moved
        else:
            tail.next = moved
        # the terminal drain ends on the ground already: there is no link to draw, the drain's end is the daylight point
        on_ground = Decision(f"{ap}.IsValid AndAlso Math.Abs({ap}.Y-{frm}.Y)<0.001",
                             Raw(gen_point(f"P{360 + e}", "DeltaXAndDeltaY", frm, ["Daylight"], dx="0", dy="0")), node, "On the ground", "Daylight link")
        # layout mode: ground below, so the assembly shows the same 5 m daylight link as the original
        return Raw(gen_point(ap, "DeltaXOnSurface", frm, [], aux=True, layout_dy="-1"), on_ground)

    def walk(node, stage, ctx):
        """ctx: tdrain = first bottom point of the terminal drain of this branch, conn = its cascade link already seen."""
        if node is None:
            return None
        if isinstance(node, Decision):
            cond = node.condition
            if re.search(r"TBenchFillWidth, ?TBenchCutWidth", cond) and not os.environ.get("BENCH_NO_A1"):
                # "> 0" is ground above = cut: the cut width belongs first, as in every geometry expression
                cond = re.sub(r"TBenchFillWidth, ?TBenchCutWidth", "TBenchCutWidth,TBenchFillWidth", cond)
                STATS["cond_swapped"] += 1
            if "Bench1 + Terminal" in node.true_label:
                cond = cond.replace("(Math.Abs(BenchWidthIn * BenchGradeIn) + Math.Abs(BenchWidthOut * BenchGradeOut))",
                                    "((BenchWidthIn * BenchGradeIn) + (BenchWidthOut * BenchGradeOut))")
            node.condition = use_fit(cond)
            node.true, node.false = walk(node.true, stage, dict(ctx)), walk(node.false, stage, dict(ctx))
            return node
        el = node.el
        if node.kind == "var" and el.get("VariableName") in REMOVED_VARS:
            return walk(node.next, stage, ctx)
        texts[node.aid] = ABS_RE.sub(lambda m: FIT_VAR[stage_of(m)], texts[node.aid])
        if node.kind == "aux" and re.fullmatch(r"AP[1-7]", el.get("PointNumber")):
            k = int(el.get("PointNumber")[2:]) - 1
            STATS["fit_stages"] += 1
            if k == 0:
                # AP1 is followed by the variable definitions; the fit goes behind them, in front of the first decision
                last = node
                while isinstance(last.next, Step) and last.next.kind == "var":
                    if last.next.el.get("VariableName") in REMOVED_VARS:
                        last.next = last.next.next
                    else:
                        last = last.next
                last.next = fit_steps(0, walk(last.next, 0, ctx))
                return node
            node.next = fit_steps(k, walk(node.next, k, ctx))
            return node
        pid = el.get("PointNumber") if node.kind in ("point", "aux") else None
        if pid == "P290":
            assert 'FromPoint="{x:Null}"' in texts[node.aid]
            texts[node.aid] = texts[node.aid].replace('FromPoint="{x:Null}"', 'FromPoint="P233"')
        if node.kind == "point":
            codes = code_list(el, ".PointCodes")
            positioning = el.get("Positioning")
            slope = "".join(a.text or "" for c in el if c.tag.endswith(".Arguments") for a in c if (a.get(X + "Key") or "").startswith("Slope"))
            # the terminal bench laid at its grade to the ground: a bench link, and its end is where the drain starts
            if positioning == "SlopeToSurface" and "TBenchGrade" in slope:
                texts[node.aid] = set_codes(set_codes(texts[node.aid], "AutoLinkCodes", ["Top"]), "PointCodes", ["TBenchDrainIn"])
            # terminal drain: first bottom point (DRAIN_FLOWLINE after a -TDrainDepth drop)
            dy = "".join(a.text or "" for c in el if c.tag.endswith(".Arguments") for a in c if (a.get(X + "Key") or "").startswith("DeltaY"))
            dx = "".join(a.text or "" for c in el if c.tag.endswith(".Arguments") for a in c if (a.get(X + "Key") or "").startswith("DeltaX"))
            if dy == "[-TDrainDepth]":
                ctx["tdrain"] = pid
                texts[node.aid] = set_codes(texts[node.aid], "AutoLinkCodes", ["Top", "Drain"])
            elif dx == "[TDrainWidthBottom]":
                texts[node.aid] = set_codes(texts[node.aid], "AutoLinkCodes", ["Top", "Drain"])
            elif dx == "[TDrainWidthOut]":
                texts[node.aid] = set_codes(set_codes(texts[node.aid], "AutoLinkCodes", ["Top", "Drain"]), "PointCodes", ["TBenchDrainOut"])
            if positioning == "SlopeToSurface" and "DaylightSlope*SlopeDirectionCorrection" in slope:
                assert ctx.get("tdrain"), pid
                # cascade: the terminal drain joins the last berm drain; without benches, in cut, it joins P1
                if not ctx.get("conn"):
                    if stage == 0:
                        return new_cascade("P1", ctx["tdrain"], "CascadeDrain=Yes AndAlso SlopeDirectionCorrection>0", ending(node, stage, ctx["tdrain"]))
                    return new_cascade(BENCH_DRAIN[min(stage, 5) - 1], ctx["tdrain"], "CascadeDrain=Yes", ending(node, stage, ctx["tdrain"]))
                return ending(node, stage, ctx["tdrain"])
            if el.get("AutoLink") == "True" and not code_list(el, ".AutoLinkCodes") and "AutoLinkCodes>" not in texts[node.aid].replace('AutoLinkCodes="', ""):
                texts[node.aid] = set_codes(texts[node.aid], "AutoLinkCodes", ["Top"])      # L238, the one slope link without a code
                STATS.setdefault("uncoded_links_fixed", []).append(el.get("AutoLinkGeometryName"))
            nxt = walk(node.next, stage, ctx)
            # cascade: each berm drain joins the one below it; berm drain 1 joins P1 in cut
            if pid in BENCH_DRAIN:
                j = BENCH_DRAIN.index(pid)
                nxt = new_cascade("P1", pid, "CascadeDrain=Yes AndAlso SlopeDirectionCorrection>0", nxt) if j == 0 \
                    else new_cascade(BENCH_DRAIN[j - 1], pid, "CascadeDrain=Yes", nxt)
            node.next = nxt
            return node
        if node.kind == "link" and not code_list(el, ".LinkCodes") and el.get("EndPoint") == ctx.get("tdrain"):
            # the original's connector (last berm drain -> terminal drain): now coded, and only with CascadeDrain
            ctx["conn"] = True
            texts[node.aid] = set_codes(texts[node.aid], "LinkCodes", ["CascadeDrain"])
            return cascade(texts[node.aid], "CascadeDrain=Yes", walk(node.next, stage, ctx))
        node.next = walk(node.next, stage, ctx)
        return node

    return walk(ir, 0, {})


# ------------------------------------------------------------------------------------------------ emit
REFS = []


def emit(node, texts, memo):
    if id(node) in memo:
        return f"<x:Reference>{memo[id(node)]}</x:Reference>"
    name = f"__ReferenceID{len(REFS)}"
    REFS.append(name)
    memo[id(node)] = name
    y = 100 + 40 * len(REFS)
    if isinstance(node, Decision):
        out = (f'<FlowDecision x:Name="{name}" Condition="{esc("[" + node.condition + "]")}" sap:VirtualizedContainerService.HintSize="70,87">'
               '<sap:WorkflowViewStateService.ViewState><scg:Dictionary x:TypeArguments="x:String, x:Object">'
               f'<x:Boolean x:Key="IsExpanded">True</x:Boolean><av:Point x:Key="ShapeLocation">265,{y}</av:Point>'
               f'<av:Size x:Key="ShapeSize">70,87</av:Size><x:String x:Key="TrueLabel">{esc(node.true_label)}</x:String>'
               f'<x:String x:Key="FalseLabel">{esc(node.false_label)}</x:String></scg:Dictionary></sap:WorkflowViewStateService.ViewState>')
        if node.true is not None:
            out += "<FlowDecision.True>" + emit(node.true, texts, memo) + "</FlowDecision.True>"
        if node.false is not None:
            out += "<FlowDecision.False>" + emit(node.false, texts, memo) + "</FlowDecision.False>"
        return out + "</FlowDecision>\n"
    body = node.build if isinstance(node, Raw) else texts[node.aid]
    out = (f'<FlowStep x:Name="{name}"><sap:WorkflowViewStateService.ViewState><scg:Dictionary x:TypeArguments="x:String, x:Object">'
           f'<av:Point x:Key="ShapeLocation">200,{y}</av:Point><av:Size x:Key="ShapeSize">200,22</av:Size>'
           '</scg:Dictionary></sap:WorkflowViewStateService.ViewState>\n' + body + "\n")
    if node.next is not None:
        out += "<FlowStep.Next>" + emit(node.next, texts, memo) + "</FlowStep.Next>"
    return out + "</FlowStep>\n"


def build_xaml(src):
    root = ET.fromstring(src)
    texts = activity_text(src)
    tree = transform(to_ir(root), texts)

    head = src[:src.index("<Flowchart.StartNode>")]
    add_defaults = "".join(f' this:Subassembly.{n}="[new EnumType({YESNO_VALUE[df]}, &quot;{df}&quot;, &quot;YesNo&quot;)]"' for n, _, _, df in YESNO_PARAMS)
    add_defaults += "".join(f' this:Subassembly.{n}="{df}"' for n, _, _, df in DOUBLE_PARAMS)
    marker = ' this:Subassembly.SurfaceTarget='
    assert head.count(marker) == 1
    head = head.replace(marker, add_defaults + marker, 1)
    members = ""
    for name, display, desc, _ in YESNO_PARAMS:
        members += (f'    <x:Property Name="{name}" Type="InArgument(asw:EnumType)">\n      <x:Property.Attributes>\n'
                    f'        <asw:EnabledFlag2Attribute EnabledFlag="True" />\n'
                    f'        <asw:DisplayName2Attribute DisplayName="{esc(display)}" />\n'
                    f'        <asw:Description2Attribute Description="{esc(desc)}" />\n'
                    f'      </x:Property.Attributes>\n    </x:Property>\n')
    for name, display, desc, _ in DOUBLE_PARAMS:
        members += (f'    <x:Property Name="{name}" Type="InArgument(x:Double)">\n      <x:Property.Attributes>\n'
                    f'        <asw:EnabledFlag2Attribute EnabledFlag="True" />\n'
                    f'        <asw:DisplayName2Attribute DisplayName="{esc(display)}" />\n'
                    f'        <asw:Description2Attribute Description="{esc(desc)}" />\n'
                    f'      </x:Property.Attributes>\n    </x:Property>\n')
    at = head.index('    <x:Property Name="SurfaceTarget"')
    head = head[:at] + members + head[at:]
    for v in REMOVED_VARS:
        head = re.sub(rf'\s*<Variable x:TypeArguments="x:\w+" Name="{v}" />', "", head)
    decl = "".join(f'\n      <Variable x:TypeArguments="x:Double" Name="{v}" />' for v in OLD_VAR + FIT_VAR + PASS_VARS)
    anchor = '<Variable x:TypeArguments="x:Int32" Name="SlopeDirectionCorrection" />'
    assert head.count(anchor) == 1
    head = head.replace(anchor, anchor + decl, 1)

    body = emit(tree, texts, {})
    refs = "\n".join(f"    <x:Reference>{r}</x:Reference>" for r in REFS)
    return "\ufeff" + head + "<Flowchart.StartNode>\n" + body + "    </Flowchart.StartNode>\n" + refs + "\n  </Flowchart>\n</Activity>"


def build_atc(src_atc):
    out = src_atc
    out = re.sub(r'<DotNetClass Assembly="[0-9a-f]+\.xaml">', f'<DotNetClass Assembly="{GUID}.xaml">', out)
    out = re.sub(r"<Version>[^<]*</Version>", f"<Version>{VERSION}</Version>", out, count=1)
    out = re.sub(r"<Description>[^<]*</Description>", f"<Description>{esc(DESCRIPTION)}</Description>", out, count=1)
    out = re.sub(r"<ToolTip>[^<]*</ToolTip>", f"<ToolTip>Bench and Berm Drain with Terminal Bench Drain assembly\\nVersion: {VERSION}</ToolTip>", out, count=1)
    out = re.sub(r' src="[^"]*"', "", out)
    out = re.sub(r'idValue="\{[0-9a-fA-F-]{36}\}"', lambda m: f'idValue="{{{uuid.uuid4()}}}"', out, count=2)
    extra = "".join(f'            <{n} DataType="long" TypeInfo="16" DisplayName="{esc(d)}" Description="{esc(ds)}">{YESNO_VALUE[df]}'
                    f'<Enum><Yes DisplayName="Yes">10</Yes><No DisplayName="No">11</No></Enum></{n}>\n' for n, d, ds, df in YESNO_PARAMS)
    extra += "".join(f'            <{n} DataType="double" TypeInfo="16" DisplayName="{esc(d)}" Description="{esc(ds)}">{df}</{n}>\n' for n, d, ds, df in DOUBLE_PARAMS)
    assert out.count("          </Params>") == 1
    return out.replace("          </Params>", extra + "          </Params>", 1)


def build_emd(src_emd):
    out = src_emd
    for v in REMOVED_VARS:
        out = re.sub(rf"\s*<string>{v}</string>", "", out)
    add = "".join(f"\n      <string>{v}</string>" for v in OLD_VAR + FIT_VAR + PASS_VARS)
    return out.replace("<string>SlopeDirectionCorrection</string>", "<string>SlopeDirectionCorrection</string>" + add, 1)


def main():
    stem, files = read_source()
    xaml = build_xaml(files[stem + ".xaml"])
    ET.fromstring(xaml.lstrip("\ufeff"))
    atc = build_atc(files[stem + ".atc"])
    out_files = [(f"{GUID}.atc", atc), ("[Content_Types].xml", files["[Content_Types].xml"]), (f"{GUID}.cfg", files[stem + ".cfg"]),
                 (f"{GUID}.xaml", xaml), (f"{GUID}.pvd", files[stem + ".pvd"]), (f"{GUID}.emd", build_emd(files[stem + ".emd"])),
                 (f"{GUID}.cdmd", files[stem + ".cdmd"])]
    os.makedirs(OUT_DIR, exist_ok=True)
    out = os.path.join(OUT_DIR, f"{FILE}.pkt")
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for name, text in out_files:
            data = text.encode("utf-8")
            if name == "[Content_Types].xml" and not data.startswith(b"\xef\xbb\xbf"):
                data = b"\xef\xbb\xbf" + data
            z.writestr(name, data)
    print("guid", GUID, "flow nodes", len(REFS), STATS)
    print("wrote", out, os.path.getsize(out), "bytes")


if __name__ == "__main__":
    main()
