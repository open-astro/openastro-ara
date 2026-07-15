import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// The desktop window's two sizes: the compact, centered **launchpad**
/// (server connect + profile box, §30) and the maximized **workstation**
/// (the §25 shell).
enum WindowMode { launchpad, workstation }

/// Drives the native window size over the `openastroara/window` channel
/// (handled by all three desktop runners). Idempotent — the router calls it
/// every build, so repeat requests for the current mode are dropped. Fails
/// silent by design: on platforms without the handler (tests, a future
/// mobile build) window sizing simply doesn't apply.
class WindowModeService {
  static const _channel = MethodChannel('openastroara/window');
  WindowMode? _current;
  bool _unsupported = false;
  // Serializes set() calls: overlapping calls (the router fires post-frame on
  // every rebuild) could otherwise interleave a failure-rollback over a later
  // call's already-committed mode (review #846 r3 TOCTOU note).
  Future<void> _chain = Future<void>.value();

  Future<void> set(WindowMode mode) {
    final task = _chain.then((_) => _apply(mode));
    _chain = task;
    return task;
  }

  Future<void> _apply(WindowMode mode) async {
    if (_unsupported || _current == mode) return;
    final previous = _current;
    _current = mode;
    try {
      await _channel.invokeMethod<void>(mode.name);
    } on MissingPluginException {
      // No native handler at all (headless test, a future mobile target) —
      // permanent for the process; stop calling rather than retry-spamming.
      _unsupported = true;
    } on PlatformException {
      // A TRANSIENT native failure: roll the tracked mode back so the next
      // request for this mode re-applies instead of silently no-oping against
      // a window that never actually changed. Deliberately narrow — a Dart
      // programming error must surface, not masquerade as "transient"
      // (review #846 r2).
      _current = previous;
    }
  }
}

final windowModeProvider = Provider<WindowModeService>(
  (_) => WindowModeService(),
);
