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

// Raw "IDR 7000.00" bypasses rupiah grouping and the comma decimal; amounts render as formatMoney("IDR", value).
test("no amount is printed with a raw IDR prefix", () => {
  const offenders = sources("src")
    .filter(path => path.replaceAll("\\", "/") !== "src/lib/format.ts")
    .flatMap(path => readFileSync(path, "utf8").split("\n").map((line, index) => ({ path, line, index }))
      .filter(({ line }) => /IDR \{|IDR \$\{|'IDR ' \+|"IDR " \+/.test(line))
      .map(({ path, index }) => `${path}:${index + 1}`));
  assert.deepEqual(offenders, []);
});

// F-023: business dates arrive as ISO "2026-10-22"; procurement and inventory pages show them as "22 Okt 2026" via formatDate.
test("procurement and inventory pages print no raw ISO calendar date", () => {
  const raw = /(?<![=(])\{[A-Za-z_.?]+\.[a-zA-Z]*(?:Date|On)(?:\s*\?\?[^}]*)?\}|\+ ?[A-Za-z_.?]+\.[a-zA-Z]*(?:Date|On)\b|\$\{[A-Za-z_.?]+\.[a-zA-Z]*(?:Date|On)\}/;
  const offenders = ["src/modules/procurement", "src/modules/inventory"].flatMap(sources)
    .filter(path => path.endsWith(".tsx"))
    .flatMap(path => readFileSync(path, "utf8").split("\n").map((line, index) => ({ path, line, index }))
      .filter(({ line }) => raw.test(line))
      .map(({ path, index }) => `${path}:${index + 1}`));
  assert.deepEqual(offenders, []);
});
