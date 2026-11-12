### Changed (every string in pt-BR)

- Brazilian Portuguese now covers the safety alert dialog, and the "Couldn't …" error sentences read wholly in the picked language: the action half (for example "start copying frames") comes from the catalogue instead of English text in code, in the library, calibration, storage, Settings, Support, time sync and backup-stream messages (#1751).
- Contributors: a test (`test/hardcoded_strings_test.dart`) lists the text still written straight into the Dart code, file by file, and fails when a file gains one or when its entry is not lowered after a migration. A language-neutral literal ends its line with `// l10n-ok` (#1751).
