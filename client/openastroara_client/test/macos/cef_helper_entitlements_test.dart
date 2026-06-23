@TestOn('mac-os || linux || windows') // pure file inspection — runs on every client CI leg
library;

import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

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
/// they fail in CI (every client leg — this is pure text inspection, no macOS
/// toolchain needed) if the wiring regresses.
void main() {
  final pbxproj = File('macos/Runner.xcodeproj/project.pbxproj');
  final helperEnts = File('macos/Runner/Helper.entitlements');

  group('macOS CEF Helper entitlements wiring', () {
    test('the Helper target uses the sandboxed Runner/Helper.entitlements', () {
      final proj = pbxproj.readAsStringSync();
      expect(proj.contains('Runner/Helper.entitlements'), isTrue,
          reason: 'the Helper target must point CODE_SIGN_ENTITLEMENTS at Runner/Helper.entitlements');
      // The injector's non-sandboxed default must NOT be what the Helper target signs with.
      expect(proj.contains('helper/helper.entitlements'), isFalse,
          reason: 're-running add_helper_target.rb reset the Helper entitlements to the plugin '
              "default; repoint them to Runner/Helper.entitlements (see macos/CEF_HELPER.md)");
    });

    test('Runner/Helper.entitlements joins the host sandbox and allows JIT', () {
      expect(helperEnts.existsSync(), isTrue, reason: 'macos/Runner/Helper.entitlements is missing');
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
