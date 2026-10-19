import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

// The work summary and the contact panel are siblings remounted per account. Sharing one key made React warn that
// children "may be duplicated and/or omitted"; each sibling needs its own key derived from the account.
test("account page siblings keyed by account use distinct keys", () => {
  const source = readFileSync("src/modules/identity/IdentityAccount.tsx", "utf8");
  const keys = [...source.matchAll(/key=\{([^}]*session\.account\.userId[^}]*)\}/g)].map(match => match[1].replace(/\s+/g, ""));
  assert.ok(keys.length >= 2, "both panels are keyed by the account");
  assert.equal(new Set(keys).size, keys.length, `duplicate keys: ${keys.join(", ")}`);
});
