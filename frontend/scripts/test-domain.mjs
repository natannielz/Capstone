import { build } from "esbuild";
import { spawnSync } from "node:child_process";
import { mkdirSync, readdirSync, renameSync } from "node:fs";
mkdirSync(".test",{recursive:true});
const files=readdirSync("tests").filter(name=>name.endsWith(".test.ts")).sort();
// A bundle whose source test was deleted must never run again.
const current=new Set(files.map(file=>file.replace(/\.ts$/,".mjs")));
for(const name of readdirSync(".test").filter(name=>name.endsWith(".test.mjs")&&!current.has(name))){mkdirSync(".test/stale-bundles",{recursive:true});renameSync(`.test/${name}`,`.test/stale-bundles/${name}`);}
const outputs=await Promise.all(files.map(async file=>{
 const outfile=`.test/${file.replace(/\.ts$/,".mjs")}`;
 await build({entryPoints:[`tests/${file}`],outfile,bundle:true,platform:"node",format:"esm",target:"node24",packages:"external",alias:{"next/link":"next/link.js"},logLevel:"warning"});
 return outfile;
}));
// Retained full-workflow regressions run explicitly in full mode; pilot tests check the pure allowlist.
const testEnv={...process.env,NODE_ENV:"test",NEXT_PUBLIC_UNIT_TOKO_RELEASE:"full"};
const result=spawnSync(process.execPath,["--test",...outputs],{stdio:"inherit",env:testEnv});
process.exitCode=result.status??1;
