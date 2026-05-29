<!--
Thank you for contributing to OpenAstro Ara. Please fill this out — keeps reviews fast and lets the registry gates do their job.
-->

## Summary

<!-- 1–3 bullets. What changed and why. -->

## Scope (COMMIT-PR-RULES.md row)

<!-- Which phase / sub-PR row from design/COMMIT-PR-RULES.md does this belong to? -->

## Test plan

- [ ] CI green (`flutter analyze` + `flutter test` + server build)
- [ ] Manual verification — describe what you actually clicked / ran

## §61.4 settings-registry checklist (only if this PR adds or modifies a user-facing setting)

- [ ] N/A — no settings touched
- [ ] Every new setting has a corresponding entry in `client/openastroara_client/lib/settings/registry.dart`
- [ ] Each new entry has a non-empty `description` and ≥3 search keywords
- [ ] Search verified — typing common keywords in WILMA's ⌘K palette finds the setting

## §69.4 help-registry checklist (only if this PR adds a new ⓘ help icon)

- [ ] N/A — no help icons added
- [ ] Every new `HelpIcon(helpKey: '...')` has a corresponding `Help(...)` entry in `client/openastroara_client/lib/help/registry.dart`
- [ ] Each entry has a non-empty `title` and `body`

## Screenshots (UI changes only)

<!-- Before/after, or just after if the change is additive. Drag images in. -->

## Notes

<!-- Anything else reviewers should know. -->
