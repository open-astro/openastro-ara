# Contributing to OpenAstro Ara

> **Contributions are paused.** Outside pull requests, issues and comments are temporarily disabled.
>
> The guidance below applies once contributions reopen.

Thank you for considering a contribution to OpenAstro Ara ("Ara"). Ara is a hard fork of
[N.I.N.A.](https://nighttime-imaging.eu/) rebuilt as a headless Linux daemon plus a cross-platform
Flutter client; see `README.md` for the lineage and the per-directory license split (MPL-2.0 daemon /
AGPL-3.0 client). Before contributing code, please open a GitHub Issue to discuss the change — it
ensures alignment with the design docs under `design/` and avoids duplicate work.

## Ways to contribute

- Fixing bugs and implementing features (the tracked queue is the GitHub issues labelled `P1`–`P5`)
- Improving documentation (`docs/USER_GUIDE.md`, `docs/RUNNING.md`, `docs/DEPLOY.md`)
- Reporting bugs — ideally with the in-app bundle (see below)
- Reporting device quirks (the `driver-quirk-report` issue template)
- Testing on hardware we don't have: the maintainers' rig is camera-only, so focuser / rotator /
  dome / safety-monitor reports from real equipment are especially valuable

## Reporting bugs

Use the **Bug report** issue template. The fastest way to a fix is attaching the in-app bundle:
**Help → Report a bug** (in the app) packages the daemon logs, your profile (with secrets redacted on
export), and recent diagnostics into one zip. Strip anything you consider private before uploading.

## Repository layout

```text
OpenAstroAra.Server/           ← ASP.NET Core daemon (.NET 10), REST + WebSocket on :5555
OpenAstroAra.{Core,Astrometry,Profile,Image,Equipment,Sequencer,PlateSolving}/
                               ← libraries inherited from NINA (MPL-2.0, headers preserved)
OpenAstroAra.{Fits,Stretch}/   ← Ara-original imaging libraries (FITS IO, display stretch)
OpenAstroAra.Test/             ← NUnit server tests
OpenAstroAra.TestHarness/      ← §42.2 virtual-observatory bench (fault-injection fakes)
bench/                         ← Linux/arm64 bench lane in a container; see bench/README.md
client/openastroara_client/    ← the Flutter client (AGPL-3.0)
design/                        ← the product spec (playbook), API contract log, PR rules
```

Read `design/PORT_PLAYBOOK.md` (the product spec, addressed by § numbers you'll see all over the
code) and `design/COMMIT-PR-RULES.md` (the PR/review discipline) before a non-trivial change.

## Building and running

`docs/RUNNING.md` covers building the daemon and client from source on Linux/macOS/Windows.
Quick version:

```bash
dotnet build                                   # full solution, warnings are errors in CI
dotnet test OpenAstroAra.Test                  # server tests (fixtures run in parallel)
dotnet test OpenAstroAra.Test --filter "TestCategory!=IO&TestCategory!=Integration"
                                               # quick unit run: skips fixtures that hit disk,
                                               # loopback HTTP or a simulator (seconds after build)
cd client/openastroara_client
flutter analyze && flutter test                # client gate
```

The virtual-observatory bench (`OpenAstroAra.TestHarness`) runs hardware-free against fakes;
`bench/README.md` runs the same suites in a `linux/arm64` container, the kernel and runtime
family the Raspberry Pi deployment targets. Run it when you touch equipment, guider or Alpaca
code and have no rig to try the change on.

## The rules that block merges (not warnings — blocks)

Every PR must pass the same gates the maintainers' own PRs pass:

1. **Warnings-as-errors analyzer build** of the full solution, plus `flutter analyze` clean.
2. **Settings registry** — every new user-facing setting must be registered in
   `client/openastroara_client/lib/settings/registry.dart` (id, label, description, keywords, path,
   type) **in the same commit** that introduces it. A setting that isn't ⌘K-searchable doesn't
   merge. CI runs `scripts/check-settings-registry.mjs`.
3. **Help registry** — the parallel rule for explainability: new settings need a help entry in
   `client/openastroara_client/lib/help/registry.dart`. CI runs `scripts/check-help-registry.mjs`.
4. **Unicode scan** — no BOMs or invisible/bidi characters (note: `dotnet sln add` prepends a BOM;
   strip it before committing).
5. **Full code review** — every PR gets one, no mechanical-change exemptions. Address findings with
   new commits (never force-push over review history) and reply to each finding.

Two hard-won conventions worth knowing before a reviewer tells you:

- **Fields need consumers.** Don't add profile/DTO fields (or Settings toggles) that nothing
  enforces yet — a control that promises behavior the daemon doesn't deliver is treated as a bug.
  Land the field together with the code that consumes it.
- **Optional ctor/parse defaults for wire changes.** Additive DTO fields carry defaults on both
  sides so older daemons and clients keep interoperating.

## Coding rules

- Match the style of the file you're in; C# is enforced by the analyzer set, Dart by the linter.
- Server: LoggerMessage-source-generated logging, RFC 7807 problem responses, 202-Accepted +
  WebSocket events for long operations (see `design/API_CONTRACT.md` for the reasoning log).
- Client: Riverpod state, models with tolerant `fromJson` parsing (absent keys → defaults).
- New C# files carry the Ara MPL-2.0 header; new client code is AGPL-3.0.
- Wire format: snake_case JSON, lowercase enum tokens.

## Branching and PRs

- Branch from `master` (`feature/<short-name>`, `fix/<short-name>`); PRs target `master`.
- Keep a PR to one logical change; multiple commits are fine (they squash on merge).
- Fill in the PR template — the registry-gate checkboxes are read, not decoration.
- For UI changes, attach screenshots.

### Your first PR, start to finish

1. **Pick an issue.** Open issues are labelled `P1`–`P5` (P1 blocks a release, P5 is nice to
   have); `good first issue` marks the self-contained ones. Comment that you are taking it so
   two people do not do the same work.
2. **Set up and build** per `docs/RUNNING.md`, then run the gate commands above once on a
   clean `master` so you know what green looks like on your machine.
3. **Branch** from `master`: `git checkout -b fix/<short-name>`.
4. **Make the change, with a test.** Find the playbook § the code cites and read it first;
   the spec decides behaviour, the issue describes the symptom. New or changed logic ships
   with a test in the same PR. A new user-facing setting needs a `settings/registry.dart`
   entry and a new ⓘ icon needs a `help/registry.dart` entry, or the registry gate fails.
5. **Run the gates for what you touched** (see "The rules that block merges"): the solution
   build and `dotnet test` for C#, `flutter analyze && flutter test` for the client, the
   registry scripts for settings/help, and `python3 .github/scripts/check-unicode.py` always.
6. **Add a CHANGELOG line** under `[Unreleased]` if a user would notice the change.
7. **Commit and push** with a conventional prefix (`fix:`, `feat:`, `docs:`, `tests:`) and
   `Closes #<issue>` in the body. Stage the files you changed by path, not `git add -A`.
8. **Open the PR** against `master` and fill in the template; attach screenshots for UI.
9. **Work the review loop.** CI and the review bot comment within minutes. Fix every finding
   in the same PR (including ones outside the original scope that the review turns up) rather
   than filing follow-up issues, push, and wait for the next round. A maintainer merges when
   checks are green and the thread is clean; the branch is squashed.

### Using Claude Code (optional but recommended)

The repo is set up for AI-assisted contribution: run `/code-review` on your diff before opening a
PR, `/security-review` for anything touching endpoints or file IO, and `/verify` to exercise UI
changes in the running app. The same review bar applies either way.

## Licensing of contributions

By contributing you agree your changes are licensed under the license of the directory they land
in: MPL-2.0 for the daemon/libraries, AGPL-3.0-or-later for `client/openastroara_client/`. Keep the
inherited NINA copyright headers intact on derived files; see `NOTICE.md`.
