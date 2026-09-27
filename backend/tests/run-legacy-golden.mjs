import { createRequire } from "node:module";
import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename, join, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
const require=createRequire(new URL("../../frontend/package.json",import.meta.url));
const { build }=require("esbuild");
const root=fileURLToPath(new URL("../",import.meta.url));
const output=mkdtempSync(join(tmpdir(),"unit-toko-golden-"));
try {
  const outfile=join(output,"legacy-golden.test.mjs");
  await build({entryPoints:[fileURLToPath(new URL("./legacy-golden.test.ts",import.meta.url))],outfile,bundle:true,platform:"node",format:"esm",target:"node24",packages:"external",logLevel:"warning"});
  const fixture=fileURLToPath(new URL("./Fixtures/legacy-scenarios.json",import.meta.url));
  const result=spawnSync(process.execPath,process.argv.includes("--write-fixture")?[outfile,"--write-fixture"]:["--test",outfile],{stdio:"inherit",cwd:root,env:{...process.env,NODE_ENV:"test",UNIT_TOKO_GOLDEN_FIXTURE:fixture}});
  process.exitCode=result.status??1;
} finally {
  const tempRoot=resolve(tmpdir())+sep;
  if(!resolve(output).startsWith(tempRoot)||!basename(output).startsWith("unit-toko-golden-"))throw new Error("Refusing to remove an unexpected output path");
  rmSync(output,{recursive:true,force:true});
}
