import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

/**
 * civil3d_style — create, inspect, copy, edit, delete, apply and export Civil 3D object styles.
 *
 * Style families (phase 1, object styles): surface, alignment, profile, corridor, feature_line, marker,
 * code_set, link, shape, point, assembly, sample_line, section, profile_view, section_view,
 * profile_view_band_set, section_view_band_set, pipe, structure, parcel, grading, catchment, ...
 * (`list` without a family reports every family the drawing has.)
 *
 * The spec shape is the same for `get` (output) and `create` / `edit` / `copy` (input):
 *   display    → { plan|model|section|profile: { <Component>: { visible, color, layer, layerColor, linetype, linetypeScale, lineweight, plotStyle } } }
 *   settings   → sub-style objects by API name in camelCase (contourStyle, boundaryStyle, gridStyle, leftAxis ...) with their properties
 *   properties → scalar / enum properties on the style itself (arrowHeadOption, radiusSnapValue ...)
 *   references → ObjectId properties given as style names (beginPointMarkerStyle: "UTNM Start" ...)
 * plus the shorthand `surface: { contours: { majorInterval, minorInterval, smooth, ... }, boundary: { exterior, interior } }`.
 */

const FAMILIES = [
  "surface", "alignment", "profile", "corridor", "feature_line", "marker", "code_set", "link", "shape", "point",
  "assembly", "sample_line", "section", "profile_view", "section_view", "profile_view_band_set", "section_view_band_set",
  "pipe", "structure", "parcel", "grading", "catchment", "intersection", "group_plot", "view_frame", "match_line",
  "mass_haul_line", "mass_haul_view", "superelevation_view", "cant_view", "projection", "slope_pattern",
  "survey_figure", "survey_network", "sheet", "building_site", "interference", "point_cloud", "table",
] as const;

const LABEL_FAMILY_PATTERN = /^(label(_set)?:[a-z_]+(\/[a-z_]+)?|band:[a-z_]+)$/i;
const FamilySchema = z.union([
  z.enum(FAMILIES),
  z.string().regex(LABEL_FAMILY_PATTERN).describe("Label families: label:<object>/<type> (label:alignment/major_station, label:alignment/point_of_intersection, label:profile/curve, label:profile/grade_break, label:surface/spot_elevation, label:general_note ...) and label sets: label_set:alignment | label_set:profile | label_set:section; band styles: band:profile_data | band:horizontal_geometry | band:vertical_geometry | band:superelevation | band:sectional_data | band:pipe_network | band:section_data | band:section_segments (band sets: profile_view_band_set / section_view_band_set with items[]). `list` without a family reports them all."),
]);
const ColorSchema = z.union([z.number().int().min(0).max(256), z.string()]).describe("ACI index 1-255, 'ByLayer', 'ByBlock', 'r,g,b' or '#rrggbb'.");
const LineweightSchema = z.union([z.number().nonnegative(), z.string()]).describe("Millimetres (0.13, 0.25, 0.35, 0.5 ...), 'ByLayer', 'ByBlock' or 'Default'.");

const DisplayComponentSchema = z.object({
  visible: z.boolean().optional(),
  color: ColorSchema.optional(),
  layer: z.string().optional().describe("Layer name; created if missing."),
  layerColor: ColorSchema.optional().describe("Colour for a layer that has to be created."),
  linetype: z.string().optional().describe("Linetype name; loaded from acadiso.lin if missing."),
  linetypeScale: z.number().positive().optional(),
  lineweight: LineweightSchema.optional(),
  plotStyle: z.string().optional(),
}).strict();

const DisplayViewSchema = z.record(z.string(), DisplayComponentSchema);
const DisplaySchema = z.object({
  plan: DisplayViewSchema.optional(),
  model: DisplayViewSchema.optional(),
  section: DisplayViewSchema.optional(),
  profile: DisplayViewSchema.optional(),
}).strict();

const ScalarSchema = z.union([z.string(), z.number(), z.boolean(), z.null()]);
const SettingsObjectSchema: z.ZodType<Record<string, unknown>> = z.lazy(() => z.record(z.string(), z.union([ScalarSchema, SettingsObjectSchema, DisplaySchema])));

