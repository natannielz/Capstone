import { build } from "esbuild";
import { spawnSync } from "node:child_process";
import { mkdirSync, readdirSync } from "node:fs";
mkdirSync(".test",{recursive:true});
const files=readdirSync("tests").filter(name=>name.endsWith(".test.ts")).sort();
const outputs=await Promise.all(files.map(async file=>{
 const outfile=`.test/${file.replace(/\.ts$/,".mjs")}`;
 await build({entryPoints:[`tests/${file}`],outfile,bundle:true,platform:"node",format:"esm",target:"node24",packages:"external",logLevel:"warning"});
 return outfile;
}));
const result=spawnSync(process.execPath,["--test",...outputs],{stdio:"inherit"});process.exitCode=result.status??1;
