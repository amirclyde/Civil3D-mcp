import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

const PipeFlowSchema = z.object({
  pipeName: z.string(),
  designFlow: z.number().positive(),
});

const GenericPipeResponseSchema = z.object({}).passthrough();

const PartSchema = z.union([
  z.string(),
  z.object({
    family: z.string().optional().describe("Part family name as in the parts list (e.g. 'Concrete Pipe', 'Concentric Cylindrical Structure')."),
    size: z.string().optional().describe("Part size name within the family."),
    innerDiameter: z.number().positive().optional().describe("Match the size by inner diameter/width (drawing units, m)."),
    diameter: z.number().positive().optional().describe("Structures: match by (inner) diameter (drawing units, m)."),
  }).strict(),
]).describe("Part size name from the network's parts list (see action 'catalog'), or {family, size | innerDiameter | diameter}.");

const EndPointSchema = z.object({ x: z.number(), y: z.number(), z: z.number().optional().describe("Legacy: centreline elevation, used only when no invert/cover/slope is given.") }).strict();

const NetworkSettingsShape = {
  referenceSurface: z.string().optional().describe("Reference surface ('' clears)."),
  referenceAlignment: z.string().optional().describe("Reference alignment ('' clears)."),
  pipeNameTemplate: z.string().optional(),
  structureNameTemplate: z.string().optional(),
  pipePlanLabelStyle: z.string().optional(),
  pipeProfileLabelStyle: z.string().optional(),
  structurePlanLabelStyle: z.string().optional(),
  structureProfileLabelStyle: z.string().optional(),
  pipePlanLayer: z.string().optional().describe("Layers are created if missing."),
  structurePlanLayer: z.string().optional(),
  pipeProfileLayer: z.string().optional(),
  structureProfileLayer: z.string().optional(),
  sectionLayer: z.string().optional(),
  layer: z.string().optional().describe("Legacy: plan layer for pipes and structures."),
};

const PartCommonShape = {
  name: z.string().optional().describe("Part name (otherwise the network's name template)."),
  description: z.string().optional(),
  style: z.string().optional().describe("Pipe or structure style (strict lookup)."),
  ruleSet: z.string().optional().describe("Override rule set for this part (strict lookup)."),
};

const PipeListArgs = z.object({ action: z.literal("list") });
const PipeGetArgs = z.object({ action: z.literal("get"), name: z.string(), includeParts: z.boolean().optional() });
const PipeGetPipeArgs = z.object({ action: z.literal("get_pipe"), networkName: z.string(), pipeName: z.string().optional(), handle: z.string().optional() })
  .refine((v) => v.pipeName || v.handle, { message: "Give pipeName or handle." });
const PipeGetStructureArgs = z.object({ action: z.literal("get_structure"), networkName: z.string(), structureName: z.string().optional(), handle: z.string().optional() })
  .refine((v) => v.structureName || v.handle, { message: "Give structureName or handle." });
