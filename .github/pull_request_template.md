<!-- Write all pull request text in Simplified Technical English (ASD-STE100): short sentences, one instruction per sentence, simple approved vocabulary, and the active voice. -->

## Thinking Path

<!--
  Required. Trace your reasoning from the top of the project down to this
  change: what OpenAstro Ara is, the subsystem or device involved, the
  problem, why it matters, what this pull request does, and the benefit.
  Use blockquote style. Aim for 5-8 steps.
-->

> - OpenAstro Ara is the observatory control app: a .NET daemon on the telescope computer and a Flutter client
> - [Which subsystem, device or screen is involved]
> - [What problem or gap exists]
> - [Why it needs to be addressed]
> - This pull request ...
> - The benefit is ...

## Linked Issues or Issue Description

<!--
  Required. Pick ONE of the two paths below.

  (A) Issue exists: link it with `Fixes #123`, `Closes #123` or `Refs #123`.
      Link duplicates and closely related issues and pull requests too, with
      a short note on why each is related.

  (B) No issue exists: describe the problem here. Keep these bold labels,
      each alone on its own line, with real content under each:
        Bug:     **What happened**  **Steps to reproduce**
        Feature: **The problem this solves**  **Proposed behavior**  **Alternatives considered**
      (the equivalent of the fields in .github/ISSUE_TEMPLATE/bug_report.md
      and feature_request.md).

  Only reference PUBLIC GitHub issues and pull requests (`#NNN` or
  github.com/open-astro/openastro-ara URLs). No private tracker ids, internal
  links, or localhost/LAN addresses: other contributors cannot open them.
-->

-

## What Changed

<!-- Bullet list of concrete changes. One bullet per logical unit. Name the file or component first (daemon project, client screen, tests, docs, CI). -->

-

## Decision records

<!--
  Required. Name, by number, every decision record in docs/adr/ this
  pull request implements, changes or adopts (for example "0001: touches the
  album cache"), or write "None". Naming a proposed record puts its rules on
  this PR's review checklist. A PR that edits a record must name it here
  (decision-records job), and a collaborator must comment
  `ADR authorized <head sha>` before it can merge (it holds until a push
  changes one of the PR's record files again).
-->

-

## Verification

<!--
  How can a reviewer confirm this works? Give the exact commands and their
  results: the RED run of each new test before the fix, the GREEN run after
  it, the build, format and analyze checks. Name each check you did not run
  locally and what covers it (for example: full solution build, 3-OS client
  matrix -> pull request CI). Hardware: name the device, firmware and
  platform, or say "not tested on hardware". UI changes: add a Before / After
  screenshot table (made-up site only).

  A PR that adds or renames a test case adds a `Falsified by:` list here, one
  line per case: - "<case name>": <file:line> <the one production-code change
  that makes it fail>. See AGENTS.md "Pull request body".
-->

-

## Risks

<!--
  What could go wrong? Mention behavior changes visible to users or clients,
  profile or settings migration, hardware safety (slews, parking, cooler,
  motion limits), breaking changes, or "Low risk" if it is genuinely minor.
-->

-

## Model Used

<!--
  Required. Name the AI model used to produce or assist with this change:
  provider and model name, exact model ID, context window if relevant,
  reasoning mode, and tools (shell, file edits, test runs).
  If no AI model was used, write "None - human-authored".
-->

-

## Checklist

- [ ] I have included a thinking path that traces from project context to this change
- [ ] I have specified the model used (with version and capability details)
- [ ] I have searched GitHub for duplicate or related issues and pull requests and linked them above
- [ ] I have either (a) linked existing issues with `Fixes #` / `Closes #` / `Refs #` OR (b) described the issue in-PR following the relevant issue template
- [ ] I have not referenced private or internal tracker ids or links (only public GitHub `#NNN` / `github.com/open-astro/openastro-ara` URLs)
- [ ] My branch name describes the change (e.g. `docs/...`, `fix/...`) and contains no private tracker id
- [ ] I have followed AGENTS.md and CONTRIBUTING.md (build, test and formatting rules)
- [ ] I have run the targeted tests locally and they pass
- [ ] I have added or updated tests where applicable
- [ ] I have added a `changelog.d/` fragment (not a CHANGELOG.md edit) and updated the relevant documentation
- [ ] Settings registry (section 61.4): N/A, or every new setting has a `lib/settings/registry.dart` entry with a description and at least 3 search keywords
- [ ] Help registry (section 69.4): N/A, or every new `HelpIcon(helpKey: ...)` has a `Help(...)` entry in `lib/help/registry.dart` with a title and body
- [ ] I have considered and documented any risks above
- [ ] All CI gates are green
- [ ] Claude Review passes with no open P1, P2s, recommendations, or follow-ups
- [ ] I will address all review-bot and reviewer comments before requesting merge
