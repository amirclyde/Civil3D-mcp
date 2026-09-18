import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

/**
 * civil3d_pressure — pressure (water) networks, split out of civil3d_pipe (17 Sep 2026).
 * The plugin handlers are still the upstream ones (PressureNetworkCommands.cs); they are rewritten on the
 * typed AeccPressurePipesMgd API in phase P5 (pipe runs from polylines, ports, swap size, validation).
 */

const OptionalPoint3DSchema = z.object({
  x: z.number(),
  y: z.number(),
  z: z.number().optional().default(0),
});

const GenericPressureResponseSchema = z.object({}).passthrough();

const PressureListPressureNetworksArgsSchema = z.object({ action: z.literal("list") });
const PressureGetPressureNetworkArgsSchema = z.object({ action: z.literal("get"), name: z.string() });
const PressureCreatePressureNetworkArgsSchema = z.object({
  action: z.literal("create"),
  name: z.string(),
  partsList: z.string(),
  layer: z.string().optional(),
  referenceAlignment: z.string().optional(),
  referenceSurface: z.string().optional(),
});
const PressureDeletePressureNetworkArgsSchema = z.object({ action: z.literal("delete"), name: z.string() });
const PressureAssignPressurePartsListArgsSchema = z.object({
  action: z.literal("assign_parts_list"),
  networkName: z.string(),
  partsList: z.string(),
});
const PressureSetPressureCoverArgsSchema = z.object({
  action: z.literal("set_cover"),
  networkName: z.string(),
  minCoverDepth: z.number(),
  maxCoverDepth: z.number().optional(),
});
const PressureValidatePressureNetworkArgsSchema = z.object({ action: z.literal("validate"), networkName: z.string() });
const PressureExportPressureNetworkArgsSchema = z.object({
  action: z.literal("export"),
  networkName: z.string(),
  includeCoordinates: z.boolean().optional().default(true),
});
const PressureConnectPressureNetworksArgsSchema = z.object({
  action: z.literal("connect_networks"),
  targetNetwork: z.string(),
  sourceNetwork: z.string(),
});
const PressureAddPressurePipeArgsSchema = z.object({
  action: z.literal("add_pipe"),
  networkName: z.string(),
  partName: z.string(),
  startPoint: OptionalPoint3DSchema,
  endPoint: OptionalPoint3DSchema,
  diameter: z.number().optional(),
});
const PressureGetPressurePipePropertiesArgsSchema = z.object({
  action: z.literal("get_pipe"),
  networkName: z.string(),
  pipeName: z.string(),
});
const PressureResizePressurePipeArgsSchema = z.object({
  action: z.literal("resize_pipe"),
  networkName: z.string(),
  pipeName: z.string(),
  newPartName: z.string(),
  newDiameter: z.number().optional(),
});
const PressureAddPressureFittingArgsSchema = z.object({
  action: z.literal("add_fitting"),
  networkName: z.string(),
  partName: z.string(),
  position: OptionalPoint3DSchema,
  rotation: z.number().optional(),
});
const PressureGetPressureFittingPropertiesArgsSchema = z.object({
  action: z.literal("get_fitting"),
  networkName: z.string(),
  fittingName: z.string(),
});
const PressureAddPressureAppurtenanceArgsSchema = z.object({
  action: z.literal("add_appurtenance"),
  networkName: z.string(),
  partName: z.string(),
  position: OptionalPoint3DSchema,
  rotation: z.number().optional(),
  onPipeName: z.string().optional(),
});

