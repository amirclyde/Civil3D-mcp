import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

// ─── Shared schemas ───────────────────────────────────────────────────────────

const GenericSectionResponseSchema = z.object({}).passthrough();

const SampleLineGroupSchema = z.object({
  name: z.string(),
  handle: z.string(),
  sampleLineCount: z.number(),
  stations: z.array(z.number()),
});

const SectionListSampleLinesResponseSchema = z.object({
  sampleLineGroups: z.array(SampleLineGroupSchema),
});

const SectionDataResponseSchema = z.object({
  station: z.number(),
  sampleLineGroupName: z.string().optional(),
  sections: z.array(z.object({
    source: z.string().nullable().optional(),
    pointCount: z.number().optional(),
    points: z.array(z.object({ offset: z.number(), elevation: z.number() })),
  }).passthrough()).optional(),
  units: z.object({
    horizontal: z.string(),
    vertical: z.string(),
  }).optional(),
}).passthrough();

// ─── Per-action input schemas ─────────────────────────────────────────────────

const SectionListSampleLinesArgsSchema = z.object({
  action: z.literal("list_sample_lines"),
  alignmentName: z.string(),
});

const SectionGetDataArgsSchema = z.object({
  action: z.literal("get_section_data"),
  alignmentName: z.string(),
  sampleLineGroupName: z.string(),
  station: z.number(),
});

const SectionCreateSampleLinesArgsSchema = z.object({
  action: z.literal("create_sample_lines"),
  alignmentName: z.string(),
  groupName: z.string(),
  stations: z.array(z.number()).optional(),
  interval: z.number().optional(),
  leftWidth: z.number(),
  rightWidth: z.number(),
  surfaces: z.array(z.string()).optional().default([]),
}).superRefine((value, ctx) => {
  if (!value.stations && value.interval === undefined) {
    ctx.addIssue({
      code: z.ZodIssueCode.custom,
      message: "Either stations or interval is required.",
      path: ["stations"],
    });
  }
});

const SectionViewCreateArgsSchema = z.object({
  action: z.literal("view_create"),
  alignmentName: z.string(),
  sampleLineGroupName: z.string(),
  insertionPoint: z.array(z.number()).length(2).describe("[x, y]"),
  style: z.string().optional(),
  bandSetStyle: z.string().optional(),
  leftOffset: z.number().optional().describe("Left offset of the section view range, negative to the left (e.g. -20)."),
  rightOffset: z.number().optional().describe("Right offset of the section view range, positive (e.g. 20)."),
  stationStart: z.number().optional(),
  stationEnd: z.number().optional(),
});

const SectionViewCreateAtStationArgsSchema = z.object({
  action: z.literal("view_create_at_station"),
  alignmentName: z.string(),
  sampleLineGroupName: z.string(),
  station: z.number().describe("Station of an existing sample line (nearest within tolerance)."),
  insertionPoint: z.array(z.number()).length(2).describe("[x, y]"),
  viewName: z.string().optional(),
  style: z.string().optional(),
  bandSetStyle: z.string().optional(),
  tolerance: z.number().positive().optional().describe("Station match tolerance (default 0.5)."),
  leftOffset: z.number().optional().describe("With rightOffset: fixed offset range instead of automatic."),
  rightOffset: z.number().optional(),
  elevationMin: z.number().optional().describe("With elevationMax: fixed elevation range instead of automatic."),
  elevationMax: z.number().optional(),
});

const SectionViewListArgsSchema = z.object({
  action: z.literal("view_list"),
  alignmentName: z.string().optional(),
  sampleLineGroupName: z.string().optional(),
});

const SectionViewUpdateStyleArgsSchema = z.object({
  action: z.literal("view_update_style"),
  alignmentName: z.string(),
  sampleLineGroupName: z.string(),
  style: z.string().optional(),
  bandSetStyle: z.string().optional(),
  applyToAll: z.boolean().optional(),
});

const SectionViewGroupCreateArgsSchema = z.object({
  action: z.literal("view_group_create"),
  alignmentName: z.string(),
  sampleLineGroupName: z.string(),
  insertionPoint: z.array(z.number()).length(2).describe("[x, y]"),
  style: z.string().optional(),
  bandSetStyle: z.string().optional(),
  plotStyle: z.string().optional(),
  leftOffset: z.number().optional(),
  rightOffset: z.number().optional(),
  stationStart: z.number().optional(),
  stationEnd: z.number().optional(),
  templatePath: z.string().optional().describe("Sheet template (.dwt/.dwg) for production placement — views are arranged sheet by sheet for Create Section Sheets."),
  layoutName: z.string().optional().describe("Layout name inside templatePath to use for production placement."),
});

