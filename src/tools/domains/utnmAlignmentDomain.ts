import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

// UTNM additions for the c3d-alignment-from-ip skill. Kept in its own domain file so upstream
// files only change by registration lines.

// ─── Alignment name rules (mirrors UtnmNameRules in UtnmAlignmentBuilder.cs) ────

export const ALIGNMENT_NAME_RULES = {
  maxLength: 100,
  invalidCharacters: "<>/\\\":;?*|=`",
  defaultPlaceholder: /^alignment\s*-\s*\(\d+\)$/i,
} as const;

/** Returns null when the name is acceptable, otherwise the reason. Uniqueness is checked in Civil 3D. */
export function alignmentNameProblem(name: string): string | null {
  if (name.trim().length === 0) return "is required.";
  if (name !== name.trim()) return "must not start or end with spaces.";
  if (name.length > ALIGNMENT_NAME_RULES.maxLength) return `is longer than ${ALIGNMENT_NAME_RULES.maxLength} characters.`;
  const invalid = [...new Set([...name].filter((character) =>
    ALIGNMENT_NAME_RULES.invalidCharacters.includes(character) || /[\u0000-\u001f\u007f]/.test(character)))];
  if (invalid.length > 0) {
    const shown = invalid.map((character) => (/[\u0000-\u001f\u007f]/.test(character) ? "(control)" : character));
    return `contains characters that are not allowed: ${shown.join(" ")}`;
  }
  if (ALIGNMENT_NAME_RULES.defaultPlaceholder.test(name)) {
    return "is a Civil 3D default placeholder; enter a proper descriptive name.";
  }
  return null;
}

// ─── New layer name rules (mirrors UtnmLayerNameRules in UtnmAlignmentBuilder.cs) ─────

export const LAYER_NAME_RULES = {
  maxLength: 255,
  invalidCharacters: "<>/\\\":;?*|,=`",
  colorIndexRange: [1, 255],
} as const;

/** Returns null when a new layer name is acceptable, otherwise the reason. Existence is checked in Civil 3D. */
export function layerNameProblem(name: string): string | null {
  if (name.trim().length === 0) return "is required.";
  if (name !== name.trim()) return "must not start or end with spaces.";
  if (name.length > LAYER_NAME_RULES.maxLength) return `is longer than ${LAYER_NAME_RULES.maxLength} characters.`;
  const invalid = [...new Set([...name].filter((character) =>
    LAYER_NAME_RULES.invalidCharacters.includes(character) || /[\u0000-\u001f\u007f]/.test(character)))];
  if (invalid.length > 0) {
    const shown = invalid.map((character) => (/[\u0000-\u001f\u007f]/.test(character) ? "(control)" : character));
    return `contains characters that are not allowed: ${shown.join(" ")}`;
  }
  return null;
}

// ─── Alignment spec v0.1 (contract between the skill and the builder) ─────────

const CurveSchema = z.discriminatedUnion("kind", [
  z.object({ kind: z.literal("none") }),
  z.object({ kind: z.literal("simple"), radius: z.number().positive() }),
  z.object({
    kind: z.literal("scs"),
    radius: z.number().positive(),
    ls_in: z.number().positive(),
    ls_out: z.number().positive(),
  }),
]);

const SpecPointSchema = z.object({
  id: z.string().min(1),
  role: z.enum(["start", "pi", "end"]),
  easting: z.number().finite(),
  northing: z.number().finite(),
  curve: CurveSchema.optional(),
});

