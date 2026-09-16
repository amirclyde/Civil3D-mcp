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
  alignmentName: z.string(),
  profileName: z.string(),
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
  targetType: z.enum(["surface", "alignment", "profile"]),
  targetName: z.string(),
  subassemblyName: z.string().optional().describe("Restrict the match to one subassembly instance in the region."),
  targetToOption: z.enum(["Nearest", "Farthest", "Flattest", "Steepest"]).optional(),
});

const CorridorTargetMappingSetArgsSchema = z.object({
  action: z.literal("target_mapping_set"),
  name: z.string(),
  regionIndex: z.number().int().nonnegative().optional(),
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
  type: z.enum(["corridor_extents", "feature_line", "polyline", "points"]).optional().describe("Default corridor_extents (outer extents of the corridor)."),
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
  surfaceName: z.string().optional().describe("surface_*: corridor surface name."),
  linkCodes: z.array(CorridorLinkCodeSchema).optional().describe("surface_create / add_link_code: link codes (string or {code, breakline})."),
  featureLineCodes: z.array(z.string()).optional(),
  overhangCorrection: z.enum(["none", "top_links", "bottom_links"]).optional(),
  boundaries: z.array(CorridorSurfaceBoundarySchema).optional().describe("surface_create: boundaries; default none (add a corridor_extents boundary for a clean surface)."),
  boundary: CorridorSurfaceBoundarySchema.optional().describe("surface_edit add_boundary."),
  boundaryName: z.string().optional(),
  operation: z.string().optional().describe("surface_edit: add_link_code | remove_link_code | set_breakline | add_feature_line_code | remove_feature_line_code | set_overhang | add_boundary | remove_boundary | set_style | rename | set_description | set_build."),
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
  code: z.string().optional().describe("feature_line_export: corridor feature line code."),
  exportAs: z.enum(["alignment", "profile", "feature_line", "polyline3d"]).optional(),
  featureLineIndex: z.number().int().nonnegative().optional(),
  outputName: z.string().optional(),
  siteName: z.string().optional(),
  layer: z.string().optional(),
  style: z.string().optional(),
  labelSet: z.string().optional(),
  dynamic: z.boolean().optional(),
  smooth: z.boolean().optional(),
  station: z.number().optional().describe("section: station to read the built cross-section (applied assembly) at; snaps to the nearest applied station."),
  alignmentName: z.string().optional().describe("create: baseline alignment (with profileName)."),
  profileName: z.string().optional().describe("create: baseline profile (must belong to alignmentName)."),
  featureLineName: z.string().optional().describe("create: use a feature line as the baseline instead of alignment + profile (stations run 0 to its length)."),
  featureLineHandle: z.string().optional().describe("create: feature line by AutoCAD handle, for unnamed or duplicate-named feature lines."),
  baselineName: z.string().optional(),
  regionName: z.string().optional(),
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
};

// ─── Domain definition ────────────────────────────────────────────────────────

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
          regionIndex: args.regionIndex ?? 0,
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
  },
  exposures: [
    {
      toolName: "civil3d_corridor",
      displayName: "Civil 3D Corridor",
      description: "Creates and reads Civil 3D corridors (create = assembly on an alignment + profile, or on a feature line, one baseline and region), controls rebuild, computes volumes, manages regions, assembly frequency and subassembly target mappings, builds corridor surfaces (link/feature-line codes, overhang correction, boundaries) and extracts corridor solids through a single domain tool.",
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
        "section",
        "feature_line_codes",
        "feature_line_export",
        "surface_info",
        "surface_create",
        "surface_edit",
        "surface_delete",
        "export_solids",
      ],
      resolveAction: (rawArgs) => ({
        action: String(rawArgs.action ?? ""),
        args: rawArgs,
      }),
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
