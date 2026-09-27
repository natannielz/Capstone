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
const testEnv={...process.env,NODE_ENV:"test"};
const result=spawnSync(process.execPath,["--test",...outputs],{stdio:"inherit",env:testEnv});
if(result.status!==0){process.exitCode=result.status??1;}
else {
 const repository=outputs.find(path=>path.endsWith("/repository.test.mjs"));
 if(!repository)throw new Error("Repository regression bundle is missing.");
 const future=spawnSync(process.execPath,["--test",repository],{stdio:"inherit",env:{...testEnv,UNIT_TOKO_TEST_DATE:"2031-09-27"}});
 process.exitCode=future.status??1;
}
