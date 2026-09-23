"""
Generate UTNM_LaneDaylightClip.pkt - a Subassembly Composer 2026 package: an optional lane, then DaylightBench
behaviour, with an optional clip line (offset target) for fixing corridor bowties on the inside of bends.
Successor of UTNM_DaylightBenchClip (v0.1): same daylight logic, plus

  * Optional lane (LaneWidth > 0, LaneSlope): replaces a stock LinkWidthAndSlope in front of the daylight, so ONE clip
    target stops the lane, the slopes and the benches. A clip inside the lane ends the lane at the clip (point code
    Valley); a clip behind the attachment point gives a zero-length result - never a negative lane width.
  * Hinge codes as the stock DaylightBench: Hinge + Hinge_Cut | Hinge_Fill.

Behaviour:
  * P1 = attachment point (no code, as the start of a stock lane). Lane end point code P2, lane link Top/Datum.
  * The hinge is the lane end (or P1 without a lane). Cut or fill is decided once, from the target surface at the
    hinge. Each slope runs at CutSlope (up) or FillSlope (down) towards the surface. If it reaches the surface within
    MaxCutHeight / MaxFillHeight the daylight point is placed (codes Daylight + Daylight_Cut|Daylight_Fill).
    Otherwise the slope stops at the max height (Bench_In), a bench of BenchWidth at BenchSlope follows (Bench_Out)
    and the next slope starts. Up to MAX_SEGMENTS slopes.
  * Clip (optional offset target "ClipTarget", e.g. the valley line of a bowtie): where the target is found at the
    station and the geometry would pass it, the current link (lane, slope or bench) stops exactly at the clip offset
    ON ITS OWN SLOPE and ends in a point coded "Valley" (link codes gain "Clip"). No target, or target not crossed,
    gives the unclipped geometry.

  * Clip level (optional elevation target "ClipElev", v0.3): map the SAME valley feature line(s) here as on ClipTarget.
    Where both are found, the clipped link ends at the clip offset AT THE FEATURE LINE'S LEVEL instead of on its own slope,
    so the two sides of a bend, and every section converging on a curve centre, end on the same XYZ and the surface is
    continuous. Only the last link changes slope. Without ClipElev the v0.2 behaviour is unchanged.

Link codes follow LinkWidthAndSlope / DaylightBench: lane Top/Datum, slope links before a bench
Top/Daylight/Slope_Link/Datum, bench links Top/Bench/Datum, the final daylight link Top/Daylight/Daylight_Cut|Fill/Datum.
"""
import os, sys, uuid, zipfile

NAME = "UTNM_LaneDaylightClip"
VERSION = "0.3"
MAX_SEGMENTS = 4
DESCRIPTION = ("UTNM optional lane + daylight with benches (LinkWidthAndSlope + DaylightBench behaviour) plus an "
               "optional clip offset target for bowtie fixes: where the ClipTarget is crossed, the lane, slope or bench "
               "stops at the clip line and ends in point code Valley: on its own slope, or at the level of the optional "
               "ClipElev elevation target (map the same valley feature line on both). Without a target it behaves like the stock parts.")
GUID = uuid.uuid4().hex
OUT_DIR = sys.argv[1] if len(sys.argv) > 1 else "."

# name, type (double | slope | grade), display, description, default
PARAMS = [
    ("LaneWidth", "double", "Lane Width", "Width of the lane in front of the daylight (0 = no lane)", 0.0),
    ("LaneSlope", "grade", "Lane Slope", "Lane cross slope. Positive slopes upward in the direction of increasing offset", 0.0),
    ("CutSlope", "slope", "Cut Slope", "Slope of the cut daylight links (2:1 = 0.5)", 0.5),
    ("MaxCutHeight", "double", "Max Cut Height", "Maximum height of one cut slope before a bench", 4.0),
    ("FillSlope", "slope", "Fill Slope", "Slope of the fill daylight links (2:1 = 0.5)", 0.5),
    ("MaxFillHeight", "double", "Max Fill Height", "Maximum height of one fill slope before a bench", 4.0),
    ("BenchWidth", "double", "Bench Width", "Width of each bench", 1.0),
    ("BenchSlope", "grade", "Bench Slope", "Bench cross slope. Positive slopes upward in the direction of increasing offset", 0.02),
]
TYPE_XAML = {"double": "x:Double", "slope": "asw:Slope", "grade": "asw:Grade"}
TYPE_INFO = {"double": 16, "slope": 10, "grade": 9}

