import {createHmac} from "node:crypto";
import {writeFileSync,mkdirSync} from "node:fs";
const seed=process.env.DEMO_PASSWORD_SEED;if(!seed||seed.length<32)throw Error("Set DEMO_PASSWORD_SEED with at least 32 characters");
const ids=["customer-demo","pic-a","pic-b","kepala","staf","kurir","laporan","penagihan","pimpinan","akuntansi","admin"];
mkdirSync(".data",{recursive:true});writeFileSync(".data/demo-accounts.json",JSON.stringify(ids.map(id=>({email:id==="customer-demo"?"customer@unit-toko.demo":id+"@unit-toko.demo",password:"Toko!"+createHmac("sha256",seed).update(id).digest("base64url").slice(0,14)})),null,2));console.log("Initial demo credentials saved privately to .data/demo-accounts.json. Existing database passwords are not changed.");
