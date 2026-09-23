import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";
import { JobLaunchResponseSchema } from "./sharedSchemas.js";

// ─── Shared schemas ───────────────────────────────────────────────────────────

const GenericCorridorResponseSchema = z.object({}).passthrough();

const CorridorStateSchema = z.enum(["built", "out_of_date", "error"]);

const CorridorSummarySchema = z.object({
  name: z.string(),
  handle: z.string(),
  baselineCount: z.number(),
  regionCount: z.number(),
  surfaceCount: z.number(),
  state: CorridorStateSchema,
  lastBuildTime: z.string().nullable(),
});

const CorridorListResponseSchema = z.object({
  corridors: z.array(CorridorSummarySchema),
});

const CorridorRegionSchema = z.object({
  name: z.string(),
  assemblyName: z.string(),
  startStation: z.number(),
  endStation: z.number(),
  frequency: z.union([z.number(), z.record(z.unknown())]).optional(),
}).passthrough();

const CorridorBaselineSchema = z.object({
  name: z.string(),
  featureLineBased: z.boolean().optional(),
  alignmentName: z.string(),
  profileName: z.string(),
  featureLineName: z.string().nullable().optional(),
  regions: z.array(CorridorRegionSchema),
});

const CorridorSurfaceSchema = z.object({
  name: z.string(),
  boundaries: z.array(z.string()),
});

const CorridorDetailResponseSchema = z.object({
  name: z.string(),
  handle: z.string(),
  style: z.string(),
  layer: z.string(),
  baselines: z.array(CorridorBaselineSchema),
  surfaces: z.array(CorridorSurfaceSchema),
  featureLineCount: z.number(),
  state: CorridorStateSchema,
});

const CorridorSurfacesResponseSchema = z.object({
  corridorName: z.string().optional(),
  surfaces: z.array(CorridorSurfaceSchema),
});

const CorridorFeatureLineSchema = z.record(z.unknown());

const CorridorFeatureLinesResponseSchema = z.object({
  corridorName: z.string().optional(),
  featureLines: z.array(CorridorFeatureLineSchema),
});

// The plugin now returns {jobId, state:"running", message} and completes the
// rebuild on a background task. Use the shared job-launch envelope so the
// contract is explicit.
const CorridorRebuildResponseSchema = JobLaunchResponseSchema;

const CorridorVolumesResponseSchema = z.object({
  cutVolume: z.number(),
  fillVolume: z.number(),
  netVolume: z.number(),
  cutArea: z.number().nullable().optional(),
  fillArea: z.number().nullable().optional(),
  units: z.object({
    volume: z.string(),
    area: z.string().optional(),
  }),
});

const CorridorFullSummaryResponseSchema = z.object({
  corridor: CorridorDetailResponseSchema,
  surfaceInventory: CorridorSurfacesResponseSchema,
  volumeAnalysis: CorridorVolumesResponseSchema.nullable(),
  summary: z.object({
    baselineCount: z.number(),
    regionCount: z.number(),
    corridorSurfaceCount: z.number(),
    featureLineCount: z.number(),
    state: CorridorStateSchema,
    totalRegionLength: z.number(),
    selectedCorridorSurface: z.string().nullable(),
    referenceSurface: z.string().nullable(),
    volumeComputationStatus: z.enum(["computed", "skipped"]),
  }),
});

// ─── Per-action input schemas ─────────────────────────────────────────────────

const CorridorListArgsSchema = z.object({
  action: z.literal("list"),
});

const CorridorGetArgsSchema = z.object({
  action: z.literal("get"),
  name: z.string(),
});

const CorridorRebuildArgsSchema = z.object({
  action: z.literal("rebuild"),
  name: z.string(),
});

const CorridorGetSurfacesArgsSchema = z.object({
  action: z.literal("get_surfaces"),
  name: z.string(),
});

const CorridorGetFeatureLinesArgsSchema = z.object({
  action: z.literal("get_feature_lines"),
  name: z.string(),
});

const CorridorComputeVolumesArgsSchema = z.object({
  action: z.literal("compute_volumes"),
  name: z.string(),
  corridorSurface: z.string(),
  referenceSurface: z.string(),
});

const CorridorSummaryArgsSchema = z.object({
  action: z.literal("summary"),
  name: z.string(),
  corridorSurface: z.string().optional(),
  referenceSurface: z.string().optional(),
});

const CorridorTargetMappingGetArgsSchema = z.object({
  action: z.literal("target_mapping_get"),
  name: z.string(),
  regionIndex: z.number().int().nonnegative().optional(),
  baselineIndex: z.number().int().nonnegative().optional(),
});

const CorridorTargetSchema = z.object({
  parameterName: z.string().describe("Subassembly target logical name or display name, e.g. 'TargetSurface' or 'Daylight Surface'. Use target_mapping_get to list them."),
  targetType: z.enum(["surface", "alignment", "profile", "feature_line"]).optional(),
  targetName: z.string().optional().describe("Object name; a feature line can also be given as 'handle:1A2B'. Not needed with clear."),
  clear: z.boolean().optional().describe("Empty this target (give parameterName, optionally subassemblyName). Clear a target BEFORE deleting the object mapped to it."),
  targetNames: z.array(z.string()).optional().describe("Further objects of the same type on the same target (e.g. a bowtie seam and its cap on ClipTarget); Civil 3D picks one per section by targetToOption."),
  subassemblyName: z.string().optional().describe("Restrict the match to one subassembly instance in the region."),
  targetToOption: z.enum(["Nearest", "Farthest", "Flattest", "Steepest"]).optional(),
});

const CorridorTargetMappingSetArgsSchema = z.object({
  action: z.literal("target_mapping_set"),
  name: z.string(),
  regionIndex: z.number().int().nonnegative().optional(),
  regionName: z.string().optional(),
  baselineIndex: z.number().int().nonnegative().optional(),
  targets: z.array(CorridorTargetSchema),
  rebuild: z.boolean().optional(),
});

const CorridorCreateArgsSchema = z.object({
  action: z.literal("create"),
  name: z.string(),
  alignmentName: z.string().optional(),
  profileName: z.string().optional(),
  featureLineName: z.string().optional(),
  featureLineHandle: z.string().optional(),
  assemblyName: z.string(),
  baselineName: z.string().optional(),
  regionName: z.string().optional(),
  startStation: z.number().optional(),
  endStation: z.number().optional(),
  frequency: z.number().positive().optional(),
  rebuild: z.boolean().optional(),
});

const CorridorRegionAddArgsSchema = z.object({
  action: z.literal("region_add"),
  name: z.string(),
  baselineIndex: z.number().int().nonnegative().optional(),
  assemblyName: z.string(),
  regionName: z.string().optional(),
  startStation: z.number(),
  endStation: z.number(),
  frequency: z.number().positive().optional(),
  rebuild: z.boolean().optional(),
});

const CorridorRegionFrequencyArgsSchema = z.object({
  action: z.literal("region_frequency"),
  name: z.string(),
  baselineIndex: z.number().int().nonnegative().optional(),
  regionIndex: z.number().int().nonnegative().optional(),
  frequency: z.number().positive(),
  rebuild: z.boolean().optional(),
});

const CorridorSectionArgsSchema = z.object({
  action: z.literal("section"),
  name: z.string(),
  station: z.number(),
  baselineIndex: z.number().int().nonnegative().optional(),
});

const CorridorFeatureLineCodesArgsSchema = z.object({
  action: z.literal("feature_line_codes"),
  name: z.string(),
  baselineIndex: z.number().int().nonnegative().optional(),
});

const CorridorFeatureLineExportArgsSchema = z.object({
  action: z.literal("feature_line_export"),
  name: z.string(),
  code: z.string().describe("Corridor feature line code, e.g. DrainTopOut_L (see feature_line_codes)."),
  exportAs: z.enum(["alignment", "profile", "feature_line", "polyline3d"]),
  baselineIndex: z.number().int().nonnegative().optional(),
  featureLineIndex: z.number().int().nonnegative().optional().describe("When a code has several feature lines (default 0)."),
  outputName: z.string().optional().describe("Name of the created alignment / profile / feature line."),
  alignmentName: z.string().optional().describe("profile: parent alignment (default: the baseline alignment)."),
  siteName: z.string().optional().describe("alignment / feature_line: site (default: siteless)."),
  layer: z.string().optional(),
  style: z.string().optional(),
  labelSet: z.string().optional(),
  dynamic: z.boolean().optional().describe("feature_line: keep it linked to the corridor (default true)."),
  smooth: z.boolean().optional().describe("feature_line: smooth the exported line (default false)."),
});

const CorridorSurfaceBoundarySchema = z.object({
  type: z.enum(["corridor_extents", "outline", "feature_line", "polyline", "points"]).optional().describe("Default corridor_extents (outer extents of the corridor). outline = the plugin computes the outer edge from the built sections as a simple polygon (static; add again after a design change): use it where a bowtie was repaired with a valley line, since Civil 3D refuses corridor_extents there as a crossing polygon."),
  baselineIndex: z.number().int().min(0).optional().describe("outline: baseline to outline (default 0)."),
  boundaryName: z.string().optional(),
  code: z.string().optional().describe("feature_line: corridor feature line code (e.g. Daylight, DrainTopOut_L)."),
  polylineHandle: z.string().optional().describe("polyline: handle of a closed polyline."),
  points: z.array(z.union([z.array(z.number()).min(2), z.object({ x: z.number(), y: z.number(), z: z.number().optional() })])).optional().describe("points: closed polygon vertices."),
  useAs: z.enum(["outside", "inside"]).optional().describe("outside = keep data inside the boundary (default); inside = hide data inside it."),
});

const CorridorLinkCodeSchema = z.union([
  z.string(),
  z.object({ code: z.string(), breakline: z.boolean().optional() }),
]);

const CorridorSurfaceInfoArgsSchema = z.object({
  action: z.literal("surface_info"),
  name: z.string(),
  surfaceName: z.string().optional(),
});