ENUM_VARIABLES = [
    (41, "AwayFromCrown"), (1, "Left"), (20, "LeftInsideLane"), (21, "LeftInsideShoulder"), (22, "LeftOutsideLane"),
    (23, "LeftOutsideShoulder"), (11, "No"), (-1, "None"), (0, "Right"), (24, "RightInsideLane"),
    (25, "RightInsideShoulder"), (26, "RightOutsideLane"), (27, "RightOutsideShoulder"), (30, "Supported"),
    (40, "TowardsCrown"), (31, "Unsupported"), (10, "Yes"),
]


def esc(s):
    return s.replace("&", "&amp;").replace('"', "&quot;").replace("<", "&lt;").replace(">", "&gt;")


COMMON = ('Geometry="[Geometry]" sap:VirtualizedContainerService.HintSize="200,22" ShowErrors="True" '
          'SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]"')


def viewstate(pad):
    return (f'{pad}<sap:WorkflowViewStateService.ViewState>\n'
            f'{pad}  <scg:Dictionary x:TypeArguments="x:String, x:Object">\n'
            f'{pad}    <x:Boolean x:Key="IsExpanded">True</x:Boolean>\n'
            f'{pad}  </scg:Dictionary>\n'
            f'{pad}</sap:WorkflowViewStateService.ViewState>')


def codes_xml(tag, codes, pad):
    """codes: plain strings, or ('expr', vb) for a VB expression."""
    items = []
    for c in codes:
        text = f"[{c[1]}]" if isinstance(c, tuple) else c
        items.append(f'{pad}    <InArgument x:TypeArguments="x:String">{esc(text)}</InArgument>')
    return (f'{pad}<{tag}>\n{pad}  <scg:List x:TypeArguments="InArgument(x:String)" Capacity="4">\n'
            + "\n".join(items) + f'\n{pad}  </scg:List>\n{pad}</{tag}>')


def args_xml(tag, args, pad):
    rows = []
    for typ, key, value in args:
        if value is None:
            rows.append(f'{pad}  <InArgument x:TypeArguments="{typ}" x:Key="{key}" />')
        else:
            rows.append(f'{pad}  <InArgument x:TypeArguments="{typ}" x:Key="{key}">{esc(value)}</InArgument>')
    return f'{pad}<{tag}>\n' + "\n".join(rows) + f'\n{pad}</{tag}>'


# ---------------------------------------------------------------- positioning argument sets
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
                ("x:Double", "DeltaXForLayout3", kw.get("layout_dx", "4")), ("x:Boolean", "ShowErrors", "False")]
    if positioning == "DeltaXOnSurface":
        return [("x:Double", "DeltaX4", kw["dx"]), ("asw:SurfaceTarget", "SurfaceTarget4", "[SurfaceTarget]"),
                ("asw:OffsetTarget", "OffsetTarget4", None), ("x:Double", "DeltaYForLayout4", kw.get("layout_dy", "1"))]
    if positioning == "SlopeAndDeltaY":
        return [("x:Double", "Slope5", kw["slope"]), ("x:Double", "DeltaY5", kw["dy"]),
                ("x:Boolean", "ReverseSlopeDirection5", "False"), ("asw:OffsetTarget", "OffsetTarget5", None),
                ("asw:ElevationTarget", "ElevationTarget5", None), ("asw:CrossSegmentType", "Superelevation5", None)]
    raise ValueError(positioning)


class Ids:
    activity = 0
    ref = 0

    @classmethod
    def next_activity(cls):
        cls.activity += 1
        return cls.activity

    @classmethod
    def next_ref(cls):
        cls.ref += 1
        return f"__ReferenceID{cls.ref - 1}"