const PipeCatalogArgs = z.object({
  action: z.enum(["catalog", "catalog_list"]),
  partsList: z.string().optional(),
  includeFields: z.boolean().optional().describe("Every catalog field of every size (for part_data work / debugging)."),
  includeCatalog: z.boolean().optional().describe("Also list the families available in the drawing's pipe network catalog."),
});
const PipePathArgs = z.object({ action: z.literal("path"), networkName: z.string(), fromPart: z.string(), toPart: z.string() });
const PipeCreateArgs = z.object({ action: z.literal("create"), name: z.string(), partsList: z.string(), ...NetworkSettingsShape, style: z.string().optional() });
const PipeEditNetworkArgs = z.object({ action: z.literal("edit_network"), name: z.string(), newName: z.string().optional(), partsList: z.string().optional(), ...NetworkSettingsShape });
const PipeDeleteArgs = z.object({ action: z.literal("delete"), name: z.string(), deleteParts: z.boolean().optional() });
const PipeAddStructureArgs = z.object({
  action: z.literal("add_structure"),
  networkName: z.string(),
  part: PartSchema.optional(),
  partName: z.string().optional().describe("Legacy alias of part."),
  x: z.number(),
  y: z.number(),
  rotation: z.number().optional().describe("Degrees."),
  rimElevation: z.number().optional(),
  rimFromSurface: z.boolean().optional().describe("Rim follows the surface (default when rimElevation is absent and a surface is known)."),
  surface: z.string().optional().describe("Surface for the rim (default: the network's reference surface)."),
  rimAdjustment: z.number().optional().describe("Rim offset from the surface."),
  sumpDepth: z.number().optional().describe("Sump below the lowest connected pipe invert (Civil 3D 'by depth')."),
  sumpElevation: z.number().optional(),
  rimToSumpHeight: z.number().optional().describe("Total rim-to-sump height (for a structure without pipes yet)."),
  applyRules: z.boolean().optional(),
  ...PartCommonShape,
}).refine((v) => v.part !== undefined || v.partName !== undefined, { message: "Give part." });
const PipeAddPipeArgs = z.object({
  action: z.literal("add_pipe"),
  networkName: z.string(),
  part: PartSchema.optional(),
  partName: z.string().optional().describe("Legacy alias of part."),
  startStructure: z.string().optional().describe("Structure name or handle."),
  endStructure: z.string().optional(),
  startPoint: EndPointSchema.optional().describe("Free end instead of a structure."),
  endPoint: EndPointSchema.optional(),
  startInvert: z.number().optional(),
  endInvert: z.number().optional(),
  slope: z.number().optional().describe("Percent, positive = falls from start to end; used with one invert or cover."),
  startCover: z.number().optional().describe("Cover to the pipe's outer top at the start (needs a surface)."),
  endCover: z.number().optional(),
  surface: z.string().optional().describe("Surface for covers (default: the network's reference surface)."),
  applyRules: z.boolean().optional().describe("Let the rule set set the elevations when none are given."),
  flowDirection: z.enum(["by_slope", "start_to_end", "end_to_start", "bidirectional"]).optional(),
  ...PartCommonShape,
}).superRefine((v, ctx) => {
  if (v.part === undefined && v.partName === undefined) ctx.addIssue({ code: z.ZodIssueCode.custom, message: "Give part.", path: ["part"] });
  if (!v.startStructure && !v.startPoint) ctx.addIssue({ code: z.ZodIssueCode.custom, message: "Give startStructure or startPoint.", path: ["startStructure"] });
  if (!v.endStructure && !v.endPoint) ctx.addIssue({ code: z.ZodIssueCode.custom, message: "Give endStructure or endPoint.", path: ["endStructure"] });
});

const PipeCalculateHglArgsSchema = z.object({
  action: z.literal("calculate_hgl"),
  networkName: z.string(),
  tailwaterElevation: z.number().optional(),
  designFlow: z.number().optional(),
  manningsN: z.number().positive().optional(),
});
const PipeHydraulicAnalysisArgsSchema = z.object({
  action: z.literal("hydraulic_analysis"),
  networkName: z.string(),
  designFlow: z.number().optional(),
  manningsN: z.number().positive().optional(),
  minCoverDepth: z.number().nonnegative().optional(),
  minVelocity: z.number().nonnegative().optional(),
  maxVelocity: z.number().positive().optional(),
  minSlope: z.number().nonnegative().optional(),
});
const PipeStructurePropertiesArgsSchema = z.object({
  action: z.literal("get_structure_properties"),
  networkName: z.string(),
  structureName: z.string(),
});
const PipeSizeNetworkArgsSchema = z.object({
  action: z.literal("size_network"),
  networkName: z.string(),
  partsList: z.string().optional(),
  defaultDesignFlow: z.number().positive().optional(),
  perPipeDesignFlows: z.array(PipeFlowSchema).optional(),
  manningsN: z.number().positive().optional().default(0.013),
  targetVelocityMin: z.number().positive().optional().default(2.0),
  targetVelocityMax: z.number().positive().optional().default(10.0),
  applyChanges: z.boolean().optional().default(false),
});
const PipeAutomateProfileViewArgsSchema = z.object({
  action: z.literal("automate_profile_view"),
  networkName: z.string(),
  profileViewName: z.string(),
  insertX: z.number(),
  insertY: z.number(),
  alignmentName: z.string().optional(),
  surfaceName: z.string().optional(),
  existingProfileName: z.string().optional(),
  surfaceProfileName: z.string().optional(),
  createSurfaceProfileIfMissing: z.boolean().optional().default(true),
  style: z.string().optional(),
  bandSet: z.string().optional(),
});

