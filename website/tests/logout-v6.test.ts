import {test} from "node:test";
import assert from "node:assert/strict";
import {POST} from "../app/api/auth/logout/route";
import {setTestDatabase, type Statement} from "../lib/server/database";
import {digest} from "../lib/server/security";

test("logout clears browser cookie only after server revocation succeeds; failures remain retryable",async()=>{
  const tokens=new Set([await digest("session-for-v6-test")]);
  let fail=true,calls=0;
  setTestDatabase({prepare(sql:string){
    assert.match(sql,/DELETE FROM sessions/);
    let id="";
    const statement:Statement={bind(...values){id=String(values[0]);return statement;},async first(){return null;},async run(){calls++;if(fail)throw new Error("Expected simulated session storage outage");tokens.delete(id);return {};}};
    return statement;
  },async batch(){return [];}});
  const request=()=>new Request("https://unit-toko.test/api/auth/logout",{method:"POST",headers:{origin:"https://unit-toko.test",cookie:"toko_session=session-for-v6-test"}});
  const failed=await POST(request());
  assert.equal(failed.status,503);assert.equal(failed.headers.get("set-cookie"),null);assert.equal(tokens.size,1);
  fail=false;
  const success=await POST(request());
  assert.equal(success.status,200);assert.equal(tokens.size,0);assert.match(success.headers.get("set-cookie")||"",/Max-Age=0/);assert.match(success.headers.get("set-cookie")||"",/HttpOnly.*Secure/);
  const blocked=await POST(new Request("https://unit-toko.test/api/auth/logout",{method:"POST",headers:{origin:"https://unrelated.test",cookie:"toko_session=session-for-v6-test"}}));
  assert.equal(blocked.status,403);assert.equal(calls,2);
});
