import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

const SheetSetSummarySchema = z.object({
  name: z.string(),
  handle: z.string(),
  alignmentName: z.string().nullable().optional(),
  viewFrameCount: z.number().optional(),
  matchLineCount: z.number().optional(),
  sheetSets: z.array(z.string()).optional(),
  sheetCount: z.number(),
}).passthrough();

const SheetDetailSchema = z.object({}).passthrough();

const GenericPlanProductionResponseSchema = z.object({}).passthrough();

const canonicalPlanProductionInputShape = {
  action: z.enum([
    "sheet_set_list",
    "sheet_set_get_info",
    "sheet_set_create",
    "sheet_add",
    "sheet_get_properties",
    "sheet_set_title_block",
    "plan_profile_sheet_update_alignment",
    "layout_list",
    "layout_create",
    "sheet_view_create",
    "sheet_view_set_scale",
    "sheet_publish_pdf",
    "sheet_set_export",
  ]).describe("sheet_set_* read Civil 3D view frame groups / view frames (read-only in the API; created by the CreateViewFrames / CreateSheets wizards). layout_* and sheet_view_* build paper-space layouts and viewports directly."),
  name: z.string().optional(),
  description: z.string().optional(),
  sheetSetName: z.string().optional(),
  sheetName: z.string().optional(),
  sheetNumber: z.string().optional(),
  layoutName: z.string().optional(),
  titleBlockPath: z.string().optional(),
  alignmentName: z.string().optional(),
  profileName: z.string().optional(),
  sheetTemplatePath: z.string().optional(),
  startStation: z.number().optional(),
  endStation: z.number().optional(),
  viewScale: z.number().optional(),
  viewName: z.string().optional(),
  centerX: z.number().optional(),
  centerY: z.number().optional(),
  width: z.number().optional(),
  height: z.number().optional(),
  scale: z.number().optional(),
  viewportHandle: z.string().optional(),
  templatePath: z.string().optional().describe("layout_create: .dwt/.dwg whose layout (templateLayoutName) is imported with its title block."),
  templateLayoutName: z.string().optional(),
  makeCurrent: z.boolean().optional(),
  includeViewports: z.boolean().optional(),
  viewCenterX: z.number().optional().describe("sheet_view_create / set_scale: model-space X the viewport looks at."),
  viewCenterY: z.number().optional(),
  twistDegrees: z.number().optional(),
  locked: z.boolean().optional(),
  layoutNames: z.array(z.string()).optional(),
  outputPath: z.string().optional(),
  overwrite: z.boolean().optional(),
  plotStyleTable: z.string().optional(),
  paperSize: z.string().optional(),
};

const SheetSetListArgsSchema = z.object({
  action: z.literal("sheet_set_list"),
});

const SheetSetGetInfoArgsSchema = z.object({
  action: z.literal("sheet_set_get_info"),
  name: z.string(),
});

const SheetSetCreateArgsSchema = z.object({
  action: z.literal("sheet_set_create"),
  name: z.string(),
  description: z.string().optional(),
});

const SheetAddArgsSchema = z.object({
  action: z.literal("sheet_add"),
  sheetSetName: z.string(),
  sheetName: z.string(),
  sheetNumber: z.string().optional(),
  layoutName: z.string().optional(),
});

const SheetGetPropertiesArgsSchema = z.object({
  action: z.literal("sheet_get_properties"),
  sheetSetName: z.string(),
  sheetName: z.string(),
});

const SheetSetTitleBlockArgsSchema = z.object({
  action: z.literal("sheet_set_title_block"),
  sheetSetName: z.string(),
  sheetName: z.string(),
  titleBlockPath: z.string(),
});

const PlanProfileSheetCreateArgsSchema = z.object({
  action: z.literal("plan_profile_sheet_create"),
  sheetSetName: z.string(),
  alignmentName: z.string(),
  profileName: z.string().optional(),
  sheetTemplatePath: z.string().optional(),
  startStation: z.number().optional(),
  endStation: z.number().optional(),
  viewScale: z.number().optional(),
});

const PlanProfileSheetUpdateAlignmentArgsSchema = z.object({
  action: z.literal("plan_profile_sheet_update_alignment"),
  sheetSetName: z.string(),
  sheetName: z.string(),
  alignmentName: z.string(),
  profileName: z.string().optional(),
});

