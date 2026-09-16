import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

const DrawingInfoResponseSchema = z.object({ fileName: z.string().optional(), filePath: z.string().optional(), coordinateSystem: z.string().nullable().optional(), linearUnits: z.enum(["feet", "meters", "other"]).optional(), angularUnits: z.enum(["degrees", "radians", "grads"]).optional(), unsavedChanges: z.boolean().optional(), objectCounts: z.object({ surfaces: z.number().optional(), alignments: z.number().optional(), profiles: z.number().optional(), corridors: z.number().optional(), pipeNetworks: z.number().optional(), points: z.number().optional(), parcels: z.number().optional() }).optional(), drawingName: z.string().optional(), projectName: z.string().nullable().optional(), units: z.string().optional() });
const DrawingSettingsResponseSchema = z.object({ coordinateSystem: z.string().nullable().optional(), coordinateZone: z.string().nullable().optional(), datum: z.string().nullable().optional(), dimensionScale: z.number().optional(), gridScaleFactor: z.number().nullable().optional(), useGridScaleFactor: z.boolean().nullable().optional(), diagnostics: z.string().nullable().optional(), elevationReference: z.string().nullable().optional(), defaultLayer: z.string().nullable().optional(), defaultStyles: z.object({ surface: z.string().nullable().optional(), alignment: z.string().nullable().optional(), profile: z.string().nullable().optional(), corridor: z.string().nullable().optional(), pipeNetwork: z.string().nullable().optional() }).optional() });
const SelectedCivilObjectsResponseSchema = z.array(z.object({ handle: z.string(), objectType: z.string(), name: z.string().optional(), description: z.string().optional() }));
const CivilObjectTypesResponseSchema = z.array(z.string());
const GenericResponseSchema = z.object({}).passthrough();

const DrawingInfoArgs = z.object({ action: z.literal("info") });
const DrawingNewArgs = z.object({ action: z.literal("new"), templatePath: z.string().optional() });
const DrawingSaveArgs = z.object({ action: z.literal("save"), saveAs: z.string().optional(), overwrite: z.boolean().optional() });
const DrawingUndoArgs = z.object({ action: z.literal("undo"), steps: z.number().int().min(1).max(10).optional() });
const DrawingRedoArgs = z.object({ action: z.literal("redo"), steps: z.number().int().min(1).max(10).optional() });
const DrawingSettingsArgs = z.object({ action: z.literal("settings") });
const SelectedObjectsArgs = z.object({ action: z.literal("selected_objects_info"), limit: z.number().optional() });
const ObjectTypesArgs = z.object({ action: z.literal("list_object_types") });
const WindowSchema = z.object({ x1: z.number(), y1: z.number(), x2: z.number(), y2: z.number() });
const EntitiesArgs = z.object({
  action: z.literal("entities"),
  layer: z.string().optional().describe("Layer name or wildcard pattern (comma-separated list allowed), e.g. 'GO-*,TEXT'."),
  types: z.array(z.string()).optional().describe("Entity type names (DBText, MText, Line, Polyline, Polyline3d, Arc, Circle, BlockReference, Hatch, ...) or the aliases 'text', 'curve', 'block', 'dimension'."),
  textContains: z.string().optional().describe("Only entities whose text / block name / attributes contain this (case-insensitive)."),
  window: WindowSchema.optional().describe("Only entities whose extents intersect this XY window."),
  layout: z.string().optional().describe("Space to scan: omit or 'model' for model space, 'current', or a layout name."),
  handles: z.array(z.string()).optional().describe("Only these handles (with includeNested, also what sits inside these block references)."),
  includeNested: z.boolean().optional().describe("Descend into block references (3 levels) and report their contents in world coordinates."),
  summary: z.boolean().optional().describe("Return counts by type/layer and the overall extents only."),
  detail: z.enum(["brief", "full"]).optional().describe("brief = handle/type/layer/text/bounds; full (default) adds geometry."),
  limit: z.number().int().positive().optional(),
  offset: z.number().int().nonnegative().optional(),
});
const SelectArgs = z.object({
  action: z.literal("select"),
  handle: z.string().optional(),
  handles: z.array(z.string()).optional(),
  zoom: z.boolean().optional().describe("Zoom the current view to the objects (default true)."),
  select: z.boolean().optional().describe("Set them as the current selection with grips (default true)."),
  zoomMargin: z.number().nonnegative().optional().describe("Margin around the extents as a fraction (default 0.25)."),
}).refine((v) => v.handle !== undefined || (v.handles !== undefined && v.handles.length > 0), { message: "handle or handles[] is required." });

