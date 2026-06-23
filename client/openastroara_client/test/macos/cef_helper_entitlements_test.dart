@TestOn('mac-os || linux || windows') // pure file inspection — runs on every client CI leg
library;

import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
// No package:path import — Dart's File/Directory accept forward slashes on every
// platform (Windows included), and `path` isn't a direct dependency.

/// §36 guard for the macOS CEF multi-process helper wiring (see `macos/CEF_HELPER.md`).
///
/// The plugin's `add_helper_target.rb` injector points the `Helper` target's
/// `CODE_SIGN_ENTITLEMENTS` at the plugin's **non-sandboxed** `helper.entitlements`.
/// Because `openastroara` is App-Sandboxed, the helper must instead use the
/// client-owned `Runner/Helper.entitlements` (which carries `app-sandbox` +
/// `inherit`), or macOS silently refuses to launch the helper from the sandboxed
/// host — a runtime-only failure that wouldn't surface at build time.
///
/// Re-running the injector resets that pointer, and a developer could forget the
/// manual repoint. These tests are the automated guard the #589 review asked for:
/// they fail in CI (every client leg — pure text inspection, no macOS toolchain
/// needed) if the wiring regresses.

/// Walk up from the test's CWD to the package root (the dir holding `pubspec.yaml`
/// next to `macos/`), so the file reads don't silently assume `flutter test`'s CWD
/// and turn a wiring regression into a confusing "file not found".
Directory _packageRoot() {
  var dir = Directory.current;
  for (var i = 0; i < 8; i++) {
    if (File('${dir.path}/pubspec.yaml').existsSync() &&
        Directory('${dir.path}/macos').existsSync()) {
      return dir;
    }
    final parent = dir.parent;
    if (parent.path == dir.path) break;
    dir = parent;
  }
  fail('could not locate the client package root (pubspec.yaml + macos/) from ${Directory.current.path}');
}

/// The values of every `CODE_SIGN_ENTITLEMENTS = …;` assignment in the project —
/// surgical, so we inspect only entitlement settings, not arbitrary substrings
/// (a comment or another target's path can't cause a false pass/fail).
List<String> _entitlementAssignments(String pbxproj) => RegExp(
      r'CODE_SIGN_ENTITLEMENTS\s*=\s*(.+?);',
    ).allMatches(pbxproj).map((m) => m.group(1)!.trim().replaceAll('"', '')).toList();

void main() {
  final root = _packageRoot();
  final pbxproj = File('${root.path}/macos/Runner.xcodeproj/project.pbxproj');
  final helperEnts = File('${root.path}/macos/Runner/Helper.entitlements');

  group('macOS CEF Helper entitlements wiring', () {
    test('every CODE_SIGN_ENTITLEMENTS assignment is client-owned (none on the plugin default)', () {
      expect(pbxproj.existsSync(), isTrue, reason: '${pbxproj.path} not found');
      final values = _entitlementAssignments(pbxproj.readAsStringSync());
      expect(values, isNotEmpty, reason: 'no CODE_SIGN_ENTITLEMENTS assignments found — pbxproj shape changed?');
      // The Helper target's 3 configs must sign with the sandboxed client file…
      expect(values, contains('Runner/Helper.entitlements'),
          reason: 'the Helper target must point CODE_SIGN_ENTITLEMENTS at Runner/Helper.entitlements');
      // …and NO assignment may reference the plugin's non-sandboxed helper.entitlements
      // (a re-run of add_helper_target.rb that forgot the repoint — see macos/CEF_HELPER.md).
      final pluginDefault = values.where((v) => v.contains('helper/helper.entitlements')).toList();
      expect(pluginDefault, isEmpty,
          reason: 'a Helper config still signs with the plugin default $pluginDefault; '
              'repoint it to Runner/Helper.entitlements');
    });

    test('Runner/Helper.entitlements joins the host sandbox and allows JIT', () {
      expect(helperEnts.existsSync(), isTrue, reason: '${helperEnts.path} is missing');
      final ents = helperEnts.readAsStringSync();
      for (final key in const [
        'com.apple.security.app-sandbox', // sandboxed host can't launch a non-sandboxed nested helper
        'com.apple.security.inherit', // join the host's sandbox container
        'com.apple.security.cs.allow-jit', // V8 in the renderer
      ]) {
        expect(ents.contains(key), isTrue, reason: 'Helper.entitlements must declare $key');
      }
    });
  });
}
