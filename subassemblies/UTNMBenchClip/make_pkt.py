"""
Generate UTNMBenchClip.pkt from UTNMBench v0.2 (../UTNMBench/UTNMBench_v0.2.pkt, itself generated from the original
UTNMBench-C3D25-DrainConnect.pkt), adding the bowtie clip targets.

The source package is NOT re-written by hand. Its flowchart is read, every activity is kept VERBATIM (same point and link
numbers, codes, expressions, decisions), and the only changes are:

  * Two optional targets:  ClipTarget (offset)  and  ClipElev (elevation).
  * In front of every link-making point a decision is inserted: "is the clip line found at this station, and would this
    link pass it?".  Yes -> the link stops at the clip offset (on its own slope, or at the ClipElev level where that target
    is found), its end is coded  Valley, the link gets the code  Clip  on top of its own codes, and the subassembly ENDS:
    no later bench, drain, terminal bench or daylight is drawn.  No -> the original activity runs, untouched.
    With no clip target mapped every such decision is False, so the result is the source part exactly.
  * Optional lane in front (LaneWidth, default 0 = none; LaneSlope): so ONE clip target also stops the lane at an angle
    point. With a lane, P1 (the source part's start point) sits at the lane end; the attachment point is P900 (Composer does not resolve P0 or four-digit names in expressions).
  * Nested flowcharts and sequences of the source are flattened into one flowchart, so that "end" really ends the part.

Usage:  python make_pkt.py [out_dir] [source_pkt]
"""
import os, re, sys, uuid, zipfile
sys.setrecursionlimit(20000)
import xml.etree.ElementTree as ET

NAME = os.environ.get("BENCH_NAME", "UTNMBenchClip")
VERSION = "0.3"
HERE = os.path.dirname(os.path.abspath(__file__))
OUT_DIR = sys.argv[1] if len(sys.argv) > 1 else HERE
SOURCE = sys.argv[2] if len(sys.argv) > 2 else os.path.join(HERE, "..", "UTNMBench", "UTNMBench_v0.2.pkt")
GUID = uuid.uuid4().hex
DESCRIPTION = ("UTNMBench (bench and berm drains with terminal bench drain) plus optional bowtie clip targets: where the "
               "ClipTarget offset target is crossed the current link stops at the clip line and ends in point code Valley, "
               "on its own slope or at the level of the optional ClipElev elevation target (map the same valley feature "
               "lines on both). Optional lane in front. Without targets it is UTNMBench.")

NS_A = "http://schemas.microsoft.com/netfx/2009/xaml/activities"
NS_S = "clr-namespace:Autodesk.SubassemblyComposer.ActivityLibrary;assembly=Subassembly.ActivityLibrary"
NS_X = "http://schemas.microsoft.com/winfx/2006/xaml"
A, S, X = "{%s}" % NS_A, "{%s}" % NS_S, "{%s}" % NS_X

EXTRA_PARAMS = [  # name, type, display, description, default
    ("LaneWidth", "double", "Lane Width", "Width of a lane in front of the benching (0 = no lane)", 0.0),
    ("LaneSlope", "grade", "Lane Slope", "Lane cross slope. Positive slopes upward in the direction of increasing offset", 0.0),
]
TYPE_XAML = {"double": "x:Double", "slope": "asw:Slope", "grade": "asw:Grade"}
TYPE_INFO = {"double": 16, "slope": 10, "grade": 9}


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
    """A generated activity (text builder) followed by next."""
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
    seen = {}          # flow node name -> IR node: v0.2 joins branches again (a decision whose two sides continue at one node)

    def activity(el, cont):
        t = el.tag
        if t == S + "CreatePoint":
            return Step("point", el.get("ActivityId"), el, cont)
        if t == S + "CreateAuxPoint":
            return Step("aux", el.get("ActivityId"), el, cont)
        if t == S + "CreateLink":
            return Step("link", el.get("ActivityId"), el, cont)
        if t == S + "InternalVariableDefine":
            return Step("var", el.get("ActivityId"), el, cont)
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
        if n and n in seen:
            return seen[n]
        if el.tag == A + "FlowStep":
            nx = el.find(A + "FlowStep.Next")
            after = node_ir(list(nx)[0], cont) if nx is not None and len(nx) else cont
            acts = [c for c in el if c.tag.startswith(S) or c.tag in (A + "Sequence", A + "Flowchart")]
            out = activity(acts[0], after) if acts else after
        elif el.tag == A + "FlowDecision":
            lb = labels(el)
            tb, fb = el.find(A + "FlowDecision.True"), el.find(A + "FlowDecision.False")
            cond = el.get("Condition")
            out = Decision(cond[1:-1] if cond.startswith("[") else cond,
                           node_ir(list(tb)[0], cont) if tb is not None and len(tb) else cont,
                           node_ir(list(fb)[0], cont) if fb is not None and len(fb) else cont,
                           lb.get("TrueLabel") or "True", lb.get("FalseLabel") or "False")
        else:
            raise ValueError("unknown flow node " + el.tag)
        if n:
            seen[n] = out
        return out

    main = root.find(A + "Flowchart")
    return node_ir(list(main.find(A + "Flowchart.StartNode"))[0], None)