type PipeNetworkDetail = {
  name: string;
  partsList?: string;
  referenceAlignment?: string;
  referenceSurface?: string;
  pipes: Array<{ name: string; partSize?: string; innerDiameterOrWidth: number; slopePercent: number | null; length2D: number }>;
};

type CatalogSize = { name?: string | null; innerDiameter?: number | null };
type PartsCatalogResponse = {
  partsLists: Array<{ name: string | null; pipeFamilies?: Array<{ name: string; sizes: CatalogSize[] }> }>;
};

function computeFullFlowCapacity(diameter: number, slopePct: number, manningsN: number): number {
  const slope = Math.max(Math.abs(slopePct) / 100, 1e-6);
  const area = Math.PI * diameter * diameter / 4;
  const hydraulicRadius = diameter / 4;
  return (1 / manningsN) * area * Math.pow(hydraulicRadius, 2 / 3) * Math.sqrt(slope);
}

function computeVelocity(flow: number, diameter: number): number {
  const area = Math.PI * diameter * diameter / 4;
  return area > 0 ? flow / area : 0;
}

function solveRequiredDiameter(flow: number, slopePct: number, manningsN: number): number {
  let low = 0.01;
  let high = 100;
  while (computeFullFlowCapacity(high, slopePct, manningsN) < flow && high < 1_000_000) high *= 2;
  for (let i = 0; i < 60; i++) {
    const mid = (low + high) / 2;
    const capacity = computeFullFlowCapacity(mid, slopePct, manningsN);
    if (capacity >= flow) high = mid;
    else low = mid;
  }
  return high;
}

function chooseBestPart(sizes: CatalogSize[], requiredDiameter: number, flow: number, velocityMin: number, velocityMax: number) {
  const candidates = sizes
    .map((size) => ({ name: size.name ?? "", diameter: size.innerDiameter ?? null }))
    .filter((candidate): candidate is { name: string; diameter: number } => candidate.diameter != null && candidate.name !== "")
    .sort((a, b) => a.diameter - b.diameter);

  const preferred = candidates.find((candidate) => {
    if (candidate.diameter < requiredDiameter) return false;
    const velocity = computeVelocity(flow, candidate.diameter);
    return velocity >= velocityMin && velocity <= velocityMax;
  });

  return preferred ?? candidates.find((candidate) => candidate.diameter >= requiredDiameter) ?? candidates[candidates.length - 1] ?? null;
}

function stripAction(args: Record<string, unknown>): Record<string, unknown> {
  const { action: _action, ...rest } = args;
  return rest;
}

/**
 * civil3d_pipe — gravity (storm / sewer) networks. Phase P0 (17 Sep 2026): typed read / create / place.
 * Design and phases: project doc claude/pipe-network-design.md.
 */