export const PRESSURE_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "pressure",
  actions: {
    list: {
      action: "list",
      inputSchema: PressureListPressureNetworksArgsSchema,
      responseSchema: GenericPressureResponseSchema,
      capabilities: ["query"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listPressureNetworks"],
      execute: async () => await withApplicationConnection(async (appClient) => await appClient.sendCommand("listPressureNetworks", {})),
    },
    get: {
      action: "get",
      inputSchema: PressureGetPressureNetworkArgsSchema,
      responseSchema: GenericPressureResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getPressureNetworkInfo"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("getPressureNetworkInfo", { name: args.name })),
    },
    create: {
      action: "create",
      inputSchema: PressureCreatePressureNetworkArgsSchema,
      responseSchema: GenericPressureResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createPressureNetwork"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("createPressureNetwork", {
        name: args.name,
        partsList: args.partsList,
        layer: args.layer,
        referenceAlignment: args.referenceAlignment,
        referenceSurface: args.referenceSurface,
      })),
    },
    delete: {
      action: "delete",
      inputSchema: PressureDeletePressureNetworkArgsSchema,
      responseSchema: GenericPressureResponseSchema,
      capabilities: ["delete"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["deletePressureNetwork"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("deletePressureNetwork", { name: args.name })),
    },
    assign_parts_list: {
      action: "assign_parts_list",
      inputSchema: PressureAssignPressurePartsListArgsSchema,
      responseSchema: GenericPressureResponseSchema,
      capabilities: ["edit", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["assignPressurePartsList"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("assignPressurePartsList", {
        networkName: args.networkName,
        partsList: args.partsList,
      })),
    },
    set_cover: {
      action: "set_cover",
      inputSchema: PressureSetPressureCoverArgsSchema,
      responseSchema: GenericPressureResponseSchema,
      capabilities: ["edit", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["setPressureNetworkCover"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("setPressureNetworkCover", {
        networkName: args.networkName,
        minCoverDepth: args.minCoverDepth,
        maxCoverDepth: args.maxCoverDepth,
      })),
    },
    validate: {
      action: "validate",
      inputSchema: PressureValidatePressureNetworkArgsSchema,
      responseSchema: GenericPressureResponseSchema,
      capabilities: ["query", "analyze", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["validatePressureNetwork"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("validatePressureNetwork", { networkName: args.networkName })),
    },
    export: {
      action: "export",
      inputSchema: PressureExportPressureNetworkArgsSchema,
      responseSchema: GenericPressureResponseSchema,
      capabilities: ["query", "export"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["exportPressureNetwork"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("exportPressureNetwork", {
        networkName: args.networkName,
        includeCoordinates: args.includeCoordinates,
      })),
    },
    connect_networks: {
      action: "connect_networks",
      inputSchema: PressureConnectPressureNetworksArgsSchema,
      responseSchema: GenericPressureResponseSchema,
      capabilities: ["edit", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["connectPressureNetworks"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("connectPressureNetworks", {
        targetNetwork: args.targetNetwork,
        sourceNetwork: args.sourceNetwork,
      })),
    },
    add_pipe: {
      action: "add_pipe",
      inputSchema: PressureAddPressurePipeArgsSchema,
      responseSchema: GenericPressureResponseSchema,
      capabilities: ["create", "edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["addPressurePipe"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("addPressurePipe", {
        networkName: args.networkName,
        partName: args.partName,
        startPoint: args.startPoint,
        endPoint: args.endPoint,
        diameter: args.diameter,
      })),
    },
    get_pipe: {
      action: "get_pipe",
      inputSchema: PressureGetPressurePipePropertiesArgsSchema,
      responseSchema: GenericPressureResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getPressurePipeProperties"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("getPressurePipeProperties", {
        networkName: args.networkName,
        pipeName: args.pipeName,
      })),
    },
    resize_pipe: {
      action: "resize_pipe",
      inputSchema: PressureResizePressurePipeArgsSchema,
      responseSchema: GenericPressureResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["resizePressurePipe"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("resizePressurePipe", {
        networkName: args.networkName,
        pipeName: args.pipeName,
        newPartName: args.newPartName,
        newDiameter: args.newDiameter,
      })),
    },
    add_fitting: {
      action: "add_fitting",
      inputSchema: PressureAddPressureFittingArgsSchema,
      responseSchema: GenericPressureResponseSchema,
      capabilities: ["create", "edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["addPressureFitting"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("addPressureFitting", {
        networkName: args.networkName,
        partName: args.partName,
        position: args.position,
        rotation: args.rotation,
      })),
    },
    get_fitting: {
      action: "get_fitting",
      inputSchema: PressureGetPressureFittingPropertiesArgsSchema,
      responseSchema: GenericPressureResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getPressureFittingProperties"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("getPressureFittingProperties", {
        networkName: args.networkName,
        fittingName: args.fittingName,
      })),
    },
    add_appurtenance: {
      action: "add_appurtenance",
      inputSchema: PressureAddPressureAppurtenanceArgsSchema,
      responseSchema: GenericPressureResponseSchema,
      capabilities: ["create", "edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["addPressureAppurtenance"],
      execute: async (args) => await withApplicationConnection(async (appClient) => await appClient.sendCommand("addPressureAppurtenance", {
        networkName: args.networkName,
        partName: args.partName,
        position: args.position,
        rotation: args.rotation,
        onPipeName: args.onPipeName,
      })),
    },
  },
  exposures: [
    {
      toolName: "civil3d_pressure",
      displayName: "Civil 3D Pressure Network",
      description: "Pressure (water) pipe networks: list/get/create/delete networks, assign parts lists, add pressure pipes, fittings and appurtenances, read part properties and export. Gravity (storm/sewer) networks are in civil3d_pipe. Note: resize_pipe, set_cover, validate and connect_networks currently refuse; they are rebuilt on the typed pressure API in a later phase.",
      inputShape: {
        action: z.enum(["list", "get", "create", "delete", "assign_parts_list", "set_cover", "validate", "export", "connect_networks", "add_pipe", "get_pipe", "resize_pipe", "add_fitting", "get_fitting", "add_appurtenance"]),
        name: z.string().optional(),
        networkName: z.string().optional(),
        pipeName: z.string().optional(),
        fittingName: z.string().optional(),
        partsList: z.string().optional(),
        partName: z.string().optional(),
        layer: z.string().optional(),
        referenceAlignment: z.string().optional(),
        referenceSurface: z.string().optional(),
        minCoverDepth: z.number().optional(),
        maxCoverDepth: z.number().optional(),
        includeCoordinates: z.boolean().optional(),
        targetNetwork: z.string().optional(),
        sourceNetwork: z.string().optional(),
        startPoint: OptionalPoint3DSchema.optional(),
        endPoint: OptionalPoint3DSchema.optional(),
        diameter: z.number().optional(),
        newPartName: z.string().optional(),
        newDiameter: z.number().optional(),
        position: OptionalPoint3DSchema.optional(),
        rotation: z.number().optional(),
        onPipeName: z.string().optional(),
      },
      supportedActions: ["list", "get", "create", "delete", "assign_parts_list", "set_cover", "validate", "export", "connect_networks", "add_pipe", "get_pipe", "resize_pipe", "add_fitting", "get_fitting", "add_appurtenance"],
      resolveAction: (rawArgs) => ({ action: String(rawArgs.action ?? ""), args: rawArgs }),
    },
    {
      toolName: "civil3d_pressure_network_list",
      displayName: "Civil 3D Pressure Network List",
      description: "Lists all pressure networks in the active Civil 3D drawing with summary counts for pipes, fittings, and appurtenances.",
      inputShape: {},
      supportedActions: ["list"],
      resolveAction: () => ({ action: "list", args: { action: "list" } }),
    },
    {
      toolName: "civil3d_pressure_network_get_info",
      displayName: "Civil 3D Pressure Network Get Info",
      description: "Gets detailed information about a pressure network including its pipes, fittings, and appurtenances.",
      inputShape: { name: z.string() },
      supportedActions: ["get"],
      resolveAction: (rawArgs) => ({ action: "get", args: { action: "get", name: rawArgs.name } }),
    },
    {
      toolName: "civil3d_pressure_network_create",
      displayName: "Civil 3D Pressure Network Create",
      description: "Creates a new pressure network in the active Civil 3D drawing.",
      inputShape: { name: z.string(), partsList: z.string(), layer: z.string().optional(), referenceAlignment: z.string().optional(), referenceSurface: z.string().optional() },
      supportedActions: ["create"],
      resolveAction: (rawArgs) => ({ action: "create", args: { action: "create", name: rawArgs.name, partsList: rawArgs.partsList, layer: rawArgs.layer, referenceAlignment: rawArgs.referenceAlignment, referenceSurface: rawArgs.referenceSurface } }),
    },
    {
      toolName: "civil3d_pressure_network_delete",
      displayName: "Civil 3D Pressure Network Delete",
      description: "Deletes a pressure network and all its components from the drawing.",
      inputShape: { name: z.string() },
      supportedActions: ["delete"],
      resolveAction: (rawArgs) => ({ action: "delete", args: { action: "delete", name: rawArgs.name } }),
    },
    {
      toolName: "civil3d_pressure_network_assign_parts_list",
      displayName: "Civil 3D Pressure Network Assign Parts List",
      description: "Assigns a pressure parts list to an existing pressure network.",
      inputShape: { networkName: z.string(), partsList: z.string() },
      supportedActions: ["assign_parts_list"],
      resolveAction: (rawArgs) => ({ action: "assign_parts_list", args: { action: "assign_parts_list", networkName: rawArgs.networkName, partsList: rawArgs.partsList } }),
    },
    {
      toolName: "civil3d_pressure_network_set_cover",
      displayName: "Civil 3D Pressure Network Set Cover",
      description: "Sets minimum and optional maximum cover requirements for a pressure network.",
      inputShape: { networkName: z.string(), minCoverDepth: z.number(), maxCoverDepth: z.number().optional() },
      supportedActions: ["set_cover"],
      resolveAction: (rawArgs) => ({ action: "set_cover", args: { action: "set_cover", networkName: rawArgs.networkName, minCoverDepth: rawArgs.minCoverDepth, maxCoverDepth: rawArgs.maxCoverDepth } }),
    },
    {
      toolName: "civil3d_pressure_network_validate",
      displayName: "Civil 3D Pressure Network Validate",
      description: "Validates a pressure network for cover violations, disconnected components, and parts mismatches.",
      inputShape: { networkName: z.string() },
      supportedActions: ["validate"],
      resolveAction: (rawArgs) => ({ action: "validate", args: { action: "validate", networkName: rawArgs.networkName } }),
    },
    {
      toolName: "civil3d_pressure_network_export",
      displayName: "Civil 3D Pressure Network Export",
      description: "Exports a pressure network as structured data including pipes, fittings, and appurtenances.",
      inputShape: { networkName: z.string(), includeCoordinates: z.boolean().optional().default(true) },
      supportedActions: ["export"],
      resolveAction: (rawArgs) => ({ action: "export", args: { action: "export", networkName: rawArgs.networkName, includeCoordinates: rawArgs.includeCoordinates } }),
    },
    {
      toolName: "civil3d_pressure_network_connect",
      displayName: "Civil 3D Pressure Network Connect",
      description: "Connects two pressure networks by merging the source network into the target network.",
      inputShape: { targetNetwork: z.string(), sourceNetwork: z.string() },
      supportedActions: ["connect_networks"],
      resolveAction: (rawArgs) => ({ action: "connect_networks", args: { action: "connect_networks", targetNetwork: rawArgs.targetNetwork, sourceNetwork: rawArgs.sourceNetwork } }),
    },
    {
      toolName: "civil3d_pressure_pipe_add",
      displayName: "Civil 3D Pressure Pipe Add",
      description: "Adds a pressure pipe segment to an existing pressure network.",
      inputShape: { networkName: z.string(), partName: z.string(), startPoint: OptionalPoint3DSchema, endPoint: OptionalPoint3DSchema, diameter: z.number().optional() },
      supportedActions: ["add_pipe"],
      resolveAction: (rawArgs) => ({ action: "add_pipe", args: { action: "add_pipe", networkName: rawArgs.networkName, partName: rawArgs.partName, startPoint: rawArgs.startPoint, endPoint: rawArgs.endPoint, diameter: rawArgs.diameter } }),
    },
    {
      toolName: "civil3d_pressure_pipe_get_properties",
      displayName: "Civil 3D Pressure Pipe Get Properties",
      description: "Gets detailed properties of a specific pressure pipe including diameter, length, material, and cover depth.",
      inputShape: { networkName: z.string(), pipeName: z.string() },
      supportedActions: ["get_pipe"],
      resolveAction: (rawArgs) => ({ action: "get_pipe", args: { action: "get_pipe", networkName: rawArgs.networkName, pipeName: rawArgs.pipeName } }),
    },
    {
      toolName: "civil3d_pressure_pipe_resize",
      displayName: "Civil 3D Pressure Pipe Resize",
      description: "Changes the part and optional diameter of an existing pressure pipe.",
      inputShape: { networkName: z.string(), pipeName: z.string(), newPartName: z.string(), newDiameter: z.number().optional() },
      supportedActions: ["resize_pipe"],
      resolveAction: (rawArgs) => ({ action: "resize_pipe", args: { action: "resize_pipe", networkName: rawArgs.networkName, pipeName: rawArgs.pipeName, newPartName: rawArgs.newPartName, newDiameter: rawArgs.newDiameter } }),
    },
    {
      toolName: "civil3d_pressure_fitting_add",
      displayName: "Civil 3D Pressure Fitting Add",
      description: "Adds a pressure fitting such as an elbow, tee, reducer, or cap to a pressure network.",
      inputShape: { networkName: z.string(), partName: z.string(), position: OptionalPoint3DSchema, rotation: z.number().optional() },
      supportedActions: ["add_fitting"],
      resolveAction: (rawArgs) => ({ action: "add_fitting", args: { action: "add_fitting", networkName: rawArgs.networkName, partName: rawArgs.partName, position: rawArgs.position, rotation: rawArgs.rotation } }),
    },
    {
      toolName: "civil3d_pressure_fitting_get_properties",
      displayName: "Civil 3D Pressure Fitting Get Properties",
      description: "Gets detailed properties of a pressure fitting including type, location, and part size.",
      inputShape: { networkName: z.string(), fittingName: z.string() },
      supportedActions: ["get_fitting"],
      resolveAction: (rawArgs) => ({ action: "get_fitting", args: { action: "get_fitting", networkName: rawArgs.networkName, fittingName: rawArgs.fittingName } }),
    },
    {
      toolName: "civil3d_pressure_appurtenance_add",
      displayName: "Civil 3D Pressure Appurtenance Add",
      description: "Adds a pressure appurtenance such as a valve, hydrant, or meter to a pressure network.",
      inputShape: { networkName: z.string(), partName: z.string(), position: OptionalPoint3DSchema, rotation: z.number().optional(), onPipeName: z.string().optional() },
      supportedActions: ["add_appurtenance"],
      resolveAction: (rawArgs) => ({ action: "add_appurtenance", args: { action: "add_appurtenance", networkName: rawArgs.networkName, partName: rawArgs.partName, position: rawArgs.position, rotation: rawArgs.rotation, onPipeName: rawArgs.onPipeName } }),
    },
  ],
};
