"""
Generate UTNM_MainDrainU.pkt — a Subassembly Composer 2026 package for a parametric RC U-drain.

Packet anatomy (verified against a package saved by Subassembly Composer 2026, "ForVail" 13.7.1429.0):
  a plain zip (OPC package) containing
    [Content_Types].xml   OPC content types, one <Default> per extension
    <guid>.xaml           WF4 flowchart with Autodesk.SubassemblyComposer activities — the geometry
    <guid>.atc            tool-catalog entry: name, description, parameter defaults, DotNetClass -> the .xaml
    <guid>.cfg            "created with" stamp
    <guid>.emd            enum data (empty)
    <guid>.pvd            preview data (superelevation / cant defaults)
    <guid>.cdmd           code data (empty)
  No DLL and no image: Civil 3D compiles the proxy assembly itself on import.
"""
import os, uuid, zipfile

NAME = "UTNM_MainDrainU"
VERSION = "0.2"
DESCRIPTION = ("UTNM parametric RC U-drain (both sides in one). Origin = drain centreline on the invert level (IL): "
               "use the IL as the corridor profile. Inputs: InternalWidth, Depth (IL to top of wall), WallThickness, "
               "BaseThickness. Flat invert. Point codes DrainTopIn/Out_L/R, DrainInvert_L/R, DrainBase_L/R, DrainCL; "
               "link codes Top/Datum; shape DrainConcrete.")
GUID = uuid.uuid4().hex
OUT_DIR = "/mnt/user-data/outputs"

PARAMS = [  # name, display, description, default
    ("InternalWidth", "Internal Width (W)", "Clear width between the wall faces", 1.5),
    ("Depth", "Depth IL to Top (D)", "Invert level to top of wall (engineer's PL - IL)", 1.5),
    ("WallThickness", "Wall Thickness (t)", "Thickness of each wall", 0.3),
    ("BaseThickness", "Base Slab Thickness (b)", "Base slab thickness below the invert", 0.45),
]
POINTS = [  # id, from, dx, dy, codes
    ("P1", None, "0", "0", ["DrainCL"]),
    ("P6", "P1", "[-InternalWidth/2]", "0", ["DrainInvert_L"]),
    ("P7", "P1", "[InternalWidth/2]", "0", ["DrainInvert_R"]),
    ("P2", "P6", "0", "[Depth]", ["DrainTopIn_L"]),
    ("P3", "P7", "0", "[Depth]", ["DrainTopIn_R"]),
    ("P4", "P2", "[-WallThickness]", "0", ["DrainTopOut_L"]),
    ("P5", "P3", "[WallThickness]", "0", ["DrainTopOut_R"]),
    ("P9", "P4", "0", "[-(Depth + BaseThickness)]", ["DrainBase_L"]),
    ("P10", "P5", "0", "[-(Depth + BaseThickness)]", ["DrainBase_R"]),
]
LINKS = [  # id, start, end, codes
    ("L1", "P4", "P2", ["Top", "DrainWallTop"]),
    ("L2", "P3", "P5", ["Top", "DrainWallTop"]),
    ("L3", "P2", "P6", ["DrainInner"]),
    ("L4", "P3", "P7", ["DrainInner"]),
    ("L5", "P6", "P7", ["DrainInvert"]),
    ("L6", "P4", "P9", ["Datum", "DrainOuter"]),
    ("L7", "P5", "P10", ["Datum", "DrainOuter"]),
    ("L8", "P9", "P10", ["Datum", "DrainBase"]),
]
SHAPES = [("S1", ["L1", "L3", "L5", "L4", "L2", "L7", "L8", "L6"], ["DrainConcrete"])]

ENUM_VARIABLES = [
    (41, "AwayFromCrown"), (1, "Left"), (20, "LeftInsideLane"), (21, "LeftInsideShoulder"), (22, "LeftOutsideLane"),
    (23, "LeftOutsideShoulder"), (11, "No"), (-1, "None"), (0, "Right"), (24, "RightInsideLane"),
    (25, "RightInsideShoulder"), (26, "RightOutsideLane"), (27, "RightOutsideShoulder"), (30, "Supported"),
    (40, "TowardsCrown"), (31, "Unsupported"), (10, "Yes"),
]