const CorridorSurfaceCreateArgsSchema = z.object({
  action: z.literal("surface_create"),
  name: z.string(),
  surfaceName: z.string(),
  linkCodes: z.array(CorridorLinkCodeSchema).optional().describe("Link codes to build from, e.g. [\"Top\"] or [{code:\"Datum\", breakline:false}]."),
  featureLineCodes: z.array(z.string()).optional().describe("Feature line codes added as breaklines."),
  overhangCorrection: z.enum(["none", "top_links", "bottom_links"]).optional(),
  boundaries: z.array(CorridorSurfaceBoundarySchema).optional(),
  style: z.string().optional().describe("Surface style name."),
  description: z.string().optional(),
  rebuild: z.boolean().optional().describe("Rebuild the corridor so the surface is built (default true)."),
});

const CorridorSurfaceEditArgsSchema = z.object({
  action: z.literal("surface_edit"),
  name: z.string(),
  surfaceName: z.string(),
  operation: z.enum([
    "add_link_code", "remove_link_code", "set_breakline",
    "add_feature_line_code", "remove_feature_line_code",
    "set_overhang", "add_boundary", "remove_boundary",
    "set_style", "rename", "set_description", "set_build",
  ]),
  code: z.string().optional(),
  linkCodes: z.array(CorridorLinkCodeSchema).optional(),
  featureLineCodes: z.array(z.string()).optional(),
  breakline: z.boolean().optional(),
  overhangCorrection: z.enum(["none", "top_links", "bottom_links"]).optional(),
  boundary: CorridorSurfaceBoundarySchema.optional(),
  boundaryName: z.string().optional(),
  style: z.string().optional(),
  newName: z.string().optional(),
  description: z.string().optional(),
  build: z.boolean().optional(),
  rebuild: z.boolean().optional(),
});

const CorridorSurfaceDeleteArgsSchema = z.object({
  action: z.literal("surface_delete"),
  name: z.string(),
  surfaceName: z.string(),
  rebuild: z.boolean().optional(),
});

const CorridorExportSolidsArgsSchema = z.object({
  action: z.literal("export_solids"),
  name: z.string(),
  exportShapes: z.boolean().optional().describe("Solids from subassembly shapes (default true)."),
  exportLinks: z.boolean().optional().describe("Also extract link bodies (default false)."),
  createSolidForShape: z.boolean().optional().describe("Default true."),
  sweepSolidForShape: z.boolean().optional().describe("Sweep instead of loft between sections (default false)."),
  includedCodes: z.array(z.string()).optional().describe("Only these shape/link codes."),
  excludedCodes: z.array(z.string()).optional(),
  layer: z.string().optional().describe("Move all solids to this layer after extraction (current drawing only)."),
  outputPath: z.string().optional().describe("Write the solids to a new .dwg instead of the current drawing."),
  maxListed: z.number().int().positive().optional().describe("How many solids to list individually (default 50)."),
});

const CorridorRegionDeleteArgsSchema = z.object({
  action: z.literal("region_delete"),
  name: z.string(),
  baselineIndex: z.number().int().nonnegative().optional(),
  regionIndex: z.number().int().nonnegative(),
  rebuild: z.boolean().optional(),
});

const CorridorStationRangeSchema = z.object({
  startStation: z.number(),
  endStation: z.number(),
  name: z.string().optional().describe("Name for the isolated region (default <namePrefix>-01, -02 ...)."),
});

const CorridorBowtiePredictArgsSchema = z.object({
  action: z.literal("bowtie_predict"),
  name: z.string(),
  baselineIndex: z.number().int().nonnegative().optional(),
  side: z.enum(["left", "right", "both"]).optional(),
  widthSource: z.enum(["built", "fixed"]).optional(),
  insideWidth: z.number().positive().optional(),
  leftWidth: z.number().positive().optional(),
  rightWidth: z.number().positive().optional(),
  code: z.string().optional(),
  sampleStep: z.number().positive().optional(),
  padding: z.number().nonnegative().optional(),
  mergeGap: z.number().nonnegative().optional(),
  maxLoopLength: z.number().positive().optional(),
  includeEdges: z.boolean().optional(),
});

const CorridorRegionSplitArgsSchema = z.object({
  action: z.literal("region_split"),
  name: z.string(),
  baselineIndex: z.number().int().nonnegative().optional(),
  regionIndex: z.number().int().nonnegative().optional(),
  regionName: z.string().optional(),
  station: z.number(),
  newName: z.string().optional(),
  matchParent: z.boolean().optional(),
  rebuild: z.boolean().optional(),
});

const CorridorRegionIsolateArgsSchema = z.object({
  action: z.literal("region_isolate"),
  name: z.string(),
  baselineIndex: z.number().int().nonnegative().optional(),
  ranges: z.array(CorridorStationRangeSchema).min(1),
  namePrefix: z.string().optional(),
  frequency: z.number().positive().optional(),
  assemblyName: z.string().optional(),
  matchParent: z.boolean().optional(),
  carrySurfaceTargets: z.boolean().optional(),
  dryRun: z.boolean().optional(),
  rebuild: z.boolean().optional(),
});

const CorridorRegionMergeArgsSchema = z.object({
  action: z.literal("region_merge"),
  name: z.string(),
  baselineIndex: z.number().int().nonnegative().optional(),
  firstRegionIndex: z.number().int().nonnegative(),
  lastRegionIndex: z.number().int().nonnegative(),
  newName: z.string().optional(),
  rebuild: z.boolean().optional(),
});

const CorridorBowtieValleyArgsSchema = z.object({
  action: z.literal("bowtie_valley"),
  name: z.string(),
  baselineIndex: z.number().int().nonnegative().optional(),
  side: z.enum(["left", "right"]),
  startStation: z.number(),
  endStation: z.number(),
  templateStationBefore: z.number().optional(),
  templateStationAfter: z.number().optional(),
  valleyName: z.string().optional(),
  linkCode: z.string().optional(),
  extension: z.number().nonnegative().optional(),
  step: z.number().positive().max(1).optional(),
  surfaceName: z.string().optional(),
  addStations: z.boolean().optional(),
  createAlignment: z.boolean().optional(),
  style: z.string().optional(),
  layer: z.string().optional(),
  dryRun: z.boolean().optional(),
  allowMismatch: z.boolean().optional(),
  clipInset: z.number().nonnegative().max(5).optional(),
});

const CorridorBowtieValleyPreviewArgsSchema = CorridorBowtieValleyArgsSchema.extend({
  action: z.literal("bowtie_valley_preview"),
});

const CorridorBowtieSeamArgsSchema = z.object({
  action: z.literal("bowtie_seam"),
  name: z.string(),
  baselineIndex: z.number().int().nonnegative().optional(),
  startStation: z.number(),
  endStation: z.number(),
  side: z.enum(["left", "right"]).optional(),
  linkCode: z.string().optional(),
  surfaceName: z.string().optional(),
  extension: z.number().nonnegative().optional(),
  step: z.number().positive().max(1).optional(),
  capInset: z.number().positive().max(1).optional(),
  searchMargin: z.number().min(5).optional(),
  maxLevelStep: z.number().nonnegative().optional(),
  addStations: z.boolean().optional(),
  seamName: z.string().optional(),
  capName: z.string().optional(),
  style: z.string().optional(),
  layer: z.string().optional(),
  snapshotPath: z.string().optional(),
  allStations: z.boolean().optional(),
  writeAs: z.enum(["feature_line", "alignment"]).optional(),
  levelFromTarget: z.boolean().optional(),
  maxLevelAdjust: z.number().nonnegative().optional(),
  acceptOffSurfaceEnds: z.boolean().optional(),
  adjustFrom: z.enum(["auto", "hinge", "last_link"]).optional(),
  levelRule: z.enum(["no_steeper", "mean"]).optional(),
  clipAssembly: z.string().optional(),
  dryRun: z.boolean().optional(),
});

const CorridorBowtieSeamPreviewArgsSchema = CorridorBowtieSeamArgsSchema.extend({
  action: z.literal("bowtie_seam_preview"),
});

const CorridorBowtieFixArgsSchema = CorridorBowtieSeamArgsSchema.extend({
  action: z.literal("bowtie_fix"),
  startStation: z.number().optional(),
  endStation: z.number().optional(),
  assemblyName: z.string().optional(),
  subassemblyName: z.string().optional(),
  regionName: z.string().optional(),
  padding: z.number().nonnegative().optional(),
  minTurnDegrees: z.number().nonnegative().optional(),
  keepOnFailure: z.boolean().optional(),
  assemblyMap: z.record(z.string()).optional(),
  parentRegion: z.string().optional(),
  parentAssembly: z.string().optional(),
  splitBefore: z.string().optional(),
  splitAfter: z.string().optional(),
});

const CorridorBowtieBendsArgsSchema = z.object({
  action: z.literal("bowtie_bends"),
  name: z.string(),
  baselineIndex: z.number().int().nonnegative().optional(),
  startStation: z.number().optional(),
  endStation: z.number().optional(),
  minTurnDegrees: z.number().nonnegative().optional(),
});

const CorridorBowtieUnfixArgsSchema = z.object({
  action: z.literal("bowtie_unfix"),
  name: z.string(),
  baselineIndex: z.number().int().nonnegative().optional(),
  regionName: z.string(),
  merge: z.boolean().optional(),
  restoreAssembly: z.boolean().optional(),
  rebuild: z.boolean().optional(),
  dryRun: z.boolean().optional(),
});

const CorridorBowtieUnfixPreviewArgsSchema = CorridorBowtieUnfixArgsSchema.extend({
  action: z.literal("bowtie_unfix_preview"),
});

