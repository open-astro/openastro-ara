# design/ — the spec and the process rules

Three living documents remain here. Nothing in this directory ships to users (user-facing docs live
in [`../docs/`](../docs/)).

| Doc | What it is |
|---|---|
| [`PORT_PLAYBOOK.md`](PORT_PLAYBOOK.md) | **The product spec.** Addressed by `§` numbers cited throughout the code, the PR reviews and the process rules. Never renumber. |
| [`API_CONTRACT.md`](API_CONTRACT.md) | **The wire-shape reasoning log** (append-only). `OpenAstroAra.Server/openapi.yaml` is a generated snapshot of every REST route (drift fails `OpenApiContractSnapshotTest`, #1131); this log holds the reasoning and the WebSocket protocol. Append an entry in the PR that adds or changes a wire shape. |
| [`COMMIT-PR-RULES.md`](COMMIT-PR-RULES.md) | **The process rules**: branch naming, PR rhythm, the §19.1 merge gate (all checks green + review body clean), review-loop discipline. Referenced by CI, the PR template and the registry-gate scripts. |

## Where "what's left" lives

**GitHub issues, labelled `P1`–`P5`** (`P1` = blocks the first release or breaks a real install;
`P5` = parked on a maintainer decision or speculative). Filter:
`gh issue list --label P1` … `--label P5`.

## Retired on 2026-09-28

The port is feature-complete. The status rollups and logs that tracked it were retired the day the
open work moved to issues #1118–#1185, because they had drifted from the code and were misleading.
They are in git history before that date: `ROADMAP.md`, `PORT_TODO.md`, `PORT_PROGRESS.md`,
`PORT_DECISIONS.md` (append-only decision log), `PHD2-GAP.md`, `TONIGHT_SKY.md`,
`NEXTGEN_PLANNING.md`, `INTEGRATION_BUDGET.md`, `PLANNING_REDESIGN.md`, `RUN_REDESIGN.md`,
`AUDIT.MD` and `archive/` (`GAPS-ARA.md`, `HANDOFF.md`). New decisions are recorded in the PR that
makes them.

**Citation keys.** Code comments and the playbook still cite these files by name — `PORT_TODO`,
`PORT_DECISIONS 2026-07-15`, `NEXTGEN §3.1`, `ROADMAP part 4` / `ROADMAP §8`, `PHD2-GAP gap 3`,
`AUDIT #H3`, `TONIGHT_SKY`, `INTEGRATION_BUDGET`, `RUN_REDESIGN`, `PLANNING_REDESIGN`, `GAPS-ARA`. Each resolves at the last commit
that had them, `51cba5c40`:
`https://github.com/open-astro/openastro-ara/blob/51cba5c40/design/<FILE>.md` (`archive/` for
`GAPS-ARA` / `HANDOFF`, `AUDIT.MD` upper-case, and `NEXTGEN` is short for `NEXTGEN_PLANNING.md`). A citation is a pointer to the reasoning, not a live
task; open work is only what the `P1`–`P5` issues say.

## Standing decisions

Carried over from the retired `PORT_DECISIONS.md` because they still govern new work:

- **Use each vendored engine's native capabilities first; fill only the gap** (2026-06-26). For
  Stellarium Web, the guider, ASTAP and the NINA-derived code, map and use what the engine already
  does before writing custom code, and keep the custom part minimal.
- **Planning compute belongs in the client; the Pi keeps execution** (2026-07-15). Target ranking,
  optimal-sub math, FOV/framing and filter advice live in the client, which must work with no Pi
  (§2). A new server-side planning endpoint needs a stated justification, such as feeding the
  execution engine directly.
- **Linux desktop client: x86-64, Wayland only** (2026-09-29, epic #1204; runner landed
  2026-10-05 in #1275/#1201). Supported and tested on Ubuntu/Kubuntu 24.04 LTS, Fedora KDE
  (current) and Arch, one distro per family; glibc floor 2.39. X11 is refused at startup. The
  server stays an arm64 Debian `.deb` and is unaffected.
- **Permanent non-goals** are listed in `PORT_PLAYBOOK.md` §55.

CI's `sanity` job verifies `PORT_PLAYBOOK.md`, `COMMIT-PR-RULES.md` and this `README.md` exist and
are non-empty.