def activity_text(xaml):
    """ActivityId -> the activity's XML text, verbatim (none of these elements nests in itself)."""
    out = {}
    for tag in ("CreatePoint", "CreateAuxPoint", "CreateLink", "InternalVariableDefine"):
        for m in re.finditer(rf"<asa2:{tag} [^>]*?ActivityId=\"(\d+)\".*?</asa2:{tag}>", xaml, re.S):
            out[m.group(1)] = m.group(0)
    return out


def args_of(el):
    out = {}
    for c in el:
        if c.tag.endswith(".Arguments"):
            for a in c:
                out[re.sub(r"\d+$", "", a.get(X + "Key"))] = a.text
    return out


def code_list(el, suffix):
    for c in el:
        if c.tag.endswith(suffix):
            return [a.text for a in c.iter(A + "InArgument")]
    return []


def unbracket(v):
    v = (v or "0").strip()
    return v[1:-1] if v.startswith("[") and v.endswith("]") else v


# ------------------------------------------------------------------------------------------------ generated activities
COMMON = ('Geometry="[Geometry]" sap:VirtualizedContainerService.HintSize="200,22" ShowErrors="True" '
          'SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]"')
VIEWSTATE = ('<sap:WorkflowViewStateService.ViewState><scg:Dictionary x:TypeArguments="x:String, x:Object">'
             '<x:Boolean x:Key="IsExpanded">True</x:Boolean></scg:Dictionary></sap:WorkflowViewStateService.ViewState>')


class Ids:
    activity = 1000
    ref = 0

    @classmethod
    def act(cls):
        cls.activity += 1
        return cls.activity


def codes_xml(tag, codes):
    items = "".join(f'<InArgument x:TypeArguments="x:String">{esc(c)}</InArgument>' for c in codes)
    return f'<{tag}><scg:List x:TypeArguments="InArgument(x:String)" Capacity="4">{items}</scg:List></{tag}>'


def pos_args(positioning, **kw):
    if positioning == "DeltaXAndDeltaY":
        return [("x:Double", "DeltaX1", kw["dx"]), ("x:Double", "DeltaY1", kw["dy"])]
    if positioning == "SlopeAndDeltaX":
        return [("x:Double", "Slope2", kw["slope"]), ("x:Double", "DeltaX2", kw["dx"]),
                ("asw:OffsetTarget", "OffsetTarget2", None), ("asw:ElevationTarget", "ElevationTarget2", None),
                ("asw:CrossSegmentType", "Superelevation2", None)]
    if positioning == "SlopeToSurface":
        return [("x:Double", "Slope3", kw["slope"]), ("x:Boolean", "ReverseSlopeDirection3", "False"),
                ("asw:SurfaceTarget", "SurfaceTarget3", "[SurfaceTarget]"),
                ("x:Double", "DeltaXForLayout3", "5"), ("x:Boolean", "ShowErrors", "False")]
    raise ValueError(positioning)


