import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../services/simple_mode_prefs_service.dart';

/// The prefs file backing [simpleModeProvider]; overridden in tests.
final simpleModePrefsProvider = Provider<SimpleModePrefsService>(
  (ref) => SimpleModePrefsService(),
);

/// Persisted "Simple Mode" UI preference (off by default: the full UI stays
/// the desktop default). The root router shows `SimpleShell` instead of
/// `AppShell` while it is on; switching resets nothing on the daemon.
class SimpleModeController extends AsyncNotifier<bool> {
  /// A switch that landed before the initial prefs read finished, so the
  /// read's late result can't undo it (same race as NightModeController).
  bool? _pending;

  @override
  Future<bool> build() async {
    final loaded = await ref.read(simpleModePrefsProvider).load();
    return _pending ?? loaded;
  }

  Future<void> set(bool on) async {
    _pending = on;
    state = AsyncData(on);
    await ref.read(simpleModePrefsProvider).save(on);
  }
}

final simpleModeProvider = AsyncNotifierProvider<SimpleModeController, bool>(
  SimpleModeController.new,
);