def point(pid, positioning, frm, codes, aux=False, **kw):
    aid = Ids.next_activity()
    tag = "asa2:CreateAuxPoint" if aux else "asa2:CreatePoint"
    from_attr = 'FromPoint="{x:Null}"' if frm is None else f'FromPoint="{frm}"'
    pc_attr = 'PointCodes="{x:Null}" ' if aux else ""

    def build(pad):
        out = (f'{pad}<{tag} AutoLinkCodes="{{x:Null}}" AutoLinkGeometryName="{{x:Null}}" {pc_attr}{from_attr} '
               f'ActivityId="{aid}" ApplyAOR="False" AutoLink="False" DisplayName="{pid}" {COMMON} '
               f'PointNumber="{pid}" Positioning="{positioning}" Side="[Side]">\n'
               + args_xml(f"{tag}.Arguments", pos_args(positioning, **kw), pad + "  ") + "\n")
        if not aux and codes:
            out += codes_xml(f"{tag}.PointCodes", codes, pad + "  ") + "\n"
        out += viewstate(pad + "  ") + "\n" + f"{pad}</{tag}>"
        return out
    return build


def link(lid, a, b, codes):
    aid = Ids.next_activity()

    def build(pad):
        return (f'{pad}<asa2:CreateLink ActivityId="{aid}" ApplyAOR="False" DisplayName="{lid}" EndPoint="{b}" '
                f'{COMMON} IsEnabled="True" LinkNumber="{lid}" StartPoint="{a}">\n'
                + codes_xml("asa2:CreateLink.LinkCodes", codes, pad + "  ") + "\n"
                + viewstate(pad + "  ") + "\n"
                f'{pad}</asa2:CreateLink>')
    return build


# ---------------------------------------------------------------- flowchart tree
class Step:
    def __init__(self, activity, nxt=None):
        self.activity, self.next = activity, nxt


class Decision:
    def __init__(self, condition, true=None, false=None, true_label="True", false_label="False"):
        self.condition, self.true, self.false = condition, true, false
        self.true_label, self.false_label = true_label, false_label


def chain(*items):
    """chain(activity1, ..., activityN, tail): Steps for the activities, ending in tail (Step/Decision/None)."""
    *acts, tail = items
    node = tail
    for act in reversed(acts):
        node = Step(act, node)
    return node


REFS = []
_layout = {"y": 100}


def serialize(node, depth):
    pad = "      " + "  " * depth
    name = Ids.next_ref()
    REFS.append(name)
    _layout["y"] += 60
    y = _layout["y"]
    if isinstance(node, Step):
        out = (f'{pad}<FlowStep x:Name="{name}">\n'
               f'{pad}  <sap:WorkflowViewStateService.ViewState>\n'
               f'{pad}    <scg:Dictionary x:TypeArguments="x:String, x:Object">\n'
               f'{pad}      <av:Point x:Key="ShapeLocation">200,{y}</av:Point>\n'
               f'{pad}      <av:Size x:Key="ShapeSize">200,22</av:Size>\n'
               f'{pad}    </scg:Dictionary>\n'
               f'{pad}  </sap:WorkflowViewStateService.ViewState>\n'
               + node.activity(pad + "  ") + "\n")
        if node.next is not None:
            out += f'{pad}  <FlowStep.Next>\n' + serialize(node.next, depth + 2) + f'{pad}  </FlowStep.Next>\n'
        return out + f'{pad}</FlowStep>\n'
    out = (f'{pad}<FlowDecision x:Name="{name}" Condition="{esc("[" + node.condition + "]")}" '
           f'sap:VirtualizedContainerService.HintSize="70,87">\n'
           f'{pad}  <sap:WorkflowViewStateService.ViewState>\n'
           f'{pad}    <scg:Dictionary x:TypeArguments="x:String, x:Object">\n'
           f'{pad}      <x:Boolean x:Key="IsExpanded">True</x:Boolean>\n'
           f'{pad}      <av:Point x:Key="ShapeLocation">265,{y}</av:Point>\n'
           f'{pad}      <av:Size x:Key="ShapeSize">70,87</av:Size>\n'
           f'{pad}      <x:String x:Key="TrueLabel">{esc(node.true_label)}</x:String>\n'
           f'{pad}      <x:String x:Key="FalseLabel">{esc(node.false_label)}</x:String>\n'
           f'{pad}    </scg:Dictionary>\n'
           f'{pad}  </sap:WorkflowViewStateService.ViewState>\n')
    if node.true is not None:
        out += f'{pad}  <FlowDecision.True>\n' + serialize(node.true, depth + 2) + f'{pad}  </FlowDecision.True>\n'
    if node.false is not None:
        out += f'{pad}  <FlowDecision.False>\n' + serialize(node.false, depth + 2) + f'{pad}  </FlowDecision.False>\n'
    return out + f'{pad}</FlowDecision>\n'


