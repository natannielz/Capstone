import { test } from "node:test";
import assert from "node:assert/strict";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";

function sources(directory: string): string[] {
  return readdirSync(directory).flatMap(name => {
    const path = join(directory, name);
    return statSync(path).isDirectory() ? sources(path) : /\.(ts|tsx)$/.test(name) ? [path] : [];
  });
}

// One formatter (src/lib/format.ts) keeps rupiah, numbers, dates and WIB identical on server and client.
test("money, number and date formatting goes through src/lib/format.ts", () => {
  const offenders = sources("src")
    .filter(path => path.replaceAll("\\", "/") !== "src/lib/format.ts")
    .flatMap(path => readFileSync(path, "utf8").split("\n").map((line, index) => ({ path, line, index }))
      .filter(({ line }) => /\.toLocale(Date|Time)?String\(|new Intl\.(NumberFormat|DateTimeFormat)\(/.test(line))
      .map(({ path, index }) => `${path}:${index + 1}`));
  assert.deepEqual(offenders, []);
});