def esc(s):
    return s.replace("&", "&amp;").replace('"', "&quot;").replace("<", "&lt;").replace(">", "&gt;")


def xml_decl():
    return '<?xml version="1.0"?>\n'


# ---------------------------------------------------------------- XAML
COMMON = ('Geometry="[Geometry]" sap:VirtualizedContainerService.HintSize="200,22" ShowErrors="True" '
          'SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]"')


def codes_xml(tag, codes, pad):
    inner = "\n".join(f'{pad}    <InArgument x:TypeArguments="x:String">{esc(c)}</InArgument>' for c in codes)
    return (f'{pad}<{tag}>\n{pad}  <scg:List x:TypeArguments="InArgument(x:String)" Capacity="4">\n'
            f'{inner}\n{pad}  </scg:List>\n{pad}</{tag}>')


def viewstate(pad):
    return (f'{pad}<sap:WorkflowViewStateService.ViewState>\n'
            f'{pad}  <scg:Dictionary x:TypeArguments="x:String, x:Object">\n'
            f'{pad}    <x:Boolean x:Key="IsExpanded">True</x:Boolean>\n'
            f'{pad}  </scg:Dictionary>\n'
            f'{pad}</sap:WorkflowViewStateService.ViewState>')


def activities():
    """Yield (xml_builder(pad) -> str) for every activity in flowchart order."""
    aid = 0
    for pid, frm, dx, dy, codes in POINTS:
        aid += 1
        def build(pad, pid=pid, frm=frm, dx=dx, dy=dy, codes=codes, aid=aid):
            from_attr = 'FromPoint="{x:Null}" ' if frm is None else f'FromPoint="{frm}" '
            return (f'{pad}<asa1:CreatePoint AutoLinkCodes="{{x:Null}}" AutoLinkGeometryName="{{x:Null}}" {from_attr}'
                    f'ActivityId="{aid}" ApplyAOR="False" AutoLink="False" DisplayName="{pid}" {COMMON} '
                    f'PointNumber="{pid}" Positioning="DeltaXAndDeltaY" Side="[Side]">\n'
                    f'{pad}  <asa1:CreatePoint.Arguments>\n'
                    f'{pad}    <InArgument x:TypeArguments="x:Double" x:Key="DeltaX1">{esc(dx)}</InArgument>\n'
                    f'{pad}    <InArgument x:TypeArguments="x:Double" x:Key="DeltaY1">{esc(dy)}</InArgument>\n'
                    f'{pad}  </asa1:CreatePoint.Arguments>\n'
                    + codes_xml("asa1:CreatePoint.PointCodes", codes, pad + "  ") + "\n"
                    + viewstate(pad + "  ") + "\n"
                    f'{pad}</asa1:CreatePoint>')
        yield build
    for lid, a, b, codes in LINKS:
        aid += 1
        def build(pad, lid=lid, a=a, b=b, codes=codes, aid=aid):
            return (f'{pad}<asa1:CreateLink ActivityId="{aid}" ApplyAOR="False" DisplayName="{lid}" EndPoint="{b}" '
                    f'{COMMON} IsEnabled="True" LinkNumber="{lid}" StartPoint="{a}">\n'
                    + codes_xml("asa1:CreateLink.LinkCodes", codes, pad + "  ") + "\n"
                    + viewstate(pad + "  ") + "\n"
                    f'{pad}</asa1:CreateLink>')
        yield build
    for sid, links, codes in SHAPES:
        aid += 1
        def build(pad, sid=sid, links=links, codes=codes, aid=aid):
            names = "\n".join(f'{pad}    <x:String>{l}</x:String>' for l in links)
            return (f'{pad}<asa1:CreateShape ActivityId="{aid}" DisplayName="{sid}" {COMMON} Links="{",".join(links)}" '
                    f'ShapeNumber="{sid}">\n'
                    f'{pad}  <asa1:CreateShape.ComponentNames>\n{names}\n{pad}  </asa1:CreateShape.ComponentNames>\n'
                    + codes_xml("asa1:CreateShape.ShapeCodes", codes, pad + "  ") + "\n"
                    + viewstate(pad + "  ") + "\n"
                    f'{pad}</asa1:CreateShape>')
        yield build