def gen_point(pid, positioning, frm, codes, aux=False, link=None, **kw):
    """link = (name, codes) makes it an auto-linked point, as the source part does."""
    aid = Ids.act()
    tag = "asa2:CreateAuxPoint" if aux else "asa2:CreatePoint"
    rows = "".join(f'<InArgument x:TypeArguments="{t}" x:Key="{k}" />' if v is None else
                   f'<InArgument x:TypeArguments="{t}" x:Key="{k}">{esc(v)}</InArgument>' for t, k, v in pos_args(positioning, **kw))
    attrs = f'FromPoint="{frm}" ' if frm else 'FromPoint="{x:Null}" '
    if link:
        attrs += f'AutoLink="True" AutoLinkGeometryName="{link[0]}" '
    else:
        attrs += 'AutoLinkCodes="{x:Null}" AutoLinkGeometryName="{x:Null}" AutoLink="False" '
    if aux or not codes:
        attrs += 'PointCodes="{x:Null}" '
    body = f'<{tag}.Arguments>{rows}</{tag}.Arguments>'
    if link:
        body += codes_xml(f"{tag}.AutoLinkCodes", link[1])
    if codes and not aux:
        body += codes_xml(f"{tag}.PointCodes", codes)
    display = pid + (f"&amp;{link[0]}" if link else "")
    return (f'<{tag} {attrs}ActivityId="{aid}" ApplyAOR="False" DisplayName="{display}" {COMMON} '
            f'PointNumber="{pid}" Positioning="{positioning}" Side="[Side]">{body}{VIEWSTATE}</{tag}>')


def gen_link(lid, a, b, codes):
    return (f'<asa2:CreateLink ActivityId="{Ids.act()}" ApplyAOR="False" DisplayName="{lid}" EndPoint="{b}" {COMMON} '
            f'IsEnabled="True" LinkNumber="{lid}" StartPoint="{a}">' + codes_xml("asa2:CreateLink.LinkCodes", codes)
            + VIEWSTATE + '</asa2:CreateLink>')


# ------------------------------------------------------------------------------------------------ the clip
SGN = "Math.Sign(AP900.Offset - P900.Offset)"
CLIPD = f"((ClipTarget.Offset - P900.Offset) * {SGN})"      # clip distance from the attachment point, + = outward


def dist(p):
    return "0.0" if p == "P900" else f"(({p}.Offset - P900.Offset) * {SGN})"


def clip_dy(frm, own_rise):
    return f"If(ClipElev.IsValid, ClipElev.Elevation - {frm}.Elevation, {own_rise})"


STATS = {"clip_checks": 0, "skipped": []}
SEQ = {"ap": 400, "v": 500}    # new names stay within three digits and clear of UTNMBench v0.2 (P1-P380, L131-L331, AP1-AP320)


CLIPPED = {}


def add_clip(node):
    """Walk the IR; put a clip decision in front of every point that makes a link. A node reached twice is handled once."""
    if node is None:
        return None
    if id(node) not in CLIPPED:
        CLIPPED[id(node)] = _add_clip(node)
    return CLIPPED[id(node)]


