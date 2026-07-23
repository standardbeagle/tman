#!/usr/bin/env node
const { spawnSync, execFileSync } = require('node:child_process');
const { existsSync } = require('node:fs');
const path = require('node:path');
const os = require('node:os');

const bin = path.join(__dirname, '..', 'vendor', os.platform() === 'win32' ? 'tman.exe' : 'tman');

if (!existsSync(bin)) {
  try {
    execFileSync(process.execPath, [path.join(__dirname, '..', 'install.js')], { stdio: 'inherit' });
  } catch {
    console.error('tman: binary download failed');
    process.exit(1);
  }
}

const r = spawnSync(bin, process.argv.slice(2), { stdio: 'inherit' });
if (r.error) {
  console.error(`tman: ${r.error.message} (try reinstalling)`);
  process.exit(127);
}
process.exit(r.status ?? 1);