# ---------------------------------------------------------------- the lane + daylight logic
class Names:
    p = 1      # P1 is the attachment point
    ap = 1     # AP1 = direction probe
    l = 0

    @classmethod
    def P(cls):
        cls.p += 1
        return f"P{cls.p}"

    @classmethod
    def AP(cls):
        cls.ap += 1
        return f"AP{cls.ap}"

    @classmethod
    def L(cls):
        cls.l += 1
        return f"L{cls.l}"


SGN = "Math.Sign(AP1.Offset - P1.Offset)"
CLIPD = f"((ClipTarget.Offset - P1.Offset) * {SGN})"   # clip distance from the attachment point, + = outward


def dist(p):
    return "0.0" if p == "P1" else f"(({p}.Offset - P1.Offset) * {SGN})"


class Ctx:
    """Cut/fill expressions for one daylight subtree: ap = surface probe at the hinge, h = hinge point."""
    def __init__(self, ap, h):
        self.cut = f"({ap}.Y > {h}.Y)"
        self.sl = f"(If({self.cut}, 1.0, -1.0) * If({self.cut}, CutSlope * 1.0, FillSlope * 1.0))"
        self.maxh = f"If({self.cut}, MaxCutHeight, MaxFillHeight)"
        self.cutfill_code = ("expr", f'If({self.cut}, "Daylight_Cut", "Daylight_Fill")')


def clip_dy(frm, slope_expr, dx_expr):
    """Rise of the clipped link: up to the ClipElev level where that target is found, else along the link's own slope."""
    # .Elevation is the point's real level (as .Offset is its real offset); .X / .Y are relative to the subassembly origin
    return f"If(ClipElev.IsValid, ClipElev.Elevation - {frm}.Elevation, ({slope_expr}) * ({dx_expr}))"


def clipped_end(frm, slope_expr, link_codes):
    """Point at the clip offset (on the link's own slope, or at the ClipElev level) + the link to it. Returns a chain node."""
    v = Names.P()
    dx = f"{CLIPD} - {dist(frm)}"
    return chain(point(v, "DeltaXAndDeltaY", frm, ["Valley"], dx=f"[{dx}]", dy=f"[{clip_dy(frm, slope_expr, dx)}]"),
                 link(Names.L(), frm, v, link_codes), None)