export const AlignmentSpecSchema = z
  .object({
    spec_version: z.literal("0.1"),
    source: z
      .object({
        type: z.string(),
        file: z.string().optional(),
        sheet: z.string().optional(),
        rows: z.string().optional(),
      })
      .passthrough(),
    coordinate_system: z.object({ name: z.string().min(1), units: z.literal("m") }),
    alignment: z.object({
      name: z.string().superRefine((name, ctx) => {
        const problem = alignmentNameProblem(name);
        if (problem) ctx.addIssue({ code: z.ZodIssueCode.custom, message: `Alignment name ${problem}` });
      }),
      type: z.literal("Centerline").default("Centerline"),
      start_station: z.number().finite(),
      spiral_type: z.literal("Clothoid").default("Clothoid"),
      // Chosen by the user from build_options; there are no silent defaults.
      layer: z.string().min(1, "A layer must be selected."),
      // true: 'layer' names a layer that does not exist yet; it is created inside the build
      // transaction with 'layer_color' (AutoCAD colour index) and rolled back with a dry run.
      create_layer: z.boolean().default(false),
      layer_color: z.number().int().min(LAYER_NAME_RULES.colorIndexRange[0]).max(LAYER_NAME_RULES.colorIndexRange[1]).optional(),
      style: z.string().min(1, "An alignment style must be selected."),
      label_set: z.string().min(1, "An alignment label set must be selected."),
      site: z.string().min(1).nullable().default(null),
      description: z.string().optional(),
    }),
    points: z.array(SpecPointSchema).min(2),
    computed: z.record(z.unknown()).optional(),
    table_check_values: z.record(z.unknown()).optional(),
    validation: z.object({
      status: z.enum(["pass", "warn", "fail"]),
      issues: z.array(z.unknown()),
      user_acknowledged_warnings: z.boolean().optional(),
    }),
  })
  .superRefine((spec, ctx) => {
    if (spec.alignment.create_layer) {
      const layerProblem = layerNameProblem(spec.alignment.layer);
      if (layerProblem) {
        ctx.addIssue({ code: z.ZodIssueCode.custom, path: ["alignment", "layer"], message: `New layer name ${layerProblem}` });
      }
      if (spec.alignment.layer_color === undefined) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom, path: ["alignment", "layer_color"],
          message: "A colour index (1-255) must be chosen for the new layer.",
        });
      }
    }
    const points = spec.points;
    if (points[0]?.role !== "start") {
      ctx.addIssue({ code: z.ZodIssueCode.custom, path: ["points", 0, "role"], message: "First point must have role 'start'." });
    }
    if (points[points.length - 1]?.role !== "end") {
      ctx.addIssue({ code: z.ZodIssueCode.custom, path: ["points", points.length - 1, "role"], message: "Last point must have role 'end'." });
    }
    points.forEach((point, index) => {
      const interior = index > 0 && index < points.length - 1;
      if (interior && point.role !== "pi") {
        ctx.addIssue({ code: z.ZodIssueCode.custom, path: ["points", index, "role"], message: `${point.id}: interior points must have role 'pi'.` });
      }
      if (interior && !point.curve) {
        ctx.addIssue({ code: z.ZodIssueCode.custom, path: ["points", index, "curve"], message: `${point.id}: every IP needs an explicit curve (use kind 'none' for no curve).` });
      }
      if (!interior && point.curve && point.curve.kind !== "none") {
        ctx.addIssue({ code: z.ZodIssueCode.custom, path: ["points", index, "curve"], message: `${point.id}: start and end points cannot carry a curve.` });
      }
    });
    const ids = new Set<string>();
    for (const point of points) {
      if (ids.has(point.id)) {
        ctx.addIssue({ code: z.ZodIssueCode.custom, path: ["points"], message: `Duplicate point id '${point.id}'.` });
      }
      ids.add(point.id);
    }
    if (spec.validation.status === "fail") {
      ctx.addIssue({ code: z.ZodIssueCode.custom, path: ["validation", "status"], message: "Spec failed validation; it cannot be built." });
    }
    if (spec.validation.status === "warn" && spec.validation.user_acknowledged_warnings !== true) {
      ctx.addIssue({ code: z.ZodIssueCode.custom, path: ["validation", "user_acknowledged_warnings"], message: "Spec has warnings the user has not acknowledged." });
    }
  });

// ─── Actions ───────────────────────────────────────────────────────────────────

const BuildOptionsArgsSchema = z.object({ action: z.literal("build_options") });

const BuildOptionsResponseSchema = z
  .object({
    currentLayer: z.string(),
    layers: z.array(z.object({ name: z.string(), usable: z.boolean() }).passthrough()),
    alignmentStyles: z.array(z.string()),
    alignmentLabelSets: z.array(z.string()),
    sites: z.array(z.string()),
    existingAlignmentNames: z.array(z.string()),
    nameRules: z.object({}).passthrough(),
    layerNameRules: z.object({}).passthrough().optional(),
  })
  .passthrough();

const GeometryArgsSchema = z.object({
  action: z.literal("geometry").optional(),
  name: z.string().min(1),
});

