import { describe, expect, it } from "vitest";
import { captureDomainToolHandlers } from "../src/tools/domainRuntime.js";
import { findManifestAction, MIGRATED_DOMAIN_DEFINITIONS } from "../src/tools/toolManifest.js";
import { executeRegisteredTool } from "../src/tools/toolHandlerRegistry.js";
import { hasApprovalRisk } from "../src/tools/approvalPolicy.js";

function responseSchema(toolName: string, action: string) {
  const match = findManifestAction(toolName, action);
  if (!match?.actionDefinition.responseSchema) throw new Error(`no response schema for ${toolName}/${action}`);
  return match.actionDefinition.responseSchema;
}

describe("drawing settings contract", () => {
  it("accepts the null styles the plugin sends (corridor style is always null)", () => {
    const pluginResponse = {
      coordinateSystem: "MAL-MSEL", coordinateZone: null, datum: null, scaleFactor: 1, elevationReference: null,
      defaultLayer: "0",
      defaultStyles: { surface: "Standard", alignment: "Standard", profile: "Standard", corridor: null, pipeNetwork: null },
    };
    expect(responseSchema("civil3d_drawing", "settings").safeParse(pluginResponse).success).toBe(true);
  });
});

describe("alignment get contract", () => {
  const entity = (index: number, order: number, type: string, entityType: string, start: number, end: number) =>
    ({ index, order, type, entityType, startStation: start, endStation: end, length: end - start });

  it("keeps group types, order, and diagnostics instead of stripping or rejecting them", () => {
    const response = {
      name: "Proposed Road", handle: "24EC6", type: "centerline", style: "Basic", layer: "C-ROAD",
      length: 2479.567, startStation: 0, endStation: 2479.567, entityCount: 2,
      entities: [
        entity(2, 0, "line", "Line", 0, 450.665),
        entity(1, 1, "spiral_curve_spiral", "SpiralCurveSpiral", 1537.223, 1673.287),
      ],
      dependentProfiles: ["EG"], dependentCorridors: ["Design Road"], isReference: false,
      diagnostics: { style: "StyleName: property not found" },
    };
    const parsed = responseSchema("civil3d_alignment", "get").parse(response) as typeof response;
    expect(parsed.entities[1]).toMatchObject({ type: "spiral_curve_spiral", entityType: "SpiralCurveSpiral", order: 1 });
    expect(parsed.diagnostics).toEqual({ style: "StyleName: property not found" });
  });
});

describe("profile contracts", () => {
  it("keeps raw Civil 3D profile and entity type names", () => {
    const response = {
      name: "EG", handle: "1", type: "surface", profileType: "EG", style: "Existing Ground", layer: "0",
      startStation: 0, endStation: 100, minElevation: 1, maxElevation: 2, entityCount: 1,
      entities: [{ index: 0, type: "parabola", entityType: "ParabolaSymmetric", startStation: 0, endStation: 60,
        startElevation: 1, endElevation: 2, grade: null, length: 60 }],
      pviCount: 3, units: { horizontal: "meters", vertical: "meters" },
    };
    const parsed = responseSchema("civil3d_profile", "get").parse(response) as typeof response;
    expect(parsed.profileType).toBe("EG");
    expect(parsed.entities[0].entityType).toBe("ParabolaSymmetric");
  });
});

describe("readable validation errors", () => {
  it("names the missing parameter instead of dumping a raw ZodError", async () => {
    for (const definition of MIGRATED_DOMAIN_DEFINITIONS) captureDomainToolHandlers(definition);
    const result = await executeRegisteredTool("civil3d_profile", { action: "list" })
      .then((value) => value, (reason) => reason);
    const text = result instanceof Error ? result.message : JSON.stringify(result);
    expect(text).toContain("Invalid parameters for civil3d_profile action 'list'");
    expect(text).toContain("alignmentName: Required");
    expect(text).not.toContain("\"code\": \"invalid_type\"");
  });
});

describe("utnm_alignment geometry", () => {
  it("is read-only and needs no approval", () => {
    const match = findManifestAction("utnm_alignment", "geometry");
    expect(match).toBeTruthy();
    expect(hasApprovalRisk({
      toolName: "utnm_alignment", action: "geometry",
      capabilities: match!.actionDefinition.capabilities, safeForRetry: match!.actionDefinition.safeForRetry,
    })).toBe(false);
  });

  it("passes the full detailed entity dump through unstripped", () => {
    const response = {
      name: "Proposed Road", startStation: 0, endStation: 10, length: 10, entityCount: 1,
      entities: [{ index: 1, order: 0, type: "spiral_curve_spiral", subEntities: [{ index: 0, properties: { Radius: 250 } }] }],
    };
    const parsed = responseSchema("utnm_alignment", "geometry").parse(response) as typeof response;
    expect(parsed.entities[0].subEntities[0].properties.Radius).toBe(250);
  });
});
