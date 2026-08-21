import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../services/night_mode_prefs_service.dart';

/// The prefs file backing [nightModeProvider]; overridden in tests.
final nightModePrefsProvider = Provider<NightModePrefsService>(
  (ref) => NightModePrefsService(),
);

/// Persisted "night mode" UI preference (off by default). Night mode renders
/// as a red colour filter over the whole app (see `main.dart`), so toggling is
/// just this flag — nothing else in the UI needs to know about it.
class NightModeController extends AsyncNotifier<bool> {
  /// A toggle that landed before the initial prefs read finished. Without it
  /// the read's result would overwrite the user's just-made choice when it
  /// resolves — a small window, but the file read is on the launch path where
  /// the first frame and a hotkey press can genuinely race.
  bool? _pending;

  @override
  Future<bool> build() async {
    final loaded = await ref.read(nightModePrefsProvider).load();
    return _pending ?? loaded;
  }

  Future<void> set(bool on) async {
    _pending = on;
    state = AsyncData(on);
    await ref.read(nightModePrefsProvider).save(on);
  }

  Future<void> toggle() => set(
    !(switch (state) {
      AsyncData(:final value) => value,
      _ => false,
    }),
  );
}

final nightModeProvider = AsyncNotifierProvider<NightModeController, bool>(
  NightModeController.new,
);