const BuildArgsSchema = z.object({
  action: z.enum(["validate_from_pis", "create_from_pis"]),
  spec: AlignmentSpecSchema,
  tolerance: z.number().positive().max(1).optional(),
});

// Passthrough: reports are open-ended while the Civil 3D 2026 property names settle.
const GeometryResponseSchema = z
  .object({
    name: z.string(),
    startStation: z.number(),
    endStation: z.number(),
    length: z.number(),
    entityCount: z.number(),
    entities: z.array(z.object({}).passthrough()),
  })
  .passthrough();

const BuildResponseSchema = z
  .object({
    name: z.string(),
    dryRun: z.boolean(),
    committed: z.boolean(),
    verificationPassed: z.boolean(),
    ips: z.array(z.object({ id: z.string(), ok: z.boolean() }).passthrough()),
  })
  .passthrough();

// Arguments arrive already validated against BuildArgsSchema by the domain runtime.
const buildExecutor = (dryRun: boolean) => async (args: Record<string, unknown>) =>
  await withApplicationConnection(
    async (appClient) => await appClient.sendCommand("utnmCreateAlignmentFromPis", {
      spec: args.spec,
      dryRun,
      tolerance: args.tolerance,
    }),
  );

export const UTNM_ALIGNMENT_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "utnm_alignment",
  actions: {
    build_options: {
      action: "build_options",
      inputSchema: BuildOptionsArgsSchema,
      responseSchema: BuildOptionsResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["utnmGetBuildOptions"],
      execute: async () => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("utnmGetBuildOptions", {}),
      ),
    },
    geometry: {
      action: "geometry",
      inputSchema: GeometryArgsSchema,
      responseSchema: GeometryResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["utnmGetAlignmentGeometry"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("utnmGetAlignmentGeometry", {
          name: args.name,
        }),
      ),
    },
    validate_from_pis: {
      action: "validate_from_pis",
      inputSchema: BuildArgsSchema,
      responseSchema: BuildResponseSchema,
      // Builds inside a transaction that is always rolled back: nothing changes in the drawing.
      capabilities: ["query", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["utnmCreateAlignmentFromPis"],
      execute: buildExecutor(true),
    },
    create_from_pis: {
      action: "create_from_pis",
      inputSchema: BuildArgsSchema,
      responseSchema: BuildResponseSchema,
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["utnmCreateAlignmentFromPis"],
      execute: buildExecutor(false),
    },
  },
  exposures: [
    {
      toolName: "utnm_alignment",
      displayName: "UTNM Alignment",
      description:
        "UTNM alignment tools. 'build_options' (read-only): the drawing's layers (with usability), alignment styles, " +
        "label sets, sites, existing alignment names and name/layer rules - show these to the user as dropdowns and let the " +
        "user choose; never pick layer/style/label set yourself. The user may instead ask for a new layer " +
        "(spec alignment.create_layer=true with layer_color 1-255); it is created inside the build transaction. " +
        "'geometry' (read-only, needs 'name'): every entity in station order with stations, " +
        "lengths, coordinates, Civil 3D entity types and sub-entity properties (radius, spiral length, A, PI...). " +
        "'validate_from_pis' (needs 'spec'): builds an alignment from an IP spec v0.1 with per-IP radius and spiral " +
        "lengths, verifies every IP, then rolls back - the drawing is not changed. 'create_from_pis' (needs 'spec', " +
        "requires approval): same build, committed only if every IP verifies. Always run validate_from_pis first.",
      inputShape: {
        action: z.enum(["build_options", "geometry", "validate_from_pis", "create_from_pis"]).optional()
          .describe("Defaults to 'geometry'."),
        name: z.string().optional().describe("Alignment name (required for 'geometry')."),
        spec: AlignmentSpecSchema.optional().describe("IP alignment spec v0.1 (required for validate/create)."),
        tolerance: z.number().positive().max(1).optional()
          .describe("Verification tolerance in metres for validate/create. Default 0.001."),
      },
      supportedActions: ["build_options", "geometry", "validate_from_pis", "create_from_pis"],
      resolveAction: (rawArgs) => {
        const action = typeof rawArgs.action === "string" ? rawArgs.action : "geometry";
        return { action, args: { ...rawArgs, action } };
      },
    },
  ],
};