const LabelComponentSchema = z.object({
  name: z.string().min(1),
  type: z.enum(["Text", "Line", "Block", "Tick", "ReferenceText", "DirectionArrow", "TextForEach"]).optional().describe("Needed when the component does not exist yet."),
  selectedType: z.string().optional().describe("ReferenceText: object type (Alignment, Profile, Surface ...); TextForEach: Curve | Spiral | CurveOrSpiral."),
  general: z.record(z.string(), ScalarSchema).optional().describe("anchorComponent ('<Feature>' or another component name), anchorLocation (AnchorPointType name: Station, PointOfIntersection, Middle, TopLeft ... — anchorPoint is a read-only native code), visible; line: startPointAnchorComponent, startAnchorPoint, endPointAnchorComponent, endAnchorPoint, useEndPointAnchor"),
  text: z.record(z.string(), ScalarSchema).optional().describe("contents (property fields like <[Station Value(Um|FD|P3|RN|AP|Sn|TP|B2|EN|W0|OF)]>), height (mm), attachment, angle (deg), xOffset/yOffset (mm), color, lineweight, maxWidth"),
  border: z.record(z.string(), ScalarSchema).optional().describe("visible, borderType, gap (mm), color, backgroundMask ..."),
  line: z.record(z.string(), ScalarSchema).optional().describe("angle (deg), length (mm), lengthType, fixedLength, color, linetype, lineweight, start/endPointXOffset/YOffset (mm)"),
  tick: z.record(z.string(), ScalarSchema).optional().describe("blockName (AeccTickLine ...), blockHeight (mm), alignWithObject, rotationAngle (deg), color"),
  block: z.record(z.string(), ScalarSchema).optional(),
  directionArrow: z.record(z.string(), ScalarSchema).optional(),
}).strict();

const LabelSetItemSchema = z.object({
  style: z.string().min(1).optional().describe("Label sets: label style name (searched under the set's object: alignment / profile / section)."),
  band: z.string().min(1).optional().describe("Band sets: band style name."),
  location: z.enum(["top", "bottom"]).optional().describe("Band sets: side of the graph (default bottom)."),
  labelType: z.string().optional().describe("Narrow the search to one type collection (major_station, curve ...)."),
  curve: z.enum(["crest", "sag"]).optional().describe("Profile sets: which curve group a profile/curve style is added to."),
  increment: z.number().optional(),
  geometryPoints: z.array(z.string()).optional().describe("Alignment geometry point types to label: BegOfAlign, EndOfAlign, TanTan, TanCurve, CurveTan, CurveCompCurve, CurveRevCurve, TanSpiral, SpiralTan, CurveSpiral, SpiralCurve, SpiralCompSpiral, SpiralRevSpiral, PI, CPI, SPI (profile sets: Profile* values)."),
  profileGeometryPoints: z.array(z.string()).optional(),
  superelevationPoints: z.array(z.string()).optional(),
}).catchall(ScalarSchema).refine((item) => Boolean(item.style || item.band), { message: "each item needs 'style' (label sets) or 'band' (band sets)" });

const StyleSpecSchema = z.object({
  description: z.string().optional(),
  display: DisplaySchema.optional(),
  settings: z.record(z.string(), SettingsObjectSchema).optional().describe("Sub-style objects by API name in camelCase (contourStyle, boundaryStyle, gridStyle, leftAxis ...)."),
  properties: z.record(z.string(), z.union([ScalarSchema, SettingsObjectSchema])).optional().describe("Scalar/enum properties on the style itself (camelCase API names). Label styles: the groups label, behavior, planReadability, leader, draggedState."),
  components: z.array(LabelComponentSchema).optional().describe("Label styles: components in draw order; missing ones are created (type required), existing ones edited."),
  removeComponents: z.array(z.string()).optional().describe("Label styles: component names to remove."),
  replaceComponents: z.boolean().optional().describe("Label styles: drop components the spec doesn't name (default true on create — clears Civil 3D's defaults; false on edit)."),
  items: z.array(LabelSetItemSchema).optional().describe("Label sets: the set's items (replaces all existing items unless replaceItems is false). Band sets: [{band, location: top|bottom, gap (mm), majorInterval, minorInterval, showLabels, staggerLabel, staggerLineHeight (mm), weeding, labelAtStartStation, labelAtEndStation}]."),
  labels: z.record(z.string(), z.record(z.string(), z.unknown())).optional().describe("Band styles: the label styles owned by the band (majorIncrement, minorIncrement, hgp, vgp, stationEquation, incrementalDistance, titleText; tangent/curve/spiral/pointOfIntersection; uphillTangent/downhillTangent/crestCurve/sagCurve) — each a label style spec (properties, components, replaceComponents)."),
  replaceItems: z.boolean().optional(),
  references: z.record(z.string(), z.string().nullable()).optional().describe("ObjectId properties by style name (beginPointMarkerStyle: 'UTNM Start')."),
  markers: z.record(z.string(), z.string().nullable()).optional().describe("Alias of references."),
  surface: z.record(z.string(), SettingsObjectSchema).optional().describe("Shorthand: { contours: { majorInterval, minorInterval, baseElevation, smooth, smoothingType, smoothingFactor, depressions }, boundary: { exterior, interior }, elevations: {...}, slopes: {...}, points: {...}, triangles: {...} }"),
  alignment: z.record(z.string(), SettingsObjectSchema).optional(),
  profile: z.record(z.string(), SettingsObjectSchema).optional(),
  corridor: z.record(z.string(), SettingsObjectSchema).optional(),
}).strict();