const LayoutListArgsSchema = z.object({
  action: z.literal("layout_list"),
  includeViewports: z.boolean().optional(),
});

const LayoutCreateArgsSchema = z.object({
  action: z.literal("layout_create"),
  layoutName: z.string(),
  templatePath: z.string().optional(),
  templateLayoutName: z.string().optional(),
  makeCurrent: z.boolean().optional(),
});

const SheetViewCreateArgsSchema = z.object({
  action: z.literal("sheet_view_create"),
  layoutName: z.string(),
  viewName: z.string().optional(),
  centerX: z.number().optional(),
  centerY: z.number().optional(),
  width: z.number().optional(),
  height: z.number().optional(),
  scale: z.number().positive().optional(),
  viewCenterX: z.number().optional(),
  viewCenterY: z.number().optional(),
  twistDegrees: z.number().optional(),
  locked: z.boolean().optional(),
});

const SheetViewSetScaleArgsSchema = z.object({
  action: z.literal("sheet_view_set_scale"),
  layoutName: z.string(),
  viewportHandle: z.string().optional(),
  scale: z.number().positive(),
  viewCenterX: z.number().optional(),
  viewCenterY: z.number().optional(),
});

const SheetPublishPdfArgsSchema = z.object({
  action: z.literal("sheet_publish_pdf"),
  layoutNames: z.array(z.string()).min(1),
  outputPath: z.string(),
  overwrite: z.boolean().optional(),
  plotStyleTable: z.string().optional(),
  paperSize: z.string().optional(),
});

const SheetSetExportArgsSchema = z.object({
  action: z.literal("sheet_set_export"),
  sheetSetName: z.string(),
  outputPath: z.string(),
  overwrite: z.boolean().optional(),
  plotStyleTable: z.string().optional(),
});

