# Architecture decision records

One `NNNN-<slug>.md` record per choice that passes all three tests below,
with a Status line under the title, then Context, Decision, Rules,
Alternatives rejected, Consequences and Links. How to reverse is required in
every record and may sit anywhere among them. `scripts/check-adr-shape.py`
(CI job Sanity) holds every record to this shape. The how-to of a rule stays
at its implementation; the record keeps the reasoning and the short,
checkable form below.

**Who reads a record.** The review bot and the agents that edit this
repository, first; people second. A rule the bot cannot apply from the record
alone is a defect in the record, not a style choice.

**What earns a record.** A choice gets a record only when all three hold:

1. Hard to reverse: changing it later costs real work across the app.
2. Surprising without context: a future reader would ask why it was done this way.
3. A real trade-off: there were alternatives and one was picked for named reasons.

A choice that fails any of the three belongs in `AGENTS.md`, `CONTRIBUTING.md`
or the code, not in a record.

**Rules.** The rules a change is checked against, one numbered line each:

```
1. <the rule, in one or two sentences>. Scope: `<glob>`[, `<glob>`]. Checked by: <test, script check, or review (<what to look for>)>.
```

A glob matches repository paths (`*` stays in one directory, `**` crosses
them) and must match at least one tracked file. A glob of `**` alone is
invalid: name the files. "Checked by" names the test or check that fails when
the rule is broken, or says `review (<what to look for>)` with the evidence a
reviewer looks for. A bare `review` is invalid. A rule states its own bound;
it never carves out code that deviates (that is a defect with an issue). The
words except, unless, until and for now fail the shape check on a rule line
unless the line ends with `<!-- allowed -->`, which says the bound is the
rule itself. About twelve rules is the point at which a record is carrying
two topics; past it, the PR body says why the record is not split. A
superseded or retired record needs no Rules.

**How to reverse.** Every record says what undoing it costs and the steps, so
a future maintainer knows the price of the hard-to-reverse choice.

**Changing a record.** A change to any file in this directory, this index
included, needs a collaborator with write, maintain or admin permission to
comment `ADR authorized <sha>` on the PR, on a line of its own, naming a
commit, normally the current head. The authorization holds for later pushes
while every record file the PR changes keeps the content it had at that
commit; a push that edits one, adds one or deletes one needs a new
authorization, and a push that changes only code or tests does not. The author
may authorize their own PR. `scripts/check-adr-authorization.py` posts the
result as the `adr-authorization` commit status
(`.github/workflows/decision-records.yml`). The PR body's `## Decision
records` section names every record the PR changes
(`scripts/check-adr-scope.py`). The review bot reads the Rules each changed
file falls under and reports a broken rule as a defect; it also checks a new
record against the three tests above and its Consequences for carve-out
language.

The gate is a tripwire, not peer review: the author may authorize their own
PR. What it guarantees is that no record changes without a named person
acknowledging it at a named commit, which is the protection against an agent
editing a rule to fit its diff.

Status is one of:

- `proposed`: written so a pilot change can be reviewed against it; not yet a rule.
- `accepted`: the rule.
- `superseded by NNNN`: kept for history; the named record is the rule.
- `retired (#NNNN)`: kept for history; the choice stopped being true with no
  successor. The issue or PR that retired it is named on the Status line; the
  Rules section is removed and the rest is frozen; the index line below
  carries a `(retired)` suffix.

A `proposed` record that is rejected is deleted, not kept. A record is
referred to by its slug until it is written, and numbered then.

- [The Sky Album collection cache is a high-water mark](0001-collection-cache-is-a-high-water-mark.md)
- [A headless daemon on the rig and a Flutter client](0002-headless-daemon-and-flutter-client.md)
- [Equipment is controlled only through ASCOM Alpaca](0003-alpaca-only-equipment.md)
- [Planning runs in the client; the rig runs the night](0004-planning-runs-in-the-client.md)
- [A trusted LAN with one controlling client](0005-trusted-lan-single-client.md)
- [The Linux desktop client is x86-64 and Wayland only](0006-linux-client-is-wayland-only.md)
- [ASTAP is the only plate solver, and it runs on the rig](0007-astap-is-the-only-solver.md)
- [A GPS receiver goes on the client, never the rig](0008-gps-lives-on-the-client.md)
- [Simple Mode is a second shell over the same rig](0009-simple-mode-is-a-shell-over-the-same-rig.md)
- [Simple Mode's queue is one sequence on the rig](0010-simple-mode-queue-is-one-sequence-on-the-rig.md)
- [A device's lifecycle is one module](0011-device-lifecycle-is-one-module.md)
- [A profile section is declared once](0012-a-profile-section-is-declared-once.md)
- [Presentations render shared client modules](0013-presentations-render-shared-client-modules.md)
