import { existsSync, readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";

const project = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const runtimeRequire = createRequire(resolve(project, "package.json"));
const cache = new Map();
export const QA_CUSTOMER_ID = "customer-qa-b";
export const QA_CUSTOMER_EMAIL = "customer-qa-b@unit-toko.demo";

/** This helper is intentionally incapable of targeting the ordinary demo or a remote database. */
export function assertLocalQaEnvironment() {
  if (resolve(process.cwd()) !== project) throw new Error("Run QA from the website directory.");
  if (process.env.TURSO_DATABASE_URL !== "file:.data/unit-toko-v7-qa.db") throw new Error("QA requires the exact isolated v7 database URL.");
  if (process.env.VERCEL || process.env.TURSO_AUTH_TOKEN || process.env.BLOB_READ_WRITE_TOKEN || process.env.NODE_ENV === "production") throw new Error("QA refuses production, remote database, or blob credentials.");
  if (!process.env.DEMO_PASSWORD_SEED || process.env.DEMO_PASSWORD_SEED.length < 32) throw new Error("QA requires its private demo seed.");
  const base = process.env.TOKO_TEST_URL || "http://127.0.0.1:3007";
  if (base !== "http://127.0.0.1:3007") throw new Error("QA requires the isolated local server on port 3007.");
  return base;
}

// Import the real repository/security code without a generated bundle or a subprocess.
// The loader accepts project-local TypeScript only; packages use Node's normal resolver.
export function loadProjectModule(relative) {
  const path = resolve(project, relative);
  if (!path.startsWith(project + "/") && !path.startsWith(project + "\\")) throw new Error("QA module must remain inside the website.");
  if (cache.has(path)) return cache.get(path).exports;
  const ts = runtimeRequire("typescript");
  const evaluatedModule = { exports: {} };
  cache.set(path, evaluatedModule);
  const output = ts.transpileModule(readFileSync(path, "utf8"), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, esModuleInterop: true },
    fileName: path,
  }).outputText;
  const localRequire = specifier => {
    if (!specifier.startsWith(".") && !specifier.startsWith("@/")) return runtimeRequire(specifier);
    const base = specifier.startsWith("@/") ? resolve(project, specifier.slice(2)) : resolve(dirname(path), specifier);
    const candidate = [base, base + ".ts", base + ".tsx", resolve(base, "index.ts")].find(file => existsSync(file) && /\.[cm]?[jt]sx?$/.test(file));
    if (!candidate) throw new Error("QA could not resolve a local source module.");
    return loadProjectModule(candidate);
  };
  new Function("require", "module", "exports", output)(localRequire, evaluatedModule, evaluatedModule.exports);
  return evaluatedModule.exports;
}

export function qaPassword(id) {
  assertLocalQaEnvironment();
  return loadProjectModule("lib/server/demo-credentials.ts").demoPassword(id);
}

export async function prepareCustomerQaFixture() {
  assertLocalQaEnvironment();
  const repository = loadProjectModule("lib/server/repository.ts");
  const security = loadProjectModule("lib/server/security.ts");
  await repository.ensureSeed();
  const db = repository.database();
  const existing = await db.prepare("SELECT payload FROM users WHERE id=?").bind(QA_CUSTOMER_ID).first();
  const emailOwner = await db.prepare("SELECT user_id FROM user_emails WHERE email=?").bind(QA_CUSTOMER_EMAIL).first();
  if (existing) {
    const actor = JSON.parse(existing.payload);
    if (actor.role !== "customer" || actor.divisionId !== null || actor.email !== QA_CUSTOMER_EMAIL || emailOwner?.user_id !== QA_CUSTOMER_ID) throw new Error("QA customer identity conflict; fixture stopped.");
  } else {
    if (emailOwner) throw new Error("QA email conflict; fixture stopped.");
    const actor = { id: QA_CUSTOMER_ID, email: QA_CUSTOMER_EMAIL, name: "Pelanggan QA Kedua", role: "customer", divisionId: null, active: true, address: "Alamat QA kedua", phone: "08000000002", position: "", avatar: "" };
    const salt = crypto.randomUUID(), hash = await security.hashPassword(qaPassword(actor.id), salt);
    await db.batch([
      db.prepare("INSERT INTO users(id,payload,division_id,role) VALUES(?,?,NULL,'customer')").bind(actor.id, JSON.stringify(actor)),
      db.prepare("INSERT INTO user_emails(email,user_id) VALUES(?,?)").bind(actor.email, actor.id),
      db.prepare("INSERT INTO credentials(user_id,salt,hash) VALUES(?,?,?)").bind(actor.id, salt, hash),
      db.prepare("UPDATE system_version SET version=version+1 WHERE id='global'"),
    ]);
  }
  const run = crypto.randomUUID().slice(0, 8);
  const initial = await repository.loadState();
  let date = loadProjectModule("lib/domain/selectors.ts").today();
  const lastClosed = initial.periods.filter(p => p.status === "closed").map(p => p.id).sort().at(-1);
  if (lastClosed && date.slice(0, 7) <= lastClosed) {
    const next = new Date(lastClosed + "-01T12:00:00Z"); next.setUTCMonth(next.getUTCMonth() + 1); date = next.toISOString().slice(0, 10);
  }
  const act = (id, type, data) => repository.execute(initial.users.find(u => u.id === id), { id: crypto.randomUUID(), type, date, data });
  const products = {};
  for (const [name, qty] of [["full", 10], ["partial", 10], ["isolation", 50]]) {
    const result = await act("kepala", "master.product", { sku: `QA-V7-${run}-${name}`, name: `Barang QA ${run} ${name}`, category: "OMI", unit: "pcs", price: 10000, minimum: 0, returnMonths: 1 });
    products[name] = result.id;
    const purchase = await act("staf", "purchase.create", { kind: "OMI reguler", supplierId: "sup-omi", note: "Fixture QA lokal", lines: [{ productId: result.id, qty, cost: 6000 }] });
    await act("staf", "purchase.confirm", { id: purchase.id });
    const state = await repository.loadState();
    const line = state.purchaseLines.find(l => l.purchaseId === purchase.id);
    await act("staf", "purchase.receive", { id: purchase.id, lines: [{ purchaseLineId: line.id, qty, code: `QA-${run}-${name}`, location: "Rak QA" }] });
  }
  return { products, date, run };
}

/** Used only to prove fail-closed authorization for a corrupt QA identity; always restore in finally. */
export async function setQaCustomerRole(role) {
  assertLocalQaEnvironment();
  if (!["customer", "unknown-qa-role"].includes(role)) throw new Error("Unsupported QA-only role mutation.");
  const db = loadProjectModule("lib/server/repository.ts").database();
  const row = await db.prepare("SELECT payload FROM users WHERE id=?").bind(QA_CUSTOMER_ID).first();
  if (!row) throw new Error("QA customer not initialized.");
  const actor = JSON.parse(row.payload);
  if (actor.id !== QA_CUSTOMER_ID || actor.email !== QA_CUSTOMER_EMAIL || actor.divisionId !== null) throw new Error("QA role fixture identity mismatch.");
  actor.role = role;
  await db.batch([
    db.prepare("UPDATE users SET payload=?,role=? WHERE id=?").bind(JSON.stringify(actor), role, QA_CUSTOMER_ID),
    db.prepare("UPDATE system_version SET version=version+1 WHERE id='global'"),
  ]);
}