def segment(c, k, hinge):
    """Slope segment k starting at real point `hinge`."""
    ap = Names.AP()
    daylight_ok = f"{ap}.IsValid" if k == MAX_SEGMENTS else f"{ap}.IsValid AndAlso Math.Abs({ap}.Y - {hinge}.Y) <= {c.maxh} + 0.0001"

    d = Names.P()
    daylight_branch = Decision(
        f"ClipTarget.IsValid AndAlso {dist(ap)} > {CLIPD}",
        true=clipped_end(hinge, c.sl, ["Top", "Datum", "Clip", "Daylight"]),
        false=chain(point(d, "SlopeToSurface", hinge, ["Daylight", c.cutfill_code], slope=f"[{c.sl}]"),
                    link(Names.L(), hinge, d, ["Top", "Daylight", c.cutfill_code, "Datum"]), None),
        true_label="Clip on slope", false_label="Daylight")

    if k == MAX_SEGMENTS:
        bench_branch = None
    else:
        bi, bo = Names.P(), Names.P()
        slope_to_bench_end = f"{dist(hinge)} + {c.maxh} / Math.Abs({c.sl})"
        after_bench = Decision(
            f"ClipTarget.IsValid AndAlso {dist(bi)} + BenchWidth > {CLIPD}",
            true=clipped_end(bi, "BenchSlope * 1.0", ["Top", "Datum", "Clip", "Bench"]),
            false=chain(point(bo, "DeltaXAndDeltaY", bi, ["Bench_Out"], dx="[BenchWidth]", dy="[BenchWidth * BenchSlope]"),
                        link(Names.L(), bi, bo, ["Top", "Bench", "Datum"]),
                        segment(c, k + 1, bo)),
            true_label="Clip on bench", false_label="Bench")
        bench_branch = Decision(
            f"ClipTarget.IsValid AndAlso {slope_to_bench_end} > {CLIPD}",
            true=clipped_end(hinge, c.sl, ["Top", "Datum", "Clip", "Daylight"]),
            false=chain(point(bi, "SlopeAndDeltaY", hinge, ["Bench_In"], slope=f"[{c.sl}]", dy=f"[If({c.cut}, 1.0, -1.0) * {c.maxh}]"),
                        link(Names.L(), hinge, bi, ["Top", "Daylight", "Slope_Link", "Datum"]),
                        after_bench),
            true_label="Clip on slope", false_label="Bench")

    probe = point(ap, "SlopeToSurface", hinge, [], aux=True, slope=f"[{c.sl}]")
    return chain(probe, Decision(daylight_ok, true=daylight_branch, false=bench_branch,
                                 true_label="Daylight here", false_label="Bench"))


def daylight(frm):
    """Hinge at real point `frm` (lane end or P1): surface probe, coded hinge point, clip-behind-hinge check, slopes."""
    ap = Names.AP()
    h = Names.P()
    c = Ctx(ap, h)
    hinge_codes = ["Hinge", ("expr", f'If({ap}.Y > {frm}.Y, "Hinge_Cut", "Hinge_Fill")')]
    v = Names.P()
    return chain(point(ap, "DeltaXOnSurface", frm, [], aux=True, dx="0", layout_dy="1"),
                 point(h, "DeltaXAndDeltaY", frm, hinge_codes, dx="0", dy="0"),
                 Decision(f"ClipTarget.IsValid AndAlso {CLIPD} <= {dist(h)} + 0.001",
                          true=chain(point(v, "DeltaXAndDeltaY", h, ["Valley"], dx="0", dy="0"), None),
                          false=segment(c, 1, h),
                          true_label="Clip at hinge", false_label="Daylight"))


def build_tree():
    p1 = point("P1", "DeltaXAndDeltaY", None, [], dx="0", dy="0")
    ap1 = point("AP1", "DeltaXAndDeltaY", "P1", [], aux=True, dx="1", dy="0")
    v0 = Names.P()
    lv, le = Names.P(), Names.P()
    lane = Decision(
        f"ClipTarget.IsValid AndAlso {CLIPD} < LaneWidth - 0.0001",
        true=chain(point(lv, "DeltaXAndDeltaY", "P1", ["Valley"], dx=f"[{CLIPD}]", dy=f"[{clip_dy('P1', 'LaneSlope * 1.0', CLIPD)}]"),
                   link(Names.L(), "P1", lv, ["Top", "Datum", "Clip"]), None),
        false=chain(point(le, "SlopeAndDeltaX", "P1", ["P2"], slope="[LaneSlope * 1.0]", dx="[LaneWidth]"),
                    link(Names.L(), "P1", le, ["Top", "Datum"]),
                    daylight(le)),
        true_label="Clip in lane", false_label="Full lane")
    has_lane = Decision("LaneWidth > 0.0001", true=lane, false=daylight("P1"), true_label="Lane", false_label="No lane")
    start = Decision(f"ClipTarget.IsValid AndAlso {CLIPD} <= 0.001",
                     true=chain(point(v0, "DeltaXAndDeltaY", "P1", ["Valley"], dx="0", dy="0"), None),
                     false=has_lane,
                     true_label="Clip behind start", false_label="Build")
    return chain(p1, ap1, start)