const SectionViewExportArgsSchema = z.object({
  action: z.literal("view_export"),
  alignmentName: z.string(),
  sampleLineGroupName: z.string(),
  outputPath: z.string(),
  overwrite: z.boolean().optional(),
  includeElevations: z.boolean().optional(),
  stationStart: z.number().optional(),
  stationEnd: z.number().optional(),
});

// ─── Canonical input shape ────────────────────────────────────────────────────

const canonicalSectionInputShape = {
  action: z.enum([
    "list_sample_lines",
    "get_section_data",
    "create_sample_lines",
    "view_create",
    "view_create_at_station",
    "view_list",
    "view_update_style",
    "view_group_create",
    "view_export",
  ]),
  alignmentName: z.string().optional(),
  sampleLineGroupName: z.string().optional(),
  station: z.number().optional(),
  groupName: z.string().optional(),
  stations: z.array(z.number()).optional(),
  interval: z.number().optional(),
  leftWidth: z.number().optional(),
  rightWidth: z.number().optional(),
  surfaces: z.array(z.string()).optional(),
  insertionPoint: z.array(z.number()).length(2).describe("[x, y]").optional(),
  style: z.string().optional(),
  bandSetStyle: z.string().optional(),
  leftOffset: z.number().optional().describe("Left offset of the section view range, negative to the left (e.g. -20)."),
  rightOffset: z.number().optional().describe("Right offset of the section view range, positive (e.g. 20)."),
  stationStart: z.number().optional(),
  stationEnd: z.number().optional(),
  applyToAll: z.boolean().optional(),
  viewName: z.string().optional().describe("view_create_at_station: section view name."),
  tolerance: z.number().positive().optional(),
  elevationMin: z.number().optional(),
  elevationMax: z.number().optional(),
  plotStyle: z.string().optional(),
  templatePath: z.string().optional(),
  layoutName: z.string().optional(),
  outputPath: z.string().optional(),
  overwrite: z.boolean().optional(),
  includeElevations: z.boolean().optional(),
};

// ─── Domain definition ────────────────────────────────────────────────────────

