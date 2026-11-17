#!/usr/bin/env node
// Dependency audit gate for one npm package directory.
//   1. Production dependencies (what can ship): any high/critical advisory fails. No exceptions exist for these.
//   2. All dependencies incl. build/test tooling: a high/critical advisory fails unless it is covered by a valid
//      entry in security/audit-exceptions.json. An entry is valid only while ALL of these hold:
//        - same advisory id, same package, same directory
//        - not expired, and not longer than 30 days from the day it was added
//        - the package is reached only through the packages named in allowedVia
//        - the registry's latest release of the package is still the one that was reviewed
//          (any newer release forces a re-review: a fix may exist)
//   Accepted advisories are printed as warnings on every run so they stay visible.
// Usage: node scripts/audit-check.mjs <package-dir>
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join, relative, resolve } from 'node:path';

const MAX_DAYS = 30;
const repo = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const serious = (severity) => severity === 'high' || severity === 'critical';

/** Every high/critical root advisory in an `npm audit --json` report, with the packages that pull it in. */
export function rootAdvisories(vulnerabilities) {
  const dependents = (name, seen = new Set()) => {
    for (const [other, entry] of Object.entries(vulnerabilities)) {
      if (seen.has(other) || !entry.via.some((via) => via === name)) continue;
      seen.add(other);
      dependents(other, seen);
    }
    return seen;
  };
  const found = new Map();
  for (const [name, entry] of Object.entries(vulnerabilities)) {
    for (const via of entry.via) {
      if (typeof via === 'string' || !serious(via.severity)) continue;   // strings are dependency edges, handled above
      const id = /GHSA-[0-9a-z]{4}-[0-9a-z]{4}-[0-9a-z]{4}/.exec(via.url ?? '')?.[0] ?? `unidentified:${via.url ?? via.title ?? name}`;
      found.set(`${id}/${name}`, { id, package: name, title: via.title ?? '', severity: via.severity, via: [...dependents(name)].sort() });
    }
  }
  return [...found.values()];
}

const days = (from, to) => Math.round((Date.parse(to) - Date.parse(from)) / 86_400_000);
const isDate = (text) => typeof text === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(text) && !Number.isNaN(Date.parse(text));

/** Why an exception does not cover a finding, or null when it does. `latestOf(package)` is the registry's latest version. */
export function refusal(finding, exception, directory, today, latestOf) {
  if (!exception) return 'not on the exception list';
  if (exception.package !== finding.package) return `exception is for package ${exception.package}`;
  if (exception.directory !== directory) return `exception applies to ${exception.directory} only`;
  if (!exception.reason || !exception.authorisedBy) return 'exception has no reason or no owner authorisation';
  if (!isDate(exception.added) || !isDate(exception.expires)) return 'exception needs real added/expires dates';
  if (days(exception.added, exception.expires) > MAX_DAYS || days(exception.added, exception.expires) < 0) return `exception runs longer than ${MAX_DAYS} days`;
  if (exception.added > today) return 'exception is dated in the future';
  if (exception.expires < today) return `exception expired on ${exception.expires}`;
  const stray = finding.via.filter((name) => !(exception.allowedVia ?? []).includes(name));
  if (stray.length > 0) return `now also reached through ${stray.join(', ')}, which was not reviewed`;
  const latest = latestOf(finding.package);
  if (latest !== exception.reviewedLatest) return `${finding.package} ${latest} has been released since the review of ${exception.reviewedLatest}: check for a fix and update`;
  return null;
}

/** Splits findings into blocking and accepted. */
export function judge(findings, exceptions, directory, today, latestOf) {
  const blocking = [], accepted = [];
  for (const finding of findings) {
    const exception = exceptions.find((e) => e.advisory === finding.id);
    const why = refusal(finding, exception, directory, today, latestOf);
    if (why === null) accepted.push({ ...finding, expires: exception.expires });
    else blocking.push({ ...finding, why });
  }
  return { blocking, accepted };
}

function main(target) {
  const dir = resolve(target);
  const label = relative(repo, dir).replaceAll('\\', '/');
  const npm = (args) => {
    try { return execFileSync('npm', args, { cwd: dir, encoding: 'utf8', shell: process.platform === 'win32', stdio: ['ignore', 'pipe', 'inherit'], maxBuffer: 64 << 20 }); }
    catch (e) { if (typeof e.stdout === 'string' && e.stdout.trim().startsWith('{')) return e.stdout; throw e; }   // npm audit exits 1 when it finds something
  };
  const audit = (extra) => {
    const report = JSON.parse(npm(['audit', '--json', ...extra]));
    if (report.error || !report.vulnerabilities) throw new Error(`npm audit did not produce a report in ${label}: ${JSON.stringify(report.error ?? report).slice(0, 300)}`);
    return report.vulnerabilities;
  };
  const exceptions = JSON.parse(readFileSync(join(repo, 'security', 'audit-exceptions.json'), 'utf8')).exceptions;
  const today = new Date().toISOString().slice(0, 10);
  const latestOf = (name) => JSON.parse(npm(['view', name, 'version', '--json']));

  const shipped = rootAdvisories(audit(['--omit=dev']));
  const { blocking, accepted } = judge(rootAdvisories(audit([])), exceptions, label, today, latestOf);
  for (const f of shipped) console.log(`::error::${label}: SHIPPED dependency ${f.package} has ${f.severity} advisory ${f.id} (${f.title}). Shipped code gets no exceptions.`);
  for (const f of blocking) console.log(`::error::${label}: ${f.package} ${f.severity} ${f.id} (${f.title}) - ${f.why}`);
  for (const f of accepted) console.log(`::warning::${label}: ACCEPTED RISK until ${f.expires} (not fixed): ${f.package} ${f.id} (${f.title}), build/test tooling via ${f.via.join(', ')}. See docs/security/accepted-risks.md`);
  if (shipped.length > 0 || blocking.length > 0) { console.log(`${label}: dependency audit FAILED`); process.exit(1); }
  console.log(`${label}: no blocking high/critical advisories; shipped dependencies clean; ${accepted.length} accepted with an expiry date`);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main(process.argv[2] ?? '.');
