import {test} from "node:test";
import assert from "node:assert/strict";
import {ApiError, logoutSession, requestJson, singleFlight} from "../lib/client/requests";

test("logout fails honestly on server error and network loss", async () => {
  await assert.rejects(logoutSession(async () => Response.json({error:"Storage unavailable"}, {status:503})), /Sesi Anda mungkin masih aktif/);
  await assert.rejects(logoutSession(async () => {throw new TypeError("Failed to fetch");}), /Belum berhasil keluar/);
  await assert.doesNotReject(logoutSession(async () => Response.json({ok:true})));
});

test("a slow refresh is shared and a failed refresh can be retried", async () => {
  const run = singleFlight<number>();
  let calls = 0;
  let finish: (value: number) => void = () => {};
  const operation = () => {calls++; return new Promise<number>(resolve => {finish = resolve;});};
  const first = run(operation), duplicate = run(operation);
  await Promise.resolve();
  assert.equal(calls, 1);
  finish(7);
  assert.deepEqual(await Promise.all([first, duplicate]), [7, 7]);
  await assert.rejects(run(async () => {throw new Error("service failed");}), /service failed/);
  assert.equal(await run(async () => 8), 8);
});

test("request errors preserve status for expired sessions and explain unreadable responses", async () => {
  await assert.rejects(requestJson("/api/state", {}, async () => Response.json({error:"Sesi berakhir"}, {status:401})), error => error instanceof ApiError && error.status === 401);
  await assert.rejects(requestJson("/api/state", {}, async () => new Response("<html>upstream failed</html>", {status:502})), /belum dapat dibaca/);
});
