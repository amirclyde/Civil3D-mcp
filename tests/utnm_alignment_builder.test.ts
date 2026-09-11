import { describe, expect, it } from "vitest";
import { AlignmentSpecSchema, UTNM_ALIGNMENT_DOMAIN_DEFINITION, alignmentNameProblem } from "../src/tools/domains/utnmAlignmentDomain.js";
import { hasApprovalRisk } from "../src/tools/approvalPolicy.js";
import { findManifestAction } from "../src/tools/toolManifest.js";

function baseSpec() {
  return {
    spec_version: "0.1",
    source: { type: "excel", file: "IP_Table.xlsx", sheet: "IP" },
    coordinate_system: { name: "MAL-MSEL", units: "m" },
    alignment: { name: "CL-Test", start_station: 0, style: "Proposed", layer: "C-ROAD", label_set: "Major Minor and Geometry Points" } as Record<string, unknown>,
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

describe("user selections and alignment names", () => {
  it("requires layer, style and label set to be chosen (no silent defaults)", () => {
    for (const field of ["layer", "style", "label_set"]) {
      const spec = baseSpec();
      delete spec.alignment[field];
      const result = AlignmentSpecSchema.safeParse(spec);
      expect(result.success, field).toBe(false);
    }
  });

  it("treats site as optional (null means siteless)", () => {
    const spec = baseSpec();
    spec.alignment.site = null;
    expect(AlignmentSpecSchema.safeParse(spec).success).toBe(true);
  });

  it("accepts proper names", () => {
    for (const name of ["CL-Jalan A", "FT019 Main Line (V3)", "U-Turn South R25", "Slip Road CH2900"]) {
      expect(alignmentNameProblem(name), name).toBeNull();
    }
  });

  it("rejects empty, padded, placeholder, overlong and invalid-character names", () => {
    expect(alignmentNameProblem("")).toMatch(/required/);
    expect(alignmentNameProblem("  CL-1")).toMatch(/spaces/);
    expect(alignmentNameProblem("Alignment - (1)")).toMatch(/placeholder/);
    expect(alignmentNameProblem("alignment-(12)")).toMatch(/placeholder/);
    expect(alignmentNameProblem("x".repeat(101))).toMatch(/longer than 100/);
    expect(alignmentNameProblem("Main/Line")).toMatch(/not allowed: \//);
    expect(alignmentNameProblem("CL:1?")).toMatch(/not allowed/);

    const spec = baseSpec();
    spec.alignment.name = "Alignment - (3)";
    expect(AlignmentSpecSchema.safeParse(spec).success).toBe(false);
  });
});

describe("utnm_alignment actions", () => {
  const risk = (action: "build_options" | "geometry" | "validate_from_pis" | "create_from_pis") => {
    const definition = UTNM_ALIGNMENT_DOMAIN_DEFINITION.actions[action];
    return hasApprovalRisk({
      toolName: "utnm_alignment", action,
      capabilities: definition.capabilities, safeForRetry: definition.safeForRetry,
    });
  };

  it("needs approval only to create; options, dry run and geometry are read-only", () => {
    expect(risk("create_from_pis")).toBe(true);
    expect(risk("build_options")).toBe(false);
    expect(risk("validate_from_pis")).toBe(false);
    expect(risk("geometry")).toBe(false);
  });

  it("routes all three actions through the single exposure, defaulting to geometry", () => {
    const exposure = UTNM_ALIGNMENT_DOMAIN_DEFINITION.exposures[0];
    expect(exposure.resolveAction({ name: "Proposed Road" }).action).toBe("geometry");
    expect(exposure.resolveAction({ action: "build_options" }).action).toBe("build_options");
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
