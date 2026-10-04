import { build } from "esbuild";
import { spawnSync } from "node:child_process";
import { mkdirSync, readdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
mkdirSync(".test",{recursive:true});
const files=readdirSync("tests").filter(name=>name.endsWith(".test.ts")).sort();
const outputs=await Promise.all(files.map(async file=>{
 const outfile=`.test/${file.replace(/\.ts$/,".mjs")}`;
 await build({entryPoints:[`tests/${file}`],outfile,bundle:true,platform:"node",format:"esm",target:"node24",packages:"external",logLevel:"warning"});
 return outfile;
}));
// Retained full-workflow regressions run explicitly in full mode; pilot tests check the pure allowlist.
const testEnv={...process.env,NODE_ENV:"test",NEXT_PUBLIC_UNIT_TOKO_RELEASE:"full",UNIT_TOKO_LEGACY_MIGRATIONS:fileURLToPath(new URL("../../backend/legacy/drizzle/",import.meta.url))};
const result=spawnSync(process.execPath,["--test",...outputs],{stdio:"inherit",env:testEnv});
if(result.status!==0){process.exitCode=result.status??1;}
else {
 const repository=outputs.find(path=>path.endsWith("/repository.test.mjs"));
 if(!repository)throw new Error("Repository regression bundle is missing.");
 const future=spawnSync(process.execPath,["--test",repository],{stdio:"inherit",env:{...testEnv,UNIT_TOKO_TEST_DATE:"2031-09-27"}});
 process.exitCode=future.status??1;
}


if (!process.exitCode) {
 const golden=spawnSync(process.execPath,[fileURLToPath(new URL("../../backend/tests/run-legacy-golden.mjs",import.meta.url))],{stdio:"inherit",env:testEnv});
 process.exitCode=golden.status??1;
}