export const PLAN_PRODUCTION_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "plan_production",
  actions: {
    sheet_set_list: {
      action: "sheet_set_list",
      inputSchema: SheetSetListArgsSchema,
      responseSchema: z.object({ sheetSets: z.array(SheetSetSummarySchema), viewFrameGroups: z.array(SheetSetSummarySchema).optional(), note: z.string().optional() }),
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listSheetSets"],
      execute: async () => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listSheetSets", {}),
      ),
    },
    sheet_set_get_info: {
      action: "sheet_set_get_info",
      inputSchema: SheetSetGetInfoArgsSchema,
      responseSchema: SheetSetSummarySchema.extend({
        viewFrames: z.array(z.object({}).passthrough()).optional(),
        matchLines: z.array(z.object({}).passthrough()).optional(),
      }),
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getSheetSetInfo"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getSheetSetInfo", { name: args.name }),
      ),
    },
    sheet_set_create: {
      action: "sheet_set_create",
      inputSchema: SheetSetCreateArgsSchema,
      responseSchema: z.object({ name: z.string(), handle: z.string(), created: z.boolean() }),
      capabilities: ["create", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createSheetSet"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createSheetSet", {
          name: args.name,
          description: args.description,
        }),
      ),
    },
    sheet_add: {
      action: "sheet_add",
      inputSchema: SheetAddArgsSchema,
      responseSchema: z.object({ name: z.string(), number: z.string(), handle: z.string(), added: z.boolean() }),
      capabilities: ["create", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["addSheet"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("addSheet", {
          sheetSetName: args.sheetSetName,
          sheetName: args.sheetName,
          sheetNumber: args.sheetNumber,
          layoutName: args.layoutName,
        }),
      ),
    },
    sheet_get_properties: {
      action: "sheet_get_properties",
      inputSchema: SheetGetPropertiesArgsSchema,
      responseSchema: SheetDetailSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getSheetProperties"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getSheetProperties", {
          sheetSetName: args.sheetSetName,
          sheetName: args.sheetName,
        }),
      ),
    },
    sheet_set_title_block: {
      action: "sheet_set_title_block",
      inputSchema: SheetSetTitleBlockArgsSchema,
      responseSchema: z.object({ sheetName: z.string(), titleBlock: z.string(), updated: z.boolean() }),
      capabilities: ["edit", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["setSheetTitleBlock"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("setSheetTitleBlock", {
          sheetSetName: args.sheetSetName,
          sheetName: args.sheetName,
          titleBlockPath: args.titleBlockPath,
        }),
      ),
    },
    plan_profile_sheet_create: {
      action: "plan_profile_sheet_create",
      inputSchema: PlanProfileSheetCreateArgsSchema,
      responseSchema: z.object({
        sheetName: z.string(),
        handle: z.string(),
        alignmentName: z.string(),
        profileName: z.string().nullable(),
        created: z.boolean(),
      }),
      capabilities: ["create", "generate"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createPlanProfileSheet"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createPlanProfileSheet", {
          sheetSetName: args.sheetSetName,
          alignmentName: args.alignmentName,
          profileName: args.profileName,
          sheetTemplatePath: args.sheetTemplatePath,
          startStation: args.startStation,
          endStation: args.endStation,
          viewScale: args.viewScale,
        }),
      ),
    },
    plan_profile_sheet_update_alignment: {
      action: "plan_profile_sheet_update_alignment",
      inputSchema: PlanProfileSheetUpdateAlignmentArgsSchema,
      responseSchema: z.object({ sheetName: z.string(), alignmentName: z.string(), updated: z.boolean() }),
      capabilities: ["edit", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["updatePlanProfileSheetAlignment"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("updatePlanProfileSheetAlignment", {
          sheetSetName: args.sheetSetName,
          sheetName: args.sheetName,
          alignmentName: args.alignmentName,
          profileName: args.profileName,
        }),
      ),
    },
    layout_list: {
      action: "layout_list",
      inputSchema: LayoutListArgsSchema,
      responseSchema: GenericPlanProductionResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listLayouts"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listLayouts", {
          includeViewports: args.includeViewports ?? true,
        }),
      ),
    },
    layout_create: {
      action: "layout_create",
      inputSchema: LayoutCreateArgsSchema,
      responseSchema: GenericPlanProductionResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createLayout"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createLayout", {
          layoutName: args.layoutName,
          templatePath: args.templatePath ?? null,
          templateLayoutName: args.templateLayoutName ?? null,
          makeCurrent: args.makeCurrent ?? false,
        }),
      ),
    },
    sheet_view_create: {
      action: "sheet_view_create",
      inputSchema: SheetViewCreateArgsSchema,
      responseSchema: GenericPlanProductionResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createSheetView"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createSheetView", {
          layoutName: args.layoutName,
          viewName: args.viewName ?? null,
          centerX: args.centerX ?? null,
          centerY: args.centerY ?? null,
          width: args.width ?? null,
          height: args.height ?? null,
          scale: args.scale ?? null,
          viewCenterX: args.viewCenterX ?? null,
          viewCenterY: args.viewCenterY ?? null,
          twistDegrees: args.twistDegrees ?? null,
          locked: args.locked ?? true,
        }),
      ),
    },
    sheet_view_set_scale: {
      action: "sheet_view_set_scale",
      inputSchema: SheetViewSetScaleArgsSchema,
      responseSchema: GenericPlanProductionResponseSchema,
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["setSheetViewScale"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("setSheetViewScale", {
          layoutName: args.layoutName,
          viewportHandle: args.viewportHandle ?? null,
          scale: args.scale,
          viewCenterX: args.viewCenterX ?? null,
          viewCenterY: args.viewCenterY ?? null,
        }),
      ),
    },
    sheet_publish_pdf: {
      action: "sheet_publish_pdf",
      inputSchema: SheetPublishPdfArgsSchema,
      responseSchema: z.object({ outputPath: z.string(), sheetsPublished: z.number(), published: z.boolean() }),
      capabilities: ["export", "generate"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["publishSheetPdf"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("publishSheetPdf", {
          layoutNames: args.layoutNames,
          outputPath: args.outputPath,
          overwrite: args.overwrite ?? false,
          plotStyleTable: args.plotStyleTable,
          paperSize: args.paperSize,
        }),
      ),
    },
    sheet_set_export: {
      action: "sheet_set_export",
      inputSchema: SheetSetExportArgsSchema,
      responseSchema: z.object({
        sheetSetName: z.string(),
        outputPath: z.string(),
        sheetsExported: z.number(),
        exported: z.boolean(),
      }),
      capabilities: ["export", "generate"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["exportSheetSet"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("exportSheetSet", {
          sheetSetName: args.sheetSetName,
          outputPath: args.outputPath,
          overwrite: args.overwrite ?? false,
          plotStyleTable: args.plotStyleTable,
        }),
      ),
    },
  },
  exposures: [
    {
      toolName: "civil3d_plan_production",
      displayName: "Civil 3D Plan Production",
      description: "Plan production: reads Civil 3D view frame groups, view frames and match lines (sheet_set_*; read-only in the Civil 3D API), and builds paper-space layouts from sheet templates and viewports at a given scale (layout_list, layout_create, sheet_view_create, sheet_view_set_scale). Wizard-only operations (creating view frames or plan/profile/section sheets) are refused with guidance.",
      inputShape: canonicalPlanProductionInputShape,
      supportedActions: [
        "sheet_set_list",
        "sheet_set_get_info",
        "sheet_set_create",
        "sheet_add",
        "sheet_get_properties",
        "sheet_set_title_block",
        "plan_profile_sheet_update_alignment",
        "layout_list",
        "layout_create",
        "sheet_view_create",
        "sheet_view_set_scale",
        "sheet_publish_pdf",
        "sheet_set_export",
      ],
      resolveAction: (rawArgs) => ({ action: String(rawArgs.action ?? ""), args: rawArgs }),
    },
    {
      toolName: "civil3d_sheet_set_list",
      displayName: "Civil 3D Sheet Set List",
      description: "Lists all Plan Production sheet sets in the active drawing.",
      inputShape: {},
      supportedActions: ["sheet_set_list"],
      resolveAction: () => ({ action: "sheet_set_list", args: { action: "sheet_set_list" } }),
    },
    {
      toolName: "civil3d_sheet_set_get_info",
      displayName: "Civil 3D Sheet Set Get Info",
      description: "Gets detailed information about a Plan Production sheet set including all sheets.",
      inputShape: { name: z.string() },
      supportedActions: ["sheet_set_get_info"],
      resolveAction: (rawArgs) => ({ action: "sheet_set_get_info", args: { action: "sheet_set_get_info", name: rawArgs.name } }),
    },
    {
      toolName: "civil3d_sheet_set_create",
      displayName: "Civil 3D Sheet Set Create",
      description: "Creates a new Plan Production sheet set in the active drawing.",
      inputShape: { name: z.string(), description: z.string().optional() },
      supportedActions: ["sheet_set_create"],
      resolveAction: (rawArgs) => ({
        action: "sheet_set_create",
        args: { action: "sheet_set_create", name: rawArgs.name, description: rawArgs.description },
      }),
    },
    {
      toolName: "civil3d_sheet_add",
      displayName: "Civil 3D Sheet Add",
      description: "Adds a new sheet to an existing Plan Production sheet set.",
      inputShape: {
        sheetSetName: z.string(),
        sheetName: z.string(),
        sheetNumber: z.string().optional(),
        layoutName: z.string().optional(),
      },
      supportedActions: ["sheet_add"],
      resolveAction: (rawArgs) => ({
        action: "sheet_add",
        args: {
          action: "sheet_add",
          sheetSetName: rawArgs.sheetSetName,
          sheetName: rawArgs.sheetName,
          sheetNumber: rawArgs.sheetNumber,
          layoutName: rawArgs.layoutName,
        },
      }),
    },
    {
      toolName: "civil3d_sheet_get_properties",
      displayName: "Civil 3D Sheet Get Properties",
      description: "Gets full properties of a specific sheet within a Plan Production sheet set.",
      inputShape: { sheetSetName: z.string(), sheetName: z.string() },
      supportedActions: ["sheet_get_properties"],
      resolveAction: (rawArgs) => ({
        action: "sheet_get_properties",
        args: { action: "sheet_get_properties", sheetSetName: rawArgs.sheetSetName, sheetName: rawArgs.sheetName },
      }),
    },
    {
      toolName: "civil3d_sheet_set_title_block",
      displayName: "Civil 3D Sheet Set Title Block",
      description: "Sets or updates the title block template on a sheet within a Plan Production sheet set.",
      inputShape: { sheetSetName: z.string(), sheetName: z.string(), titleBlockPath: z.string() },
      supportedActions: ["sheet_set_title_block"],
      resolveAction: (rawArgs) => ({
        action: "sheet_set_title_block",
        args: {
          action: "sheet_set_title_block",
          sheetSetName: rawArgs.sheetSetName,
          sheetName: rawArgs.sheetName,
          titleBlockPath: rawArgs.titleBlockPath,
        },
      }),
    },
    {
      toolName: "civil3d_plan_profile_sheet_update_alignment",
      displayName: "Civil 3D Plan Profile Sheet Update Alignment",
      description: "Updates the alignment and optionally the profile on an existing Plan/Profile sheet.",
      inputShape: {
        sheetSetName: z.string(),
        sheetName: z.string(),
        alignmentName: z.string(),
        profileName: z.string().optional(),
      },
      supportedActions: ["plan_profile_sheet_update_alignment"],
      resolveAction: (rawArgs) => ({
        action: "plan_profile_sheet_update_alignment",
        args: {
          action: "plan_profile_sheet_update_alignment",
          sheetSetName: rawArgs.sheetSetName,
          sheetName: rawArgs.sheetName,
          alignmentName: rawArgs.alignmentName,
          profileName: rawArgs.profileName,
        },
      }),
    },
    {
      toolName: "civil3d_sheet_view_create",
      displayName: "Civil 3D Sheet View Create",
      description: "Creates a viewport or view on a sheet layout.",
      inputShape: {
        layoutName: z.string(),
        viewName: z.string().optional(),
        centerX: z.number().optional(),
        centerY: z.number().optional(),
        width: z.number().optional(),
        height: z.number().optional(),
        scale: z.number().optional(),
      },
      supportedActions: ["sheet_view_create"],
      resolveAction: (rawArgs) => ({
        action: "sheet_view_create",
        args: {
          action: "sheet_view_create",
          layoutName: rawArgs.layoutName,
          viewName: rawArgs.viewName,
          centerX: rawArgs.centerX,
          centerY: rawArgs.centerY,
          width: rawArgs.width,
          height: rawArgs.height,
          scale: rawArgs.scale,
        },
      }),
    },
    {
      toolName: "civil3d_sheet_view_set_scale",
      displayName: "Civil 3D Sheet View Set Scale",
      description: "Updates the scale of a viewport on a sheet layout.",
      inputShape: {
        layoutName: z.string(),
        viewportHandle: z.string().optional(),
        scale: z.number().positive(),
      },
      supportedActions: ["sheet_view_set_scale"],
      resolveAction: (rawArgs) => ({
        action: "sheet_view_set_scale",
        args: {
          action: "sheet_view_set_scale",
          layoutName: rawArgs.layoutName,
          viewportHandle: rawArgs.viewportHandle,
          scale: rawArgs.scale,
        },
      }),
    },
    {
      toolName: "civil3d_sheet_publish_pdf",
      displayName: "Civil 3D Sheet Publish PDF",
      description: "Publishes one or more sheet layouts to a PDF file.",
      inputShape: {
        layoutNames: z.array(z.string()).min(1),
        outputPath: z.string(),
        overwrite: z.boolean().optional(),
        plotStyleTable: z.string().optional(),
        paperSize: z.string().optional(),
      },
      supportedActions: ["sheet_publish_pdf"],
      resolveAction: (rawArgs) => ({
        action: "sheet_publish_pdf",
        args: {
          action: "sheet_publish_pdf",
          layoutNames: rawArgs.layoutNames,
          outputPath: rawArgs.outputPath,
          overwrite: rawArgs.overwrite,
          plotStyleTable: rawArgs.plotStyleTable,
          paperSize: rawArgs.paperSize,
        },
      }),
    },
    {
      toolName: "civil3d_sheet_set_export",
      displayName: "Civil 3D Sheet Set Export",
      description: "Exports all sheets in a Plan Production sheet set to a single multi-page PDF.",
      inputShape: { sheetSetName: z.string(), outputPath: z.string(), overwrite: z.boolean().optional(), plotStyleTable: z.string().optional() },
      supportedActions: ["sheet_set_export"],
      resolveAction: (rawArgs) => ({
        action: "sheet_set_export",
        args: {
          action: "sheet_set_export",
          sheetSetName: rawArgs.sheetSetName,
          outputPath: rawArgs.outputPath,
          overwrite: rawArgs.overwrite,
          plotStyleTable: rawArgs.plotStyleTable,
        },
      }),
    },
  ],
};
