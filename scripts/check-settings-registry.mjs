#!/usr/bin/env node
// §61.4 settings-registry gate — Layer 1 (pre-commit hook) + Layer 2 (CI check).
// Specced in design/COMMIT-PR-RULES.md "Settings-registry gate".
//
// Validates that:
//   1. Every Setting(...) entry in lib/settings/registry.dart has non-empty
//      description AND keywords.
//   2. No duplicate `id` strings across the registry.
//
// Future work (intentionally NOT in v1):
//   - Cross-reference panel widgets vs registry IDs (would need a Dart AST
//     parser to identify "this is a settable widget"). For now we trust the
//     PR template checkbox to catch missing registrations; the script just
//     enforces quality of what IS registered.
//
// Usage:
//   node scripts/check-settings-registry.mjs            # full repo check
//   node scripts/check-settings-registry.mjs --staged   # only files staged for commit
//
// Exit codes:
//   0 — all checks pass
//   1 — at least one FAIL

import { readFileSync, existsSync } from 'node:fs';
import { execSync } from 'node:child_process';

const REGISTRY = 'client/openastroara_client/lib/settings/registry.dart';

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

// Match `Setting(...)` blocks. The registry uses a single top-level
// `const List<Setting> settingsRegistry = [ Setting(...), Setting(...), ];`
// pattern, so a non-greedy block match across the entries is sufficient.
const entryRe = /Setting\s*\(\s*([\s\S]*?)\s*\)\s*,?/g;
const entries = [];
let m;
while ((m = entryRe.exec(src)) !== null) {
  entries.push({ body: m[1], index: m.index });
}

if (entries.length === 0) {
  // An empty registry is fine — the foundation PR ships an empty registry
  // and bulk-population follows in subsequent sub-PRs. Don't fail here.
  console.log('✓ settings-registry: 0 entries (empty registry is allowed).');
  process.exit(0);
}

const failures = [];
const ids = new Map();

for (const entry of entries) {
  // Extract id, description, keywords from the body. Each field is a
  // labelled keyword arg.
  const id = (entry.body.match(/id:\s*'([^']+)'/) || [])[1];
  const description = (entry.body.match(/description:\s*'([^']*)'/) || [])[1];
  // keywords: ['foo', 'bar'] — match the bracketed list.
  const keywordsRaw = (entry.body.match(/keywords:\s*\[([^\]]*)\]/) || [])[1];
  const keywords = keywordsRaw
    ? keywordsRaw
        .split(',')
        .map((s) => s.replace(/['"]/g, '').trim())
        .filter(Boolean)
    : [];

  if (!id) {
    failures.push(`Entry near char ${entry.index}: missing or malformed \`id\``);
    continue;
  }
  if (ids.has(id)) {
    failures.push(`Entry \`${id}\`: duplicate id (first defined near char ${ids.get(id)})`);
  } else {
    ids.set(id, entry.index);
  }
  if (!description || description.trim().length === 0) {
    failures.push(`Entry \`${id}\`: empty or missing \`description\` — §61.4 requires a 1-2 sentence explanation`);
  }
  if (keywords.length === 0) {
    failures.push(`Entry \`${id}\`: empty \`keywords\` list — §61.4 requires search terms for discoverability`);
  }
}

if (failures.length > 0) {
  console.error('✗ settings-registry check FAILED:');
  for (const f of failures) {
    console.error(`  - ${f}`);
  }
  console.error('');
  console.error('See design/COMMIT-PR-RULES.md "Settings-registry gate" + PORT_PLAYBOOK.md §61.');
  process.exit(1);
}

console.log(`✓ settings-registry: ${entries.length} entries, all valid.`);
process.exit(0);
