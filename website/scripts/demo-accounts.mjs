import {createHmac} from "node:crypto";
import {writeFileSync,mkdirSync} from "node:fs";
const seed=process.env.DEMO_PASSWORD_SEED;if(!seed)throw Error("Set DEMO_PASSWORD_SEED");
const ids=["pic-a","pic-b","kepala","staf","kurir","laporan","penagihan","pimpinan","akuntansi","admin"];
mkdirSync(".data",{recursive:true});writeFileSync(".data/demo-accounts.json",JSON.stringify(ids.map(id=>({email:id+"@unit-toko.demo",password:"Toko!"+createHmac("sha256",seed).update(id).digest("base64url").slice(0,14)})),null,2));console.log("Credentials saved privately to .data/demo-accounts.json");