const IfExistsSchema = z.enum(["error", "update", "skip"]);

const ListArgs = z.object({
  action: z.literal("list"),
  family: FamilySchema.optional().describe("Omit to get every family with its style count."),
  includeUsage: z.boolean().optional().describe("Also report which drawing objects use each style (slower)."),
});
const GetArgs = z.object({ action: z.literal("get"), family: FamilySchema, name: z.string().min(1) });
const CreateArgs = z.object({
  action: z.literal("create"),
  family: FamilySchema,
  name: z.string().min(1),
  spec: StyleSpecSchema.optional(),
  ifExists: IfExistsSchema.optional().describe("error (default) | update (apply the spec to the existing style) | skip"),
}).merge(StyleSpecSchema.partial()).strip();
const EditArgs = z.object({
  action: z.literal("edit"),
  family: FamilySchema,
  name: z.string().min(1),
  newName: z.string().min(1).optional(),
  spec: StyleSpecSchema.optional(),
}).merge(StyleSpecSchema.partial()).strip();
const CopyArgs = z.object({
  action: z.literal("copy"),
  family: FamilySchema,
  source: z.string().min(1),
  name: z.string().min(1),
  spec: StyleSpecSchema.optional().describe("Partial spec applied to the copy."),
  ifExists: IfExistsSchema.optional(),
}).merge(StyleSpecSchema.partial()).strip();
const DeleteArgs = z.object({
  action: z.literal("delete"),
  family: FamilySchema,
  name: z.string().min(1),
  replaceWith: z.string().min(1).optional().describe("Style to reassign to objects that use the deleted one (required when in use)."),
});
const ApplyTargetSchema = z.object({
  handle: z.string().optional(),
  type: z.enum(["surface", "alignment", "profile", "corridor", "feature_line", "profile_view"]).optional(),
  name: z.string().optional(),
  alignmentName: z.string().optional().describe("For profile targets."),
}).refine((v) => Boolean(v.handle) || (Boolean(v.type) && Boolean(v.name)), { message: "Give {handle} or {type, name}." });
const ApplyArgs = z.object({
  action: z.literal("apply"),
  family: FamilySchema,
  name: z.string().min(1),
  objects: z.array(ApplyTargetSchema).min(1),
  replaceGroups: z.boolean().optional().describe("label_set:profile: erase the profile's existing label groups of the same types before creating the new ones (default true)."),
  dimensionAnchorScale: z.number().optional().describe("label_set:profile: multiplier applied to the set items' dimension anchor value when it is copied onto the label groups (default 1)."),
});
const LibraryItemSchema = StyleSpecSchema.extend({
  family: FamilySchema,
  name: z.string().min(1),
  ifExists: IfExistsSchema.optional(),
});
const LibraryApplyArgs = z.object({
  action: z.literal("library_apply"),
  items: z.array(LibraryItemSchema).optional(),
  path: z.string().optional().describe("JSON file: an array of style specs, or {\"styles\": [...]}."),
  ifExists: IfExistsSchema.optional().describe("Default for items that don't say: update."),
}).refine((v) => (v.items && v.items.length > 0) || Boolean(v.path), { message: "Give items[] or path." });
const ExportArgs = z.object({
  action: z.literal("export"),
  family: FamilySchema,
  names: z.array(z.string().min(1)).optional().describe("Omit to export the whole family."),
  outputPath: z.string().min(1).describe("Target .dwg (created; a style library seed)."),
  overwrite: z.boolean().optional(),
});

