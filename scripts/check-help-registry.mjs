#!/usr/bin/env node
// §69.4 help-registry gate — parallel to settings-registry gate (§61.4).
//
// Validates that:
//   1. Every Help(...) entry in lib/help/registry.dart has non-empty
//      title AND body.
//   2. No duplicate `key` strings.
//
// Usage:
//   node scripts/check-help-registry.mjs            # full repo check
//   node scripts/check-help-registry.mjs --staged   # only files staged for commit
//
// Exit codes:
//   0 — all checks pass
//   1 — at least one FAIL

import { readFileSync, existsSync } from 'node:fs';
import { execSync } from 'node:child_process';

const REGISTRY = 'client/openastroara_client/lib/help/registry.dart';

const args = process.argv.slice(2);
const stagedOnly = args.includes('--staged');

if (stagedOnly) {
  const staged = execSync('git diff --cached --name-only --diff-filter=ACM', {
    encoding: 'utf8',
  })
    .split('\n')
    .filter(Boolean);
  if (!staged.includes(REGISTRY)) {
    process.exit(0);
  }
}

if (!existsSync(REGISTRY)) {
  console.error(`✗ FAIL: ${REGISTRY} not found`);
  process.exit(1);
}

const src = readFileSync(REGISTRY, 'utf8');

// Match `Help(...)` blocks within the `const Map<String, Help> helpRegistry`.
const entryRe = /Help\s*\(\s*([\s\S]*?)\s*\)\s*,?/g;
const entries = [];
let m;
while ((m = entryRe.exec(src)) !== null) {
  entries.push({ body: m[1], index: m.index });
}

if (entries.length === 0) {
  console.log('✓ help-registry: 0 entries (empty registry is allowed).');
  process.exit(0);
}

const failures = [];
const keys = new Map();

for (const entry of entries) {
  const key = (entry.body.match(/key:\s*'([^']+)'/) || [])[1];
  const title = (entry.body.match(/title:\s*'([^']*)'/) || [])[1];
  // body: '...multiline raw string...' OR body: 'singleline'
  // Match the simpler single-quoted form; multi-line raw strings (''') are
  // also accepted by checking for ''' presence.
  const hasBody = /body:\s*('([^']*)'|'''[\s\S]*?''')/.test(entry.body);

  if (!key) {
    failures.push(`Entry near char ${entry.index}: missing or malformed \`key\``);
    continue;
  }
  if (keys.has(key)) {
    failures.push(`Entry \`${key}\`: duplicate key (first defined near char ${keys.get(key)})`);
  } else {
    keys.set(key, entry.index);
  }
  if (!title || title.trim().length === 0) {
    failures.push(`Entry \`${key}\`: empty or missing \`title\``);
  }
  if (!hasBody) {
    failures.push(`Entry \`${key}\`: missing \`body\` field`);
  }
}

if (failures.length > 0) {
  console.error('✗ help-registry check FAILED:');
  for (const f of failures) {
    console.error(`  - ${f}`);
  }
  console.error('');
  console.error('See design/COMMIT-PR-RULES.md "Help-registry gate" + PORT_PLAYBOOK.md §69.');
  process.exit(1);
}

console.log(`✓ help-registry: ${entries.length} entries, all valid.`);
process.exit(0);