def flow_steps(builders, index, depth):
    """Nest FlowStep -> activity -> FlowStep.Next -> FlowStep ... exactly as Composer serialises a straight chain."""
    pad = "      " + "    " * depth
    y = 109 + 60 * index
    out = (f'{pad}<FlowStep x:Name="__ReferenceID{index}">\n'
           f'{pad}  <sap:WorkflowViewStateService.ViewState>\n'
           f'{pad}    <scg:Dictionary x:TypeArguments="x:String, x:Object">\n'
           f'{pad}      <av:Point x:Key="ShapeLocation">200,{y}</av:Point>\n'
           f'{pad}      <av:Size x:Key="ShapeSize">200,22</av:Size>\n')
    if index < len(builders) - 1:
        out += f'{pad}      <av:PointCollection x:Key="ConnectorLocation">300,{y + 22} 300,{y + 60}</av:PointCollection>\n'
    out += (f'{pad}    </scg:Dictionary>\n'
            f'{pad}  </sap:WorkflowViewStateService.ViewState>\n'
            + builders[index](pad + "  ") + "\n")
    if index < len(builders) - 1:
        out += (f'{pad}  <FlowStep.Next>\n'
                + flow_steps(builders, index + 1, depth + 1)
                + f'{pad}  </FlowStep.Next>\n')
    out += f'{pad}</FlowStep>\n'
    return out


def build_xaml():
    defaults = ' this:Subassembly.Side="[new EnumType(0, &quot;Right&quot;, &quot;Side&quot;)]"'
    for name, _, _, default in PARAMS:
        defaults += f' this:Subassembly.{name}="{default}"'

    members = [
        '    <x:Property Name="Geometry" Type="InOutArgument(asw:Geometry)" />',
        '    <x:Property Name="SubassemblyErrorCenter" Type="InOutArgument(asw:SubassemblyErrorCenter)" />',
        '    <x:Property Name="SubassemblyRunMode" Type="InOutArgument(asw:SubassemblyRunMode)" />',
        '    <x:Property Name="Side" Type="InArgument(asw:EnumType)">\n'
        '      <x:Property.Attributes>\n'
        '        <asw:EnabledFlag2Attribute EnabledFlag="True" />\n'
        '      </x:Property.Attributes>\n'
        '    </x:Property>',
    ]
    for name, display, desc, _ in PARAMS:
        members.append(
            f'    <x:Property Name="{name}" Type="InArgument(x:Double)">\n'
            f'      <x:Property.Attributes>\n'
            f'        <asw:DisplayName2Attribute DisplayName="{esc(display)}" />\n'
            f'        <asw:Description2Attribute Description="{esc(desc)}" />\n'
            f'        <asw:EnabledFlag2Attribute EnabledFlag="True" />\n'
            f'      </x:Property.Attributes>\n'
            f'    </x:Property>')

    variables = "\n".join(
        f'      <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType({v}, &quot;{n}&quot;)]" Modifiers="ReadOnly" Name="{n}" />'
        for v, n in ENUM_VARIABLES)

    builders = list(activities())
    refs = "\n".join(f'    <x:Reference>__ReferenceID{i}</x:Reference>' for i in range(len(builders)))

    return (
        '﻿<Activity mc:Ignorable="sads sap" x:Class="Subassembly"' + defaults + '\n'
        ' xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"\n'
        ' xmlns:asa="clr-namespace:Autodesk.SubassemblyComposer.API;assembly=Subassembly.API"\n'
        ' xmlns:asa1="clr-namespace:Autodesk.SubassemblyComposer.ActivityLibrary;assembly=Subassembly.ActivityLibrary"\n'
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
        '  <sap:VirtualizedContainerService.HintSize>654,1276</sap:VirtualizedContainerService.HintSize>\n'
        '  <mva:VisualBasic.Settings>Assembly references and imported namespaces serialized as XML namespaces</mva:VisualBasic.Settings>\n'
        '  <Flowchart sap:VirtualizedContainerService.HintSize="614,1236" mva:VisualBasic.Settings="Assembly references and imported namespaces serialized as XML namespaces">\n'
        '    <Flowchart.Variables>\n' + variables + '\n    </Flowchart.Variables>\n'
        '    <sap:WorkflowViewStateService.ViewState>\n'
        '      <scg:Dictionary x:TypeArguments="x:String, x:Object">\n'
        '        <x:Boolean x:Key="IsExpanded">False</x:Boolean>\n'
        '        <av:Point x:Key="ShapeLocation">270,2.5</av:Point>\n'
        '        <av:Size x:Key="ShapeSize">60,74.6666666666667</av:Size>\n'
        '        <av:PointCollection x:Key="ConnectorLocation">300,77.1666666666667 300,109</av:PointCollection>\n'
        '      </scg:Dictionary>\n'
        '    </sap:WorkflowViewStateService.ViewState>\n'
        '    <Flowchart.StartNode>\n'
        + flow_steps(builders, 0, 0)
        + '    </Flowchart.StartNode>\n' + refs + '\n'
        '  </Flowchart>\n'
        '</Activity>')


