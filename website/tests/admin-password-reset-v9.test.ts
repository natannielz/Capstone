import {test} from "node:test";
import assert from "node:assert/strict";
import {createClient, type Client, type InValue} from "@libsql/client";
import {migrateDatabase, setTestDatabase, type Database, type Statement} from "../lib/server/database";
import {ensureSeed} from "../lib/server/repository";
import {demoPassword} from "../lib/server/demo-credentials";
import {digest, hashPassword} from "../lib/server/security";
import {POST as resetPassword} from "../app/api/admin/password-reset/route";
import {POST as login} from "../app/api/auth/login/route";
import {POST as changePassword} from "../app/api/profile/route";

class Query implements Statement {
  constructor(readonly client: Client, readonly sql: string, readonly args: InValue[] = []) {}
  bind(...values: unknown[]) { return new Query(this.client, this.sql, values as InValue[]); }
  async first<T>() { return (await this.client.execute({sql: this.sql, args: this.args})).rows[0] as T || null; }
  run() { return this.client.execute({sql: this.sql, args: this.args}); }
}

test("v9 administrator recovery is scoped, atomic, audited and safe to retry", async t => {
  const oldSeed = process.env.DEMO_PASSWORD_SEED;
  process.env.DEMO_PASSWORD_SEED = "v9-isolated-password-reset-tests-not-a-live-secret";
  const db = createClient({url: ":memory:"});
  let intercept: (() => Promise<void>) | undefined;
  let failAudit = false;
  const adapter: Database = {
    prepare: sql => new Query(db, sql),
    async batch(statements) {
      const queries = statements as Query[];
      if (queries.some(q => q.sql.includes("INSERT INTO mutation_guards")) && intercept) {
        const run = intercept; intercept = undefined; await run();
      }
      const actual = queries.map(q => ({sql: failAudit && q.sql.startsWith("INSERT INTO audits") ? "INSERT INTO missing_v9_table VALUES(1)" : q.sql, args: failAudit && q.sql.startsWith("INSERT INTO audits") ? [] : q.args}));
      return (await db.batch(actual, "write")).map(r => ({results: r.rows as Record<string, unknown>[]}));
    },
  };
  const session = async (id: string, userId: string) => db.execute({sql: "INSERT OR REPLACE INTO sessions(id,user_id,expires_at) VALUES(?,?,?)", args: [await digest(id), userId, Date.now() + 60_000]});
  const newPassword = "Customer-reset-v9-only!";
  const input = (extra: Record<string, unknown> = {}) => ({id: crypto.randomUUID(), targetId: "customer-demo", currentPassword: demoPassword("admin"), newPassword, reason: "Pemilik akun meminta pemulihan demo", ...extra});
  const request = (data: unknown, who = "v9-admin", origin = "https://unit-toko.test") => resetPassword(new Request("https://unit-toko.test/api/admin/password-reset", {method: "POST", headers: {origin, cookie: `toko_session=${who}`}, body: JSON.stringify(data)}));
  const changeOwnPassword = (who: string, currentPassword: string, nextPassword: string) => changePassword(new Request("https://unit-toko.test/api/profile", {method: "POST", headers: {origin: "https://unit-toko.test", cookie: `toko_session=${who}`}, body: JSON.stringify({currentPassword, newPassword: nextPassword})}));
  const credential = async (id = "customer-demo") => (await db.execute({sql: "SELECT salt,hash FROM credentials WHERE user_id=?", args: [id]})).rows[0];
  const count = async (table: string) => Number((await db.execute(`SELECT COUNT(*) AS n FROM ${table}`)).rows[0].n);
  const revision = async () => Number((await db.execute("SELECT version FROM system_version WHERE id='global'")).rows[0].version);
  try {
    await migrateDatabase(db); setTestDatabase(adapter); await ensureSeed();
    await session("v9-admin", "admin"); await session("v9-staf", "staf");
    await session("v9-customer-one", "customer-demo"); await session("v9-customer-two", "customer-demo");
    await t.test("unauthenticated, wrong role, self, cross-origin and malformed requests cannot change credentials", async () => {
      const original = await credential();
      assert.equal((await request(input(), "missing-session")).status, 401);
      assert.equal((await request(input(), "v9-staf")).status, 403);
      assert.equal((await request(input({targetId: "admin"}))).status, 403);
      assert.equal((await request(input(), "v9-admin", "https://outside.invalid")).status, 403);
      for (const value of [null, [], input({reason: ""}), input({newPassword: "short"}), input({role: "admin"})]) assert.equal((await request(value)).status, 400);
      assert.equal((await request(input({currentPassword: "incorrect-admin-password"}))).status, 400);
      assert.deepEqual(await credential(), original);
    });
    const valid = input();
    await t.test("successful recovery revokes only target sessions and stores an audit without passwords", async () => {
      const previous = await credential(), before = await revision();
      const response = await request(valid);
      assert.equal(response.status, 200);
      assert.notEqual((await credential()).hash, previous.hash);
      assert.equal(await count("sessions"), 2);
      assert.equal(await revision(), before + 1);
      const audit = (await db.execute("SELECT payload FROM audits WHERE json_extract(payload,'$.action')='admin.password.reset'")).rows;
      assert.equal(audit.length, 1);
      const payload = JSON.parse(String(audit[0].payload));
      assert.equal(payload.actorId, "admin"); assert.equal(payload.targetId, "customer-demo");
      assert.equal(payload.date, new Date(payload.at).toLocaleDateString("en-CA", {timeZone: "Asia/Jakarta"}));
      assert.match(payload.description, /Pemilik akun meminta/);
      const recorded = JSON.stringify([await response.json(), audit, (await db.execute("SELECT * FROM commands WHERE id LIKE 'admin-password-reset:%'")).rows]);
      assert.ok(!recorded.includes(newPassword) && !recorded.includes(demoPassword("admin")));
      const saved = (await db.execute({sql: "SELECT fingerprint FROM commands WHERE id=?", args: [`admin-password-reset:${valid.id}`]})).rows[0];
      const fastVerifier = await digest(JSON.stringify({id: valid.id, targetId: valid.targetId, newPassword, reason: valid.reason}));
      assert.notEqual(saved.fingerprint, fastVerifier, "Command logs must not introduce a fast offline password verifier");
      const authenticate = (password: string) => login(new Request("https://unit-toko.test/api/auth/login", {method: "POST", headers: {origin: "https://unit-toko.test"}, body: JSON.stringify({email: "customer@unit-toko.demo", password, portal: "customer"})}));
      assert.equal((await authenticate(demoPassword("customer-demo"))).status, 401);
      assert.equal((await authenticate(newPassword)).status, 200);
    });
    await t.test("lost-response retries do not reset again or revoke later sessions; changed payload is rejected", async () => {
      const before = {credential: await credential(), revision: await revision(), sessions: await count("sessions"), audits: await count("audits")};
      assert.equal((await request(valid)).status, 200);
      assert.equal((await request({...valid, reason: "Different reason"})).status, 409);
      assert.equal((await request({...valid, newPassword: "Changed-reset-password-v9!"})).status, 409);
      assert.deepEqual({credential: await credential(), revision: await revision(), sessions: await count("sessions"), audits: await count("audits")}, before);
    });
    await t.test("replaying completed recovery never replaces a password subsequently chosen by its owner", async () => {
      await session("v9-customer-owner", "customer-demo");
      const ownerPassword = "Owner-chosen-password-v9!";
      assert.equal((await changeOwnPassword("v9-customer-owner", newPassword, ownerPassword)).status, 200);
      await session("v9-customer-after-change", "customer-demo");
      const before = {credential: await credential(), revision: await revision(), sessions: await count("sessions"), audits: await count("audits")};
      assert.equal((await request(valid)).status, 200);
      assert.deepEqual({credential: await credential(), revision: await revision(), sessions: await count("sessions"), audits: await count("audits")}, before);
      assert.equal(before.credential.hash, await hashPassword(ownerPassword, String(before.credential.salt)));
    });
    await t.test("concurrent retries of the same reset commit exactly once", async () => {
      const attempt = input({newPassword: "Concurrent-reset-password-v9!"});
      const before = {revision: await revision(), audits: await count("audits"), commands: await count("commands")};
      let arrived!: () => void, release!: () => void;
      const firstAtCommit = new Promise<void>(resolve => {arrived = resolve;});
      const continueFirst = new Promise<void>(resolve => {release = resolve;});
      intercept = async () => {arrived(); await continueFirst;};
      const first = request(attempt);
      await firstAtCommit;
      try {
        const second = await request(attempt);
        assert.equal(second.status, 200);
        await session("v9-customer-after-concurrent-reset", "customer-demo");
      } finally { release(); }
      assert.equal((await first).status, 200);
      assert.equal(await revision(), before.revision + 1);
      assert.equal(await count("audits"), before.audits + 1);
      assert.equal(await count("commands"), before.commands + 1);
      const stillSignedIn = await db.execute({sql: "SELECT id FROM sessions WHERE id=?", args: [await digest("v9-customer-after-concurrent-reset")]});
      assert.equal(stillSignedIn.rows.length, 1, "A concurrent retry must not revoke a session created after the first commit");
      assert.equal(await count("mutation_guards"), 0);
    });
    await t.test("audit storage failure rolls back credential replacement and session revocation", async child => {
      const before = {credential: await credential(), revision: await revision(), sessions: await count("sessions"), audits: await count("audits")};
      const logs = child.mock.method(console, "error", () => {});
      failAudit = true;
      try {assert.equal((await request(input())).status, 503);} finally {failAudit = false;}
      const recordedLogs = JSON.stringify(logs.mock.calls.map(call => call.arguments));
      assert.ok(!recordedLogs.includes(newPassword) && !recordedLogs.includes(demoPassword("admin")));
      assert.deepEqual({credential: await credential(), revision: await revision(), sessions: await count("sessions"), audits: await count("audits")}, before);
      assert.equal(await count("mutation_guards"), 0);
    });
    await t.test("a concurrent password change wins and cannot be overwritten by stale reset", async () => {
      const nextSalt = "v9-concurrent-salt", nextHash = await hashPassword("Concurrent-user-password-v9!", nextSalt);
      intercept = async () => { await db.execute({sql: "UPDATE credentials SET salt=?,hash=? WHERE user_id='customer-demo'", args: [nextSalt, nextHash]}); };
      assert.equal((await request(input())).status, 409);
      assert.deepEqual(await credential(), {salt: nextSalt, hash: nextHash});
    });
    await t.test("revoked administrator authority is rechecked inside the write transaction", async () => {
      const before = await credential();
      intercept = async () => { await db.execute("UPDATE users SET role='staf',payload=json_set(payload,'$.role','staf') WHERE id='admin'"); };
      assert.equal((await request(input())).status, 403);
      assert.deepEqual(await credential(), before);
      await db.execute("UPDATE users SET role='admin',payload=json_set(payload,'$.role','admin') WHERE id='admin'");
    });
    await t.test("administrator session revoked after reauthentication cannot authorize the reset", async () => {
      const before = {credential: await credential(), revision: await revision(), audits: await count("audits"), commands: await count("commands")};
      intercept = async () => {await db.execute({sql: "DELETE FROM sessions WHERE id=?", args: [await digest("v9-admin")]});};
      assert.equal((await request(input())).status, 401);
      assert.deepEqual({credential: await credential(), revision: await revision(), audits: await count("audits"), commands: await count("commands")}, before);
      assert.equal(await count("mutation_guards"), 0);
      await session("v9-admin", "admin");
    });
    await t.test("administrator password change during reset invalidates stale authority and preserves the target", async () => {
      const oldAdmin = await credential("admin");
      const before = {credential: await credential(), revision: await revision(), audits: await count("audits"), commands: await count("commands")};
      intercept = async () => {
        assert.equal((await changeOwnPassword("v9-admin", demoPassword("admin"), "Administrator-new-password-v9!")).status, 200);
      };
      assert.equal((await request(input())).status, 401);
      assert.notDeepEqual(await credential("admin"), oldAdmin);
      assert.deepEqual({credential: await credential(), revision: await revision(), audits: await count("audits"), commands: await count("commands")}, before);
      assert.equal(await count("mutation_guards"), 0);
      await db.execute({sql: "UPDATE credentials SET salt=?,hash=? WHERE user_id='admin'", args: [oldAdmin.salt, oldAdmin.hash]});
      await session("v9-admin", "admin");
    });
    await t.test("recovery audit uses the Jakarta calendar date when UTC is still the previous day", async child => {
      child.mock.timers.enable({apis: ["Date"], now: new Date("2026-09-01T18:30:00Z")});
      const attempt = input({reason: "Pemeriksaan tanggal lokal Jakarta"});
      assert.equal((await request(attempt)).status, 200);
      const row = (await db.execute({sql: "SELECT payload FROM audits WHERE json_extract(payload,'$.description') LIKE ?", args: ["%Pemeriksaan tanggal lokal Jakarta"]})).rows[0];
      assert.ok(row);
      const audit = JSON.parse(String(row.payload));
      assert.equal(audit.at, "2026-09-01T18:30:00.000Z");
      assert.equal(audit.date, "2026-09-02");
    });
    await t.test("reauthentication failures are rate limited", async () => {
      const before = await credential();
      for (let i = 0; i < 8; i++) assert.equal((await request(input({currentPassword: "wrong-admin-password"}))).status, 400);
      assert.equal((await request(input())).status, 429);
      assert.deepEqual(await credential(), before);
    });
  } finally {
    db.close();
    if (oldSeed === undefined) delete process.env.DEMO_PASSWORD_SEED; else process.env.DEMO_PASSWORD_SEED = oldSeed;
  }
});