const CorridorBowtieRefreshArgsSchema = z.object({
  action: z.literal("bowtie_refresh"),
  name: z.string(),
  baselineIndex: z.number().int().nonnegative().optional(),
  regionName: z.string().optional(),
  linkCode: z.string().optional(),
  extension: z.number().nonnegative().optional(),
  step: z.number().positive().max(1).optional(),
  surfaceName: z.string().optional(),
  tolerance: z.number().positive().optional(),
  dryRun: z.boolean().optional(),
  rebuild: z.boolean().optional(),
  rebuildFirst: z.boolean().optional(),
  allowMismatch: z.boolean().optional(),
  clipInset: z.number().nonnegative().max(5).optional(),
});

const CorridorBowtieRefreshPreviewArgsSchema = CorridorBowtieRefreshArgsSchema.extend({
  action: z.literal("bowtie_refresh_preview"),
});

const CorridorRegionStationsArgsSchema = z.object({
  action: z.literal("region_stations"),
  name: z.string(),
  baselineIndex: z.number().int().nonnegative().optional(),
  regionIndex: z.number().int().nonnegative().optional(),
  regionName: z.string().optional(),
  operation: z.enum(["list", "delete", "clear"]).optional(),
  stations: z.array(z.number()).optional(),
  rebuild: z.boolean().optional(),
});

const CorridorRegionStationsListArgsSchema = CorridorRegionStationsArgsSchema.extend({
  action: z.literal("region_stations_list"),
  operation: z.literal("list").optional(),
});

const CorridorBowtieCheckArgsSchema = z.object({
  action: z.literal("bowtie_check"),
  name: z.string(),
  baselineIndex: z.number().int().nonnegative().optional(),
  side: z.enum(["left", "right", "both"]).optional(),
  startStation: z.number().optional(),
  endStation: z.number().optional(),
  linkCode: z.string().optional(),
  minOffset: z.number().nonnegative().optional(),
  code: z.string().optional(),
  tolerance: z.number().nonnegative().optional(),
  maxListed: z.number().int().positive().optional(),
});

// ─── Canonical input shape ────────────────────────────────────────────────────

const canonicalCorridorInputShape = {
  action: z.enum([
    "list",
    "get",
    "create",
    "rebuild",
    "get_surfaces",
    "get_feature_lines",
    "compute_volumes",
    "summary",
    "target_mapping_get",
    "target_mapping_set",
    "region_add",
    "region_frequency",
    "region_delete",
    "region_split",
    "region_isolate",
    "region_merge",
    "bowtie_predict",
    "bowtie_valley",
    "bowtie_seam",
    "bowtie_fix",
    "bowtie_bends",
    "bowtie_unfix",
    "bowtie_refresh",
    "bowtie_check",
    "region_stations",
    "section",
    "feature_line_codes",
    "feature_line_export",
    "surface_info",
    "surface_create",
    "surface_edit",
    "surface_delete",
    "export_solids",
  ]),
  name: z.string().optional().describe("Corridor name."),
  surfaceName: z.string().optional().describe("surface_*: corridor surface name. bowtie_valley / bowtie_refresh: daylight surface (default: the region's surface target)."),
  linkCodes: z.array(CorridorLinkCodeSchema).optional().describe("surface_create / add_link_code: link codes (string or {code, breakline})."),
  featureLineCodes: z.array(z.string()).optional(),
  overhangCorrection: z.enum(["none", "top_links", "bottom_links"]).optional(),
  boundaries: z.array(CorridorSurfaceBoundarySchema).optional().describe("surface_create: boundaries; default none (add a corridor_extents boundary for a clean surface, or outline where bowties were repaired with valley lines)."),
  boundary: CorridorSurfaceBoundarySchema.optional().describe("surface_edit add_boundary."),
  boundaryName: z.string().optional(),
  operation: z.string().optional().describe("surface_edit: add_link_code | remove_link_code | set_breakline | add_feature_line_code | remove_feature_line_code | set_overhang | add_boundary | remove_boundary | set_style | rename | set_description | set_build. region_stations: list | delete | clear (the region's added stations)."),
  breakline: z.boolean().optional(),
  newName: z.string().optional(),
  description: z.string().optional(),
  build: z.boolean().optional(),
  exportShapes: z.boolean().optional(),
  exportLinks: z.boolean().optional(),
  createSolidForShape: z.boolean().optional(),
  sweepSolidForShape: z.boolean().optional(),
  includedCodes: z.array(z.string()).optional(),
  excludedCodes: z.array(z.string()).optional(),
  outputPath: z.string().optional().describe("export_solids: write to a new .dwg."),
  maxListed: z.number().int().positive().optional(),
  code: z.string().optional().describe("feature_line_export: corridor feature line code. bowtie_predict: only points with this code define the inside edge (default: outermost point per side). bowtie_check: feature line checked for loops (default Daylight)."),
  exportAs: z.enum(["alignment", "profile", "feature_line", "polyline3d"]).optional(),
  featureLineIndex: z.number().int().nonnegative().optional(),
  outputName: z.string().optional(),
  siteName: z.string().optional(),
  layer: z.string().optional(),
  style: z.string().optional(),
  labelSet: z.string().optional(),
  dynamic: z.boolean().optional(),
  smooth: z.boolean().optional(),
  station: z.number().optional().describe("section: station to read the built cross-section (applied assembly) at; snaps to the nearest applied station. region_split: split station (more than 0.01 inside the region)."),
  alignmentName: z.string().optional().describe("create: baseline alignment (with profileName)."),
  profileName: z.string().optional().describe("create: baseline profile (must belong to alignmentName)."),
  featureLineName: z.string().optional().describe("create: use a feature line as the baseline instead of alignment + profile (stations run 0 to its length)."),
  featureLineHandle: z.string().optional().describe("create: feature line by AutoCAD handle, for unnamed or duplicate-named feature lines."),
  baselineName: z.string().optional(),
  regionName: z.string().optional().describe("region_stations / target_mapping_set: the region (or regionIndex); an unknown name is refused. bowtie_refresh: only this region (default: every region whose ClipTarget is mapped to an alignment)."),
  corridorSurface: z.string().optional(),
  referenceSurface: z.string().optional(),
  regionIndex: z.number().int().nonnegative().optional(),
  baselineIndex: z.number().int().nonnegative().optional(),
  targets: z.array(CorridorTargetSchema).optional(),
  assemblyName: z.string().optional(),
  startStation: z.number().optional(),
  endStation: z.number().optional(),
  frequency: z.number().positive().optional().describe("Assembly frequency in drawing units, applied along tangents, curves, spirals and profile curves."),
  rebuild: z.boolean().optional().describe("Rebuild the corridor after the change (default true)."),
  side: z.enum(["left", "right", "both"]).optional().describe("bowtie_predict / bowtie_check: side(s) to scan (default both). bowtie_valley: the inside of the bend (left or right)."),
  widthSource: z.enum(["built", "fixed"]).optional().describe("bowtie_predict: built = inside reach from the built corridor sections (default); fixed = insideWidth / leftWidth / rightWidth."),
  insideWidth: z.number().positive().optional().describe("bowtie_predict (fixed): inside reach in metres, both sides."),
  leftWidth: z.number().positive().optional(),
  rightWidth: z.number().positive().optional(),
  sampleStep: z.number().positive().optional().describe("bowtie_predict: sampling step along the baseline (default length/4000, 0.1-0.5 m)."),
  padding: z.number().nonnegative().optional().describe("bowtie_predict: metres added before/after each bowtie in the split plan (default 1). bowtie_fix: metres of region kept either side of the meet stations (default 2)."),
  subassemblyName: z.string().optional().describe("bowtie_fix: the clip subassembly on the INSIDE of the bend (the one that gets ClipTarget / ClipElev). Default: the only subassembly of the region with a ClipTarget whose name ends in L/Left or R/Right to match side."),
  mergeGap: z.number().nonnegative().optional().describe("bowtie_predict: merge split ranges closer than this (default 2 m)."),
  maxLoopLength: z.number().positive().optional().describe("bowtie_predict: ignore self-crossings longer than this along the baseline (hairpins, default 200 m)."),
  includeEdges: z.boolean().optional().describe("bowtie_predict: return the predicted inside-edge points around each bowtie."),
  ranges: z.array(CorridorStationRangeSchema).optional().describe("region_isolate: station ranges to give their own region - pass bowtie_predict's splitPlan."),
  namePrefix: z.string().optional().describe("region_isolate: name prefix for isolated regions (default BT)."),
  carrySurfaceTargets: z.boolean().optional().describe("region_isolate with assemblyName: give the new assembly's surface targets the parent region's surface in the same step (default true), so no rebuild ever runs with a surface target missing."),
  matchParent: z.boolean().optional().describe("region_split / region_isolate: copy the parent's assembly, targets and frequency onto the new pieces (default true)."),
  dryRun: z.boolean().optional().describe("region_isolate: report the splits without changing the corridor. bowtie_valley / bowtie_refresh: compute and check without changing anything (read-only, needs no approval)."),
  firstRegionIndex: z.number().int().nonnegative().optional().describe("region_merge: first region of the range to merge."),
  lastRegionIndex: z.number().int().nonnegative().optional().describe("region_merge: last region of the range to merge."),
  templateStationBefore: z.number().optional().describe("bowtie_valley: applied station whose section is the incoming leg's template (default: last applied station at/before startStation; must be unclipped)."),
  templateStationAfter: z.number().optional().describe("bowtie_valley: applied station whose section is the outgoing leg's template (default: first applied station at/after endStation)."),
  valleyName: z.string().optional().describe("bowtie_valley: name of the valley alignment (default '<corridor> <region> Valley L|R')."),
  linkCode: z.string().optional().describe("bowtie_valley: link code whose chain is the design surface (default Top). bowtie_check: links tested for crossings (default Top)."),
  extension: z.number().nonnegative().optional().describe("bowtie_valley: metres the templates are extended past their daylight (default 3)."),
  step: z.number().positive().max(1).optional().describe("bowtie_valley: marching step along the bisector (default 0.1 m)."),
  addStations: z.boolean().optional().describe("bowtie_valley: add a corridor station on each leg where the valley meets the daylight surface (default true)."),
  createAlignment: z.boolean().optional().describe("bowtie_valley: create the valley alignment (default true; false = compute only)."),
  minOffset: z.number().nonnegative().optional().describe("bowtie_check: only links reaching beyond this offset (e.g. past a drain's outer wall)."),
  tolerance: z.number().nonnegative().optional().describe("bowtie_check: crossings closer than this to a link end count as touching (default 0.005 m). bowtie_refresh: a valley that moved less than this stays as it is (default 0.01 m)."),
  allowMismatch: z.boolean().optional().describe("bowtie_valley / bowtie_refresh: build the valley even when a check fails (different inside sections, meet stations not straddling the bend or outside the region); default false = refuse."),
  rebuildFirst: z.boolean().optional().describe("bowtie_refresh: rebuild the corridor before reading its sections (default true; dry runs never rebuild)."),
  clipInset: z.number().nonnegative().max(5).optional().describe("bowtie_valley / bowtie_refresh, curved bends only: how far short of the curve's centre of curvature the inside sections stop, in metres (default 2 % of the radius, clamped to 0.05-0.5 m)."),
  stations: z.array(z.number()).optional().describe("region_stations delete: added stations to remove."),
  capInset: z.number().positive().max(1).optional().describe("bowtie_seam: how far short of the curve centre the converging (arc) sections stop (default 0.05 m)."),
  searchMargin: z.number().min(5).optional().describe("bowtie_seam: metres of baseline and sections read either side of the bend (default 60)."),
  maxLevelStep: z.number().nonnegative().optional().describe("bowtie_seam: largest step in level accepted where a cut slope and a fill slope cover the same ground (default 0.30 m); above it the bend is a design conflict."),
  seamName: z.string().optional().describe("bowtie_seam: name of the valley line (default '<corridor> <region> Valley L|R')."),
  capName: z.string().optional().describe("bowtie_seam: name of the apex bar on a curve (default '<corridor> <region> Apex L|R')."),
  snapshotPath: z.string().optional().describe("bowtie_seam: also write the snapshot (baseline samples, unclipped sections, ground grid) to this .json file, to solve offline with bowtie-kernel."),
  allStations: z.boolean().optional().describe("bowtie_seam: list every section in the result, not only the clipped ones."),
  levelFromTarget: z.boolean().optional().describe("bowtie_seam: the clip subassembly (UTNM_LaneDaylightClip v0.3, target ClipElev) also takes its level from the valley line, the mean of the two sides, so both sides end on the same XYZ (default true with feature lines)."),
  maxLevelAdjust: z.number().nonnegative().optional().describe("bowtie_seam with levelFromTarget: largest distance a link may be moved off its own slope (default 0.30 m); above it the bend is a design conflict."),
  acceptOffSurfaceEnds: z.boolean().optional().describe("bowtie_seam / bowtie_fix: accept clipped sections that never come near the daylight surface (walls, fixed-width sections in a region that still has a surface target). Default false: that usually means the wrong surface."),
  writeAs: z.enum(["feature_line", "alignment"]).optional().describe("bowtie_seam: write the valley line and the apex bar as siteless feature lines carrying their levels (default) or as alignments."),
  minTurnDegrees: z.number().nonnegative().optional().describe("bowtie_bends / bowtie_fix: bends turning less than this are ignored (default 3)."),
  adjustFrom: z.enum(["auto", "hinge", "last_link"]).optional().describe("bowtie_seam / bowtie_fix: how the clip part takes a link to the valley level - hinge (spread over every slope and bench from the hinge: UTNM_LaneDaylightClip v0.4, Spread From Hinge = Yes), last_link (v0.3, or Spread From Hinge = No); auto (default) reads it from the clip assembly."),
  levelRule: z.enum(["no_steeper", "mean"]).optional().describe("bowtie_seam / bowtie_fix: the valley level where the two sides differ - no_steeper (default: a side in cut is only lowered, in fill only raised, so no slope comes out steeper than designed; the mean where no such level exists, flagged) or mean (halfway)."),
  clipAssembly: z.string().optional().describe("bowtie_seam: the clip assembly the region will get (for adjustFrom auto); bowtie_fix fills it from assemblyName."),
  keepOnFailure: z.boolean().optional().describe("bowtie_fix: leave a repair that fails part-way in the drawing for inspection (default false: every change made for that bend is rolled back with bowtie_unfix)."),
  assemblyMap: z.record(z.string()).optional().describe("bowtie_fix: clip assembly per parent assembly, e.g. {\"MD302 Drain 3.3\": \"MD302 Drain 3.3 LDC04\", \"MD303 Drain 3.6\": \"MD303 Drain 3.6 LDC04\"} - for a clash range that spans regions with different assemblies (each piece gets its own). Parent assemblies not listed fall back to assemblyName."),
  merge: z.boolean().optional().describe("bowtie_unfix: merge the region back with the pieces it was cut from (default true)."),
  restoreAssembly: z.boolean().optional().describe("bowtie_unfix: give the region back the assembly it had before the repair (default true)."),
};