export const DRAWING_RUNTIME_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "drawing",
  actions: {
    info: { action: "info", inputSchema: DrawingInfoArgs, responseSchema: DrawingInfoResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: false, safeForRetry: true, pluginMethods: ["getDrawingInfo"], execute: async () => await withApplicationConnection(async (appClient) => await appClient.sendCommand("getDrawingInfo", {})) },
    new: { action: "new", inputSchema: DrawingNewArgs, responseSchema: GenericResponseSchema, capabilities: ["create", "manage"], requiresActiveDrawing: false, safeForRetry: false, pluginMethods: ["newDrawing"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("newDrawing", { templatePath: args.templatePath })) },
    save: { action: "save", inputSchema: DrawingSaveArgs, responseSchema: GenericResponseSchema, capabilities: ["edit", "manage"], requiresActiveDrawing: false, safeForRetry: false, pluginMethods: ["saveDrawing"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("saveDrawing", { saveAs: args.saveAs, overwrite: args.overwrite ?? false })) },
    undo: { action: "undo", inputSchema: DrawingUndoArgs, responseSchema: GenericResponseSchema, capabilities: ["edit", "manage"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["undoDrawing"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("undoDrawing", { steps: args.steps ?? 1 })) },
    redo: { action: "redo", inputSchema: DrawingRedoArgs, responseSchema: GenericResponseSchema, capabilities: ["edit", "manage"], requiresActiveDrawing: true, safeForRetry: false, pluginMethods: ["redoDrawing"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("redoDrawing", { steps: args.steps ?? 1 })) },
    settings: { action: "settings", inputSchema: DrawingSettingsArgs, responseSchema: DrawingSettingsResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: false, safeForRetry: true, pluginMethods: ["getDrawingSettings"], execute: async () => await withApplicationConnection(async (appClient) => await appClient.sendCommand("getDrawingSettings", {})) },
    selected_objects_info: { action: "selected_objects_info", inputSchema: SelectedObjectsArgs, responseSchema: SelectedCivilObjectsResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["getSelectedCivilObjectsInfo"], execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("getSelectedCivilObjectsInfo", { limit: args.limit || 100 })) },
    entities: { action: "entities", inputSchema: EntitiesArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["listEntities"], execute: async (args) => await withApplicationConnection(async (appClient) => { const { action: _action, ...rest } = args; return await appClient.sendCommand("listEntities", rest); }) },
    select: { action: "select", inputSchema: SelectArgs, responseSchema: GenericResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: true, safeForRetry: true, pluginMethods: ["selectEntities"], execute: async (args) => await withApplicationConnection(async (appClient) => { const { action: _action, ...rest } = args; return await appClient.sendCommand("selectEntities", rest); }) },
    list_object_types: { action: "list_object_types", inputSchema: ObjectTypesArgs, responseSchema: CivilObjectTypesResponseSchema, capabilities: ["query", "inspect"], requiresActiveDrawing: false, safeForRetry: true, pluginMethods: ["listCivilObjectTypes"], execute: async () => await withApplicationConnection(async (appClient) => await appClient.sendCommand("listCivilObjectTypes", {})) },
  },
  exposures: [
    { toolName: "civil3d_drawing", displayName: "Civil 3D Drawing", description: "Reads drawing state, settings, selection context, plain AutoCAD entities (text, lines, polylines, dimensions, blocks — with layer/type/window/text filters), selects and zooms to objects by handle, and runs document operations through a single domain tool.", inputShape: { action: z.enum(["info", "new", "save", "undo", "redo", "settings", "selected_objects_info", "list_object_types", "entities", "select"]), templatePath: z.string().optional(), saveAs: z.string().optional(), overwrite: z.boolean().optional(), steps: z.number().int().min(1).max(10).optional(), limit: z.number().optional(), offset: z.number().int().nonnegative().optional(), layer: z.string().optional().describe("entities: layer name / wildcard list."), types: z.array(z.string()).optional().describe("entities: type names or aliases text/curve/block/dimension."), textContains: z.string().optional(), window: WindowSchema.optional(), layout: z.string().optional().describe("entities: 'model' (default), 'current' or a layout name."), handles: z.array(z.string()).optional(), handle: z.string().optional(), includeNested: z.boolean().optional(), summary: z.boolean().optional(), detail: z.enum(["brief", "full"]).optional(), zoom: z.boolean().optional(), select: z.boolean().optional(), zoomMargin: z.number().nonnegative().optional() }, supportedActions: ["info", "new", "save", "undo", "redo", "settings", "selected_objects_info", "list_object_types", "entities", "select"], resolveAction: (rawArgs) => ({ action: String(rawArgs.action ?? ""), args: rawArgs }) },
    { toolName: "get_drawing_info", displayName: "Get Drawing Info", description: "Retrieves basic information about the active Civil 3D drawing.", inputShape: {}, supportedActions: ["info"], resolveAction: () => ({ action: "info", args: { action: "info" } }) },
    { toolName: "get_selected_civil_objects_info", displayName: "Get Selected Civil Objects Info", description: "Gets basic properties of currently selected Civil 3D objects.", inputShape: { limit: z.number().optional() }, supportedActions: ["selected_objects_info"], resolveAction: (rawArgs) => ({ action: "selected_objects_info", args: { action: "selected_objects_info", ...rawArgs } }) },
    { toolName: "list_civil_object_types", displayName: "List Civil Object Types", description: "Lists major Civil 3D object types available in the current context.", inputShape: {}, supportedActions: ["list_object_types"], resolveAction: () => ({ action: "list_object_types", args: { action: "list_object_types" } }) },
  ],
};
