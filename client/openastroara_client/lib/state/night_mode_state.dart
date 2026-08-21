import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../services/night_mode_prefs_service.dart';

/// Build-time selected night-mode rendering: `'overlay'` (a red colour filter
/// laid over the normal dark UI) or `'theme'` (a full red Material 3 theme).
/// Choose at build time with `--dart-define=NIGHT_MODE_KIND=overlay|theme`;
/// both share the same toggle, hotkey and persistence. Defaults to `overlay`.
const String nightModeKind = String.fromEnvironment(
  'NIGHT_MODE_KIND',
  defaultValue: 'overlay',
);

/// Whether the active build renders night mode as a red overlay (vs a theme).
bool get isNightModeOverlay => nightModeKind == 'overlay';

/// The prefs file backing [nightModeProvider]; overridden in tests.
final nightModePrefsProvider = Provider<NightModePrefsService>(
  (ref) => NightModePrefsService(),
);

/// Persisted "night mode" UI preference (off by default). The rendering
/// approach is the build-time [nightModeKind], so toggling just flips the flag.
class NightModeController extends AsyncNotifier<bool> {
  @override
  Future<bool> build() => ref.read(nightModePrefsProvider).load();

  Future<void> set(bool on) async {
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
