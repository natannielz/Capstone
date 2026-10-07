import { build } from 'esbuild';
import { mkdirSync } from 'node:fs';
import { spawnSync } from 'node:child_process';

mkdirSync('.test', { recursive: true });
const outfile = '.test/pack-conversion-execution.test.mjs';
await build({ entryPoints: ['./tests/pack-conversion-execution.test.ts'], tsconfig: './tsconfig.json', outfile, bundle: true, platform: 'node', format: 'esm', target: 'node24', packages: 'external', logLevel: 'warning' });
const result = spawnSync(process.execPath, ['--test', outfile], { stdio: 'inherit', env: { ...process.env, NODE_ENV: 'test', NEXT_PUBLIC_UNIT_TOKO_RELEASE: 'full' } });
process.exitCode = result.status ?? 1;