# ---------------------------------------------------------------- XAML
def build_xaml():
    tree = build_tree()
    defaults = ' this:Subassembly.Side="[new EnumType(0, &quot;Right&quot;, &quot;Side&quot;)]"'
    for name, typ, _, _, default in PARAMS:
        if typ == "slope":
            defaults += f' this:Subassembly.{name}="[new Slope({default})]"'
        elif typ == "grade":
            defaults += f' this:Subassembly.{name}="[new Grade({default})]"'
        else:
            defaults += f' this:Subassembly.{name}="{default}"'
    defaults += (' this:Subassembly.SurfaceTarget="[new PreviewSurfaceTarget(&quot;SurfaceTarget&quot;, True, 1000, 2, -1000, 2)]"'
                 ' this:Subassembly.ClipTarget="[new PreviewOffsetTarget(&quot;ClipTarget&quot;, False, 6)]"'
                 ' this:Subassembly.ClipElev="[new PreviewElevationTarget(&quot;ClipElev&quot;, False, 1)]"')

    members = [
        '    <x:Property Name="Geometry" Type="InOutArgument(asw:Geometry)" />',
        '    <x:Property Name="SubassemblyErrorCenter" Type="InOutArgument(asw:SubassemblyErrorCenter)" />',
        '    <x:Property Name="SubassemblyRunMode" Type="InOutArgument(asw:SubassemblyRunMode)" />',
        '    <x:Property Name="Side" Type="InArgument(asw:EnumType)">\n'
        '      <x:Property.Attributes>\n        <asw:EnabledFlag2Attribute EnabledFlag="True" />\n'
        '      </x:Property.Attributes>\n    </x:Property>',
    ]
    for name, typ, display, desc, _ in PARAMS:
        members.append(
            f'    <x:Property Name="{name}" Type="InArgument({TYPE_XAML[typ]})">\n'
            f'      <x:Property.Attributes>\n'
            f'        <asw:EnabledFlag2Attribute EnabledFlag="True" />\n'
            f'        <asw:DisplayName2Attribute DisplayName="{esc(display)}" />\n'
            f'        <asw:Description2Attribute Description="{esc(desc)}" />\n'
            f'      </x:Property.Attributes>\n'
            f'    </x:Property>')
    for name, typ, display in (("SurfaceTarget", "asw:SurfaceTarget", "Daylight Surface"),
                               ("ClipTarget", "asw:OffsetTarget", "Clip Offset Target"),
                               ("ClipElev", "asw:ElevationTarget", "Clip Level Target")):
        members.append(
            f'    <x:Property Name="{name}" Type="InArgument({typ})">\n'
            f'      <x:Property.Attributes>\n'
            f'        <asw:EnabledFlag2Attribute EnabledFlag="True" />\n'
            f'        <asw:DisplayName2Attribute DisplayName="{display}" />\n'
            f'      </x:Property.Attributes>\n'
            f'    </x:Property>')

    variables = "\n".join(
        f'      <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType({v}, &quot;{n}&quot;)]" Modifiers="ReadOnly" Name="{n}" />'
        for v, n in ENUM_VARIABLES)

    body = serialize(tree, 0)
    refs = "\n".join(f'    <x:Reference>{r}</x:Reference>' for r in REFS)

    return (
        '﻿<Activity mc:Ignorable="sads sap" x:Class="Subassembly"' + defaults + '\n'
        ' xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"\n'
        ' xmlns:asa="clr-namespace:Autodesk.SubassemblyComposer.API;assembly=Subassembly.API"\n'
        ' xmlns:asa1="clr-namespace:Autodesk.SubassemblyComposer.API;assembly=Subassembly.WorkflowEngine"\n'
        ' xmlns:asa2="clr-namespace:Autodesk.SubassemblyComposer.ActivityLibrary;assembly=Subassembly.ActivityLibrary"\n'
        ' xmlns:asw="clr-namespace:Autodesk.SubassemblyComposer.WorkflowEngine;assembly=Subassembly.WorkflowEngine"\n'
        ' xmlns:av="http://schemas.microsoft.com/winfx/2006/xaml/presentation"\n'
        ' xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"\n'
        ' xmlns:mva="clr-namespace:Microsoft.VisualBasic.Activities;assembly=System.Activities"\n'
        ' xmlns:s="clr-namespace:System;assembly=mscorlib"\n'
        ' xmlns:sa="clr-namespace:System.Activities;assembly=System.Activities"\n'
        ' xmlns:sads="http://schemas.microsoft.com/netfx/2010/xaml/activities/debugger"\n'
        ' xmlns:sap="http://schemas.microsoft.com/netfx/2009/xaml/activities/presentation"\n'
        ' xmlns:scg="clr-namespace:System.Collections.Generic;assembly=mscorlib"\n'
        ' xmlns:this="clr-namespace:"\n'
        ' xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">\n'
        '  <x:Members>\n' + "\n".join(members) + '\n  </x:Members>\n'
        '  <sap:VirtualizedContainerService.HintSize>1200,2400</sap:VirtualizedContainerService.HintSize>\n'
        '  <mva:VisualBasic.Settings>Assembly references and imported namespaces serialized as XML namespaces</mva:VisualBasic.Settings>\n'
        '  <Flowchart DisplayName="Main Workflow" sap:VirtualizedContainerService.HintSize="1160,2360" mva:VisualBasic.Settings="Assembly references and imported namespaces serialized as XML namespaces">\n'
        '    <Flowchart.Variables>\n' + variables + '\n    </Flowchart.Variables>\n'
        '    <sap:WorkflowViewStateService.ViewState>\n'
        '      <scg:Dictionary x:TypeArguments="x:String, x:Object">\n'
        '        <x:Boolean x:Key="IsExpanded">False</x:Boolean>\n'
        '        <av:Point x:Key="ShapeLocation">270,2.5</av:Point>\n'
        '        <av:Size x:Key="ShapeSize">60,74.6666666666667</av:Size>\n'
        '        <av:PointCollection x:Key="ConnectorLocation">300,77.1666666666667 300,109</av:PointCollection>\n'
        '      </scg:Dictionary>\n'
        '    </sap:WorkflowViewStateService.ViewState>\n'
        '    <Flowchart.StartNode>\n' + body + '    </Flowchart.StartNode>\n' + refs + '\n'
        '  </Flowchart>\n'
        '</Activity>')


