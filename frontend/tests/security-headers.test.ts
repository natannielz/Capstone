import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { securityHeaders } from "../src/lib/security-headers";

const value = (headers: { key: string; value: string }[], key: string) => headers.find(header => header.key === key)?.value;

// ASVS 5.0 L1 (V3 web frontend): every page carries a CSP, nosniff, anti-framing, a referrer policy and a permissions policy.
test("production pages carry a strict CSP and the baseline security headers", () => {
  const headers = securityHeaders({ development: false });
  const csp = value(headers, "Content-Security-Policy")!;
  for (const directive of ["default-src 'self'", "object-src 'none'", "base-uri 'self'", "frame-ancestors 'none'", "img-src 'self' blob: data:", "connect-src 'self'"])
    assert.ok(csp.includes(directive), directive);
  assert.doesNotMatch(csp, /unsafe-eval/);
  assert.doesNotMatch(csp, /\n/);
  assert.equal(value(headers, "X-Content-Type-Options"), "nosniff");
  assert.equal(value(headers, "X-Frame-Options"), "DENY");
  assert.equal(value(headers, "Referrer-Policy"), "strict-origin-when-cross-origin");
  // Camera stays available to this origin for barcode scanning and payment-proof capture.
  assert.equal(value(headers, "Permissions-Policy"), "camera=(self), microphone=(), geolocation=(), payment=(), usb=()");
  assert.match(value(headers, "Strict-Transport-Security")!, /^max-age=\d{8,}; includeSubDomains$/);
});

test("development allows React's eval-based debugging but never framing", () => {
  const csp = value(securityHeaders({ development: true }), "Content-Security-Policy")!;
  assert.match(csp, /script-src [^;]*'unsafe-eval'/);
  assert.ok(csp.includes("frame-ancestors 'none'"));
  assert.equal(value(securityHeaders({ development: true }), "Strict-Transport-Security"), undefined);
});

test("next.config applies the headers to every route", () => {
  const config = readFileSync("next.config.ts", "utf8");
  assert.match(config, /async headers\(\)/);
  assert.match(config, /source: "\/\(\.\*\)"/);
  assert.match(config, /securityHeaders\(/);
});

// Sign-in, step-up and sign-out are form posts the API redirects to the identity provider; Chrome applies form-action
// to that redirect, so the provider origin is allowed explicitly (or any HTTPS target until it is configured).
test("form-action allows this origin and the identity provider for sign-in redirects", () => {
  const configured = value(securityHeaders({ development: false, identityOrigin: "https://unit-toko.au.auth0.com" }), "Content-Security-Policy")!;
  assert.ok(configured.includes("form-action 'self' https://unit-toko.au.auth0.com;"), configured);
  const fallback = value(securityHeaders({ development: false }), "Content-Security-Policy")!;
  assert.ok(fallback.includes("form-action 'self' https:;"), fallback);
  assert.throws(() => securityHeaders({ development: false, identityOrigin: "http://insecure.example" }));
  assert.throws(() => securityHeaders({ development: false, identityOrigin: "https://idp.example/path" }));
});