export const SECTION_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "section",
  actions: {
    list_sample_lines: {
      action: "list_sample_lines",
      inputSchema: SectionListSampleLinesArgsSchema,
      responseSchema: SectionListSampleLinesResponseSchema,
      capabilities: ["query"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listSampleLineGroups"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listSampleLineGroups", {
          alignmentName: args.alignmentName,
        }),
      ),
    },
    get_section_data: {
      action: "get_section_data",
      inputSchema: SectionGetDataArgsSchema,
      responseSchema: SectionDataResponseSchema,
      capabilities: ["query", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getSectionData"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("getSectionData", {
          alignmentName: args.alignmentName,
          sampleLineGroupName: args.sampleLineGroupName,
          station: args.station,
        }),
      ),
    },
    create_sample_lines: {
      action: "create_sample_lines",
      inputSchema: SectionCreateSampleLinesArgsSchema,
      responseSchema: GenericSectionResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createSampleLines"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createSampleLines", {
          alignmentName: args.alignmentName,
          groupName: args.groupName,
          stations: args.stations,
          interval: args.interval,
          leftWidth: args.leftWidth,
          rightWidth: args.rightWidth,
          surfaces: args.surfaces,
        }),
      ),
    },
    view_create: {
      action: "view_create",
      inputSchema: SectionViewCreateArgsSchema,
      responseSchema: GenericSectionResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createSectionViews"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => {
          const pt = args.insertionPoint as [number, number];
          return await appClient.sendCommand("createSectionViews", {
            alignmentName: args.alignmentName,
            sampleLineGroupName: args.sampleLineGroupName,
            insertionX: pt[0],
            insertionY: pt[1],
            style: args.style ?? null,
            bandSetStyle: args.bandSetStyle ?? null,
            leftOffset: args.leftOffset ?? null,
            rightOffset: args.rightOffset ?? null,
            stationStart: args.stationStart ?? null,
            stationEnd: args.stationEnd ?? null,
          });
        },
      ),
    },
    view_list: {
      action: "view_list",
      inputSchema: SectionViewListArgsSchema,
      responseSchema: GenericSectionResponseSchema,
      capabilities: ["query"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listSectionViews"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("listSectionViews", {
          alignmentName: args.alignmentName ?? null,
          sampleLineGroupName: args.sampleLineGroupName ?? null,
        }),
      ),
    },
    view_create_at_station: {
      action: "view_create_at_station",
      inputSchema: SectionViewCreateAtStationArgsSchema,
      responseSchema: GenericSectionResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createSectionViewAtStation"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("createSectionViewAtStation", {
          alignmentName: args.alignmentName,
          sampleLineGroupName: args.sampleLineGroupName,
          station: args.station,
          insertionX: (args.insertionPoint as [number, number])[0],
          insertionY: (args.insertionPoint as [number, number])[1],
          viewName: args.viewName ?? null,
          style: args.style ?? null,
          bandSetStyle: args.bandSetStyle ?? null,
          tolerance: args.tolerance ?? 0.5,
          leftOffset: args.leftOffset ?? null,
          rightOffset: args.rightOffset ?? null,
          elevationMin: args.elevationMin ?? null,
          elevationMax: args.elevationMax ?? null,
        }),
      ),
    },
    view_update_style: {
      action: "view_update_style",
      inputSchema: SectionViewUpdateStyleArgsSchema,
      responseSchema: GenericSectionResponseSchema,
      capabilities: ["edit", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["updateSectionViewStyles"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("updateSectionViewStyles", {
          alignmentName: args.alignmentName,
          sampleLineGroupName: args.sampleLineGroupName,
          style: args.style ?? null,
          bandSetStyle: args.bandSetStyle ?? null,
          applyToAll: args.applyToAll ?? true,
        }),
      ),
    },
    view_group_create: {
      action: "view_group_create",
      inputSchema: SectionViewGroupCreateArgsSchema,
      responseSchema: GenericSectionResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createSectionViewGroup"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => {
          const pt = args.insertionPoint as [number, number];
          return await appClient.sendCommand("createSectionViewGroup", {
            alignmentName: args.alignmentName,
            sampleLineGroupName: args.sampleLineGroupName,
            insertionX: pt[0],
            insertionY: pt[1],
            style: args.style ?? null,
            bandSetStyle: args.bandSetStyle ?? null,
            plotStyle: args.plotStyle ?? null,
            leftOffset: args.leftOffset ?? null,
            rightOffset: args.rightOffset ?? null,
            stationStart: args.stationStart ?? null,
            stationEnd: args.stationEnd ?? null,
            templatePath: args.templatePath ?? null,
            layoutName: args.layoutName ?? null,
          });
        },
      ),
    },
    view_export: {
      action: "view_export",
      inputSchema: SectionViewExportArgsSchema,
      responseSchema: GenericSectionResponseSchema,
      capabilities: ["export"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["exportSectionData"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("exportSectionData", {
          alignmentName: args.alignmentName,
          sampleLineGroupName: args.sampleLineGroupName,
          outputPath: args.outputPath,
          overwrite: args.overwrite ?? false,
          includeElevations: args.includeElevations ?? true,
          stationStart: args.stationStart ?? null,
          stationEnd: args.stationEnd ?? null,
        }),
      ),
    },
  },
  exposures: [
    {
      toolName: "civil3d_section",
      displayName: "Civil 3D Section",
      description: "Reads Civil 3D section data, manages sample lines, and controls section view creation and export through a single domain tool.",
      inputShape: canonicalSectionInputShape,
      supportedActions: [
        "list_sample_lines",
        "get_section_data",
        "create_sample_lines",
        "view_create",
        "view_create_at_station",
        "view_list",
        "view_update_style",
        "view_group_create",
        "view_export",
      ],
      resolveAction: (rawArgs) => ({
        action: String(rawArgs.action ?? ""),
        args: rawArgs,
      }),
    },
    {
      toolName: "civil3d_section_view_create",
      displayName: "Civil 3D Section View Create",
      description: "Create Civil 3D section views for a sample line group at the specified insertion point. Optionally applies a style, band set, and constrains to a station range.",
      inputShape: {
        alignmentName: z.string(),
        sampleLineGroupName: z.string(),
        insertionPoint: z.array(z.number()).length(2).describe("[x, y]"),
        style: z.string().optional(),
        bandSetStyle: z.string().optional(),
        leftOffset: z.number().nonnegative().optional(),
        rightOffset: z.number().nonnegative().optional(),
        stationStart: z.number().optional(),
        stationEnd: z.number().optional(),
      },
      supportedActions: ["view_create"],
      resolveAction: (rawArgs) => ({
        action: "view_create",
        args: {
          action: "view_create",
          alignmentName: rawArgs.alignmentName,
          sampleLineGroupName: rawArgs.sampleLineGroupName,
          insertionPoint: rawArgs.insertionPoint,
          style: rawArgs.style,
          bandSetStyle: rawArgs.bandSetStyle,
          leftOffset: rawArgs.leftOffset,
          rightOffset: rawArgs.rightOffset,
          stationStart: rawArgs.stationStart,
          stationEnd: rawArgs.stationEnd,
        },
      }),
    },
    {
      toolName: "civil3d_section_view_list",
      displayName: "Civil 3D Section View List",
      description: "List Civil 3D section views in the active drawing, optionally filtered by alignment and sample line group.",
      inputShape: {
        alignmentName: z.string().optional(),
        sampleLineGroupName: z.string().optional(),
      },
      supportedActions: ["view_list"],
      resolveAction: (rawArgs) => ({
        action: "view_list",
        args: {
          action: "view_list",
          alignmentName: rawArgs.alignmentName,
          sampleLineGroupName: rawArgs.sampleLineGroupName,
        },
      }),
    },
    {
      toolName: "civil3d_section_view_update_style",
      displayName: "Civil 3D Section View Update Style",
      description: "Update the display style and/or band set style on existing Civil 3D section views for a sample line group.",
      inputShape: {
        alignmentName: z.string(),
        sampleLineGroupName: z.string(),
        style: z.string().optional(),
        bandSetStyle: z.string().optional(),
        applyToAll: z.boolean().optional(),
      },
      supportedActions: ["view_update_style"],
      resolveAction: (rawArgs) => ({
        action: "view_update_style",
        args: {
          action: "view_update_style",
          alignmentName: rawArgs.alignmentName,
          sampleLineGroupName: rawArgs.sampleLineGroupName,
          style: rawArgs.style,
          bandSetStyle: rawArgs.bandSetStyle,
          applyToAll: rawArgs.applyToAll,
        },
      }),
    },
    {
      toolName: "civil3d_section_view_group_create",
      displayName: "Civil 3D Section View Group Create",
      description: "Creates a section view group (all sample lines in a station range) using the drawing's draft-placement settings, or production placement on a sheet template when templatePath + layoutName are given.",
      inputShape: {
        alignmentName: z.string(),
        sampleLineGroupName: z.string(),
        insertionPoint: z.array(z.number()).length(2).describe("[x, y]"),
        style: z.string().optional(),
        bandSetStyle: z.string().optional(),
        plotStyle: z.string().optional(),
        leftOffset: z.number().optional(),
        rightOffset: z.number().optional(),
        stationStart: z.number().optional(),
        stationEnd: z.number().optional(),
        templatePath: z.string().optional(),
        layoutName: z.string().optional(),
      },
      supportedActions: ["view_group_create"],
      resolveAction: (rawArgs) => ({
        action: "view_group_create",
        args: {
          action: "view_group_create",
          alignmentName: rawArgs.alignmentName,
          sampleLineGroupName: rawArgs.sampleLineGroupName,
          insertionPoint: rawArgs.insertionPoint,
          style: rawArgs.style,
          bandSetStyle: rawArgs.bandSetStyle,
          plotStyle: rawArgs.plotStyle,
          leftOffset: rawArgs.leftOffset,
          rightOffset: rawArgs.rightOffset,
          stationStart: rawArgs.stationStart,
          stationEnd: rawArgs.stationEnd,
          templatePath: rawArgs.templatePath,
          layoutName: rawArgs.layoutName,
        },
      }),
    },
    {
      toolName: "civil3d_section_view_export",
      displayName: "Civil 3D Section View Export",
      description: "Export Civil 3D section data to a CSV or text file, including station, offset, and surface elevation data per section.",
      inputShape: {
        alignmentName: z.string(),
        sampleLineGroupName: z.string(),
        outputPath: z.string(),
        overwrite: z.boolean().optional(),
        includeElevations: z.boolean().optional(),
        stationStart: z.number().optional(),
        stationEnd: z.number().optional(),
      },
      supportedActions: ["view_export"],
      resolveAction: (rawArgs) => ({
        action: "view_export",
        args: {
          action: "view_export",
          alignmentName: rawArgs.alignmentName,
          sampleLineGroupName: rawArgs.sampleLineGroupName,
          outputPath: rawArgs.outputPath,
          overwrite: rawArgs.overwrite,
          includeElevations: rawArgs.includeElevations,
          stationStart: rawArgs.stationStart,
          stationEnd: rawArgs.stationEnd,
        },
      }),
    },
  ],
};