# ---------------------------------------------------------------- side files (as UTNM_MainDrainU)
def xml_decl():
    return '<?xml version="1.0"?>\n'


def build_atc():
    params = ['            <Side DataType="long" TypeInfo="16" DisplayName="Side" Description="Side">0<Enum>'
              '<Left DisplayName="Left">1</Left><Right DisplayName="Right">0</Right></Enum></Side>']
    for name, typ, display, desc, default in PARAMS:
        params.append(f'            <{name} DataType="double" TypeInfo="{TYPE_INFO[typ]}" DisplayName="{esc(display)}" '
                      f'Description="{esc(desc)}">{default}</{name}>')
    return (xml_decl() +
            '<Category xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">\n'
            f'  <ItemID idValue="{{{uuid.uuid4()}}}" />\n'
            '  <Properties>\n    <ItemName>Category1</ItemName>\n    <Images>\n      <Image cx="93" cy="123" />\n'
            '    </Images>\n  </Properties>\n  <CustomData />\n  <Source />\n  <Palettes />\n  <Packages />\n  <Tools>\n'
            f'    <Tool Name="{NAME}">\n'
            f'      <ItemID idValue="{{{uuid.uuid4()}}}" />\n'
            '      <Properties>\n'
            f'        <ItemName>{NAME}</ItemName>\n'
            '        <Images>\n          <Image cx="64" cy="64" />\n        </Images>\n'
            f'        <ToolTip>Version: {VERSION}</ToolTip>\n'
            f'        <Description>{esc(DESCRIPTION)}</Description>\n'
            '        <Help>\n          <HelpFile />\n          <HelpCommand />\n          <HelpData />\n        </Help>\n'
            '      </Properties>\n      <Source />\n'
            '      <StockToolRef idValue="{7F55AAC0-0256-48D7-BFA5-914702663FDE}" />\n'
            '      <Data>\n        <AeccDbSubassembly>\n'
            '          <GeometryGenerateMode>UseDotNet</GeometryGenerateMode>\n'
            f'          <DotNetClass Assembly="{GUID}.xaml">Subassembly.{NAME}</DotNetClass>\n'
            f'          <Version>{VERSION}</Version>\n'
            '          <Params>\n' + "\n".join(params) + '\n          </Params>\n'
            '        </AeccDbSubassembly>\n        <Units>m</Units>\n      </Data>\n    </Tool>\n  </Tools>\n'
            '  <StockTools />\n</Category>')