# ---------------------------------------------------------------- ATC
def build_atc():
    params = ['            <Side DataType="long" TypeInfo="16" DisplayName="Side" Description="Side">0<Enum>'
              '<Left DisplayName="Left">1</Left><Right DisplayName="Right">0</Right></Enum></Side>']
    for name, display, desc, default in PARAMS:
        params.append(f'            <{name} DataType="double" TypeInfo="16" DisplayName="{esc(display)}" '
                      f'Description="{esc(desc)}">{default}</{name}>')
    return (
        xml_decl() +
        '<Category xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">\n'
        f'  <ItemID idValue="{{{uuid.uuid4()}}}" />\n'
        '  <Properties>\n'
        '    <ItemName>Category1</ItemName>\n'
        '    <Images>\n'
        '      <Image cx="93" cy="123" />\n'
        '    </Images>\n'
        '  </Properties>\n'
        '  <CustomData />\n'
        '  <Source />\n'
        '  <Palettes />\n'
        '  <Packages />\n'
        '  <Tools>\n'
        f'    <Tool Name="{NAME}">\n'
        f'      <ItemID idValue="{{{uuid.uuid4()}}}" />\n'
        '      <Properties>\n'
        f'        <ItemName>{NAME}</ItemName>\n'
        '        <Images>\n'
        '          <Image cx="64" cy="64" />\n'
        '        </Images>\n'
        f'        <ToolTip>Version: {VERSION}</ToolTip>\n'
        f'        <Description>{esc(DESCRIPTION)}</Description>\n'
        '        <Help>\n'
        '          <HelpFile />\n'
        '          <HelpCommand />\n'
        '          <HelpData />\n'
        '        </Help>\n'
        '      </Properties>\n'
        '      <Source />\n'
        '      <StockToolRef idValue="{7F55AAC0-0256-48D7-BFA5-914702663FDE}" />\n'
        '      <Data>\n'
        '        <AeccDbSubassembly>\n'
        '          <GeometryGenerateMode>UseDotNet</GeometryGenerateMode>\n'
        f'          <DotNetClass Assembly="{GUID}.xaml">Subassembly.{NAME}</DotNetClass>\n'
        f'          <Version>{VERSION}</Version>\n'
        '          <Params>\n' + "\n".join(params) + '\n'
        '          </Params>\n'
        '        </AeccDbSubassembly>\n'
        '        <Units>m</Units>\n'
        '      </Data>\n'
        '    </Tool>\n'
        '  </Tools>\n'
        '  <StockTools />\n'
        '</Category>')


def build_cfg():
    return (xml_decl() +
            '<Configuration xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">\n'
            '  <CreatedWith>\n'
            '    <ProductName>Autodesk Subassembly Composer</ProductName>\n'
            '    <Version>ForVail</Version>\n'
            '    <VersionNumber>13.7.1429.0</VersionNumber>\n'
            '  </CreatedWith>\n'
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
    unpacked = os.path.join(OUT_DIR, f"{NAME}_pkt_contents")
    os.makedirs(unpacked, exist_ok=True)
    for name, text in files:
        with open(os.path.join(unpacked, name), "w", encoding="utf-8") as f:
            f.write(text)
    print("guid", GUID)
    print("wrote", out, os.path.getsize(out), "bytes")


if __name__ == "__main__":
    main()
