import type { Transport } from "@modelcontextprotocol/sdk/shared/transport.js";
import type { JSONRPCMessage } from "@modelcontextprotocol/sdk/types.js";

/**
 * MCP SDK 1.x converts zod v3 tool schemas with zod-to-json-schema, which stamps
 * "$schema": draft-07 on every inputSchema/outputSchema. Newer MCP clients
 * (Claude Code 2.1.x) validate structured tool output with a JSON Schema 2020-12
 * validator and refuse any schema that declares another dialect, so every tool
 * call fails before it reaches Civil 3D. The SDK offers no option to change the
 * emitted dialect, so we strip the marker from tools/list results at the
 * transport boundary. A schema without "$schema" is validated as 2020-12, which
 * accepts everything zod-to-json-schema emits for our schemas.
 */
export const JSON_SCHEMA_DIALECT_KEY = "$schema";

export function stripJsonSchemaDialect<T>(schema: T): T {
  if (Array.isArray(schema)) {
    return schema.map((item) => stripJsonSchemaDialect(item)) as T;
  }
  if (!schema || typeof schema !== "object") {
    return schema;
  }
  const result: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(schema as Record<string, unknown>)) {
    if (key === JSON_SCHEMA_DIALECT_KEY) continue;
    result[key] = stripJsonSchemaDialect(value);
  }
  return result as T;
}

function isToolListResult(message: JSONRPCMessage): boolean {
  if (!("result" in message) || !message.result || typeof message.result !== "object") {
    return false;
  }
  const tools = (message.result as { tools?: unknown }).tools;
  return Array.isArray(tools) && tools.every((tool) => tool && typeof tool === "object" && "inputSchema" in tool);
}

export function sanitizeToolListMessage(message: JSONRPCMessage): JSONRPCMessage {
  if (!isToolListResult(message)) {
    return message;
  }
  const result = (message as unknown as { result: { tools: Array<Record<string, unknown>> } }).result;
  const tools = result.tools.map((tool) => {
    const next = { ...tool };
    if (next.inputSchema) next.inputSchema = stripJsonSchemaDialect(next.inputSchema);
    if (next.outputSchema) next.outputSchema = stripJsonSchemaDialect(next.outputSchema);
    return next;
  });
  return { ...message, result: { ...result, tools } } as JSONRPCMessage;
}

/** Wraps a server transport so tools/list results never declare a JSON Schema dialect. */
export function withToolSchemaCompat<T extends Transport>(transport: T): T {
  const originalSend = transport.send.bind(transport);
  transport.send = (message, options) => originalSend(sanitizeToolListMessage(message), options);
  return transport;
}
