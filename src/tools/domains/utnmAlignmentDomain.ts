import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

// UTNM additions for the c3d-alignment-from-ip skill. Kept in its own domain file so upstream
// files only change by registration lines.

const GeometryArgsSchema = z.object({
  action: z.literal("geometry").optional(),
  name: z.string().min(1),
});

// Passthrough: the detailed report is intentionally open-ended while the Civil 3D 2026
// property names are being confirmed live. Nothing is stripped.
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
  },
  exposures: [
    {
      toolName: "utnm_alignment",
      displayName: "UTNM Alignment Geometry",
      description:
        "Read-only, detailed horizontal alignment geometry: every entity in station order with exact stations " +
        "and lengths, start/end coordinates, Civil 3D entity type (including spiral-curve-spiral and other groups), " +
        "and all readable properties of each entity and its sub-entities (spiral in, arc, spiral out). " +
        "Requires 'name' (the alignment name).",
      inputShape: {
        action: z.enum(["geometry"]).optional(),
        name: z.string().describe("Alignment name (required)"),
      },
      supportedActions: ["geometry"],
      resolveAction: (rawArgs) => ({
        action: "geometry",
        args: { ...rawArgs, action: "geometry" },
      }),
    },
  ],
};