const AbbreviationsArgs = z.object({
  action: z.literal("abbreviations"),
  alignment: z.record(z.string(), z.string()).optional().describe("AbbreviationAlignmentType → text, e.g. { AlignmentBeginning: 'PBT', AlignmentEnd: 'PAT', TangentCurveIntersect: 'PC' }."),
  alignmentEntity: z.record(z.string(), z.string()).optional(),
  profile: z.record(z.string(), z.string()).optional().describe("AbbreviationProfileType → text (ProfileStart, ProfileEnd, BeginVerticalCurve, EndVerticalCurve, PointOfVerticalIntersection ...)."),
});
const PlaceLabelsArgs = z.object({
  action: z.literal("place_labels"),
  kind: z.enum(["alignment_segments", "alignment_stations", "alignment_ip_tables", "profile_numbers"]),
  alignmentName: z.string().min(1),
  profileName: z.string().optional(),
  profileViewName: z.string().optional(),
  tangentStyle: z.string().optional().describe("alignment_segments: label:alignment/line style for every tangent."),
  curveStyle: z.string().optional().describe("alignment_segments: label:alignment/curve style for every curve."),
  spiralStyle: z.string().optional(),
  piStyle: z.string().optional().describe("alignment_segments: label:alignment/point_of_intersection style, one per curve entity."),
  piNumberComponent: z.string().optional().describe("Text component of the PI style that receives the IP number (default 'IP Number')."),
  piNumberFormat: z.string().optional().describe("Default 'I.P. {n}'."),
  piNumbers: z.array(z.number().int()).optional().describe("IP numbers per curve entity in station order (from the design table); default piStartNumber, +1 each."),
  piStartNumber: z.number().int().optional(),
  numberComponent: z.string().optional().describe("profile_numbers: text component of the PVI / crest / sag label styles that receives the VIP number (default 'VIP Number')."),
  numberFormat: z.string().optional().describe("Default 'VIP NO.  {n}'."),
  numbers: z.array(z.number().int()).optional().describe("VIP numbers per PVI in station order (from the design table); default startNumber, +1 each."),
  startNumber: z.number().int().optional(),
  stationStyle: z.string().optional().describe("alignment_stations: label:alignment/station_offset style for the start/end chainage labels."),
  markerStyle: z.string().optional().describe("alignment_stations: marker style (default '_No Markers')."),
  stations: z.array(z.number()).optional().describe("alignment_stations: stations to label (default: alignment start and end)."),
  offset: z.number().optional(),
  overrides: z.array(z.object({ station: z.number(), components: z.record(z.string(), z.string()) })).optional().describe("alignment_stations: text component overrides per station, e.g. [{station: 0, components: {'IP Number': 'I.P. 1'}}]."),
  endStyle: z.string().optional().describe("alignment_ip_tables: label:alignment/station_offset style for the start/end IP tables."),
  coordsComponent: z.string().optional(),
  dataComponent: z.string().optional(),
  coordsFormat: z.string().optional(),
  dataFormat: z.string().optional().describe("alignment_ip_tables: default 'A   = {A}\\PAc  = {Ac}\\PT   = {T}\\PEs  = {Es}\\PL.C. = {LC}' (A deflection DMS, Ac ahead azimuth DMS, T/Es/LC metres)."),
  replace: z.boolean().optional().describe("alignment_ip_tables: erase the alignment's existing PI / station-offset labels first (default true)."),
  overrideCoords: z.boolean().optional(),
});
const InspectLabelsArgs = z.object({
  action: z.literal("inspect_labels"),
  alignmentName: z.string().min(1),
  profileName: z.string().optional(),
  profileViewName: z.string().optional(),
});

const GenericResponse = z.object({}).passthrough();

function stripAction(args: Record<string, unknown>): Record<string, unknown> {
  const { action: _action, ...rest } = args;
  return rest;
}

