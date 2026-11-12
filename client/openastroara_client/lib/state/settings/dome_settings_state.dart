import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/dome_settings.dart';
import '../profile_management_state.dart';

/// #1689 — the profile's dome geometry and follow-the-mount setting. A value
/// only once the daemon has answered, and [save] keeps the old value until the
/// daemon has taken the new one (it refuses a scope outside the dome).
class DomeSettingsNotifier extends AsyncNotifier<DomeSettings> {
  @override
  Future<DomeSettings> build() async {
    final api = ref.watch(profileApiProvider);
    // Another profile, another dome.
    ref.watch(profileManagementProvider.select((p) => p.value?.activeId));
    return api == null ? const DomeSettings() : api.getDomeSettings();
  }

  /// Throws when the daemon refused, for the caller to say why.
  Future<void> save(DomeSettings next) async {
    final api = ref.read(profileApiProvider);
    if (api == null) throw StateError('no rig connected');
    state = AsyncData(await api.putDomeSettings(next));
  }
}

final domeSettingsProvider =
    AsyncNotifierProvider<DomeSettingsNotifier, DomeSettings>(
      DomeSettingsNotifier.new,
    );
