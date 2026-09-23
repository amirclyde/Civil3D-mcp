import { describe, expect, it } from "vitest";
import { z } from "zod";
import { zodToJsonSchema } from "zod-to-json-schema";
import { tolerantInputShape } from "../src/tools/domainRuntime.js";

describe("tolerant tool input shape", () => {
  const shape = tolerantInputShape({
    name: z.string(),
    redo: z.boolean().optional().describe("redo"),
    frequency: z.number().positive().optional(),
    map: z.record(z.string()).optional(),
  });
  const schema = z.object(shape);

  it("accepts booleans and numbers sent as text by a client with an older schema", () => {
    const parsed = schema.parse({ name: "c", redo: "true", frequency: "2.5", map: { a: "b" } });
    expect(parsed).toEqual({ name: "c", redo: true, frequency: 2.5, map: { a: "b" } });
    expect(schema.parse({ name: "c", redo: "False" }).redo).toBe(false);
  });

  it("still takes real values and still refuses nonsense", () => {
    expect(schema.parse({ name: "c", redo: false, frequency: 1 })).toEqual({ name: "c", redo: false, frequency: 1 });
    expect(schema.safeParse({ name: "c", redo: "maybe" }).success).toBe(false);
    expect(schema.safeParse({ name: "c", frequency: "abc" }).success).toBe(false);
    expect(schema.safeParse({ name: "c", frequency: "-1" }).success).toBe(false);
  });

  it("advertises the same JSON schema types", () => {
    const json = zodToJsonSchema(schema) as any;
    expect(json.properties.redo.type).toBe("boolean");
    expect(json.properties.frequency.type).toBe("number");
    expect(json.required).toEqual(["name"]);
  });
});