// ─── Domain definition ────────────────────────────────────────────────────────


type CorridorRawArgs = Record<string, unknown>;

/**
 * Read-only variants of edit actions run without approval: bowtie_valley / bowtie_refresh with dryRun true and
 * region_stations with operation list (or none) resolve to their *_preview / *_list actions.
 */
function resolveCorridorAction(rawArgs: CorridorRawArgs): { action: string; args: CorridorRawArgs } {
  const action = String(rawArgs.action ?? "");
  if ((action === "bowtie_valley" || action === "bowtie_refresh" || action === "bowtie_seam" || action === "bowtie_unfix") && rawArgs.dryRun === true) {
    const preview = `${action}_preview`;
    return { action: preview, args: { ...rawArgs, action: preview } };
  }
  if (action === "region_stations" && (rawArgs.operation === undefined || rawArgs.operation === null || rawArgs.operation === "list")) {
    const { operation: _operation, ...rest } = rawArgs;
    return { action: "region_stations_list", args: { ...rest, action: "region_stations_list" } };
  }
  return { action, args: rawArgs };
}

function bowtieSeamParams(args: CorridorRawArgs, dryRun: boolean) {
  return {
    corridorName: args.name,
    baselineIndex: args.baselineIndex ?? 0,
    startStation: args.startStation,
    endStation: args.endStation,
    side: args.side ?? null,
    linkCode: args.linkCode ?? null,
    surfaceName: args.surfaceName ?? null,
    extension: args.extension ?? null,
    step: args.step ?? null,
    capInset: args.capInset ?? null,
    searchMargin: args.searchMargin ?? null,
    maxLevelStep: args.maxLevelStep ?? null,
    addStations: args.addStations ?? true,
    seamName: args.seamName ?? null,
    capName: args.capName ?? null,
    style: args.style ?? null,
    layer: args.layer ?? null,
    snapshotPath: args.snapshotPath ?? null,
    allStations: args.allStations ?? false,
    writeAs: args.writeAs ?? null,
    levelFromTarget: args.levelFromTarget ?? null,
    maxLevelAdjust: args.maxLevelAdjust ?? null,
    acceptOffSurfaceEnds: args.acceptOffSurfaceEnds ?? null,
    adjustFrom: args.adjustFrom ?? null,
    levelRule: args.levelRule ?? null,
    clipAssembly: args.clipAssembly ?? null,
    skipRegionCheck: args.skipRegionCheck ?? null,
    fixPieces: args.fixPieces ?? null,
    parentRegion: args.parentRegion ?? null,
    parentAssembly: args.parentAssembly ?? null,
    splitBefore: args.splitBefore ?? null,
    splitAfter: args.splitAfter ?? null,
    dryRun,
  };
}

function bowtieRefreshParams(args: CorridorRawArgs, dryRun: boolean) {
  return {
    corridorName: args.name,
    baselineIndex: args.baselineIndex ?? 0,
    regionName: args.regionName ?? null,
    linkCode: args.linkCode ?? null,
    extension: args.extension ?? null,
    step: args.step ?? null,
    surfaceName: args.surfaceName ?? null,
    tolerance: args.tolerance ?? null,
    dryRun,
    rebuild: args.rebuild ?? true,
    rebuildFirst: dryRun ? false : (args.rebuildFirst ?? true),
    allowMismatch: args.allowMismatch ?? false,
    clipInset: args.clipInset ?? null,
  };
}