def _add_clip(node):
    if isinstance(node, Decision):
        node.true, node.false = add_clip(node.true), add_clip(node.false)
        return node
    node.next = add_clip(node.next)
    if not isinstance(node, Step) or node.kind != "point":
        return node
    el = node.el
    pid, frm, positioning = el.get("PointNumber"), el.get("FromPoint"), el.get("Positioning")
    if not frm or frm == "{x:Null}":
        return node
    a = args_of(el)
    n = int(pid[1:])
    auto = el.get("AutoLink") == "True"
    link_codes = code_list(el, ".AutoLinkCodes") if auto else None
    if not auto:   # an explicit CreateLink frm -> pid right after it?
        nx = node.next
        if isinstance(nx, Step) and nx.kind == "link" and nx.el.get("StartPoint") == frm and nx.el.get("EndPoint") == pid:
            link_codes = code_list(nx.el, ".LinkCodes")
    if link_codes is None:
        STATS["skipped"].append(pid)        # a marker point (no link): nothing to clip
        return node

    dx = f"Math.Max(0.0, {CLIPD} - {dist(frm)})"
    pre = None
    if positioning == "DeltaXAndDeltaY":
        DX, DY = unbracket(a["DeltaX"]), unbracket(a["DeltaY"])
        reach = f"{dist(frm)} + ({DX})"
        rise = f"If(({DX}) > 0.000001, ({DY}) * {dx} / ({DX}), 0.0)"
    elif positioning == "SlopeAndDeltaX":
        SL, DX = unbracket(a["Slope"]), unbracket(a["DeltaX"])
        reach = f"{dist(frm)} + ({DX})"
        rise = f"({SL}) * 1.0 * {dx}"
    elif positioning == "SlopeAndDeltaY":
        SL, DY = unbracket(a["Slope"]), unbracket(a["DeltaY"])
        reach = f"{dist(frm)} + Math.Abs(({DY}) * 1.0 / (({SL}) * 1.0))"
        rise = f"({SL}) * 1.0 * {dx}"
    elif positioning == "SlopeToSurface":
        SL = unbracket(a["Slope"])
        SEQ["ap"] += 1
        ap = f"AP{SEQ['ap']}"
        pre = gen_point(ap, "SlopeToSurface", frm, [], aux=True, slope=f"[{SL}]")
        reach = None
        rise = f"({SL}) * 1.0 * {dx}"
    else:
        raise ValueError(f"{pid}: positioning {positioning} is not handled")

    if reach is None:
        cond = f"ClipTarget.IsValid AndAlso ((Not {ap}.IsValid) OrElse {CLIPD} < {dist(ap)} - 0.0001)"
    else:
        cond = f"ClipTarget.IsValid AndAlso {CLIPD} < {reach} - 0.0001"
    SEQ["v"] += 1
    v, l = f"P{SEQ['v']}", f"L{SEQ['v'] + 100}"
    codes = list(link_codes) + [c for c in ("Clip",) if c not in link_codes]
    valley = Raw(gen_point(v, "DeltaXAndDeltaY", frm, ["Valley"], link=(l, codes), dx=f"[{dx}]", dy=f"[{clip_dy(frm, rise)}]"))
    STATS["clip_checks"] += 1
    d = Decision(cond, valley, node, f"Clip before {pid}", pid)
    return Raw(pre, d) if pre else d


def front(flow):
    """P900 (attachment), side probe, clip behind the start, optional lane; P1 of the source becomes the lane end."""
    # P1 is made once (a second CreatePoint of the same name, even in another branch, is not something Composer allows);
    # without a lane it coincides with P900 and no lane link is drawn
    lane_link = Decision("LaneWidth > 0.0001", Raw(gen_link("L900", "P900", "P1", ["Top", "Datum"]), flow), SHARED, "Lane link", "No lane")
    p1 = Raw(gen_point("P1", "SlopeAndDeltaX", "P900", ["Top"], slope="[LaneSlope * 1.0]", dx="[LaneWidth]"), lane_link)
    lane_clip = Raw(gen_point("P902", "DeltaXAndDeltaY", "P900", ["Valley"], link=("L902", ["Top", "Datum", "Clip"]),
                              dx=f"[{CLIPD}]", dy=f"[{clip_dy('P900', '(LaneSlope * 1.0) * ' + CLIPD)}]"))
    has_lane = Decision(f"LaneWidth > 0.0001 AndAlso ClipTarget.IsValid AndAlso {CLIPD} < LaneWidth - 0.0001", lane_clip, p1, "Clip in lane", "Build")
    dbg = os.environ.get("BENCH_FRONT", "")
    if dbg == "f1":
        return Raw(gen_point("P900", "DeltaXAndDeltaY", None, [], dx="0", dy="0"),
                   Raw(gen_point("AP900", "DeltaXAndDeltaY", "P900", [], aux=True, dx="1", dy="0"),
                       Raw(gen_point("P1", "DeltaXAndDeltaY", "P900", ["Top"], dx="0", dy="0"), flow)))
    if dbg == "f3":
        p1 = Raw(gen_point("P1", "SlopeAndDeltaX", "P900", ["Top"], slope="[LaneSlope * 1.0]", dx="[LaneWidth]"),
                 Raw(gen_link("L900", "P900", "P1", ["Top", "Datum"]), flow))
        has_lane = Decision(f"LaneWidth > 0.0001 AndAlso ClipTarget.IsValid AndAlso {CLIPD} < LaneWidth - 0.0001", lane_clip, p1, "Clip in lane", "Build")
    behind = Raw(gen_point("P901", "DeltaXAndDeltaY", "P900", ["Valley"], dx="0", dy="0"))
    if dbg == "f2":
        has_lane = Raw(gen_point("P1", "DeltaXAndDeltaY", "P900", ["Top"], dx="0", dy="0"), flow)
    start = Decision(f"ClipTarget.IsValid AndAlso {CLIPD} <= 0.001", behind, has_lane, "Clip behind start", "Build")
    probe = Raw(gen_point("AP900", "DeltaXAndDeltaY", "P900", [], aux=True, dx="1", dy="0"), start)
    return Raw(gen_point("P900", "DeltaXAndDeltaY", None, [], dx="0", dy="0"), probe)


