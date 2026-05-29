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

// Find `Help(...)` blocks. Same balanced-paren walk as the settings
// script — string literals (single, double, triple-single) and nested
// calls in entry bodies break a naive non-greedy regex.
function extractEntries(src, keyword) {
  const entries = [];
  const startRe = new RegExp(`(?:^|[\\s,\\[{:])${keyword}\\s*\\(`, 'g');
  let m;
  while ((m = startRe.exec(src)) !== null) {
    const openParenIdx = src.indexOf('(', m.index);
    if (openParenIdx === -1) continue;
    let depth = 1;
    let i = openParenIdx + 1;
    let inSingle = false;
    let inDouble = false;
    let inTriple = false;
    while (i < src.length && depth > 0) {
      // Triple-single-quote opens/closes match before single-quote so we
      // don't misread `'''...'''` body literals as three separate quotes.
      if (!inSingle && !inDouble && src.startsWith("'''", i)) {
        inTriple = !inTriple;
        i += 3;
        continue;
      }
      if (inTriple) {
        i++;
        continue;
      }
      const c = src[i];
      if (!inSingle && !inDouble) {
        if (c === '(') depth++;
        else if (c === ')') depth--;
        else if (c === "'") inSingle = true;
        else if (c === '"') inDouble = true;
      } else if (inSingle && c === "'") {
        inSingle = false;
      } else if (inDouble && c === '"') {
        inDouble = false;
      }
      i++;
    }
    if (depth === 0) {
      entries.push({ body: src.slice(openParenIdx + 1, i - 1), index: m.index });
    }
  }
  return entries;
}

const entries = extractEntries(src, 'Help');

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

  // Skip class constructor matches: `const Help({required this.key, ...})`
  // is matched by the same regex but has no quoted `key:` literal.
  if (!key) continue;
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
