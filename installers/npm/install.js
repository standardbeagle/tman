#!/usr/bin/env node
const { execFileSync } = require('node:child_process');
const { createWriteStream, chmodSync, mkdirSync, existsSync } = require('node:fs');
const { pipeline } = require('node:stream/promises');
const path = require('node:path');
const os = require('node:os');

const pkg = require('./package.json');

const ASSETS = {
  'linux-x64': 'tman-linux-x64.tar.gz',
  'linux-arm64': 'tman-linux-arm64.tar.gz',
  'darwin-x64': 'tman-osx-x64.tar.gz',
  'darwin-arm64': 'tman-osx-arm64.tar.gz',
  'win32-x64': 'tman-win-x64.zip',
};

const key = `${os.platform()}-${os.arch()}`;
const asset = ASSETS[key];
if (!asset) {
  console.error(`tman: unsupported platform ${key}`);
  process.exit(1);
}

const url = `https://github.com/standardbeagle/tman/releases/download/v${pkg.version}/${asset}`;
const vendor = path.join(__dirname, 'vendor');
const archive = path.join(os.tmpdir(), asset);

async function main() {
  mkdirSync(vendor, { recursive: true });
  console.log(`tman: downloading ${url}`);
  const res = await fetch(url, { redirect: 'follow' });
  if (!res.ok) {
    console.error(`tman: download failed: ${res.status} ${res.statusText}`);
    process.exit(1);
  }
  await pipeline(res.body, createWriteStream(archive));

  const isZip = asset.endsWith('.zip');
  const args = isZip ? ['-xf', archive, '-C', vendor] : ['-xzf', archive, '-C', vendor];
  execFileSync('tar', args, { stdio: 'inherit' });

  const bin = path.join(vendor, os.platform() === 'win32' ? 'tman.exe' : 'tman');
  if (!existsSync(bin)) {
    console.error('tman: archive did not contain the binary');
    process.exit(1);
  }
  if (os.platform() !== 'win32') chmodSync(bin, 0o755);
  console.log(`tman: installed to ${bin}`);
}

main().catch((e) => {
  console.error(`tman: install failed: ${e.message}`);
  process.exit(1);
});