export const STYLE_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "style",
  actions: {
    list: {
      action: "list", inputSchema: ListArgs, responseSchema: GenericResponse,
      capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["listStyles"],
      execute: async (args) => await withApplicationConnection(async (c) => await c.sendCommand("listStyles", stripAction(args))),
    },
    get: {
      action: "get", inputSchema: GetArgs, responseSchema: GenericResponse,
      capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["getStyle"],
      execute: async (args) => await withApplicationConnection(async (c) => await c.sendCommand("getStyle", stripAction(args))),
    },
    create: {
      action: "create", inputSchema: CreateArgs, responseSchema: GenericResponse,
      capabilities: ["create", "manage"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["createStyle"],
      execute: async (args) => await withApplicationConnection(async (c) => await c.sendCommand("createStyle", stripAction(args))),
    },
    edit: {
      action: "edit", inputSchema: EditArgs, responseSchema: GenericResponse,
      capabilities: ["edit", "manage"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["editStyle"],
      execute: async (args) => await withApplicationConnection(async (c) => await c.sendCommand("editStyle", stripAction(args))),
    },
    copy: {
      action: "copy", inputSchema: CopyArgs, responseSchema: GenericResponse,
      capabilities: ["create", "manage"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["copyStyle"],
      execute: async (args) => await withApplicationConnection(async (c) => await c.sendCommand("copyStyle", stripAction(args))),
    },
    delete: {
      action: "delete", inputSchema: DeleteArgs, responseSchema: GenericResponse,
      capabilities: ["delete", "manage"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["deleteStyle"],
      execute: async (args) => await withApplicationConnection(async (c) => await c.sendCommand("deleteStyle", stripAction(args))),
    },
    apply: {
      action: "apply", inputSchema: ApplyArgs, responseSchema: GenericResponse,
      capabilities: ["edit", "manage"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["applyStyle"],
      execute: async (args) => await withApplicationConnection(async (c) => await c.sendCommand("applyStyle", stripAction(args))),
    },
    library_apply: {
      action: "library_apply", inputSchema: LibraryApplyArgs, responseSchema: GenericResponse,
      capabilities: ["create", "edit", "manage"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["applyStyleLibrary"],
      execute: async (args) => await withApplicationConnection(async (c) => await c.sendCommand("applyStyleLibrary", stripAction(args))),
    },
    export: {
      action: "export", inputSchema: ExportArgs, responseSchema: GenericResponse,
      capabilities: ["export", "manage"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["exportStyles"],
      execute: async (args) => await withApplicationConnection(async (c) => await c.sendCommand("exportStyles", stripAction(args))),
    },
    abbreviations: {
      action: "abbreviations", inputSchema: AbbreviationsArgs, responseSchema: GenericResponse,
      capabilities: ["edit", "manage"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["styleAbbreviations"],
      execute: async (args) => await withApplicationConnection(async (c) => await c.sendCommand("styleAbbreviations", stripAction(args))),
    },
    place_labels: {
      action: "place_labels", inputSchema: PlaceLabelsArgs, responseSchema: GenericResponse,
      capabilities: ["create", "edit"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["placeLabels"],
      execute: async (args) => await withApplicationConnection(async (c) => await c.sendCommand("placeLabels", stripAction(args))),
    },
    inspect_labels: {
      action: "inspect_labels", inputSchema: InspectLabelsArgs, responseSchema: GenericResponse,
      capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["placeLabels"],
      execute: async (args) => await withApplicationConnection(async (c) => await c.sendCommand("placeLabels", { ...stripAction(args), kind: "inspect" })),
    },
  },
  exposures: [
    {
      toolName: "civil3d_style",
      displayName: "Civil 3D Style",
      description: "Creates, inspects, copies, edits, deletes, applies and exports Civil 3D styles from a JSON spec: object styles (surface, alignment, profile, corridor, feature line, marker, code set, profile/section view ...), label styles with their components (family label:<object>/<type>) and label sets (label_set:alignment|profile|section). 'get' returns the same spec shape 'create' accepts, so styles round-trip between drawings; 'library_apply' upserts a whole JSON library; 'apply' with a label_set family imports the set into an alignment or profile; 'place_labels' adds tangent/curve/spiral/PI labels along an alignment and numbers IP/VIP labels from the design tables; 'abbreviations' reads/sets the drawing's geometry-point abbreviations (PBT/PAT ...); 'inspect_labels' (read-only) lists an alignment's / profile's labels with locations, stations, dragged state, dimension anchors and text overrides. NOTE: the IP tables (alignment_ip_tables), IP numbers and VIP numbers/PBT/PAT (profile_numbers) are written into the labels as text overrides computed from the geometry, not live property fields — after ANY edit to an alignment or profile (PIs, radii, spirals, PVIs, curves) re-run place_labels alignment_ip_tables / profile_numbers so the tables show the new values.",
      inputShape: {
        action: z.enum(["list", "get", "create", "edit", "copy", "delete", "apply", "library_apply", "export", "abbreviations", "place_labels", "inspect_labels"]),
        family: FamilySchema.optional(),
        name: z.string().optional(),
        newName: z.string().optional(),
        source: z.string().optional().describe("copy: style to copy from."),
        spec: StyleSpecSchema.optional(),
        description: z.string().optional(),
        display: DisplaySchema.optional(),
        settings: z.record(z.string(), SettingsObjectSchema).optional(),
        properties: z.record(z.string(), z.union([ScalarSchema, SettingsObjectSchema])).optional(),
        references: z.record(z.string(), z.string().nullable()).optional(),
        surface: z.record(z.string(), SettingsObjectSchema).optional(),
        components: z.array(LabelComponentSchema).optional(),
        removeComponents: z.array(z.string()).optional(),
        replaceComponents: z.boolean().optional(),
        replaceItems: z.boolean().optional(),
        labels: z.record(z.string(), z.record(z.string(), z.unknown())).optional(),
        ifExists: IfExistsSchema.optional(),
        includeUsage: z.boolean().optional(),
        replaceWith: z.string().optional(),
        objects: z.array(ApplyTargetSchema).optional(),
        items: z.array(z.record(z.string(), z.unknown())).optional().describe("library_apply: style specs (family, name, ...); create/edit of a label_set: the set items ({style, increment, geometryPoints, curve})."),
        alignment: z.record(z.string(), z.unknown()).optional().describe("abbreviations: AbbreviationAlignmentType → text; create: alignment settings shorthand."),
        profile: z.record(z.string(), z.unknown()).optional().describe("abbreviations: AbbreviationProfileType → text; create: profile settings shorthand."),
        alignmentEntity: z.record(z.string(), z.string()).optional(),
        kind: z.enum(["alignment_segments", "alignment_stations", "alignment_ip_tables", "profile_numbers"]).optional(),
        alignmentName: z.string().optional(),
        profileName: z.string().optional(),
        profileViewName: z.string().optional(),
        tangentStyle: z.string().optional(),
        curveStyle: z.string().optional(),
        spiralStyle: z.string().optional(),
        piStyle: z.string().optional(),
        piNumberComponent: z.string().optional(),
        piNumberFormat: z.string().optional(),
        piNumbers: z.array(z.number().int()).optional(),
        piStartNumber: z.number().int().optional(),
        numberComponent: z.string().optional(),
        numberFormat: z.string().optional(),
        numbers: z.array(z.number().int()).optional(),
        startNumber: z.number().int().optional(),
        stationStyle: z.string().optional(),
        markerStyle: z.string().optional(),
        stations: z.array(z.number()).optional(),
        offset: z.number().optional(),
        overrides: z.array(z.object({ station: z.number(), components: z.record(z.string(), z.string()) })).optional(),
        endStyle: z.string().optional(),
        coordsComponent: z.string().optional(),
        dataComponent: z.string().optional(),
        coordsFormat: z.string().optional(),
        dataFormat: z.string().optional(),
        replace: z.boolean().optional(),
        overrideCoords: z.boolean().optional(),
        replaceGroups: z.boolean().optional(),
        dimensionAnchorScale: z.number().optional(),
        path: z.string().optional(),
        names: z.array(z.string()).optional(),
        outputPath: z.string().optional(),
        overwrite: z.boolean().optional(),
      },
      supportedActions: ["list", "get", "create", "edit", "copy", "delete", "apply", "library_apply", "export", "abbreviations", "place_labels", "inspect_labels"],
      capabilities: ["query", "inspect", "create", "edit", "delete", "manage", "export"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      resolveAction: (rawArgs) => ({ action: String(rawArgs.action ?? ""), args: rawArgs }),
    },
  ],
};
