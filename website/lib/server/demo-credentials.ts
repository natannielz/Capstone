import { createHmac } from "node:crypto";
export function demoPassword(id:string){
  const seed=process.env.DEMO_PASSWORD_SEED;
  if(!seed||seed.length<32)throw new Error("DEMO_PASSWORD_SEED must contain at least 32 characters.");
  return "Toko!"+createHmac("sha256",seed).update(id).digest("base64url").slice(0,14);
}
