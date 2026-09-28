import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../state/night_mode_state.dart';

/// Wraps the app root so Ctrl+N (Cmd+N on macOS) toggles night mode from
/// anywhere. It WAS a bare `N` with a "skip while a text field has focus"
/// guard — which didn't cover every place you can type (typing "ldn" into the
/// planetarium search flipped the display mid-word). A modified chord can't
/// collide with typing, so no guard is needed. The [Focus] gives the shortcut
/// a target to receive keys.
///
/// A const widget rather than a helper function (#1111): the helper built a
/// fresh `CallbackShortcuts` + `Focus(autofocus: true)` on every root
/// rebuild; this widget only rebuilds when its own dependencies change, and
/// it has none.
class NightHotkey extends ConsumerWidget {
  const NightHotkey({super.key, required this.child});

  final Widget child;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    void toggle() => ref.read(nightModeProvider.notifier).toggle();
    return CallbackShortcuts(
      bindings: <ShortcutActivator, VoidCallback>{
        const SingleActivator(LogicalKeyboardKey.keyN, control: true): toggle,
        const SingleActivator(LogicalKeyboardKey.keyN, meta: true): toggle,
      },
      child: Focus(autofocus: true, child: child),
    );
  }
}
