"""
Generate UTNM_RCDrain.pkt — a Subassembly Composer package for a parametric RC U-drain —
using an Autodesk-shipped SAC package (RailPlatform, Civil 3D 2026) as the structural template.

Files inside a .pkt (as Civil 3D extracts them into XamlSubassemblies\<Units>\<Name>\):
  <guid>.xaml  WF4 flowchart with Autodesk.SubassemblyComposer activities (the geometry)
  <guid>.atc   tool-catalog entry: name, description, parameter defaults, DotNetClass
  <guid>.dll   stub assembly that compiles the runtime proxy; patched from the template
  <guid>.cfg   "created with" stamp
  <guid>.emd   enum data (empty)
  <guid>.pvd   preview data (superelevation/cant defaults)
  <name>.png   palette icon
"""
import io, os, re, uuid, zipfile, struct, zlib

TEMPLATE_DIR = "/mnt/user-data/uploads/enu/XamlSubassemblies/Metric/RailPlatform"
TEMPLATE_GUID = "f0421fc6bf51476a84133b279d4d1937"
TEMPLATE_CLASS = "RailPlatform"          # 12 characters — the patched name must be the same length

NAME = "UTNM_RCDrain"                    # 12 characters (stub-DLL constraint), also the class name
assert len(NAME) == len(TEMPLATE_CLASS)
DESCRIPTION = ("UTNM parametric RC U-drain (both sides in one). Origin at the drain centreline on the invert "
               "level (IL): use the IL as the corridor profile. Inputs: InternalWidth, Depth (IL to top of wall), "
               "WallThickness, BaseThickness. Flat invert. Codes: DrainTopIn/Out_L/R, DrainInvert_L/R, DrainBase_L/R, "
               "DrainCL; links Top/Datum; shape DrainConcrete.")
GUID = uuid.uuid4().hex
OUT_DIR = "/mnt/user-data/outputs"

PARAMS = [  # name, display, description, default
    ("InternalWidth", "Internal Width (W)", "Clear width between the wall faces", 1.5),
    ("Depth", "Depth IL to Top (D)", "Invert level to top of wall (engineer's PL - IL)", 1.5),
    ("WallThickness", "Wall Thickness (t)", "Thickness of each wall", 0.3),
    ("BaseThickness", "Base Slab Thickness (b)", "Base slab thickness below the invert", 0.45),
]

