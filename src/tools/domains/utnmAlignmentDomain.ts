import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

// UTNM additions for the c3d-alignment-from-ip skill. Kept in its own domain file so upstream
// files only change by registration lines.

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
      name: z.string().min(1),
      type: z.literal("Centerline").default("Centerline"),
      start_station: z.number().finite(),
      spiral_type: z.literal("Clothoid").default("Clothoid"),
      site: z.string().nullable().optional(),
      layer: z.string().nullable().optional(),
      style: z.string().nullable().optional(),
      label_set: z.string().nullable().optional(),
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
        "UTNM alignment tools. 'geometry' (read-only, needs 'name'): every entity in station order with stations, " +
        "lengths, coordinates, Civil 3D entity types and sub-entity properties (radius, spiral length, A, PI...). " +
        "'validate_from_pis' (needs 'spec'): builds an alignment from an IP spec v0.1 with per-IP radius and spiral " +
        "lengths, verifies every IP, then rolls back - the drawing is not changed. 'create_from_pis' (needs 'spec', " +
        "requires approval): same build, committed only if every IP verifies. Always run validate_from_pis first.",
      inputShape: {
        action: z.enum(["geometry", "validate_from_pis", "create_from_pis"]).optional()
          .describe("Defaults to 'geometry'."),
        name: z.string().optional().describe("Alignment name (required for 'geometry')."),
        spec: AlignmentSpecSchema.optional().describe("IP alignment spec v0.1 (required for validate/create)."),
        tolerance: z.number().positive().max(1).optional()
          .describe("Verification tolerance in metres for validate/create. Default 0.001."),
      },
      supportedActions: ["geometry", "validate_from_pis", "create_from_pis"],
      resolveAction: (rawArgs) => {
        const action = typeof rawArgs.action === "string" ? rawArgs.action : "geometry";
        return { action, args: { ...rawArgs, action } };
      },
    },
  ],
};
