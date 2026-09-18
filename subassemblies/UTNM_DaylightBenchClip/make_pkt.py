"""
Generate UTNM_DaylightBenchClip.pkt - a Subassembly Composer 2026 package: DaylightBench behaviour plus an optional
clip line (offset target) for fixing corridor bowties on the inside of bends.

Behaviour (mirrors the stock DaylightBench as used on the Pasir Gudang drains):
  * P1 = hinge at the attachment point (code Hinge).
  * Cut or fill is decided once, from the target surface at the hinge (AP1 = point on surface at P1).
  * Each slope runs at CutSlope (up) or FillSlope (down) towards the surface. If it reaches the surface within
    MaxCutHeight / MaxFillHeight the daylight point is placed (codes Daylight + Daylight_Cut|Daylight_Fill).
    Otherwise the slope stops at the max height (Bench_In), a bench of BenchWidth at BenchSlope follows (Bench_Out,
    positive = upward away from the hinge, as the stock part) and the next slope starts. Up to MAX_SEGMENTS slopes.
  * Clip (optional offset target "ClipTarget", e.g. the valley line of a bowtie): when the target is found at the
    station and the geometry would pass it, the current link stops exactly at the clip offset ON ITS OWN SLOPE
    (slope or bench) and ends in a point coded "Valley" instead of "Daylight". No target / target not crossed at
    this station -> identical to the unclipped geometry. A clip behind the hinge gives a zero-length result.

Link codes follow DaylightBench: slope links before a bench Top/Daylight/Slope_Link/Datum, bench links
Top/Bench/Datum, the final daylight link Top/Daylight/Daylight_Cut|Fill/Datum, a clipped link Top/Datum/Clip.

Package format as in ../UTNM_MainDrainU/make_pkt.py; activity XAML follows a Composer 2026 package that uses
decisions, aux points and SlopeToSurface / SlopeAndDeltaX / SlopeAndDeltaY / DeltaXOnSurface positioning
(argument keys: DeltaX1/DeltaY1, Slope2/DeltaX2, Slope3/SurfaceTarget3, DeltaX4/SurfaceTarget4, Slope5/DeltaY5).
"""
import os, sys, uuid, zipfile

NAME = "UTNM_DaylightBenchClip"
VERSION = "0.1"
MAX_SEGMENTS = 4
DESCRIPTION = ("UTNM daylight with benches (DaylightBench behaviour) plus an optional clip offset target for bowtie "
               "fixes: where the ClipTarget is crossed, the slope or bench stops on its own slope at the clip line and "
               "ends in point code Valley. Without a target it behaves like DaylightBench.")
GUID = uuid.uuid4().hex
OUT_DIR = sys.argv[1] if len(sys.argv) > 1 else "."

# name, type (double | slope | grade), display, description, default
PARAMS = [
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


# ---------------------------------------------------------------- the daylight logic
class Names:
    p = 1      # P1 is the hinge
    ap = 2     # AP1 = surface at hinge, AP2 = direction probe
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


CUT = "(AP1.Y > P1.Y)"
SGN = "Math.Sign(AP2.Offset - P1.Offset)"
SL = f"(If({CUT}, 1.0, -1.0) * If({CUT}, CutSlope * 1.0, FillSlope * 1.0))"
MAXH = f"If({CUT}, MaxCutHeight, MaxFillHeight)"
CLIPD = f"((ClipTarget.Offset - P1.Offset) * {SGN})"
CUTFILL_CODE = ("expr", f'If({CUT}, "Daylight_Cut", "Daylight_Fill")')


def dist(p):
    return "0.0" if p == "P1" else f"(({p}.Offset - P1.Offset) * {SGN})"


def clipped_end(frm, slope_expr, link_codes_extra):
    """Point on the current link at the clip offset + the link to it. Returns a chain node."""
    v = Names.P()
    return chain(point(v, "SlopeAndDeltaX", frm, ["Valley"], slope=f"[{slope_expr}]", dx=f"[{CLIPD} - {dist(frm)}]"),
                 link(Names.L(), frm, v, ["Top", "Datum", "Clip"] + link_codes_extra), None)


def segment(k, hinge):
    """Slope segment k starting at real point `hinge`."""
    ap = Names.AP()
    daylight_ok = f"{ap}.IsValid" if k == MAX_SEGMENTS else f"{ap}.IsValid AndAlso Math.Abs({ap}.Y - {hinge}.Y) <= {MAXH} + 0.0001"

    # daylight reached on this slope
    d = Names.P()
    daylight_branch = Decision(
        f"ClipTarget.IsValid AndAlso {dist(ap)} > {CLIPD}",
        true=clipped_end(hinge, SL, ["Daylight"]),
        false=chain(point(d, "SlopeToSurface", hinge, ["Daylight", CUTFILL_CODE], slope=f"[{SL}]"),
                    link(Names.L(), hinge, d, ["Top", "Daylight", CUTFILL_CODE, "Datum"]), None),
        true_label="Clip on slope", false_label="Daylight")

    if k == MAX_SEGMENTS:
        bench_branch = None
    else:
        bi, bo = Names.P(), Names.P()
        slope_to_bench_end = f"{dist(hinge)} + {MAXH} / Math.Abs({SL})"
        after_bench = Decision(
            f"ClipTarget.IsValid AndAlso {dist(bi)} + BenchWidth > {CLIPD}",
            true=clipped_end(bi, "BenchSlope * 1.0", ["Bench"]),
            false=chain(point(bo, "DeltaXAndDeltaY", bi, ["Bench_Out"], dx="[BenchWidth]", dy="[BenchWidth * BenchSlope]"),
                        link(Names.L(), bi, bo, ["Top", "Bench", "Datum"]),
                        segment(k + 1, bo)),
            true_label="Clip on bench", false_label="Bench")
        bench_branch = Decision(
            f"ClipTarget.IsValid AndAlso {slope_to_bench_end} > {CLIPD}",
            true=clipped_end(hinge, SL, ["Daylight"]),
            false=chain(point(bi, "SlopeAndDeltaY", hinge, ["Bench_In"], slope=f"[{SL}]", dy=f"[If({CUT}, 1.0, -1.0) * {MAXH}]"),
                        link(Names.L(), hinge, bi, ["Top", "Daylight", "Slope_Link", "Datum"]),
                        after_bench),
            true_label="Clip on slope", false_label="Bench")

    probe = point(ap, "SlopeToSurface", hinge, [], aux=True, slope=f"[{SL}]")
    return chain(probe, Decision(daylight_ok, true=daylight_branch, false=bench_branch,
                                 true_label="Daylight here", false_label="Bench"))


def build_tree():
    p1 = point("P1", "DeltaXAndDeltaY", None, ["Hinge"], dx="0", dy="0")
    ap1 = point("AP1", "DeltaXOnSurface", "P1", [], aux=True, dx="0", layout_dy="1")
    ap2 = point("AP2", "DeltaXAndDeltaY", "P1", [], aux=True, dx="1", dy="0")
    v0 = Names.P()
    start = Decision(f"ClipTarget.IsValid AndAlso {CLIPD} <= 0.001",
                     true=chain(point(v0, "DeltaXAndDeltaY", "P1", ["Valley"], dx="0", dy="0"), None),
                     false=segment(1, "P1"),
                     true_label="Clip behind hinge", false_label="Daylight")
    return chain(p1, ap1, ap2, start)


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
                 ' this:Subassembly.ClipTarget="[new PreviewOffsetTarget(&quot;ClipTarget&quot;, False, 6)]"')

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
                               ("ClipTarget", "asw:OffsetTarget", "Clip Offset Target")):
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
