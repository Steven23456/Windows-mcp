#!/usr/bin/env node
// Keeps docs/testing/e2e-test-results.md in step with docs/testing/e2e-test-plan.md.
//
//   node scripts/e2e/sync-results.mjs            add a NOT RUN log row for every plan test id the
//                                                results log lacks, recompute the feature-coverage
//                                                counts from the log, rewrite the plan's Appendix C
//                                                index; report what changed
//   node scripts/e2e/sync-results.mjs --check    report only; exit 1 if the log lacks any plan id
//                                                or the coverage counts are stale
//
// It never edits an existing log row, never touches a Notes cell, and never removes anything.
// Test ids are the first cell of any plan table row matching ^[A-Z][A-Z0-9_]*-\d{2}$; the
// second and third cells are the tier and channel. The feature a test belongs to is the id
// without its -NN suffix (HOST and XC are features of their own).
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const planPath = join(root, 'docs', 'testing', 'e2e-test-plan.md');
const resultsPath = join(root, 'docs', 'testing', 'e2e-test-results.md');
const check = process.argv.includes('--check');
const ID = /^[A-Z][A-Z0-9_]*-\d{2}$/;

const cells = line => line.trim().replace(/^\|/, '').replace(/\|$/, '').split('|').map(c => c.trim());

// ---- plan: ordered test ids with tier, channel and the section heading they sit under ----
const plan = readFileSync(planPath, 'utf8');
const planLines = plan.split(/\r?\n/);
const tests = [];
let heading = '';
for (const line of planLines) {
  const h = /^#{2,4}\s+(.*)$/.exec(line);
  if (h) { heading = h[1].trim(); continue; }
  if (!line.trim().startsWith('|')) continue;
  const c = cells(line);
  if (!ID.test(c[0])) continue;
  if (tests.some(t => t.id === c[0])) { console.error(`duplicate test id in plan: ${c[0]}`); process.exitCode = 1; continue; }
  tests.push({ id: c[0], tier: c[1] ?? '', ch: c[2] ?? '', heading });
}
const featureOf = id => id.replace(/-\d{2}$/, '');

// ---- results: locate the log table and the coverage table ----
const results = readFileSync(resultsPath, 'utf8');
const eol = results.includes('\r\n') ? '\r\n' : '\n';
const lines = results.split(/\r?\n/);

function findTable(headingRegex) {
  const start = lines.findIndex(l => headingRegex.test(l));
  if (start < 0) throw new Error(`results document lacks a heading matching ${headingRegex}`);
  let i = start + 1;
  while (i < lines.length && !lines[i].trim().startsWith('|')) { if (/^#{2}\s/.test(lines[i])) throw new Error(`no table under ${lines[start]}`); i++; }
  const header = i, sep = i + 1;
  let end = sep + 1;
  while (end < lines.length && lines[end].trim().startsWith('|')) end++;
  return { header, sep, firstRow: sep + 1, end };
}

const log = findTable(/^##\s+3\.\s+Test log/);
const existing = new Set();
const statusByTest = new Map();
for (let i = log.firstRow; i < log.end; i++) {
  const c = cells(lines[i]);
  if (!ID.test(c[0])) continue;
  existing.add(c[0]);
  statusByTest.set(c[0], (c[3] ?? '').toUpperCase());
}
const missing = tests.filter(t => !existing.has(t.id));
const orphaned = [...existing].filter(id => !tests.some(t => t.id === id));

// ---- coverage recomputation ----
const classify = s => s.startsWith('PASS') ? 'pass' : s.startsWith('FAIL') ? 'fail' : s.startsWith('BLOCKED') ? 'blocked' : s.startsWith('SKIP') ? 'skipped' : 'notrun';
const counts = new Map();
for (const t of tests) {
  const f = featureOf(t.id);
  const c = counts.get(f) ?? { planned: 0, pass: 0, fail: 0, blocked: 0, skipped: 0, notrun: 0 };
  c.planned++;
  c[classify(statusByTest.get(t.id) ?? 'NOT RUN')]++;
  counts.set(f, c);
}
const cov = findTable(/^##\s+2\.\s+Feature coverage/);
const covRows = new Map();
for (let i = cov.firstRow; i < cov.end; i++) { const c = cells(lines[i]); if (c[0]) covRows.set(c[0], { line: i, notes: c[7] ?? '' }); }
const covLine = (f, c, notes) => `| ${f} | ${c.planned} | ${c.pass + c.fail} | ${c.pass} | ${c.fail} | ${c.blocked} | ${c.skipped} | ${notes} |`;
let staleCoverage = 0;
const newCov = [];
for (const [f, c] of [...counts.entries()].sort((a, b) => a[0].localeCompare(b[0]))) {
  const row = covRows.get(f);
  const want = covLine(f, c, row?.notes ?? '');
  if (!row) newCov.push(want);
  else if (lines[row.line].trim() !== want) { staleCoverage++; if (!check) lines[row.line] = want; }
}

// ---- report / write ----
console.log(`plan: ${tests.length} test ids across ${counts.size} features`);
console.log(`results log: ${existing.size} rows; missing ${missing.length}; not in plan ${orphaned.length}; coverage rows stale ${staleCoverage}, new ${newCov.length}`);
if (orphaned.length) console.log(`  rows in the log that are not in the plan (left untouched): ${orphaned.join(', ')}`);
if (check) {
  if (missing.length) console.log(`  missing: ${missing.map(t => t.id).join(', ')}`);
  process.exit(missing.length || staleCoverage || newCov.length ? 1 : 0);
}

// insert missing log rows at the end of the log table, in plan order
const newRows = missing.map(t => `| ${t.id} | ${t.tier} | ${t.ch} | NOT RUN | | |`);
lines.splice(log.end, 0, ...newRows);
// coverage rows are inserted after the log rows so indices above stay valid only if cov is above log — recompute
if (newCov.length) {
  const cov2 = findTable(/^##\s+2\.\s+Feature coverage/);
  lines.splice(cov2.end, 0, ...newCov);
}
writeFileSync(resultsPath, lines.join(eol));

// Appendix C index in the plan, between the markers
const indexStart = planLines.findIndex(l => l.trim() === '<!-- INDEX-START -->');
const indexEnd = planLines.findIndex(l => l.trim() === '<!-- INDEX-END -->');
if (indexStart >= 0 && indexEnd > indexStart) {
  const byFeature = new Map();
  for (const t of tests) { const f = featureOf(t.id); (byFeature.get(f) ?? byFeature.set(f, []).get(f)).push(t); }
  const idx = ['', `${tests.length} test ids, ${byFeature.size} features (generated; do not edit by hand).`, '', '| Feature | Tests | Ids |', '|---|---|---|'];
  for (const [f, ts] of [...byFeature.entries()].sort((a, b) => a[0].localeCompare(b[0]))) idx.push(`| ${f} | ${ts.length} | ${ts.map(t => t.id).join(', ')} |`);
  idx.push('');
  planLines.splice(indexStart + 1, indexEnd - indexStart - 1, ...idx);
  writeFileSync(planPath, planLines.join(plan.includes('\r\n') ? '\r\n' : '\n'));
}
console.log(`wrote ${newRows.length} log rows, ${newCov.length} coverage rows, ${staleCoverage} coverage updates`);
