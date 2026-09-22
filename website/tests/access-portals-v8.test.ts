import {test} from "node:test";
import assert from "node:assert/strict";
import {createClient, type Client, type InValue} from "@libsql/client";
import {ROLES} from "../lib/domain/accounts";
import {loginForDestination, safeLoginDestination} from "../lib/domain/navigation";
import {migrateDatabase, setTestDatabase, type Database, type Statement} from "../lib/server/database";
import {ensureSeed} from "../lib/server/repository";
import {demoPassword} from "../lib/server/demo-credentials";
import {POST as loginPOST} from "../app/api/auth/login/route";

test("v8 legacy links select the matching portal and retain safe shopping or staff context", () => {
  for (const next of ["/shop?category=pantry", "/shop/kopi?sku=kopi-p3", "/cart", "/checkout?mode=buy", "/account", "/account/orders/order_1"]) {
    assert.equal(loginForDestination(next), `/customer/login?next=${encodeURIComponent(next)}`);
  }
  const next = "/workspace?view=admin";
  assert.equal(loginForDestination(next), `/staff/login?next=${encodeURIComponent(next)}`);
  for (const bad of [null, "", "https://evil.invalid", "//evil.invalid", "/\\evil.invalid", "/api/state", "/shop/../../api/state", "/customer/login", "/staff/login", "/checkout\n", "/accounting", "/shop/%2f%2fevil.invalid"]) {
    assert.equal(loginForDestination(bad), "/staff/login");
  }
});

test("v8 actual role governs post-login navigation in either portal", () => {
  for (const role of ROLES) {
    assert.equal(safeLoginDestination(null, role), role === "customer" ? "/shop" : "/workspace");
    assert.equal(safeLoginDestination("/customer/login", role), role === "customer" ? "/shop" : "/workspace");
    assert.equal(safeLoginDestination("/staff/login", role), role === "customer" ? "/shop" : "/workspace");
    assert.equal(safeLoginDestination("/checkout", role), role === "customer" ? "/checkout" : "/workspace");
  }
  assert.equal(safeLoginDestination("/workspace?view=admin", "customer"), "/shop");
  assert.equal(safeLoginDestination("/workspace?view=admin", "admin"), "/workspace?view=admin");
});

class Query implements Statement {
  constructor(readonly client: Client, readonly sql: string, readonly args: InValue[] = []) {}
  bind(...values: unknown[]) { return new Query(this.client, this.sql, values as InValue[]); }
  async first<T>() { return (await this.client.execute({sql: this.sql, args: this.args})).rows[0] as T || null; }
  run() { return this.client.execute({sql: this.sql, args: this.args}); }
}
function adapter(client: Client): Database {
  return {
    prepare: sql => new Query(client, sql),
    async batch(statements) {
      return (await client.batch(statements.map(statement => {
        const query = statement as Query;
        return {sql: query.sql, args: query.args};
      }), "write")).map(result => ({results: result.rows as Record<string, unknown>[]}));
    },
  };
}

test("v8 login portal checks real credentials before rejecting a mismatched role without issuing a session", async () => {
  const oldSeed = process.env.DEMO_PASSWORD_SEED;
  process.env.DEMO_PASSWORD_SEED = "isolated-v8-portal-test-only-not-a-live-secret";
  const client = createClient({url: ":memory:"});
  const request = (value: unknown) => loginPOST(new Request("http://localhost/api/auth/login", {method: "POST", headers: {"Content-Type": "application/json"}, body: JSON.stringify(value)}));
  const count = async () => Number((await client.execute("SELECT COUNT(*) AS count FROM sessions")).rows[0].count);
  try {
    await migrateDatabase(client); setTestDatabase(adapter(client)); await ensureSeed();
    for (const {id, role} of (await client.execute("SELECT id, role FROM users")).rows) {
      const email = id === "customer-demo" ? "customer@unit-toko.demo" : `${id}@unit-toko.demo`;
      const right = role === "customer" ? "customer" : "staff";
      const wrong = right === "customer" ? "staff" : "customer";
      const before = await count();
      const invalidPassword = await request({email, password: "wrong-password", portal: wrong});
      assert.equal(invalidPassword.status, 401);
      assert.deepEqual(await invalidPassword.json(), {error: "Email atau kata sandi tidak sesuai."});
      const mismatch = await request({email, password: demoPassword(String(id)), portal: wrong});
      assert.equal(mismatch.status, 403, String(id));
      assert.match((await mismatch.json() as {error: string}).error, right === "customer" ? /halaman pelanggan/ : /halaman staf & admin/);
      assert.equal(mismatch.headers.get("set-cookie"), null);
      assert.equal(await count(), before);
      const matched = await request({email, password: demoPassword(String(id)), portal: right});
      assert.equal(matched.status, 200, String(id));
      assert.match(matched.headers.get("set-cookie") || "", /HttpOnly/);
      assert.equal((await matched.json() as {user: {role: string}}).user.role, role);
      assert.equal(await count(), before + 1);
    }
    const before = await count();
    for (const portal of [null, true, 1, {}, [], "", "admin", "CUSTOMER"]) {
      const response = await request({email: "customer@unit-toko.demo", password: demoPassword("customer-demo"), portal});
      assert.equal(response.status, 400);
      assert.equal(response.headers.get("set-cookie"), null);
    }
    for (const malformed of [null, [], "not-an-object"]) assert.equal((await request(malformed)).status, 400);
    assert.equal(await count(), before);
    const compatible = await request({email: "customer@unit-toko.demo", password: demoPassword("customer-demo")});
    assert.equal(compatible.status, 200, "existing API clients can omit portal");
  } finally {
    client.close();
    if (oldSeed === undefined) delete process.env.DEMO_PASSWORD_SEED; else process.env.DEMO_PASSWORD_SEED = oldSeed;
  }
});