SHARED = object()   # placeholder: "continue with the source flow" (emitted once, referenced the second time)


# ------------------------------------------------------------------------------------------------ emit
REFS = []


def emit(node, texts, flow_after_p1, memo):
    if node is SHARED:
        node = flow_after_p1
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
            out += "<FlowDecision.True>" + emit(node.true, texts, flow_after_p1, memo) + "</FlowDecision.True>"
        if node.false is not None:
            out += "<FlowDecision.False>" + emit(node.false, texts, flow_after_p1, memo) + "</FlowDecision.False>"
        return out + "</FlowDecision>\n"
    body = node.build if isinstance(node, Raw) else texts[node.aid]
    out = (f'<FlowStep x:Name="{name}"><sap:WorkflowViewStateService.ViewState><scg:Dictionary x:TypeArguments="x:String, x:Object">'
           f'<av:Point x:Key="ShapeLocation">200,{y}</av:Point><av:Size x:Key="ShapeSize">200,22</av:Size>'
           '</scg:Dictionary></sap:WorkflowViewStateService.ViewState>\n' + body + "\n")
    if node.next is not None:
        out += "<FlowStep.Next>" + emit(node.next, texts, flow_after_p1, memo) + "</FlowStep.Next>"
    return out + "</FlowStep>\n"


def build_xaml(src):
    root = ET.fromstring(src)
    texts = activity_text(src)
    ir = to_ir(root)
    # the source starts with P1 (origin): drop that step, P1 is made by the front part instead
    assert isinstance(ir, Step) and ir.el.get("PointNumber") == "P1", "the source flow should start with P1"
    flow = ir.next
    # (the repair of P290 is made in UTNMBench v0.2 itself)
    mode = os.environ.get("BENCH_MODE", "full")            # debugging: flat = flatten only, front = no clip checks
    if mode == "flat":
        flow = ir
        tree = ir
    else:
        if mode != "front":
            flow = add_clip(flow)
        tree = front(flow)

    head_end = src.index("<Flowchart.StartNode>")
    head = src[:head_end]
    # defaults on <Activity ...>
    add_defaults = ""
    for name, typ, _, _, default in EXTRA_PARAMS:
        add_defaults += (f' this:Subassembly.{name}="[new Grade({default})]"' if typ == "grade" else f' this:Subassembly.{name}="{default}"')
    add_defaults += (' this:Subassembly.ClipTarget="[new PreviewOffsetTarget(&quot;ClipTarget&quot;, False, 6)]"'
                     ' this:Subassembly.ClipElev="[new PreviewElevationTarget(&quot;ClipElev&quot;, False, 1)]"')
    marker = ' this:Subassembly.SurfaceTarget='
    assert head.count(marker) == 1
    head = head.replace(marker, add_defaults + marker, 1)
    members = ""
    for name, typ, display, desc, _ in EXTRA_PARAMS:
        members += (f'    <x:Property Name="{name}" Type="InArgument({TYPE_XAML[typ]})">\n      <x:Property.Attributes>\n'
                    f'        <asw:EnabledFlag2Attribute EnabledFlag="True" />\n'
                    f'        <asw:DisplayName2Attribute DisplayName="{esc(display)}" />\n'
                    f'        <asw:Description2Attribute Description="{esc(desc)}" />\n'
                    f'      </x:Property.Attributes>\n    </x:Property>\n')
    for name, typ, display in (("ClipTarget", "asw:OffsetTarget", "Clip Offset Target"), ("ClipElev", "asw:ElevationTarget", "Clip Level Target")):
        members += (f'    <x:Property Name="{name}" Type="InArgument({typ})">\n      <x:Property.Attributes>\n'
                    f'        <asw:EnabledFlag2Attribute EnabledFlag="True" />\n'
                    f'        <asw:DisplayName2Attribute DisplayName="{display}" />\n'
                    f'      </x:Property.Attributes>\n    </x:Property>\n')
    assert head.count("  </x:Members>") == 1
    head = head.replace("  </x:Members>", members + "  </x:Members>", 1)

    body = emit(tree, texts, flow, {})
    refs = "\n".join(f"    <x:Reference>{r}</x:Reference>" for r in REFS)
    return "﻿" + head + "<Flowchart.StartNode>\n" + body + "    </Flowchart.StartNode>\n" + refs + "\n  </Flowchart>\n</Activity>"


