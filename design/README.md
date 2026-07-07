# design/ — the port's working docs

This directory is the OpenAstro Ara project's internal design record: the product spec, the
process rules, the live work queue, and the append-only logs that explain *why* things are the
way they are. Nothing in here ships to users (user-facing docs live in [`../docs/`](../docs/)).

## The documents

| Doc | What it is | How it's maintained |
|---|---|---|
| [`PORT_PLAYBOOK.md`](PORT_PLAYBOOK.md) | **The product spec.** ~12,800 lines, addressed by `§` numbers cited throughout the code, PRs, and every other doc. Has its own Table of Contents and the **"Port completion status — v0.0.1 section checklist"** (✅/🟡/⬜/🚫 per §section) near the top. | User-authoritative. Feature PRs flip their §checklist marker; substance changes are the maintainer's call. |
| [`PORT_TODO.md`](PORT_TODO.md) | **The work queue.** Every deferred item, review follow-up, parked blocker, and `TODO(port)` marker. Split in two: **open + mixed sections on top**, a "✅ Done / obsolete — archived entries" half below. | Every PR that defers something logs it here; fully-closed sections move (verbatim) below the archive line. |
| [`PORT_PROGRESS.md`](PORT_PROGRESS.md) | **The per-PR narrative.** A short accurate "Current" block (phase / last merged / in progress / next) on top of the full phase-by-phase history. | The port-driver skill updates "Current" + appends a Completed entry on every PR. |
| [`PORT_DECISIONS.md`](PORT_DECISIONS.md) | **Append-only decision log** — every non-obvious call, with date, reason, and a file/line pointer to where it's encoded. | Append only. Never edit prior entries. |
| [`API_CONTRACT.md`](API_CONTRACT.md) | **Append-only API reasoning log** for the REST/WS surface. The contract itself is `OpenAstroAra.Server/openapi.yaml`; this file records *why* wire shapes look the way they do. | Append only. One entry per endpoint/wire-shape decision. |
| [`COMMIT-PR-RULES.md`](COMMIT-PR-RULES.md) | **The process rules**: branch naming, PR rhythm, the §19.1 merge-gate (all checks green + review *body* clean), review-loop discipline. Referenced by CI, the PR template, and the registry-gate scripts. | Updated when the maintainer changes process; dated workflow notes at the top. |
| [`PHD2-GAP.md`](PHD2-GAP.md) | **External-integration tracker** for the `openastro-guider` daemon: the gap analysis that produced upstream PR open-astro/openastro-guider#57, and the ARA-side adoption work owed once it merges. | Status header updated as the upstream PR progresses. |
| [`TONIGHT_SKY.md`](TONIGHT_SKY.md) | **Feature spec (shipped)** — the §36.8 equipment-aware Tonight's Sky planner. Documents the live scoring weights/thresholds (`TonightSkyService` points here as rationale); deferred tails noted inline. | Reference; touch only when the scoring model changes. |
| [`NEXTGEN_PLANNING.md`](NEXTGEN_PLANNING.md) | **Feature spec (shipped, slices 1–4)** — the Glover Optimal-Sub exposure intelligence. Still-deferred forks recorded in its status header (§6 native sequence model, adaptive/runtime Glover, §3 star-detectability bounds). | Reference; status header tracks slices. |
| [`archive/`](archive/) | **Closed-out docs**, kept verbatim for the historical record: [`GAPS-ARA.md`](archive/GAPS-ARA.md) (the May-2026 design-phase gap tracker — fully resolved into the playbook) and [`HANDOFF.md`](archive/HANDOFF.md) (a June-2026 agent-handoff snapshot, long superseded). | Frozen. Don't add new content; each carries an ARCHIVED banner saying where its living concerns went. |

## Where "what's left" lives

There is deliberately **no duplicated status rollup here** — these four sources are authoritative,
each at a different altitude:

1. **Right now / next PR** → [`PORT_PROGRESS.md`](PORT_PROGRESS.md) → the "Current" block.
2. **The open work queue** (deferred items, follow-ups, parked blockers) → [`PORT_TODO.md`](PORT_TODO.md), everything **above** its "Done / obsolete" archive line.
3. **Section-level completion** across all 77 spec sections → `PORT_PLAYBOOK.md` → **"Port completion status — v0.0.1 section checklist"** (near the top).
4. **v0.1.0+ roadmap** (deferred features with rationale) → `PORT_PLAYBOOK.md` **§55**.

Externally-gated threads: the `v0.0.1-ara.1` release tag + RPi smoke test are **user/hardware-gated**
(see PORT_PROGRESS "Current"); guider-daemon event adoption is gated on upstream
open-astro/openastro-guider#57 (see [`PHD2-GAP.md`](PHD2-GAP.md)).

## Conventions

- **§ numbers** always refer to `PORT_PLAYBOOK.md` sections — the coordinate system for the whole
  project. Never renumber.
- The two **append-only logs** (`PORT_DECISIONS.md`, `API_CONTRACT.md`) grow at the bottom; prior
  entries are never edited, even when later superseded (supersede with a new entry).
- CI's `sanity` job verifies `PORT_PLAYBOOK.md`, `COMMIT-PR-RULES.md`, and this `README.md` exist
  and are non-empty.
- When a doc's job is finished, it moves to `archive/` with a dated banner — it is not deleted
  (these files are cited by PR discussions, commit messages, and each other).
