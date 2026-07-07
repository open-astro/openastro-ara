# Contributing to OpenAstro Ara

Thank you for considering a contribution to OpenAstro Ara ("Ara"). Ara is a hard fork of
[N.I.N.A.](https://nighttime-imaging.eu/) rebuilt as a headless Linux daemon plus a cross-platform
Flutter client; see `README.md` for the lineage and the per-directory license split (MPL-2.0 daemon /
AGPL-3.0 client). Before contributing code, please open a GitHub Issue to discuss the change — it
ensures alignment with the design docs under `design/` and avoids duplicate work.

## Ways to contribute

- Fixing bugs and implementing features (see `design/PORT_TODO.md` for the tracked queue)
- Improving documentation (`docs/USER_GUIDE.md`, `docs/RUNNING.md`, `docs/DEPLOY.md`)
- Reporting bugs — ideally with the in-app bundle (see below)
- Reporting device quirks (the `driver-quirk-report` issue template)
- Testing on hardware we don't have: the maintainers' rig is camera-only, so focuser / rotator /
  dome / safety-monitor reports from real equipment are especially valuable

## Reporting bugs

Use the **Bug report** issue template. The fastest way to a fix is attaching the in-app bundle:
**WILMA → Help → Report a bug** packages the daemon logs, your profile (with secrets redacted on
export), and recent diagnostics into one zip. Strip anything you consider private before uploading.

## Repository layout

```text
OpenAstroAra.Server/           ← ASP.NET Core daemon (.NET 10), REST + WebSocket on :5555
OpenAstroAra.{Core,Astrometry,Profile,Image,Equipment,Sequencer,PlateSolving}/
                               ← libraries inherited from NINA (MPL-2.0, headers preserved)
OpenAstroAra.{Fits,Stretch}/   ← Ara-original imaging libraries (FITS IO, display stretch)
OpenAstroAra.Test/             ← NUnit server tests
OpenAstroAra.TestHarness/      ← §42.2 virtual-observatory bench (fault-injection fakes)
client/openastroara_client/    ← WILMA, the Flutter client (AGPL-3.0)
design/                        ← the port playbook + append-only decision/TODO logs
```

Read `design/PORT_PLAYBOOK.md` (the product spec, addressed by § numbers you'll see all over the
code) and `design/COMMIT-PR-RULES.md` (the PR/review discipline) before a non-trivial change.

## Building and running

`docs/RUNNING.md` covers building the daemon and client from source on Linux/macOS/Windows.
Quick version:

```bash
dotnet build                                   # full solution, warnings are errors in CI
dotnet test OpenAstroAra.Test                  # server tests
cd client/openastroara_client
flutter analyze && flutter test                # client gate
```

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

### Using Claude Code (optional but recommended)

The repo is set up for AI-assisted contribution: run `/code-review` on your diff before opening a
PR, `/security-review` for anything touching endpoints or file IO, and `/verify` to exercise UI
changes in the running app. The same review bar applies either way.

## Licensing of contributions

By contributing you agree your changes are licensed under the license of the directory they land
in: MPL-2.0 for the daemon/libraries, AGPL-3.0-or-later for `client/openastroara_client/`. Keep the
inherited NINA copyright headers intact on derived files; see `NOTICE.md`.
