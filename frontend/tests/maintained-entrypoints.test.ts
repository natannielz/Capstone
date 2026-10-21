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
      // The full release renders a real cart; only its screen is stubbed, so the page file itself still runs.
      builder.onResolve({filter: /modules[\\/]ordering[\\/]CartWorkspace$/}, () => ({path: "cart", namespace: "test"}));
      builder.onLoad({filter: /.*/, namespace: "test"}, args => ({
        contents: args.path === "cart" ? "export function CartWorkspace() { return null; }"
          : "export function redirect(destination) { throw Object.assign(new Error('redirect'), {destination}); }", loader: "js",
      }));
    }}],
  });
  return import(`data:text/javascript;base64,${Buffer.from(result.outputFiles[0].text).toString("base64")}`);
}

async function destination(page: Page, next?: string | string[], id = "41", renders = false): Promise<string> {
  try { await page({searchParams: Promise.resolve({next}), params: Promise.resolve({id})}); }
  catch (error) {
    if (error instanceof Error && "destination" in error && typeof error.destination === "string") return error.destination;
    throw error;
  }
  if (renders) return "rendered";
  throw new Error("The compatibility page did not redirect.");
}

for (const profile of ["pilot", "full"] as const) {
  test(`${profile}: old entrypoints open the canonical page and keep the destination and query (D2, G-008, G-009)`, async () => {
    const routes = await loadRoutes(profile);
    const pilot = profile === "pilot", requests = pilot ? "/ordering/requests" : "/ordering/orders";
    const signIn = (returnPath: string) => `/identity/login?returnPath=${encodeURIComponent(returnPath)}`;
    assert.equal(await destination(routes.account), "/identity/account");
    assert.equal(await destination(routes.orders), requests);
    for (const id of ["41", "9223372036854775807"]) assert.equal(await destination(routes.order, undefined, id), pilot ? requests : "/ordering/orders?order=" + id);
    for (const id of ["order_old", "not-a-current-order", "9223372036854775808"]) assert.equal(await destination(routes.order, undefined, id), requests);
    assert.equal(await destination(routes.cart, undefined, "41", true), pilot ? requests : "rendered");
    assert.equal(await destination(routes.checkout), pilot ? requests : "/ordering/checkout");

    for (const [name, login] of [["login", routes.login], ["customer", routes.customerLogin], ["staff", routes.staffLogin]] as const) {
      for (const [raw, expected] of [
        ["/account", "/identity/account"], ["/workspace?view=admin", "/workspace?view=admin"], ["/workspace", "/workspace"],
        ["/shop?category=pantry", "/shop?category=pantry"],
        ["/account/orders/41?orderId=41#details", pilot ? "/ordering/requests?orderId=41" : "/ordering/orders?order=41&orderId=41"],
        ["/cart", pilot ? requests : "/cart"],
        ["/checkout?draftId=41", pilot ? "/ordering/requests?draftId=41" : "/ordering/checkout?draftId=41"],
        ["/ordering/requests", "/ordering/requests"],
      ]) {
        assert.equal(await destination(login, raw), signIn(expected), raw);
      }
      if (!pilot) {
        assert.equal(await destination(login, "/ordering/orders?orderId=41"), signIn("/ordering/orders?orderId=41"));
        assert.equal(await destination(login, "/shop/00000065-0000-4000-8000-000000000101"), signIn("/shop/00000065-0000-4000-8000-000000000101"));
      }
      // Without a usable destination the person still reaches sign-in; the staff door then leads to the staff home.
      const fallback = name === "staff" ? signIn("/workspace") : "/identity/login";
      for (const raw of [undefined, ["/shop", "/checkout"], "https://example.com", "//example.com", "/\\example.com", "/shop\n", "/api/state", "/auth/login", "/customer/login", "/staff/login", "/login", "/shop/%2f%2fevil.com", "/shop/../api/state", "/%2e%2e/checkout", "/shop/legacy-product?sku=OLD", "/" + "a".repeat(2048)]) {
        assert.equal(await destination(login, raw), fallback, String(raw));
      }
    }
  });
}
