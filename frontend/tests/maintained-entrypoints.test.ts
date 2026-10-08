import {test} from "node:test";
import assert from "node:assert/strict";
import {existsSync} from "node:fs";
import {fileURLToPath} from "node:url";
import {build} from "esbuild";

type Page = (props: {searchParams: Promise<{next?: string | string[]}>; params: Promise<{id: string}>}) => unknown;
type Routes = Record<"login" | "customerLogin" | "staffLogin" | "account" | "orders" | "order" | "cart" | "checkout", Page>;

test("retired business, identity and report endpoints cannot expose a second runtime", () => {
  for (const route of ["state", "commands", "catalog", "session", "auth/login", "auth/logout", "reports",
    "profile", "profile/avatar", "admin/password-reset", "attachments", "attachments/[id]", "documents/[kind]/[id]", "test-environment"]) {
    for (const extension of ["ts", "tsx", "js", "jsx"]) {
      assert.equal(existsSync(new URL(`../src/app/api/${route}/route.${extension}`, import.meta.url)), false, `/api/${route}`);
    }
  }
});

async function loadRoutes(profile: "pilot" | "full"): Promise<Routes> {
  // Execute the actual server pages in both release profiles. Only Next's redirect
  // transport is replaced; resolving an old session/storefront dependency fails the test.
  const result = await build({
    stdin: {
      contents: `
        export {default as login} from './src/app/login/page';
        export {default as customerLogin} from './src/app/customer/login/page';
        export {default as staffLogin} from './src/app/staff/login/page';
        export {default as account} from './src/app/account/page';
        export {default as orders} from './src/app/account/orders/page';
        export {default as order} from './src/app/account/orders/[id]/page';
        export {default as cart} from './src/app/cart/page';
        export {default as checkout} from './src/app/checkout/page';`,
      resolveDir: fileURLToPath(new URL("..", import.meta.url)), loader: "ts",
    },
    bundle: true, write: false, platform: "node", format: "esm", target: "node24",
    define: {"process.env.NEXT_PUBLIC_UNIT_TOKO_RELEASE": JSON.stringify(profile)},
    plugins: [{name: "maintained-pages", setup(builder) {
      builder.onResolve({filter: /page-session|backend[\\/]legacy|components[\\/](?:login|customer-account|shop)/}, () => {
        throw new Error("A compatibility page must not load legacy authentication or storefront state.");
      });
      builder.onResolve({filter: /^next\/navigation$/}, () => ({path: "redirect", namespace: "test"}));
      builder.onLoad({filter: /.*/, namespace: "test"}, () => ({
        contents: "export function redirect(destination) { throw Object.assign(new Error('redirect'), {destination}); }", loader: "js",
      }));
    }}],
  });
  return import(`data:text/javascript;base64,${Buffer.from(result.outputFiles[0].text).toString("base64")}`);
}

async function destination(page: Page, next?: string | string[], id = "41"): Promise<string> {
  try { await page({searchParams: Promise.resolve({next}), params: Promise.resolve({id})}); }
  catch (error) {
    if (error instanceof Error && "destination" in error && typeof error.destination === "string") return error.destination;
    throw error;
  }
  throw new Error("The compatibility page did not redirect.");
}

for (const profile of ["pilot", "full"] as const) {
  test(`${profile}: old entrypoints open maintained screens without selecting legacy records`, async () => {
    const routes = await loadRoutes(profile);
    const requests = profile === "pilot" ? "/ordering/requests" : "/ordering/orders";
    assert.equal(await destination(routes.account), "/identity/account");
    assert.equal(await destination(routes.orders, "/account/orders?orderId=41"), requests);
    for (const id of ["41", "9223372036854775807", "order_old", "not-a-current-order"]) {
      assert.equal(await destination(routes.order, undefined, id), requests);
    }
    assert.equal(await destination(routes.cart), profile === "pilot" ? requests : "/ordering/drafts");
    assert.equal(await destination(routes.checkout), profile === "pilot" ? requests : "/ordering/checkout");

    for (const login of [routes.login, routes.customerLogin, routes.staffLogin]) {
      for (const [raw, expected] of [
        ["/account", "/identity/account"], ["/workspace?view=admin", "/identity/account"],
        ["/shop?category=pantry", "/shop"], ["/shop/legacy-product?sku=OLD", "/shop"],
        ["/account/orders/41?orderId=41#details", requests],
        ["/cart", profile === "pilot" ? requests : "/ordering/drafts"],
        ["/checkout?draftId=41", profile === "pilot" ? requests : "/ordering/checkout"],
        ["/ordering/requests", "/ordering/requests"],
        ["/ordering/orders?orderId=41", profile === "pilot" ? "/identity/account" : "/ordering/orders"],
      ]) {
        assert.equal(await destination(login, raw), `/identity/login?returnPath=${encodeURIComponent(expected)}`, raw);
      }
      for (const raw of [undefined, ["/shop", "/checkout"], "https://example.com", "//example.com", "/\\example.com", "/shop\n", "/api/state", "/auth/login", "/customer/login", "/staff/login", "/login", "/shop/%2f%2fevil.com", "/shop/../api/state", "/%2e%2e/checkout", "/" + "a".repeat(2048)]) {
        assert.equal(await destination(login, raw), "/identity/login?returnPath=%2Fidentity%2Faccount", String(raw));
      }
    }
  });
}