def build_atc(src_atc):
    out = src_atc
    out = out.replace('<Tool Name="UTNMBench">', f'<Tool Name="{NAME}">').replace("<ItemName>UTNMBench</ItemName>", f"<ItemName>{NAME}</ItemName>")
    out = re.sub(r'<DotNetClass Assembly="[0-9a-f]+\.xaml">Subassembly\.UTNMBench</DotNetClass>',
                 f'<DotNetClass Assembly="{GUID}.xaml">Subassembly.{NAME}</DotNetClass>', out)
    out = re.sub(r"<Version>[^<]*</Version>", f"<Version>{VERSION}</Version>", out, count=1)
    out = re.sub(r"<Description>[^<]*</Description>", f"<Description>{esc(DESCRIPTION)}</Description>", out, count=1)
    out = re.sub(r"<ToolTip>[^<]*</ToolTip>", f"<ToolTip>UTNMBench + bowtie clip targets\\nVersion: {VERSION}</ToolTip>", out, count=1)
    out = re.sub(r' src="[^"]*"', "", out)                       # the icon path of the author's temp folder
    out = re.sub(r'idValue="\{[0-9a-fA-F-]{36}\}"', lambda m: f'idValue="{{{uuid.uuid4()}}}"', out, count=2)   # category + tool ids; the stock tool ref stays
    extra = "".join(f'            <{n} DataType="double" TypeInfo="{TYPE_INFO[t]}" DisplayName="{esc(d)}" Description="{esc(ds)}">{df}</{n}>\n'
                    for n, t, d, ds, df in EXTRA_PARAMS)
    assert out.count("          </Params>") == 1
    return out.replace("          </Params>", extra + "          </Params>", 1)


def main():
    stem, files = read_source()
    xaml = build_xaml(files[stem + ".xaml"])
    ET.fromstring(xaml.lstrip("﻿"))                          # well-formed?
    atc = build_atc(files[stem + ".atc"])
    assert "StockToolRef idValue=\"{7F55AAC0-0256-48D7-BFA5-914702663FDE}\"" in atc
    out_files = [(f"{GUID}.atc", atc), ("[Content_Types].xml", files["[Content_Types].xml"]), (f"{GUID}.cfg", files[stem + ".cfg"]),
                 (f"{GUID}.xaml", xaml), (f"{GUID}.pvd", files[stem + ".pvd"]), (f"{GUID}.emd", files[stem + ".emd"]),
                 (f"{GUID}.cdmd", files[stem + ".cdmd"])]
    os.makedirs(OUT_DIR, exist_ok=True)
    out = os.path.join(OUT_DIR, f"{NAME}.pkt")
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for name, text in out_files:
            data = text.encode("utf-8")
            if name == "[Content_Types].xml" and not data.startswith(b"\xef\xbb\xbf"):
                data = b"\xef\xbb\xbf" + data
            z.writestr(name, data)
    print("guid", GUID, "flow nodes", len(REFS), "clip checks", STATS["clip_checks"], "marker points left alone", STATS["skipped"])
    print("wrote", out, os.path.getsize(out), "bytes")


if __name__ == "__main__":
    main()
