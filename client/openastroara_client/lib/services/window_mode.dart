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

  Future<void> set(WindowMode mode) async {
    if (_current == mode) return;
    _current = mode;
    try {
      await _channel.invokeMethod<void>(mode.name);
    } catch (_) {/* no native handler — headless test or unsupported target */}
  }
}

final windowModeProvider = Provider<WindowModeService>(
  (_) => WindowModeService(),
);
