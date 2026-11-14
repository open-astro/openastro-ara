# AGENTS.md

## Agent skills

### Issue tracker

Issues live in GitHub Issues on `open-astro/openastro-ara` (use `gh -R open-astro/openastro-ara`). See `docs/agents/issue-tracker.md`.

### Triage labels

Default vocabulary: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: `GLOSSARY.md` and `docs/adr/` at the repo root, created lazily. See `docs/agents/domain.md`.

## Flutter client state

Client state is hand-written Riverpod 3 providers (`Notifier`, `AsyncNotifier`) in `client/openastroara_client/lib/state/`; the client has no code generation.

- `ref.watch` in `build`, `ref.read` in callbacks, `ref.listen` for side effects.
- A widget whose content is an `AsyncValue` renders data, loading and error (`.when`, or a `switch` on `AsyncData` / `AsyncLoading` / `AsyncError`). Code that needs only the current value reads it as nullable.

## Changelog

A user-visible change adds `changelog.d/<branch-slug>.md`; never edit `CHANGELOG.md` (only the release step writes it). Format and commands: `changelog.d/README.md`; CI runs `scripts/changelog-fragments.py --check`.

## Pull request body

The PR body follows `.github/pull_request_template.md`. The `PR body` workflow checks it when a PR is opened, edited or pushed (three jobs, optional until branch protection requires them):

- `pr-template` (`scripts/check-pr-template.py`): every `##` heading of the template is in the body, and each section holds own content, not the template's placeholder text. A heading of the form "A or B" accepts either path alone. Dependabot PRs skip it.
- `falsified-by` (`scripts/check-falsified-by.py`): every test case the PR adds or renames is listed under `Falsified by:` with the one production-code change that makes it fail. A case is a Dart `test(` or `testWidgets(` name, a C# NUnit or xUnit method (`Class.Method`) or a Python tooling test (`Class.method`). Grammar: `- "<case name>": <path>:<line> <change of 3 words or more>`. The path must exist and be production code (the client library, a daemon project that is not a test project, `scripts/` outside `scripts/tests/`, `.github/workflows/`, `packaging/`); a helper path (test tree, harness) is accepted only when the case name says a `Fake*`, `Mock*` or `TestHarness` is the subject. A moved case is not new; a line that names no case at HEAD is stale. Put the list in the Verification section.
- `decision-records` (`scripts/check-adr-scope.py`): the `## Decision records` section names, by four-digit number, every record the PR adds, edits, renames or deletes, or says `None`. Naming a `proposed` record makes the PR its pilot change: that record's rules join the review checklist, and they do not on any other PR.

The PR body reaches these scripts only through the environment (`PR_BODY`), never a command line. The decision-record grammar (Status line, sections, numbered rules with `Scope:` and `Checked by:`) is in `docs/adr/README.md` and `scripts/adr_grammar.py`; `scripts/check-adr-shape.py` enforces it in the Sanity job. A PR that changes a record needs a collaborator's `ADR authorized <sha>` comment (`adr-authorization` status).