export const PIPE_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "pipe",
  actions: {
    list: {
      action: "list",
      inputSchema: PipeListArgs,
      responseSchema: GenericPipeResponseSchema,
      capabilities: ["query"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listPipeNetworks"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("listPipeNetworks", {})),
    },
    get: {
      action: "get",
      inputSchema: PipeGetArgs,
      responseSchema: GenericPipeResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getPipeNetwork"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("getPipeNetwork", stripAction(args))),
    },
    get_pipe: {
      action: "get_pipe",
      inputSchema: PipeGetPipeArgs,
      responseSchema: GenericPipeResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getPipe"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("getPipe", stripAction(args))),
    },
    get_structure: {
      action: "get_structure",
      inputSchema: PipeGetStructureArgs,
      responseSchema: GenericPipeResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getStructure"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("getStructure", stripAction(args))),
    },
    catalog: {
      action: "catalog",
      inputSchema: PipeCatalogArgs,
      responseSchema: GenericPipeResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listPipePartsCatalog"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("listPipePartsCatalog", stripAction(args))),
    },
    catalog_list: {
      action: "catalog_list",
      inputSchema: PipeCatalogArgs,
      responseSchema: GenericPipeResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listPipePartsCatalog"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("listPipePartsCatalog", stripAction(args))),
    },
    path: {
      action: "path",
      inputSchema: PipePathArgs,
      responseSchema: GenericPipeResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getPipeNetworkPath"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("getPipeNetworkPath", stripAction(args))),
    },
    create: {
      action: "create",
      inputSchema: PipeCreateArgs,
      responseSchema: GenericPipeResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createPipeNetwork"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("createPipeNetwork", stripAction(args))),
    },
    edit_network: {
      action: "edit_network",
      inputSchema: PipeEditNetworkArgs,
      responseSchema: GenericPipeResponseSchema,
      capabilities: ["edit", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["editPipeNetwork"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("editPipeNetwork", stripAction(args))),
    },
    delete: {
      action: "delete",
      inputSchema: PipeDeleteArgs,
      responseSchema: GenericPipeResponseSchema,
      capabilities: ["delete"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["deletePipeNetwork"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("deletePipeNetwork", stripAction(args))),
    },
    add_structure: {
      action: "add_structure",
      inputSchema: PipeAddStructureArgs,
      responseSchema: GenericPipeResponseSchema,
      capabilities: ["create", "edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["addStructureToNetwork"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("addStructureToNetwork", stripAction(args))),
    },
    add_pipe: {
      action: "add_pipe",
      inputSchema: PipeAddPipeArgs,
      responseSchema: GenericPipeResponseSchema,
      capabilities: ["create", "edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["addPipeToNetwork"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("addPipeToNetwork", stripAction(args))),
    },
    calculate_hgl: {
      action: "calculate_hgl",
      inputSchema: PipeCalculateHglArgsSchema,
      responseSchema: GenericPipeResponseSchema,
      capabilities: ["query", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["calculatePipeNetworkHgl"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("calculatePipeNetworkHgl", {
        networkName: args.networkName,
        tailwaterElevation: args.tailwaterElevation ?? null,
        designFlow: args.designFlow ?? null,
        manningsN: args.manningsN ?? 0.013,
      })),
    },
    hydraulic_analysis: {
      action: "hydraulic_analysis",
      inputSchema: PipeHydraulicAnalysisArgsSchema,
      responseSchema: GenericPipeResponseSchema,
      capabilities: ["query", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["analyzePipeNetworkHydraulics"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("analyzePipeNetworkHydraulics", {
        networkName: args.networkName,
        designFlow: args.designFlow ?? null,
        manningsN: args.manningsN ?? 0.013,
        minCoverDepth: args.minCoverDepth ?? 2.0,
        minVelocity: args.minVelocity ?? 2.0,
        maxVelocity: args.maxVelocity ?? 10.0,
        minSlope: args.minSlope ?? 0.5,
      })),
    },
    get_structure_properties: {
      action: "get_structure_properties",
      inputSchema: PipeStructurePropertiesArgsSchema,
      responseSchema: GenericPipeResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getPipeStructureProperties"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("getPipeStructureProperties", {
        networkName: args.networkName,
        structureName: args.structureName,
      })),
    },
    size_network: {
      action: "size_network",
      inputSchema: PipeSizeNetworkArgsSchema,
      responseSchema: GenericPipeResponseSchema,
      capabilities: ["analyze", "edit", "generate"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["getPipeNetwork", "listPipePartsCatalog", "resizePipeInNetwork"],
      execute: async (args) => await withApplicationConnection(async (appClient) => {
        const network = await appClient.sendCommand("getPipeNetwork", { name: args.networkName }) as PipeNetworkDetail;
        const partsListName = args.partsList ?? network.partsList;
        if (!partsListName) throw new Error("No parts list was provided and the pipe network does not expose one.");

        const catalog = await appClient.sendCommand("listPipePartsCatalog", { partsList: partsListName }) as PartsCatalogResponse;
        const parts = (catalog.partsLists.find((item) => item.name === partsListName)?.pipeFamilies ?? []).flatMap((family) => family.sizes);
        if (parts.length === 0) throw new Error(`Parts list '${partsListName}' does not contain any pipe parts.`);

        const perPipeDesignFlows = (args.perPipeDesignFlows ?? []) as Array<z.infer<typeof PipeFlowSchema>>;
        const flowMap = new Map(perPipeDesignFlows.map((item) => [item.pipeName.toLowerCase(), item.designFlow]));
        const recommendations: Array<Record<string, unknown>> = [];
        let appliedCount = 0;

        for (const pipe of network.pipes) {
          const designFlow = flowMap.get(pipe.name.toLowerCase()) ?? args.defaultDesignFlow;
          if (!designFlow) {
            recommendations.push({ pipeName: pipe.name, currentDiameter: pipe.innerDiameterOrWidth, status: "skipped", reason: "No design flow was supplied for this pipe." });
            continue;
          }

          const resolvedDesignFlow = Number(designFlow);
          const requiredDiameter = solveRequiredDiameter(resolvedDesignFlow, pipe.slopePercent ?? 0, Number(args.manningsN));
          const selectedPart = chooseBestPart(parts, requiredDiameter, resolvedDesignFlow, Number(args.targetVelocityMin), Number(args.targetVelocityMax));
          if (!selectedPart) {
            recommendations.push({ pipeName: pipe.name, currentDiameter: pipe.innerDiameterOrWidth, status: "skipped", reason: "No pipe size in the parts list reports an inner diameter." });
            continue;
          }

          const selectedVelocity = computeVelocity(resolvedDesignFlow, selectedPart.diameter);
          const shouldApply = args.applyChanges && (selectedPart.name !== pipe.partSize || Math.abs(selectedPart.diameter - pipe.innerDiameterOrWidth) > 1e-6);

          if (shouldApply) {
            await appClient.sendCommand("resizePipeInNetwork", {
              networkName: args.networkName,
              pipeName: pipe.name,
              newPartName: selectedPart.name,
                          });
            appliedCount++;
          }

          recommendations.push({
            pipeName: pipe.name,
            currentDiameter: pipe.innerDiameterOrWidth,
            designFlow: resolvedDesignFlow,
            requiredDiameter: Number(requiredDiameter.toFixed(3)),
            selectedPart: selectedPart.name,
            selectedDiameter: selectedPart.diameter,
            selectedVelocity: Number(selectedVelocity.toFixed(3)),
            applied: shouldApply,
            status: "ok",
          });
        }

        return { networkName: args.networkName, partsList: partsListName, applyChanges: args.applyChanges, appliedCount, recommendations };
      }),
    },
    automate_profile_view: {
      action: "automate_profile_view",
      inputSchema: PipeAutomateProfileViewArgsSchema,
      responseSchema: GenericPipeResponseSchema,
      capabilities: ["create", "manage", "generate"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["getPipeNetwork", "listProfiles", "createProfileFromSurface", "profileViewCreate"],
      execute: async (args) => await withApplicationConnection(async (appClient) => {
        const network = await appClient.sendCommand("getPipeNetwork", { name: args.networkName }) as PipeNetworkDetail;
        const alignmentName = args.alignmentName ?? network.referenceAlignment;
        if (!alignmentName) throw new Error("The pipe network does not expose a reference alignment. Provide 'alignmentName' explicitly.");

        const surfaceName = args.surfaceName ?? network.referenceSurface;
        const profileName = String(args.existingProfileName ?? args.surfaceProfileName ?? `EG_${alignmentName}`);
        const profileList = await appClient.sendCommand("listProfiles", { alignmentName }) as { profiles?: Array<{ name: string }> };
        const existingNames = new Set((profileList.profiles ?? []).map((profile) => profile.name.toLowerCase()));

        if (!existingNames.has(profileName.toLowerCase())) {
          if (!args.createSurfaceProfileIfMissing) throw new Error(`Profile '${profileName}' does not exist and automatic creation is disabled.`);
          if (!surfaceName) throw new Error("No surface was supplied or found on the pipe network, so the EG profile cannot be created.");
          await appClient.sendCommand("createProfileFromSurface", { alignmentName, profileName, surfaceName });
        }

        const profileView = await appClient.sendCommand("profileViewCreate", {
          alignmentName,
          profileViewName: args.profileViewName,
          insertX: args.insertX,
          insertY: args.insertY,
          style: args.style,
          bandSet: args.bandSet,
        });

        return { networkName: args.networkName, alignmentName, surfaceName: surfaceName ?? null, profileName, profileView };
      }),
    },
  },
  exposures: [
    {
      toolName: "civil3d_pipe",
      displayName: "Civil 3D Pipe Network",
      description: "Gravity (storm / sewer) pipe networks on the typed Civil 3D API. Read: list, get (pipes with start/end invert, crown, cover, slope %, 1:n, flow; structures with rim, sump, depth and the invert of every connected pipe), get_pipe, get_structure, catalog (parts lists → families → sizes with inner diameters; includeCatalog lists the catalog families), path (shortest connected path). Write: create / edit_network / delete, add_structure (rim from rimElevation or a surface, sump by depth / elevation / rim-to-sump height), add_pipe between structures or points with elevations from startInvert + endInvert, one invert + slope (%), start/end cover, or applyRules. Nothing defaults to elevation 0. Pressure (water) networks are in civil3d_pressure. calculate_hgl, hydraulic_analysis, get_structure_properties, size_network and automate_profile_view are legacy upstream workflows (imperial defaults, not yet verified) awaiting the analysis phase; interference checking is not available yet.",
      inputShape: {
        action: z.enum(["list", "get", "get_pipe", "get_structure", "catalog", "catalog_list", "path", "create", "edit_network", "delete", "add_structure", "add_pipe", "calculate_hgl", "hydraulic_analysis", "get_structure_properties", "size_network", "automate_profile_view"]),
        name: z.string().optional(),
        newName: z.string().optional(),
        networkName: z.string().optional(),
        pipeName: z.string().optional(),
        structureName: z.string().optional(),
        handle: z.string().optional(),
        includeParts: z.boolean().optional(),
        partsList: z.string().optional(),
        includeFields: z.boolean().optional(),
        includeCatalog: z.boolean().optional(),
        fromPart: z.string().optional(),
        toPart: z.string().optional(),
        deleteParts: z.boolean().optional(),
        ...NetworkSettingsShape,
        part: PartSchema.optional(),
        partName: z.string().optional(),
        x: z.number().optional(),
        y: z.number().optional(),
        rotation: z.number().optional(),
        rimElevation: z.number().optional(),
        rimFromSurface: z.boolean().optional(),
        surface: z.string().optional(),
        rimAdjustment: z.number().optional(),
        sumpDepth: z.number().optional(),
        sumpElevation: z.number().optional(),
        rimToSumpHeight: z.number().optional(),
        startStructure: z.string().optional(),
        endStructure: z.string().optional(),
        startPoint: EndPointSchema.optional(),
        endPoint: EndPointSchema.optional(),
        startInvert: z.number().optional(),
        endInvert: z.number().optional(),
        slope: z.number().optional(),
        startCover: z.number().optional(),
        endCover: z.number().optional(),
        applyRules: z.boolean().optional(),
        flowDirection: z.enum(["by_slope", "start_to_end", "end_to_start", "bidirectional"]).optional(),
        description: z.string().optional(),
        style: z.string().optional(),
        ruleSet: z.string().optional(),
        tailwaterElevation: z.number().optional(),
        designFlow: z.number().optional(),
        manningsN: z.number().positive().optional(),
        minCoverDepth: z.number().nonnegative().optional(),
        minVelocity: z.number().nonnegative().optional(),
        maxVelocity: z.number().positive().optional(),
        minSlope: z.number().nonnegative().optional(),
        defaultDesignFlow: z.number().positive().optional(),
        perPipeDesignFlows: z.array(PipeFlowSchema).optional(),
        targetVelocityMin: z.number().positive().optional(),
        targetVelocityMax: z.number().positive().optional(),
        applyChanges: z.boolean().optional(),
        profileViewName: z.string().optional(),
        insertX: z.number().optional(),
        insertY: z.number().optional(),
        alignmentName: z.string().optional(),
        surfaceName: z.string().optional(),
        existingProfileName: z.string().optional(),
        surfaceProfileName: z.string().optional(),
        createSurfaceProfileIfMissing: z.boolean().optional(),
        bandSet: z.string().optional(),
      },
      supportedActions: ["list", "get", "get_pipe", "get_structure", "catalog", "catalog_list", "path", "create", "edit_network", "delete", "add_structure", "add_pipe", "calculate_hgl", "hydraulic_analysis", "get_structure_properties", "size_network", "automate_profile_view"],
      resolveAction: (rawArgs) => ({ action: String(rawArgs.action ?? ""), args: rawArgs }),
    },
    {
      toolName: "civil3d_pipe_network",
      displayName: "Civil 3D Pipe Network",
      description: "Reads Civil 3D gravity pipe network data including networks, pipes (inverts, cover, slope) and structures (rim, sump, connected pipe inverts).",
      inputShape: {
        action: z.enum(["list", "get", "get_pipe", "get_structure"]),
        name: z.string().optional(),
        networkName: z.string().optional(),
        pipeName: z.string().optional(),
        structureName: z.string().optional(),
        handle: z.string().optional(),
      },
      supportedActions: ["list", "get", "get_pipe", "get_structure"],
      resolveAction: (rawArgs) => ({ action: String(rawArgs.action ?? ""), args: rawArgs }),
    },
    {
      toolName: "civil3d_pipe_network_edit",
      displayName: "Civil 3D Pipe Network Edit",
      description: "Creates and modifies Civil 3D pipe networks, pipes, and structures.",
      inputShape: {
        action: z.enum(["create", "add_pipe", "add_structure"]),
        name: z.string().optional(),
        partsList: z.string().optional(),
        referenceSurface: z.string().optional(),
        referenceAlignment: z.string().optional(),
        style: z.string().optional(),
        layer: z.string().optional(),
        networkName: z.string().optional(),
        startPoint: EndPointSchema.optional(),
        endPoint: EndPointSchema.optional(),
        startStructure: z.string().optional(),
        endStructure: z.string().optional(),
        part: PartSchema.optional(),
        partName: z.string().optional(),
        startInvert: z.number().optional(),
        endInvert: z.number().optional(),
        slope: z.number().optional(),
        startCover: z.number().optional(),
        endCover: z.number().optional(),
        surface: z.string().optional(),
        applyRules: z.boolean().optional(),
        x: z.number().optional(),
        y: z.number().optional(),
        rotation: z.number().optional(),
        rimElevation: z.number().optional(),
        rimFromSurface: z.boolean().optional(),
        sumpDepth: z.number().optional(),
        sumpElevation: z.number().optional(),
        rimToSumpHeight: z.number().optional(),
      },
      supportedActions: ["create", "add_pipe", "add_structure"],
      resolveAction: (rawArgs) => ({ action: String(rawArgs.action ?? ""), args: rawArgs }),
    },
    {
      toolName: "civil3d_pipe_catalog",
      displayName: "Civil 3D Pipe Catalog",
      description: "Lists available Civil 3D pipe parts lists and part names to help choose valid inputs for pipe network creation and editing tools.",
      inputShape: { partsList: z.string().optional() },
      supportedActions: ["catalog_list"],
      resolveAction: (rawArgs) => ({ action: "catalog_list", args: { action: "catalog_list", partsList: rawArgs.partsList } }),
    },
    {
      toolName: "civil3d_pipe_network_hgl_calculate",
      displayName: "Civil 3D Pipe Network HGL Calculate",
      description: "Calculates hydraulic grade line and energy grade line values for a gravity pipe network.",
      inputShape: { networkName: z.string(), tailwaterElevation: z.number().optional(), designFlow: z.number().optional(), manningsN: z.number().positive().optional() },
      supportedActions: ["calculate_hgl"],
      resolveAction: (rawArgs) => ({ action: "calculate_hgl", args: { action: "calculate_hgl", networkName: rawArgs.networkName, tailwaterElevation: rawArgs.tailwaterElevation, designFlow: rawArgs.designFlow, manningsN: rawArgs.manningsN } }),
    },
    {
      toolName: "civil3d_pipe_hydraulic_analysis",
      displayName: "Civil 3D Pipe Hydraulic Analysis",
      description: "Runs hydraulic capacity analysis on a gravity pipe network using Manning-based checks.",
      inputShape: { networkName: z.string(), designFlow: z.number().optional(), manningsN: z.number().positive().optional(), minCoverDepth: z.number().nonnegative().optional(), minVelocity: z.number().nonnegative().optional(), maxVelocity: z.number().positive().optional(), minSlope: z.number().nonnegative().optional() },
      supportedActions: ["hydraulic_analysis"],
      resolveAction: (rawArgs) => ({ action: "hydraulic_analysis", args: { action: "hydraulic_analysis", networkName: rawArgs.networkName, designFlow: rawArgs.designFlow, manningsN: rawArgs.manningsN, minCoverDepth: rawArgs.minCoverDepth, minVelocity: rawArgs.minVelocity, maxVelocity: rawArgs.maxVelocity, minSlope: rawArgs.minSlope } }),
    },
    {
      toolName: "civil3d_pipe_structure_properties",
      displayName: "Civil 3D Pipe Structure Properties",
      description: "Retrieves detailed properties for a structure in a gravity pipe network.",
      inputShape: { networkName: z.string(), structureName: z.string() },
      supportedActions: ["get_structure_properties"],
      resolveAction: (rawArgs) => ({ action: "get_structure_properties", args: { action: "get_structure_properties", networkName: rawArgs.networkName, structureName: rawArgs.structureName } }),
    },
    {
      toolName: "civil3d_pipe_network_size",
      displayName: "Civil 3D Pipe Network Size",
      description: "Sizes gravity-network pipes from Manning full-flow capacity, chooses matching catalog parts, and optionally applies the selected sizes back to the drawing.",
      inputShape: { networkName: z.string(), partsList: z.string().optional(), defaultDesignFlow: z.number().positive().optional(), perPipeDesignFlows: z.array(PipeFlowSchema).optional(), manningsN: z.number().positive().optional().default(0.013), targetVelocityMin: z.number().positive().optional().default(2), targetVelocityMax: z.number().positive().optional().default(10), applyChanges: z.boolean().optional().default(false) },
      supportedActions: ["size_network"],
      resolveAction: (rawArgs) => ({ action: "size_network", args: { action: "size_network", networkName: rawArgs.networkName, partsList: rawArgs.partsList, defaultDesignFlow: rawArgs.defaultDesignFlow, perPipeDesignFlows: rawArgs.perPipeDesignFlows, manningsN: rawArgs.manningsN, targetVelocityMin: rawArgs.targetVelocityMin, targetVelocityMax: rawArgs.targetVelocityMax, applyChanges: rawArgs.applyChanges } }),
    },
    {
      toolName: "civil3d_pipe_profile_view_automation",
      displayName: "Civil 3D Pipe Profile View Automation",
      description: "Automates a gravity-pipe profile-view setup by resolving the network alignment/surface, creating an EG profile if needed, and creating the profile view with optional style and band set.",
      inputShape: { networkName: z.string(), profileViewName: z.string(), insertX: z.number(), insertY: z.number(), alignmentName: z.string().optional(), surfaceName: z.string().optional(), existingProfileName: z.string().optional(), surfaceProfileName: z.string().optional(), createSurfaceProfileIfMissing: z.boolean().optional().default(true), style: z.string().optional(), bandSet: z.string().optional() },
      supportedActions: ["automate_profile_view"],
      resolveAction: (rawArgs) => ({ action: "automate_profile_view", args: { action: "automate_profile_view", networkName: rawArgs.networkName, profileViewName: rawArgs.profileViewName, insertX: rawArgs.insertX, insertY: rawArgs.insertY, alignmentName: rawArgs.alignmentName, surfaceName: rawArgs.surfaceName, existingProfileName: rawArgs.existingProfileName, surfaceProfileName: rawArgs.surfaceProfileName, createSurfaceProfileIfMissing: rawArgs.createSurfaceProfileIfMissing, style: rawArgs.style, bandSet: rawArgs.bandSet } }),
    },
  ],
};