export const CORRIDOR_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "corridor",
  actions: {
    list: {
      action: "list",
      inputSchema: CorridorListArgsSchema,
      responseSchema: CorridorListResponseSchema,
      capabilities: ["query"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listCorridors"],
      execute: async () => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listCorridors", {}),
      ),
    },
    get: {
      action: "get",
      inputSchema: CorridorGetArgsSchema,
      responseSchema: CorridorDetailResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getCorridor"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getCorridor", {
          name: args.name,
        }),
      ),
    },
    create: {
      action: "create",
      inputSchema: CorridorCreateArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createCorridor"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createCorridor", {
          name: args.name,
          alignmentName: args.alignmentName ?? null,
          profileName: args.profileName ?? null,
          featureLineName: args.featureLineName ?? null,
          featureLineHandle: args.featureLineHandle ?? null,
          assemblyName: args.assemblyName,
          baselineName: args.baselineName ?? null,
          regionName: args.regionName ?? null,
          startStation: args.startStation ?? null,
          endStation: args.endStation ?? null,
          frequency: args.frequency ?? null,
          rebuild: args.rebuild ?? true,
        }),
      ),
    },
    rebuild: {
      action: "rebuild",
      inputSchema: CorridorRebuildArgsSchema,
      responseSchema: CorridorRebuildResponseSchema,
      capabilities: ["manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["rebuildCorridor"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("rebuildCorridor", {
          name: args.name,
        }),
      ),
    },
    get_surfaces: {
      action: "get_surfaces",
      inputSchema: CorridorGetSurfacesArgsSchema,
      responseSchema: CorridorSurfacesResponseSchema,
      capabilities: ["query"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getCorridorSurfaces"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getCorridorSurfaces", {
          name: args.name,
        }),
      ),
    },
    get_feature_lines: {
      action: "get_feature_lines",
      inputSchema: CorridorGetFeatureLinesArgsSchema,
      responseSchema: CorridorFeatureLinesResponseSchema,
      capabilities: ["query"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getCorridorFeatureLines"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getCorridorFeatureLines", {
          name: args.name,
        }),
      ),
    },
    compute_volumes: {
      action: "compute_volumes",
      inputSchema: CorridorComputeVolumesArgsSchema,
      responseSchema: CorridorVolumesResponseSchema,
      capabilities: ["analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["computeCorridorVolumes"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("computeCorridorVolumes", {
          name: args.name,
          corridorSurface: args.corridorSurface,
          referenceSurface: args.referenceSurface,
        }),
      ),
    },
    summary: {
      action: "summary",
      inputSchema: CorridorSummaryArgsSchema,
      responseSchema: CorridorFullSummaryResponseSchema,
      capabilities: ["query", "analyze", "generate"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getCorridor", "getCorridorSurfaces", "computeCorridorVolumes"],
      execute: async (args) => await withApplicationConnection(async (appClient) => {
        const corridor = CorridorDetailResponseSchema.parse(
          await appClient.sendCommand("getCorridor", { name: args.name }),
        );

        const surfaceInventory = CorridorSurfacesResponseSchema.parse(
          await appClient.sendCommand("getCorridorSurfaces", { name: args.name }),
        );

        const selectedCorridorSurface = (args.corridorSurface as string | undefined)
          ?? (surfaceInventory.surfaces.length === 1 ? surfaceInventory.surfaces[0].name : null);
        const referenceSurface = (args.referenceSurface as string | undefined) ?? null;

        let volumeAnalysis: z.infer<typeof CorridorVolumesResponseSchema> | null = null;
        if (selectedCorridorSurface && referenceSurface) {
          volumeAnalysis = CorridorVolumesResponseSchema.parse(
            await appClient.sendCommand("computeCorridorVolumes", {
              name: args.name,
              corridorSurface: selectedCorridorSurface,
              referenceSurface,
            }),
          );
        }

        const regionCount = corridor.baselines.reduce(
          (total, baseline) => total + baseline.regions.length,
          0,
        );

        const totalRegionLength = corridor.baselines.reduce(
          (baselineTotal, baseline) =>
            baselineTotal + baseline.regions.reduce(
              (regionTotal, region) => regionTotal + (region.endStation - region.startStation),
              0,
            ),
          0,
        );

        return CorridorFullSummaryResponseSchema.parse({
          corridor,
          surfaceInventory,
          volumeAnalysis,
          summary: {
            baselineCount: corridor.baselines.length,
            regionCount,
            corridorSurfaceCount: surfaceInventory.surfaces.length,
            featureLineCount: corridor.featureLineCount,
            state: corridor.state,
            totalRegionLength,
            selectedCorridorSurface,
            referenceSurface,
            volumeComputationStatus: volumeAnalysis ? "computed" : "skipped",
          },
        });
      }),
    },
    target_mapping_get: {
      action: "target_mapping_get",
      inputSchema: CorridorTargetMappingGetArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getCorridorTargetMappings"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getCorridorTargetMappings", {
          corridorName: args.name,
          regionIndex: args.regionIndex ?? null,
          baselineIndex: args.baselineIndex ?? 0,
        }),
      ),
    },
    target_mapping_set: {
      action: "target_mapping_set",
      inputSchema: CorridorTargetMappingSetArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["edit", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["setCorridorTargetMappings"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("setCorridorTargetMappings", {
          corridorName: args.name,
          regionIndex: args.regionIndex,
          regionName: args.regionName,
          baselineIndex: args.baselineIndex ?? 0,
          targets: args.targets,
          rebuild: args.rebuild ?? true,
        }),
      ),
    },
    region_add: {
      action: "region_add",
      inputSchema: CorridorRegionAddArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["create", "edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["addCorridorRegion"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("addCorridorRegion", {
          corridorName: args.name,
          baselineIndex: args.baselineIndex ?? 0,
          assemblyName: args.assemblyName,
          regionName: args.regionName ?? null,
          startStation: args.startStation,
          endStation: args.endStation,
          frequency: args.frequency ?? null,
          rebuild: args.rebuild ?? true,
        }),
      ),
    },
    region_frequency: {
      action: "region_frequency",
      inputSchema: CorridorRegionFrequencyArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["setCorridorRegionFrequency"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("setCorridorRegionFrequency", {
          corridorName: args.name,
          baselineIndex: args.baselineIndex ?? 0,
          regionIndex: args.regionIndex ?? 0,
          frequency: args.frequency,
          rebuild: args.rebuild ?? true,
        }),
      ),
    },
    section: {
      action: "section",
      inputSchema: CorridorSectionArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getCorridorSection"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getCorridorSection", {
          name: args.name,
          station: args.station,
          baselineIndex: args.baselineIndex ?? 0,
        }),
      ),
    },
    feature_line_codes: {
      action: "feature_line_codes",
      inputSchema: CorridorFeatureLineCodesArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listCorridorFeatureLineCodes"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listCorridorFeatureLineCodes", {
          name: args.name,
          baselineIndex: args.baselineIndex ?? 0,
        }),
      ),
    },
    feature_line_export: {
      action: "feature_line_export",
      inputSchema: CorridorFeatureLineExportArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["exportCorridorFeatureLine"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("exportCorridorFeatureLine", {
          name: args.name,
          code: args.code,
          exportAs: args.exportAs,
          baselineIndex: args.baselineIndex ?? 0,
          featureLineIndex: args.featureLineIndex ?? 0,
          outputName: args.outputName ?? null,
          alignmentName: args.alignmentName ?? null,
          siteName: args.siteName ?? null,
          layer: args.layer ?? null,
          style: args.style ?? null,
          labelSet: args.labelSet ?? null,
          dynamic: args.dynamic ?? true,
          smooth: args.smooth ?? false,
        }),
      ),
    },
    surface_info: {
      action: "surface_info",
      inputSchema: CorridorSurfaceInfoArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["corridorSurfaceInfo"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("corridorSurfaceInfo", {
          name: args.name,
          surfaceName: args.surfaceName ?? null,
        }),
      ),
    },
    surface_create: {
      action: "surface_create",
      inputSchema: CorridorSurfaceCreateArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["create", "edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["corridorSurfaceCreate"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("corridorSurfaceCreate", {
          name: args.name,
          surfaceName: args.surfaceName,
          linkCodes: args.linkCodes ?? [],
          featureLineCodes: args.featureLineCodes ?? [],
          overhangCorrection: args.overhangCorrection ?? null,
          boundaries: args.boundaries ?? [],
          style: args.style ?? null,
          description: args.description ?? null,
          rebuild: args.rebuild ?? true,
        }),
      ),
    },
    surface_edit: {
      action: "surface_edit",
      inputSchema: CorridorSurfaceEditArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["corridorSurfaceEdit"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("corridorSurfaceEdit", {
          name: args.name,
          surfaceName: args.surfaceName,
          operation: args.operation,
          code: args.code ?? null,
          linkCodes: args.linkCodes ?? [],
          featureLineCodes: args.featureLineCodes ?? [],
          breakline: args.breakline ?? null,
          overhangCorrection: args.overhangCorrection ?? null,
          boundary: args.boundary ?? null,
          boundaryName: args.boundaryName ?? null,
          style: args.style ?? null,
          newName: args.newName ?? null,
          description: args.description ?? null,
          build: args.build ?? null,
          rebuild: args.rebuild ?? true,
        }),
      ),
    },
    surface_delete: {
      action: "surface_delete",
      inputSchema: CorridorSurfaceDeleteArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["delete", "edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["corridorSurfaceDelete"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("corridorSurfaceDelete", {
          name: args.name,
          surfaceName: args.surfaceName,
          rebuild: args.rebuild ?? true,
        }),
      ),
    },
    export_solids: {
      action: "export_solids",
      inputSchema: CorridorExportSolidsArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["corridorExportSolids"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("corridorExportSolids", {
          name: args.name,
          exportShapes: args.exportShapes ?? true,
          exportLinks: args.exportLinks ?? false,
          createSolidForShape: args.createSolidForShape ?? true,
          sweepSolidForShape: args.sweepSolidForShape ?? false,
          includedCodes: args.includedCodes ?? [],
          excludedCodes: args.excludedCodes ?? [],
          layer: args.layer ?? null,
          outputPath: args.outputPath ?? null,
          maxListed: args.maxListed ?? 50,
        }),
      ),
    },
    region_delete: {
      action: "region_delete",
      inputSchema: CorridorRegionDeleteArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["delete", "edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["deleteCorridorRegion"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("deleteCorridorRegion", {
          corridorName: args.name,
          baselineIndex: args.baselineIndex ?? 0,
          regionIndex: args.regionIndex,
          rebuild: args.rebuild ?? true,
        }),
      ),
    },
    bowtie_predict: {
      action: "bowtie_predict",
      inputSchema: CorridorBowtiePredictArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["query", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["predictCorridorBowties"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("predictCorridorBowties", {
          corridorName: args.name,
          baselineIndex: args.baselineIndex ?? null,
          side: args.side ?? "both",
          widthSource: args.widthSource ?? "built",
          insideWidth: args.insideWidth ?? null,
          leftWidth: args.leftWidth ?? null,
          rightWidth: args.rightWidth ?? null,
          code: args.code ?? null,
          sampleStep: args.sampleStep ?? null,
          padding: args.padding ?? null,
          mergeGap: args.mergeGap ?? null,
          maxLoopLength: args.maxLoopLength ?? null,
          includeEdges: args.includeEdges ?? false,
        }),
      ),
    },
    region_split: {
      action: "region_split",
      inputSchema: CorridorRegionSplitArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["splitCorridorRegion"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("splitCorridorRegion", {
          corridorName: args.name,
          baselineIndex: args.baselineIndex ?? 0,
          regionIndex: args.regionIndex ?? null,
          regionName: args.regionName ?? null,
          station: args.station,
          newRegionName: args.newName ?? null,
          matchParent: args.matchParent ?? true,
          rebuild: args.rebuild ?? true,
        }),
      ),
    },
    region_isolate: {
      action: "region_isolate",
      inputSchema: CorridorRegionIsolateArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["isolateCorridorRanges"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("isolateCorridorRanges", {
          corridorName: args.name,
          baselineIndex: args.baselineIndex ?? 0,
          ranges: args.ranges,
          namePrefix: args.namePrefix ?? "BT",
          frequency: args.frequency ?? null,
          assemblyName: args.assemblyName ?? null,
          matchParent: args.matchParent ?? true,
          carrySurfaceTargets: args.carrySurfaceTargets ?? true,
          dryRun: args.dryRun ?? false,
          rebuild: args.rebuild ?? true,
        }),
      ),
    },
    region_merge: {
      action: "region_merge",
      inputSchema: CorridorRegionMergeArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["mergeCorridorRegions"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("mergeCorridorRegions", {
          corridorName: args.name,
          baselineIndex: args.baselineIndex ?? 0,
          firstRegionIndex: args.firstRegionIndex,
          lastRegionIndex: args.lastRegionIndex,
          newRegionName: args.newName ?? null,
          rebuild: args.rebuild ?? true,
        }),
      ),
    },
    bowtie_valley: {
      action: "bowtie_valley",
      inputSchema: CorridorBowtieValleyArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["bowtieValley"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("bowtieValley", {
          corridorName: args.name,
          baselineIndex: args.baselineIndex ?? 0,
          side: args.side,
          startStation: args.startStation,
          endStation: args.endStation,
          templateStationBefore: args.templateStationBefore ?? null,
          templateStationAfter: args.templateStationAfter ?? null,
          valleyName: args.valleyName ?? null,
          linkCode: args.linkCode ?? null,
          extension: args.extension ?? null,
          step: args.step ?? null,
          surfaceName: args.surfaceName ?? null,
          addStations: args.addStations ?? true,
          createAlignment: args.createAlignment ?? true,
          style: args.style ?? null,
          layer: args.layer ?? null,
          dryRun: args.dryRun ?? false,
          allowMismatch: args.allowMismatch ?? false,
          clipInset: args.clipInset ?? null,
        }),
      ),
    },
    // bowtie_valley with dryRun: true resolves here - read-only, so no approval.
    bowtie_valley_preview: {
      action: "bowtie_valley_preview",
      inputSchema: CorridorBowtieValleyPreviewArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["query", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["bowtieValley"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("bowtieValley", {
          corridorName: args.name,
          baselineIndex: args.baselineIndex ?? 0,
          side: args.side,
          startStation: args.startStation,
          endStation: args.endStation,
          templateStationBefore: args.templateStationBefore ?? null,
          templateStationAfter: args.templateStationAfter ?? null,
          valleyName: args.valleyName ?? null,
          linkCode: args.linkCode ?? null,
          extension: args.extension ?? null,
          step: args.step ?? null,
          surfaceName: args.surfaceName ?? null,
          addStations: args.addStations ?? true,
          createAlignment: args.createAlignment ?? true,
          style: args.style ?? null,
          layer: args.layer ?? null,
          dryRun: true,
          allowMismatch: args.allowMismatch ?? false,
          clipInset: args.clipInset ?? null,
        }),
      ),
    },
    // The seam (valley) of one bend from the tested bowtie kernel: angle points and curves, cut, fill and changes between them.
    bowtie_seam: {
      action: "bowtie_seam",
      inputSchema: CorridorBowtieSeamArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["bowtieSeam"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("bowtieSeam", bowtieSeamParams(args, args.dryRun === true)),
      ),
    },
    // Every bend in a station range (default: the whole baseline), each in one step: solve, give the clash range its own
    // region (optionally with a clip assembly, surface targets carried over in the same transaction), write the valley
    // lines, map ClipTarget + ClipElev, rebuild, check. A bend whose repair fails part-way is rolled back (bowtie_unfix)
    // unless keepOnFailure; bends without a bowtie, or already repaired, are skipped. The inside of each bend is found from
    // the baseline; side only filters.
    bowtie_fix: {
      action: "bowtie_fix",
      inputSchema: CorridorBowtieFixArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["bowtieBends", "bowtieSeam", "isolateCorridorRanges", "getCorridorTargetMappings", "setCorridorTargetMappings", "checkCorridorBowties", "bowtieUnfix"],
      execute: async (args) => await withApplicationConnection(async (appClient) => {
        const call = async (method: string, params: Record<string, unknown>): Promise<{ ok: true; value: any } | { ok: false; error: string }> => {
          try { return { ok: true, value: await appClient.sendCommand(method, params) }; }
          catch (e) { return { ok: false, error: e instanceof Error ? e.message : String(e) }; }
        };
        const baselineIndex = args.baselineIndex ?? 0;
        const sideFilter = args.side === "left" || args.side === "right" ? args.side : null;
        const bendsRes = await call("bowtieBends", {
          corridorName: args.name, baselineIndex, startStation: args.startStation ?? null, endStation: args.endStation ?? null,
          minTurnDegrees: args.minTurnDegrees ?? null,
        });
        if (!bendsRes.ok) return { corridorName: args.name, ok: false, message: `The bends could not be read: ${bendsRes.error}. Nothing was changed.`, bends: [] };
        const bends = ((bendsRes.value?.bends ?? []) as any[]).filter((b) => !sideFilter || b.side === sideFilter);

        const fixOne = async (bend: any) => {
          const stages: Record<string, unknown> = {};
          const side = String(bend.side);
          const bendArgs = { ...args, startStation: bend.startStation, endStation: bend.endStation, side, clipAssembly: args.clipAssembly ?? args.assemblyName ?? null };
          const out = (outcome: string, message: string, extra: Record<string, unknown> = {}) =>
            ({ startStation: bend.startStation, endStation: bend.endStation, side, type: bend.type, turnDegrees: bend.turnDegrees, outcome, message, ...extra, stages });
          if (bend.repaired) return out("skipped", `Region '${bend.region}' already carries a valley repair (mapped ClipTarget): refresh it with bowtie_refresh, or undo it with bowtie_unfix first.`);

          // 1. solve on the sections as built (read-only)
          const pre = await call("bowtieSeam", bowtieSeamParams({ ...bendArgs, snapshotPath: null, skipRegionCheck: true }, true));
          if (!pre.ok) {
            const already = /already clipped/i.test(pre.error);
            return out(already ? "skipped" : "failed", already ? "Sections in range are already clipped: this bend is already repaired." : `Could not be solved: ${pre.error}. Nothing was changed.`);
          }
          const preview = pre.value;
          const status = preview?.result?.status;
          stages.preview = { status, reasons: preview?.result?.reasons, blocking: preview?.blocking, meetStations: preview?.result?.meetStations, region: preview?.region, checks: preview?.result?.checks };
          if (status === "NoBowtie" || status === "NoBend") return out("skipped", "No bowtie on this bend: nothing to repair.");
          if (status !== "Ok" || (preview?.blocking ?? []).length > 0)
            return out("refused", `Cannot be repaired as it stands (${status}): ${[...(preview?.result?.reasons ?? []), ...(preview?.blocking ?? [])].join("; ")}. Nothing was changed.`, { highlight: preview?.highlight ?? null });
          const meet = (preview.result.meetStations ?? []) as number[];
          if (meet.length !== 2 || meet.some((m) => typeof m !== "number")) return out("refused", "The solver gave no meet stations. Nothing was changed.");
          const pad = Number(args.padding ?? 2);
          const region = preview.region as { name: string; start: number; end: number; assemblyName?: string | null };
          // the clash range may run past the region at the apex (a bend on a region boundary): the isolation splits it by region
          const from = Math.floor(Math.min(meet[0], preview.result.clipFrom ?? meet[0]) - pad);
          const to = Math.ceil(Math.max(meet[1], preview.result.clipTo ?? meet[1]) + pad);
          const apexStation = Number(preview.result?.apex?.station ?? 0.5 * (from + to));
          const assemblyMap = (args.assemblyMap ?? null) as Record<string, string> | null;

          // 2. own region(s) for the clash range; a different assembly gets the parent's surface targets in the same transaction
          const swapping = (typeof args.assemblyName === "string" && args.assemblyName.length > 0) || (assemblyMap !== null && Object.keys(assemblyMap).length > 0);
          const alreadyIsolated = Math.abs(region.start - from) < 0.011 && Math.abs(region.end - to) < 0.011;
          type Piece = { name: string; start: number; end: number; parentRegion: string | null; parentAssembly: string | null; splitBefore: string | null; splitAfter: string | null };
          let pieces: Piece[] = [{ name: region.name, start: region.start, end: region.end, parentRegion: null, parentAssembly: null, splitBefore: null, splitAfter: null }];
          let changed = false;
          const rollback = async (stage: string, message: string) => {
            if (!changed || args.keepOnFailure === true) return out("failed", `${message}${changed ? " The changes for this bend were left in the drawing (keepOnFailure)." : " Nothing was changed."}`, { failedAt: stage });
            const undone: any[] = []; let ok = true;
            for (const p of [...pieces].sort((a, b) => b.start - a.start)) {
              const undo = await call("bowtieUnfix", {
                corridorName: args.name, baselineIndex, regionName: p.name, merge: true, restoreAssembly: true, rebuild: true, dryRun: false,
                parentRegion: p.parentRegion, parentAssembly: p.parentAssembly, splitBefore: p.splitBefore, splitAfter: p.splitAfter,
              });
              ok = ok && undo.ok;
              undone.push(undo.ok ? { region: p.name, targetsCleared: undo.value?.targetsCleared, valleyLinesErased: undo.value?.valleyLinesErased, stationsDeleted: undo.value?.stationsDeleted, assemblyRestored: undo.value?.assemblyRestored, merged: undo.value?.merged, rebuildError: undo.value?.rebuildError, warnings: undo.value?.warnings } : { region: p.name, error: undo.error });
            }
            stages.rollback = undone;
            return out(ok ? "rolled_back" : "failed", `${message} ${ok ? "Every change made for this bend was undone (see stages.rollback)." : `The roll-back failed in part: check ${pieces.map((p) => `'${p.name}'`).join(", ")} by hand (bowtie_unfix).`}`, { failedAt: stage });
          };
          if (!alreadyIsolated || swapping) {
            const iso = await call("isolateCorridorRanges", {
              corridorName: args.name, baselineIndex,
              ranges: [{ startStation: from, endStation: to, ...(args.regionName && bends.length === 1 ? { name: args.regionName } : {}) }],
              namePrefix: "BT", frequency: null, assemblyName: typeof args.assemblyName === "string" && args.assemblyName.length > 0 ? args.assemblyName : null,
              assemblyMap, matchParent: true, carrySurfaceTargets: true, dryRun: false, rebuild: swapping,
            });
            if (!iso.ok) return out("failed", `The region could not be isolated: ${iso.error}. Nothing was changed.`, { failedAt: "isolate" });
            const isolated = iso.value;
            const made = (isolated?.isolated ?? []) as any[];
            stages.isolate = { isolated: made, skipped: isolated?.skipped, rebuilt: isolated?.rebuilt, rebuildError: isolated?.rebuildError };
            if (made.length === 0) return out("failed", `Nothing was isolated for ${from}-${to} (${JSON.stringify(isolated?.skipped ?? [])}).`, { failedAt: "isolate" });
            changed = true;
            pieces = made.map((m) => ({ name: String(m.name), start: Number(m.startStation), end: Number(m.endStation), parentRegion: m.fromRegion ?? null,
              parentAssembly: m.parentAssemblyName ?? null, splitBefore: m.regionBefore ?? null, splitAfter: m.regionAfter ?? null }));
            if (isolated?.rebuildError) return await rollback("isolate", `The region was isolated but the rebuild failed: ${isolated.rebuildError}.`);
          }
          const apexPiece = pieces.find((p) => apexStation >= p.start - 0.011 && apexStation <= p.end + 0.011) ?? pieces[0];

          // 3. the inside clip subassembly of every piece
          const mapping = await call("getCorridorTargetMappings", { corridorName: args.name, regionIndex: null, baselineIndex });
          if (!mapping.ok) return await rollback("subassembly", `The targets could not be read: ${mapping.error}.`);
          const sideRe = side === "left" ? /(^|[\s_\-])(l|left)$/i : /(^|[\s_\-])(r|right)$/i;
          const subs: { piece: Piece; sub: string; hasElev: boolean }[] = [];
          for (const p of pieces) {
            const reg = (mapping.value?.regions ?? []).find((r: any) => r.regionName === p.name);
            const clipSubs = [...new Set(((reg?.targets ?? []) as any[]).filter((t) => t.parameterName === "ClipTarget").map((t) => String(t.subassemblyName)))];
            const bySide = clipSubs.filter((n) => sideRe.test(n));
            const sub = typeof args.subassemblyName === "string" && args.subassemblyName.length > 0 && clipSubs.includes(args.subassemblyName) ? args.subassemblyName : (bySide.length === 1 ? bySide[0] : null);
            if (!sub)
              return await rollback("subassembly", `Region '${p.name}' has no unambiguous clip subassembly for the ${side} side (found: ${clipSubs.join(", ") || "none with a ClipTarget"}); pass subassemblyName, or assemblyName / assemblyMap for an assembly that has one.`);
            subs.push({ piece: p, sub, hasElev: ((reg?.targets ?? []) as any[]).some((t) => t.parameterName === "ClipElev" && t.subassemblyName === sub) });
          }
          stages.clipSubassemblies = subs.map((x) => ({ region: x.piece.name, subassembly: x.sub, clipElev: x.hasElev }));
          const hasElev = subs.every((x) => x.hasElev);

          // 4. the valley lines (after a swap: solved again on the clip assemblies' own, still unclipped, sections); the
          //    context of every piece is recorded on them so bowtie_unfix can put each region back later
          const fixPieces = pieces.map((p) => [p.name, p.parentRegion ?? "", p.parentAssembly ?? "", p.splitBefore ?? "", p.splitAfter ?? ""].join("~")).join("|");
          const seamRes = await call("bowtieSeam", bowtieSeamParams({
            ...bendArgs, skipRegionCheck: true, fixPieces,
            parentRegion: apexPiece.parentRegion, parentAssembly: apexPiece.parentAssembly, splitBefore: apexPiece.splitBefore, splitAfter: apexPiece.splitAfter,
            levelFromTarget: args.levelFromTarget ?? hasElev,
          }, false));
          if (!seamRes.ok) return await rollback("seam", `The valley lines could not be written: ${seamRes.error}.`);
          const seam = seamRes.value;
          stages.seam = { status: seam?.result?.status, blocking: seam?.blocking, written: seam?.written, stationsAdded: seam?.stationsAdded, checks: seam?.result?.checks, warnings: seam?.warnings, adjustFrom: seam?.adjustFrom, levelRule: seam?.levelRule };
          const written = (seam?.written ?? []) as any[];
          if (written.length === 0) return await rollback("seam", "No valley line was written (see stages.seam).");
          changed = true;
          const names = written.map((w) => String(w.name));
          const kind = written[0].type === "alignment" ? "alignment" : "feature_line";

          // 5. map on every piece, rebuild once (with the last)
          const maps: any[] = [];
          for (let k = 0; k < subs.length; k++) {
            const { piece, sub } = subs[k];
            const targets: any[] = [{ parameterName: "ClipTarget", subassemblyName: sub, targetType: kind, targetName: names[0], targetNames: names.slice(1), targetToOption: "Nearest" }];
            if (hasElev && kind === "feature_line")
              targets.push({ parameterName: "ClipElev", subassemblyName: sub, targetType: kind, targetName: names[0], targetNames: names.slice(1), targetToOption: "Nearest" });
            const mapped = await call("setCorridorTargetMappings", { corridorName: args.name, regionName: piece.name, baselineIndex, targets, rebuild: k === subs.length - 1 });
            if (!mapped.ok) return await rollback("map", `The targets of '${piece.name}' could not be mapped: ${mapped.error}.`);
            maps.push({ region: piece.name, subassembly: sub, applied: mapped.value?.applied, rebuilt: mapped.value?.rebuilt, rebuildError: mapped.value?.rebuildError });
            if (mapped.value?.rebuildError) { stages.map = maps; return await rollback("map", `Targets were mapped but the rebuild failed: ${mapped.value.rebuildError}.`); }
          }
          stages.map = maps;

          // 6. verify on the built corridor
          const check = await call("checkCorridorBowties", {
            corridorName: args.name, baselineIndex, side, startStation: from - 5, endStation: to + 5,
            linkCode: args.linkCode ?? null, minOffset: null, code: null, tolerance: null, maxListed: null,
          });
          if (!check.ok) return await rollback("check", `The result could not be checked: ${check.error}.`);
          stages.check = check.value;
          if (check.value?.clean !== true) return await rollback("check", "The repair was applied but the built corridor still shows crossings or loops (see stages.check).");
          const regionNames = pieces.map((p) => p.name);
          return out("repaired", `Bend ${from}-${to} (${side}) repaired in ${regionNames.length > 1 ? "regions" : "region"} ${regionNames.map((n) => `'${n}'`).join(" + ")}: no link crossings, no daylight loops.`,
            { regions: regionNames, valleyLines: names, highlight: seam?.highlight ?? preview?.highlight ?? null, levelMove: seam?.result?.checks?.maxLevelAdjust ?? null, slopeChange: seam?.result?.checks?.maxSlopeChange ?? null });
        };

        const results: any[] = [];
        for (const bend of bends) results.push(await fixOne(bend));
        const count = (o: string) => results.filter((r) => r.outcome === o).length;
        const summary = { bends: results.length, repaired: count("repaired"), skipped: count("skipped"), refused: count("refused"), rolledBack: count("rolled_back"), failed: count("failed") };
        return {
          corridorName: args.name,
          ok: summary.failed === 0 && summary.rolledBack === 0,
          summary,
          message: bends.length === 0 ? "No bend in the range." :
            `${summary.repaired} repaired, ${summary.skipped} skipped (no bowtie or already repaired), ${summary.refused} refused as they stand, ${summary.rolledBack} rolled back, ${summary.failed} failed.`,
          highlight: results.filter((r) => r.highlight || (typeof r.slopeChange === "number" && r.slopeChange > 0.1)).map((r) => ({ startStation: r.startStation, endStation: r.endStation, side: r.side, sectionChanges: r.highlight, levelMove: r.levelMove, slopeChange: r.slopeChange })),
          bends: results,
        };
      }),
    },
    bowtie_bends: {
      action: "bowtie_bends",
      inputSchema: CorridorBowtieBendsArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["query", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["bowtieBends"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("bowtieBends", {
          corridorName: args.name, baselineIndex: args.baselineIndex ?? 0,
          startStation: args.startStation ?? null, endStation: args.endStation ?? null, minTurnDegrees: args.minTurnDegrees ?? null,
        }),
      ),
    },
    bowtie_unfix: {
      action: "bowtie_unfix",
      inputSchema: CorridorBowtieUnfixArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["bowtieUnfix"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("bowtieUnfix", {
          corridorName: args.name, baselineIndex: args.baselineIndex ?? 0, regionName: args.regionName,
          merge: args.merge ?? true, restoreAssembly: args.restoreAssembly ?? true, rebuild: args.rebuild ?? true, dryRun: false,
        }),
      ),
    },
    // bowtie_unfix with dryRun: true resolves here - read-only, so no approval.
    bowtie_unfix_preview: {
      action: "bowtie_unfix_preview",
      inputSchema: CorridorBowtieUnfixPreviewArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["query"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["bowtieUnfix"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("bowtieUnfix", {
          corridorName: args.name, baselineIndex: args.baselineIndex ?? 0, regionName: args.regionName,
          merge: args.merge ?? true, restoreAssembly: args.restoreAssembly ?? true, rebuild: false, dryRun: true,
        }),
      ),
    },
    // bowtie_seam with dryRun: true resolves here - read-only, so no approval.
    bowtie_seam_preview: {
      action: "bowtie_seam_preview",
      inputSchema: CorridorBowtieSeamPreviewArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["query", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["bowtieSeam"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("bowtieSeam", bowtieSeamParams(args, true)),
      ),
    },
    bowtie_refresh: {
      action: "bowtie_refresh",
      inputSchema: CorridorBowtieRefreshArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["bowtieRefresh"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("bowtieRefresh", bowtieRefreshParams(args, args.dryRun === true)),
      ),
    },
    // bowtie_refresh with dryRun: true resolves here - read-only, so no approval.
    bowtie_refresh_preview: {
      action: "bowtie_refresh_preview",
      inputSchema: CorridorBowtieRefreshPreviewArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["query", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["bowtieRefresh"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("bowtieRefresh", bowtieRefreshParams(args, true)),
      ),
    },
    region_stations: {
      action: "region_stations",
      inputSchema: CorridorRegionStationsArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["corridorRegionStations"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("corridorRegionStations", {
          corridorName: args.name,
          baselineIndex: args.baselineIndex ?? 0,
          regionIndex: args.regionIndex ?? null,
          regionName: args.regionName ?? null,
          operation: args.operation ?? "list",
          stations: args.stations ?? null,
          rebuild: args.rebuild ?? true,
        }),
      ),
    },
    // region_stations with operation list (or none) resolves here - read-only, so no approval.
    region_stations_list: {
      action: "region_stations_list",
      inputSchema: CorridorRegionStationsListArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["query"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["corridorRegionStations"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("corridorRegionStations", {
          corridorName: args.name,
          baselineIndex: args.baselineIndex ?? 0,
          regionIndex: args.regionIndex ?? null,
          regionName: args.regionName ?? null,
          operation: "list",
          stations: null,
          rebuild: false,
        }),
      ),
    },
    bowtie_check: {
      action: "bowtie_check",
      inputSchema: CorridorBowtieCheckArgsSchema,
      responseSchema: GenericCorridorResponseSchema,
      capabilities: ["query", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["checkCorridorBowties"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("checkCorridorBowties", {
          corridorName: args.name,
          baselineIndex: args.baselineIndex ?? 0,
          side: args.side ?? "both",
          startStation: args.startStation ?? null,
          endStation: args.endStation ?? null,
          linkCode: args.linkCode ?? null,
          minOffset: args.minOffset ?? null,
          code: args.code ?? null,
          tolerance: args.tolerance ?? null,
          maxListed: args.maxListed ?? null,
        }),
      ),
    },
  },
  exposures: [
    {
      toolName: "civil3d_corridor",
      displayName: "Civil 3D Corridor",
      description: "Creates and reads Civil 3D corridors (create = assembly on an alignment + profile, or on a feature line, one baseline and region), controls rebuild, computes volumes, manages regions (add, split, isolate station ranges, merge, delete), predicts bowties (bowtie_predict: where the inside edge runs backwards or crosses itself, with a split plan), builds the valley line of a bend as a clip target (bowtie_valley; refuses bends whose legs differ or whose valley meets the ground on the wrong side or outside the region) and refreshes every mapped valley after a design change (bowtie_refresh), repairs one bend in one step (bowtie_fix: solve, isolate the clash range with an optional clip assembly whose surface targets are carried over, write the valley lines, map ClipTarget + ClipElev, rebuild, check), verifies the built result (bowtie_check: link crossings and feature-line loops), lists or removes a region's added stations (region_stations); dry runs and listings need no approval, assembly frequency and subassembly target mappings, builds corridor surfaces (link/feature-line codes, overhang correction, boundaries) and extracts corridor solids through a single domain tool.",
      inputShape: canonicalCorridorInputShape,
      supportedActions: [
        "list",
        "get",
        "create",
        "rebuild",
        "get_surfaces",
        "get_feature_lines",
        "compute_volumes",
        "summary",
        "target_mapping_get",
        "target_mapping_set",
        "region_add",
        "region_frequency",
        "region_delete",
        "region_split",
        "region_isolate",
        "region_merge",
        "bowtie_predict",
        "bowtie_valley",
        "bowtie_valley_preview",
        "bowtie_seam",
        "bowtie_seam_preview",
        "bowtie_fix",
        "bowtie_bends",
        "bowtie_unfix",
        "bowtie_unfix_preview",
        "bowtie_refresh",
        "bowtie_refresh_preview",
        "bowtie_check",
        "region_stations",
        "region_stations_list",
        "section",
        "feature_line_codes",
        "feature_line_export",
        "surface_info",
        "surface_create",
        "surface_edit",
        "surface_delete",
        "export_solids",
      ],
      resolveAction: resolveCorridorAction,
    },
    {
      toolName: "civil3d_corridor_summary",
      displayName: "Civil 3D Corridor Summary",
      description: "Builds a corridor summary by fetching corridor details, corridor surfaces, and optional volume analysis against a reference surface.",
      inputShape: {
        name: z.string(),
        corridorSurface: z.string().optional(),
        referenceSurface: z.string().optional(),
      },
      supportedActions: ["summary"],
      resolveAction: (rawArgs) => ({
        action: "summary",
        args: {
          action: "summary",
          name: rawArgs.name,
          corridorSurface: rawArgs.corridorSurface,
          referenceSurface: rawArgs.referenceSurface,
        },
      }),
    },
    {
      toolName: "civil3d_corridor_target_mapping_get",
      displayName: "Civil 3D Corridor Target Mapping Get",
      description: "Retrieve the current subassembly target mappings for a Civil 3D corridor. Returns all target parameters for each baseline region.",
      inputShape: {
        corridorName: z.string(),
        regionIndex: z.number().int().nonnegative().optional(),
        baselineIndex: z.number().int().nonnegative().optional(),
      },
      supportedActions: ["target_mapping_get"],
      resolveAction: (rawArgs) => ({
        action: "target_mapping_get",
        args: {
          action: "target_mapping_get",
          name: rawArgs.corridorName,
          regionIndex: rawArgs.regionIndex,
          baselineIndex: rawArgs.baselineIndex,
        },
      }),
    },
    {
      toolName: "civil3d_corridor_target_mapping_set",
      displayName: "Civil 3D Corridor Target Mapping Set",
      description: "Set or update subassembly target mappings on a Civil 3D corridor region. Assigns surfaces, alignments or profiles as targets for subassembly parameters (use target_mapping_get to list the parameter names).",
      inputShape: {
        corridorName: z.string(),
        regionIndex: z.number().int().nonnegative().optional(),
        baselineIndex: z.number().int().nonnegative().optional(),
        targets: z.array(CorridorTargetSchema),
        rebuild: z.boolean().optional(),
      },
      supportedActions: ["target_mapping_set"],
      resolveAction: (rawArgs) => ({
        action: "target_mapping_set",
        args: {
          action: "target_mapping_set",
          name: rawArgs.corridorName,
          regionIndex: rawArgs.regionIndex,
          baselineIndex: rawArgs.baselineIndex,
          targets: rawArgs.targets,
          rebuild: rawArgs.rebuild,
        },
      }),
    },
    {
      toolName: "civil3d_corridor_region_add",
      displayName: "Civil 3D Corridor Region Add",
      description: "Add a new region to a Civil 3D corridor baseline, defining which assembly applies over a station range and at what sampling frequency.",
      inputShape: {
        corridorName: z.string(),
        baselineIndex: z.number().int().nonnegative().optional(),
        assemblyName: z.string(),
        regionName: z.string().optional(),
        startStation: z.number(),
        endStation: z.number(),
        frequency: z.number().positive().optional(),
        rebuild: z.boolean().optional(),
      },
      supportedActions: ["region_add"],
      resolveAction: (rawArgs) => ({
        action: "region_add",
        args: {
          action: "region_add",
          name: rawArgs.corridorName,
          baselineIndex: rawArgs.baselineIndex,
          assemblyName: rawArgs.assemblyName,
          regionName: rawArgs.regionName,
          startStation: rawArgs.startStation,
          endStation: rawArgs.endStation,
          frequency: rawArgs.frequency,
          rebuild: rawArgs.rebuild,
        },
      }),
    },
    {
      toolName: "civil3d_corridor_region_delete",
      displayName: "Civil 3D Corridor Region Delete",
      description: "Delete a region from a Civil 3D corridor baseline by its zero-based index. Rebuilds the corridor after deletion.",
      inputShape: {
        corridorName: z.string(),
        baselineIndex: z.number().int().nonnegative().optional(),
        regionIndex: z.number().int().nonnegative(),
        rebuild: z.boolean().optional(),
      },
      supportedActions: ["region_delete"],
      resolveAction: (rawArgs) => ({
        action: "region_delete",
        args: {
          action: "region_delete",
          name: rawArgs.corridorName,
          baselineIndex: rawArgs.baselineIndex,
          regionIndex: rawArgs.regionIndex,
          rebuild: rawArgs.rebuild,
        },
      }),
    },
  ],
};