def build_cfg():
    return (xml_decl() +
            '<Configuration xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">\n'
            '  <CreatedWith>\n    <ProductName>Autodesk Subassembly Composer</ProductName>\n'
            '    <Version>ForVail</Version>\n    <VersionNumber>13.7.1429.0</VersionNumber>\n  </CreatedWith>\n'
            '</Configuration>')


def build_emd():
    return (xml_decl() +
            '<EnumData xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">\n'
            '  <EnumDatas>\n    <Groups />\n    <DefinedVariables />\n  </EnumDatas>\n</EnumData>')


def build_cdmd():
    return (xml_decl() +
            '<CodeData xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">\n'
            '  <CodeItems />\n</CodeData>')


def build_pvd():
    slopes = [("LeftInsideLane", -0.02), ("LeftInsideShoulder", -0.05), ("LeftOutsideLane", -0.02), ("LeftOutsideShoulder", -0.05),
              ("RightInsideLane", -0.02), ("RightInsideShoulder", -0.05), ("RightOutsideLane", -0.02), ("RightOutsideShoulder", -0.05)]
    rows = "\n".join(f'      <PreviewCrossSlope CrossSegmentType="{t}" Slope="{s}" IsDefined="true" />' for t, s in slopes)
    return (xml_decl() +
            '<PreviewData xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">\n'
            '  <Superelevation>\n    <CrossSlopes>\n' + rows + '\n    </CrossSlopes>\n  </Superelevation>\n'
            '  <Cant>\n    <CantParams>\n'
            '      <PreviewCantParam Name="CantPivotType" Value="CenterLine" />\n'
            '      <PreviewCantParam Name="LeftRail" Value="" />\n'
            '      <PreviewCantParam Name="LeftRailDeltaElevation" Value="0" />\n'
            '      <PreviewCantParam Name="RightRail" Value="" />\n'
            '      <PreviewCantParam Name="RightRailDeltaElevation" Value="0" />\n'
            '    </CantParams>\n  </Cant>\n</PreviewData>')


def build_content_types():
    return ('﻿<?xml version="1.0" encoding="utf-8"?>'
            '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
            '<Default Extension="atc" ContentType="" /><Default Extension="cfg" ContentType="" />'
            '<Default Extension="xaml" ContentType="" /><Default Extension="pvd" ContentType="" />'
            '<Default Extension="emd" ContentType="" /><Default Extension="cdmd" ContentType="" /></Types>')


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    files = [
        (f"{GUID}.atc", build_atc()),
        ("[Content_Types].xml", build_content_types()),
        (f"{GUID}.cfg", build_cfg()),
        (f"{GUID}.xaml", build_xaml()),
        (f"{GUID}.pvd", build_pvd()),
        (f"{GUID}.emd", build_emd()),
        (f"{GUID}.cdmd", build_cdmd()),
    ]
    out = os.path.join(OUT_DIR, f"{NAME}.pkt")
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for name, text in files:
            z.writestr(name, text.encode("utf-8"))
    print("guid", GUID, "nodes", len(REFS), "activities", Ids.activity, "points", Names.p, "aux", Names.ap, "links", Names.l)
    print("wrote", out, os.path.getsize(out), "bytes")


if __name__ == "__main__":
    main()
