import { mkdir, readFile, writeFile } from "node:fs/promises";
import openapiTS, { astToString } from "openapi-typescript";

const args = process.argv.slice(2);
if (args.some((arg) => arg !== "--check")) {
  throw new Error("Usage: node generate-client.mjs [--check]");
}

const input = new URL("./openapi.json", import.meta.url);
const output = new URL("../../frontend/src/api/generated/schema.ts", import.meta.url);
const schema = JSON.parse(await readFile(input, "utf8"));
if (!schema.openapi?.startsWith("3.") || !schema.paths) {
  throw new Error("Expected the server-generated OpenAPI 3 contract.");
}
const ast = await openapiTS(schema, { alphabetize: true, immutable: true });
const text = [
  "// Generated from backend/contracts/openapi.json. Do not edit manually.",
  "// Regenerate: npm run generate --prefix backend/contracts",
  astToString(ast).trimEnd(),
  "",
].join("\n");

if (args.includes("--check")) {
  const existing = await readFile(output, "utf8");
  if (existing.replaceAll("\r\n", "\n") !== text) {
    throw new Error("API client types are stale. Export the backend schema and regenerate the client.");
  }
  console.log("Generated API types match the committed backend contract.");
} else {
  await mkdir(new URL("./", output), { recursive: true });
  await writeFile(output, text, "utf8");
  console.log("Generated frontend/src/api/generated/schema.ts.");
}
