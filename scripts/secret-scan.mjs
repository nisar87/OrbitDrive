#!/usr/bin/env node
// Minimal tracked-file secret scan (private keys, cloud credentials, tokens). Not a substitute for a full scanner.
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
const patterns = [
  [/-----BEGIN (RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----/, 'private key'],
  [/"type"\s*:\s*"service_account"/, 'Google service-account key'],
  [/AIza[0-9A-Za-z_-]{35}/, 'Google API key'],
  [/gh[pousr]_[A-Za-z0-9]{36,}/, 'GitHub token'],
  [/AKIA[0-9A-Z]{16}/, 'AWS access key'],
  [/xox[baprs]-[0-9A-Za-z-]{10,}/, 'Slack token'],
  [/[A-Za-z]:\\Users\\(?!Public|Default)[A-Za-z0-9._-]+\\/, 'personal Windows path'],
];
let failures = 0;
for (const file of execFileSync('git', ['ls-files'], { encoding: 'utf8' }).split('\n').filter(Boolean)) {
  if (/package-lock\.json$|\.png$|secret-scan\.mjs$/.test(file)) continue;
  let text;
  try { text = readFileSync(file, 'utf8'); } catch { continue; }
  for (const [pattern, label] of patterns) if (pattern.test(text)) { console.error(`${file}: possible ${label}`); failures++; }
}
if (failures) process.exit(1);
console.log('secret scan: no findings in tracked files');
