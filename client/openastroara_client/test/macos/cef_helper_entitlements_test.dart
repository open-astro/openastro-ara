@TestOn('mac-os || linux || windows') // pure file inspection — runs on every client CI leg
library;

import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
// No package:path import — Dart's File/Directory accept forward slashes on every
// platform (Windows included), and `path` isn't a direct dependency.

/// §36 guard for the macOS CEF multi-process helper wiring (see `macos/CEF_HELPER.md`).
///
/// The plugin's `add_helper_target.rb` injector points the `Helper` target's
/// `CODE_SIGN_ENTITLEMENTS` at the plugin's `helper.entitlements`. We repoint it at
/// the client-owned `Runner/Helper.entitlements` instead, so the helper's sandbox
/// posture matches the host's. Re-running the injector resets that pointer, and a
/// developer could forget the manual repoint — these tests are the automated guard
/// the #589 review asked for: they fail in CI (every client leg — pure text
/// inspection, no macOS toolchain needed) if the wiring regresses.
///
/// CEF-149 sandbox model (the §36 CEF 130→149 / OSR move): the App Sandbox is **off**
/// on the host. CEF's browser process registers a PID-suffixed *global* Mach bootstrap
/// name for the helper rendezvous, which `bootstrap_check_in` denies under the sandbox
/// ("Permission denied (1100)") — aborting `CefInitialize`. The name is PID-suffixed so
/// a static `temporary-exception.mach-register.global-name` can't cover it, so the
/// sandbox has to be disabled. With the host unsandboxed there is no container to join,
/// so the helper drops `app-sandbox`/`inherit` too (declaring them with no host
/// container left the helper's sandbox init invalid and crash-looped the network
/// service). These tests pin that contract: helper is non-sandboxed + keeps the JIT
/// keys; the host explicitly sets `app-sandbox` to false (a guard against an accidental
/// re-enable that would re-break the Mach rendezvous).

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
  final debugEnts = File('${root.path}/macos/Runner/DebugProfile.entitlements');
  final releaseEnts = File('${root.path}/macos/Runner/Release.entitlements');

  // The three Hardened-Runtime keys the CEF host must carry. allow-unsigned-
  // executable-memory is required on the *host* (not just the renderer) because
  // this build runs the GPU in-process under ANGLE SwiftShader, whose Reactor
  // JIT allocates W+X memory outside the V8 JIT path — see macos/CEF_HELPER.md.
  const hostCsKeys = [
    'com.apple.security.cs.allow-jit',
    'com.apple.security.cs.allow-unsigned-executable-memory',
    'com.apple.security.cs.disable-library-validation',
  ];

  group('macOS CEF Helper entitlements wiring', () {
    test('every CODE_SIGN_ENTITLEMENTS assignment is client-owned (none on the plugin default)', () {
      expect(pbxproj.existsSync(), isTrue, reason: '${pbxproj.path} not found');
      final values = _entitlementAssignments(pbxproj.readAsStringSync());
      expect(values, isNotEmpty, reason: 'no CODE_SIGN_ENTITLEMENTS assignments found — pbxproj shape changed?');
      // ALL helper targets' configs must sign with the sandboxed client file.
      // CEF on macOS needs the full five-helper set (base + GPU/Renderer/Plugin/
      // Alerts), and a Flutter macOS project has three build configs
      // (Debug/Profile/Release), so the helpers contribute exactly 5 × 3 = 15
      // `Runner/Helper.entitlements` assignments. Asserting the count (not just
      // "≥1") catches a *partial* repoint — e.g. a re-run of add_helper_target.rb
      // that the manual fix-up only corrected for some targets/configs — which a
      // "contains" check would silently pass.
      const helperTargets = 5;
      const buildConfigs = 3;
      final helperAssignments =
          values.where((v) => v == 'Runner/Helper.entitlements').length;
      expect(helperAssignments, helperTargets * buildConfigs,
          reason: 'all ${helperTargets * buildConfigs} helper configs '
              '($helperTargets targets × $buildConfigs configs) must point '
              'CODE_SIGN_ENTITLEMENTS at Runner/Helper.entitlements; found $helperAssignments');
      // …and NO assignment may reference the plugin's non-sandboxed helper.entitlements
      // (a re-run of add_helper_target.rb that forgot the repoint — see macos/CEF_HELPER.md).
      final pluginDefault = values.where((v) => v.contains('helper/helper.entitlements')).toList();
      expect(pluginDefault, isEmpty,
          reason: 'a Helper config still signs with the plugin default $pluginDefault; '
              'repoint it to Runner/Helper.entitlements');
    });

    test('Runner/Helper.entitlements is non-sandboxed (matches the unsandboxed host) and allows JIT', () {
      expect(helperEnts.existsSync(), isTrue, reason: '${helperEnts.path} is missing');
      final ents = helperEnts.readAsStringSync();
      // Match the actual <key>…</key> DECLARATION, not a bare substring — these key
      // names also appear in the file's header comment explaining why they're dropped,
      // so a `contains` check would false-positive on the prose.
      bool declaresKey(String key) =>
          RegExp('<key>${RegExp.escape(key)}</key>').hasMatch(ents);
      // Sandbox keys must be ABSENT: with the host unsandboxed there's no container to
      // join, and declaring app-sandbox/inherit here crash-loops CEF's network service.
      for (final key in const [
        'com.apple.security.app-sandbox',
        'com.apple.security.inherit',
      ]) {
        expect(declaresKey(key), isFalse,
            reason: 'Helper.entitlements must NOT declare $key — the host is unsandboxed '
                '(CEF 149 Mach rendezvous); a nested sandbox crash-loops the network service');
      }
      // …but the JIT key the renderer's V8 needs must stay.
      expect(declaresKey('com.apple.security.cs.allow-jit'), isTrue,
          reason: 'Helper.entitlements must declare com.apple.security.cs.allow-jit (V8 in the renderer)');
    });

    test('host DebugProfile/Release entitlements carry all three CEF cs.* keys', () {
      // Completes the guard: the pbxproj test above proves the Helper *points* at
      // the right file, but a regression that dropped a cs.* key from the host
      // entitlements would still build and then abort on-device (JIT / SwiftShader
      // W+X). Assert the keys are present in both host configs.
      for (final f in [debugEnts, releaseEnts]) {
        expect(f.existsSync(), isTrue, reason: '${f.path} is missing');
        final ents = f.readAsStringSync();
        for (final key in hostCsKeys) {
          expect(ents.contains(key), isTrue,
              reason: '${f.path} must declare $key (CEF host Hardened-Runtime requirement)');
        }
      }
    });

    test('host DebugProfile/Release explicitly disable the App Sandbox (CEF 149 Mach rendezvous)', () {
      // Pin the sandbox-off decision. Re-enabling app-sandbox on the host re-breaks
      // CefInitialize (the PID-suffixed global Mach bootstrap name is denied under the
      // sandbox), so flipping this back to <true/> must fail CI loudly rather than
      // ship a build that crashes the moment the Planning atlas opens.
      final sandboxValue =
          RegExp(r'<key>com\.apple\.security\.app-sandbox</key>\s*<(true|false)/>');
      for (final f in [debugEnts, releaseEnts]) {
        final m = sandboxValue.firstMatch(f.readAsStringSync());
        expect(m, isNotNull,
            reason: '${f.path} must declare com.apple.security.app-sandbox (with an explicit boolean)');
        expect(m!.group(1), 'false',
            reason: '${f.path}: com.apple.security.app-sandbox must be <false/> — CEF 149 '
                'multi-process Mach rendezvous cannot register a global bootstrap name under the sandbox');
      }
    });

    test('DebugProfile and Release host entitlements stay byte-identical', () {
      // One audited entitlement set for both configs — drift between them is how a
      // Release build silently loses a key that Debug has (or vice versa).
      expect(debugEnts.readAsStringSync(), releaseEnts.readAsStringSync(),
          reason: 'DebugProfile.entitlements and Release.entitlements must be identical');
    });
  });
}
