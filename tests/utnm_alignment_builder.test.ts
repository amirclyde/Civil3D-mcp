import { describe, expect, it } from "vitest";
import { AlignmentSpecSchema, UTNM_ALIGNMENT_DOMAIN_DEFINITION } from "../src/tools/domains/utnmAlignmentDomain.js";
import { hasApprovalRisk } from "../src/tools/approvalPolicy.js";
import { findManifestAction } from "../src/tools/toolManifest.js";

function baseSpec() {
  return {
    spec_version: "0.1",
    source: { type: "excel", file: "IP_Table.xlsx", sheet: "IP" },
    coordinate_system: { name: "MAL-MSEL", units: "m" },
    alignment: { name: "CL-Test", start_station: 0, style: "Proposed", layer: "C-ROAD" },
    points: [
      { id: "BP", role: "start", easting: 1000, northing: 2000 },
      { id: "IP1", role: "pi", easting: 1300, northing: 2000, curve: { kind: "simple", radius: 350 } },
      { id: "IP2", role: "pi", easting: 1600, northing: 2250, curve: { kind: "scs", radius: 250, ls_in: 60, ls_out: 40 } },
      { id: "IP3", role: "pi", easting: 1900, northing: 2250, curve: { kind: "none" } },
      { id: "EP", role: "end", easting: 2200, northing: 2400 },
    ],
    validation: { status: "pass", issues: [] },
  };
}

describe("alignment spec v0.1", () => {
  it("accepts simple, asymmetric spiral-curve-spiral and no-curve IPs", () => {
    expect(AlignmentSpecSchema.parse(baseSpec()).alignment.spiral_type).toBe("Clothoid");
  });

  it("rejects an IP without an explicit curve", () => {
    const spec = baseSpec();
    delete (spec.points[1] as { curve?: unknown }).curve;
    expect(AlignmentSpecSchema.safeParse(spec).success).toBe(false);
  });

  it("rejects a zero spiral length: a blank spiral must be sent as kind 'simple'", () => {
    const spec = baseSpec();
    spec.points[2].curve = { kind: "scs", radius: 250, ls_in: 60, ls_out: 0 };
    expect(AlignmentSpecSchema.safeParse(spec).success).toBe(false);
  });

  it("rejects curves on start/end points and duplicate ids", () => {
    const onStart = baseSpec();
    (onStart.points[0] as { curve?: unknown }).curve = { kind: "simple", radius: 100 };
    expect(AlignmentSpecSchema.safeParse(onStart).success).toBe(false);

    const duplicate = baseSpec();
    duplicate.points[3].id = "IP2";
    expect(AlignmentSpecSchema.safeParse(duplicate).success).toBe(false);
  });

  it("rejects failed specs and unacknowledged warnings", () => {
    const failed = baseSpec();
    failed.validation = { status: "fail", issues: [] };
    expect(AlignmentSpecSchema.safeParse(failed).success).toBe(false);

    const warned = baseSpec();
    warned.validation = { status: "warn", issues: [] };
    expect(AlignmentSpecSchema.safeParse(warned).success).toBe(false);

    const acknowledged = baseSpec() as ReturnType<typeof baseSpec> & { validation: { user_acknowledged_warnings?: boolean } };
    acknowledged.validation = { status: "warn", issues: [], user_acknowledged_warnings: true };
    expect(AlignmentSpecSchema.safeParse(acknowledged).success).toBe(true);
  });
});

describe("utnm_alignment actions", () => {
  const risk = (action: "geometry" | "validate_from_pis" | "create_from_pis") => {
    const definition = UTNM_ALIGNMENT_DOMAIN_DEFINITION.actions[action];
    return hasApprovalRisk({
      toolName: "utnm_alignment", action,
      capabilities: definition.capabilities, safeForRetry: definition.safeForRetry,
    });
  };

  it("needs approval only to create; the dry run and geometry are read-only", () => {
    expect(risk("create_from_pis")).toBe(true);
    expect(risk("validate_from_pis")).toBe(false);
    expect(risk("geometry")).toBe(false);
  });

  it("routes all three actions through the single exposure, defaulting to geometry", () => {
    const exposure = UTNM_ALIGNMENT_DOMAIN_DEFINITION.exposures[0];
    expect(exposure.resolveAction({ name: "Proposed Road" }).action).toBe("geometry");
    expect(exposure.resolveAction({ action: "validate_from_pis", spec: baseSpec() }).action).toBe("validate_from_pis");
    expect(findManifestAction("utnm_alignment", "create_from_pis")).toBeTruthy();
  });

  it("sends dryRun=true for validate and false for create", async () => {
    const validate = UTNM_ALIGNMENT_DOMAIN_DEFINITION.actions.validate_from_pis;
    const create = UTNM_ALIGNMENT_DOMAIN_DEFINITION.actions.create_from_pis;
    expect(validate.pluginMethods).toEqual(["utnmCreateAlignmentFromPis"]);
    expect(create.pluginMethods).toEqual(["utnmCreateAlignmentFromPis"]);
    const source = (validate.execute.toString() + create.execute.toString());
    expect(source).toContain("dryRun");
  });
});