# Points: (id, from, dx, dy, codes)
POINTS = [
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
# Links: (id, start, end, codes)
LINKS = [
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


def read_template(ext, mode="r"):
    path = os.path.join(TEMPLATE_DIR, TEMPLATE_GUID + ext)
    with open(path, mode, **({} if mode == "rb" else {"encoding": "utf-8-sig"})) as f:
        return f.read()


def esc(s):
    return s.replace("&", "&amp;").replace('"', "&quot;").replace("<", "&lt;").replace(">", "&gt;")


# ---------------------------------------------------------------- XAML
def build_xaml():
    tpl = read_template(".xaml")
    # namespaces block and flowchart variables are copied verbatim from the template
    ns_block = tpl[tpl.index("\n xmlns="):tpl.index(">\n  <x:Members>")]
    vars_block = tpl[tpl.index("    <Flowchart.Variables>"):tpl.index("    </Flowchart.Variables>") + len("    </Flowchart.Variables>")]

    defaults = ' this:Subassembly.Side="[new EnumType(0, &quot;Right&quot;, &quot;Side&quot;)]"'
    for name, _, _, default in PARAMS:
        defaults += f' this:Subassembly.{name}="{default}"'

    members = [
        '    <x:Property Name="Geometry" Type="InOutArgument(asw:Geometry)" />',
        '    <x:Property Name="SubassemblyErrorCenter" Type="InOutArgument(asw:SubassemblyErrorCenter)" />',
        '    <x:Property Name="SubassemblyRunMode" Type="InOutArgument(asw:SubassemblyRunMode)" />',
        '    <x:Property Name="Side" Type="InArgument(asw:EnumType)" />',
    ]
    for name, display, desc, _ in PARAMS:
        members.append(
            f'    <x:Property Name="{name}" Type="InArgument(x:Double)">\n'
            f'      <x:Property.Attributes>\n'
            f'        <asw:DisplayName2Attribute DisplayName="{esc(display)}" />\n'
            f'        <asw:Description2Attribute Description="{esc(desc)}" />\n'
            f'      </x:Property.Attributes>\n'
            f'    </x:Property>')

    common = ('Geometry="[Geometry]" sap:VirtualizedContainerService.HintSize="200,22" ShowErrors="True" '
              'SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]"')

    def codes_xml(tag, codes, indent):
        pad = " " * indent
        inner = "\n".join(f'{pad}    <InArgument x:TypeArguments="x:String">{esc(c)}</InArgument>' for c in codes)
        return (f'{pad}<{tag}>\n{pad}  <scg:List x:TypeArguments="InArgument(x:String)" Capacity="4">\n'
                f'{inner}\n{pad}  </scg:List>\n{pad}</{tag}>')

    body = []
    aid = 1
    for pid, frm, dx, dy, codes in POINTS:
        from_attr = 'FromPoint="{x:Null}" ' if frm is None else f'FromPoint="{frm}" '
        body.append(
            f'              <asa1:CreatePoint AutoLinkCodes="{{x:Null}}" AutoLinkGeometryName="{{x:Null}}" {from_attr}'
            f'ActivityId="{aid}" ApplyAOR="False" AutoLink="False" DisplayName="{pid}" {common} '
            f'PointNumber="{pid}" Positioning="DeltaXAndDeltaY" Side="[Side]">\n'
            f'                <asa1:CreatePoint.Arguments>\n'
            f'                  <InArgument x:TypeArguments="x:Double" x:Key="DeltaX1">{esc(dx)}</InArgument>\n'
            f'                  <InArgument x:TypeArguments="x:Double" x:Key="DeltaY1">{esc(dy)}</InArgument>\n'
            f'                </asa1:CreatePoint.Arguments>\n'
            + codes_xml("asa1:CreatePoint.PointCodes", codes, 16) + "\n"
            f'              </asa1:CreatePoint>')
        aid += 1
    for lid, a, b, codes in LINKS:
        body.append(
            f'              <asa1:CreateLink ActivityId="{aid}" ApplyAOR="False" DisplayName="{lid}" EndPoint="{b}" '
            f'{common} IsEnabled="True" LinkNumber="{lid}" StartPoint="{a}">\n'
            + codes_xml("asa1:CreateLink.LinkCodes", codes, 16) + "\n"
            f'              </asa1:CreateLink>')
        aid += 1
    for sid, links, codes in SHAPES:
        names = "\n".join(f'                  <x:String>{l}</x:String>' for l in links)
        body.append(
            f'              <asa1:CreateShape ActivityId="{aid}" DisplayName="{sid}" {common} Links="{",".join(links)}" '
            f'ShapeNumber="{sid}">\n'
            f'                <asa1:CreateShape.ComponentNames>\n{names}\n                </asa1:CreateShape.ComponentNames>\n'
            + codes_xml("asa1:CreateShape.ShapeCodes", codes, 16) + "\n"
            f'              </asa1:CreateShape>')
        aid += 1

    xaml = (
        '﻿<Activity mc:Ignorable="sads sap" x:Class="Subassembly"' + defaults + ns_block + '>\n'
        '  <x:Members>\n' + "\n".join(members) + '\n  </x:Members>\n'
        '  <sap:VirtualizedContainerService.HintSize>684,883</sap:VirtualizedContainerService.HintSize>\n'
        '  <mva:VisualBasic.Settings>Assembly references and imported namespaces serialized as XML namespaces</mva:VisualBasic.Settings>\n'
        '  <Flowchart sap:VirtualizedContainerService.HintSize="644,843" mva:VisualBasic.Settings="Assembly references and imported namespaces serialized as XML namespaces">\n'
        + vars_block + '\n'
        '    <Flowchart.StartNode>\n'
        '      <FlowStep x:Name="__ReferenceID0">\n'
        '        <sap:WorkflowViewStateService.ViewState>\n'
        '          <scg:Dictionary x:TypeArguments="x:String, x:Object">\n'
        '            <av:Point x:Key="ShapeLocation">200,99</av:Point>\n'
        '            <av:Size x:Key="ShapeSize">200,51</av:Size>\n'
        '          </scg:Dictionary>\n'
        '        </sap:WorkflowViewStateService.ViewState>\n'
        '        <Sequence DisplayName="RC U-drain" sap:VirtualizedContainerService.HintSize="200,51">\n'
        '          <sap:WorkflowViewStateService.ViewState>\n'
        '            <scg:Dictionary x:TypeArguments="x:String, x:Object">\n'
        '              <x:Boolean x:Key="IsExpanded">True</x:Boolean>\n'
        '            </scg:Dictionary>\n'
        '          </sap:WorkflowViewStateService.ViewState>\n'
        + "\n".join(body) + '\n'
        '        </Sequence>\n'
        '      </FlowStep>\n'
        '    </Flowchart.StartNode>\n'
        '    <x:Reference>__ReferenceID0</x:Reference>\n'
        '  </Flowchart>\n'
        '</Activity>')
    return xaml


# ---------------------------------------------------------------- ATC
def build_atc():
    params = ['            <Side DataType="long" TypeInfo="16" DisplayName="Side" Description="Side">0<Enum>'
              '<Left DisplayName="Left">1</Left><Right DisplayName="Right">0</Right></Enum></Side>']
    for name, display, desc, default in PARAMS:
        params.append(f'            <{name} DataType="double" TypeInfo="16" DisplayName="{esc(display)}" '
                      f'Description="{esc(desc)}">{default}</{name}>')
    return (
        '<?xml version="1.0"?>\n'
        '<Category xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">\n'
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
        f'          <Image cx="64" cy="64" src="{NAME}.png" />\n'
        '        </Images>\n'
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
        f'          <DotNetClass Assembly="{GUID}.dll">Subassembly.{NAME}</DotNetClass>\n'
        '          <Params>\n' + "\n".join(params) + '\n'
        '          </Params>\n'
        '        </AeccDbSubassembly>\n'
        '        <Units>m</Units>\n'
        '      </Data>\n'
        '    </Tool>\n'
        '  </Tools>\n'
        '  <StockTools />\n'
        '</Category>')


# ---------------------------------------------------------------- DLL (same-length string patch)
def build_dll():
    d = read_template(".dll", "rb")
    for old, new in [(TEMPLATE_GUID, GUID), (TEMPLATE_CLASS, NAME)]:
        assert len(old) == len(new)
        for enc in ("ascii", "utf-16-le"):
            o, n = old.encode(enc), new.encode(enc)
            assert d.count(o) > 0, (old, enc)
            d = d.replace(o, n)
    assert TEMPLATE_GUID.encode() not in d and TEMPLATE_CLASS.encode() not in d
    return d


# ---------------------------------------------------------------- icon (64x64 PNG, drawn by hand)
def build_png():
    w = h = 64
    rows = []
    for y in range(h):
        row = bytearray([0])
        for x in range(w):
            wall = (14 <= x < 20 or 44 <= x < 50) and 12 <= y < 56
            base = 50 <= y < 56 and 14 <= x < 50
            if wall or base:
                row += bytes([90, 90, 90, 255])
            elif 20 <= x < 44 and 12 <= y < 50:
                row += bytes([210, 232, 250, 255])
            else:
                row += bytes([255, 255, 255, 0])
        rows.append(bytes(row))
    raw = b"".join(rows)

    def chunk(tag, data):
        c = tag + data
        return struct.pack(">I", len(data)) + c + struct.pack(">I", zlib.crc32(c) & 0xFFFFFFFF)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    files = {
        f"{GUID}.xaml": build_xaml().encode("utf-8"),
        f"{GUID}.atc": build_atc().encode("utf-8"),
        f"{GUID}.dll": build_dll(),
        f"{GUID}.cfg": read_template(".cfg").encode("utf-8"),
        f"{GUID}.emd": read_template(".emd").encode("utf-8"),
        f"{GUID}.pvd": read_template(".pvd").encode("utf-8"),
        f"{NAME}.png": build_png(),
    }
    out = os.path.join(OUT_DIR, f"{NAME}.pkt")
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for name, data in files.items():
            z.writestr(name, data)
    # also drop the unpacked files for inspection
    unpacked = os.path.join(OUT_DIR, f"{NAME}_pkt_contents")
    os.makedirs(unpacked, exist_ok=True)
    for name, data in files.items():
        with open(os.path.join(unpacked, name), "wb") as f:
            f.write(data)
    print("guid", GUID)
    print("wrote", out, os.path.getsize(out), "bytes")


if __name__ == "__main__":
    main()
