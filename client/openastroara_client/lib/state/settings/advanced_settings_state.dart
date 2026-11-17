import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/advanced_settings_prefs_service.dart';

/// The prefs file backing [advancedSettingsProvider]; overridden in tests.
final advancedSettingsPrefsProvider = Provider<AdvancedSettingsPrefsService>(
  (ref) => AdvancedSettingsPrefsService(),
);

/// #1544 — the one global Advanced toggle of Settings. Off (the default), a
/// panel shows only the settings whose registry tier is `simple`; on, it shows
/// the `advanced` ones too. A per-device view preference, kept in the app
/// support directory, not in the rig's profile.
class AdvancedSettings extends AsyncNotifier<bool> {
  // A toggle that landed before the first read finished must win over it.
  bool? _pending;

  @override
  Future<bool> build() async {
    final loaded = await ref.read(advancedSettingsPrefsProvider).load();
    return _pending ?? loaded;
  }

  Future<void> set(bool on) async {
    _pending = on;
    state = AsyncData(on);
    await ref.read(advancedSettingsPrefsProvider).save(on);
  }
}

final advancedSettingsProvider = AsyncNotifierProvider<AdvancedSettings, bool>(
  AdvancedSettings.new,
);
