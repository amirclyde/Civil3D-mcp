import { describe, expect, it } from "vitest";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { registerTools } from "../src/tools/register.js";
import { sanitizeToolListMessage, stripJsonSchemaDialect, withToolSchemaCompat } from "../src/toolSchemaCompat.js";

function findDialectMarkers(value: unknown, path = "$"): string[] {
  if (Array.isArray(value)) {
    return value.flatMap((item, index) => findDialectMarkers(item, `${path}[${index}]`));
  }
  if (!value || typeof value !== "object") return [];
  return Object.entries(value as Record<string, unknown>).flatMap(([key, child]) =>
    key === "$schema" ? [`${path}.${key}`] : findDialectMarkers(child, `${path}.${key}`),
  );
}

async function listToolsThrough(wrap: boolean) {
  const server = new McpServer({ name: "dialect-smoke", version: "test" });
  const client = new Client({ name: "dialect-client", version: "test" });
  await registerTools(server);
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  try {
    await server.connect(wrap ? withToolSchemaCompat(serverTransport) : serverTransport);
    await client.connect(clientTransport);
    return (await client.listTools()).tools;
  } finally {
    await client.close();
    await server.close();
  }
}

describe("tool schema dialect compatibility", () => {
  it("strips $schema markers recursively without touching other keys", () => {
    const stripped = stripJsonSchemaDialect({
      $schema: "http://json-schema.org/draft-07/schema#",
      type: "object",
      properties: { nested: { $schema: "x", type: "string", enum: ["$schema"] } },
      definitions: { a: { $schema: "y", type: "number" } },
    });
    expect(findDialectMarkers(stripped)).toEqual([]);
    expect(stripped).toEqual({
      type: "object",
      properties: { nested: { type: "string", enum: ["$schema"] } },
      definitions: { a: { type: "number" } },
    });
  });

  it("leaves messages that are not tools/list results untouched", () => {
    const message = { jsonrpc: "2.0" as const, id: 1, result: { content: [{ type: "text", text: "$schema" }] } };
    expect(sanitizeToolListMessage(message)).toBe(message);
  });

  it("the SDK still emits a draft-07 dialect without the wrapper (remove the wrapper once this fails)", async () => {
    const tools = await listToolsThrough(false);
    const health = tools.find((tool) => tool.name === "civil3d_health");
    expect(health?.outputSchema?.$schema).toBe("http://json-schema.org/draft-07/schema#");
  });

  it("publishes every tool without a JSON Schema dialect through the wrapped transport", async () => {
    const tools = await listToolsThrough(true);
    expect(tools.length).toBeGreaterThan(30);
    const offenders = tools.flatMap((tool) => [
      ...findDialectMarkers(tool.inputSchema, `${tool.name}.inputSchema`),
      ...findDialectMarkers(tool.outputSchema, `${tool.name}.outputSchema`),
    ]);
    expect(offenders).toEqual([]);
    const utnm = tools.find((tool) => tool.name === "utnm_alignment");
    expect(utnm?.outputSchema).toBeDefined();
    expect(utnm?.inputSchema.properties).toHaveProperty("spec");
  });
});
