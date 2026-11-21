import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/dome_settings.dart';
import '../../services/profile_api.dart';
import '../equipment/dome_state.dart';
import '../profile_management_state.dart';
import 'loaded_section.dart';

/// #1689 — the profile's dome geometry and follow-the-mount setting. A value
/// only once the rig has answered (ADR 0015). [apply] shows the new geometry
/// while it saves; a failed save (such as a scope outside the dome) puts the
/// rig's copy back, except a 412: the edit stays until Reload.
class DomeSettingsNotifier extends SelfLoadingSection<DomeSettings> {
  @override
  LoadedSection<DomeSettings> build() {
    // The follow switch (or the daemon turning following off: park, safety,
    // emergency stop) makes the daemon write this section outside the profile
    // routes. A daemon with #1807 publishes `profile.updated` for it; an older
    // one does not, so load it again here, or the next geometry edit is sent
    // against a tag the rig no longer holds.
    ref.listen(domeProvider.select((s) => s.value?.following), (before, after) {
      final api = ref.read(profileApiProvider);
      if (before != null && before != after && api != null) load(api);
    });
    return super.build();
  }

  /// Throws when the rig refused, for the caller to say why.
  Future<void> apply(DomeSettings next) => replaceAndSave(next);

  @override
  Future<Tagged<DomeSettings>> fetch(ProfileApi api) => api.getDomeSection();

  @override
  Future<Tagged<DomeSettings>> send(
    ProfileApi api,
    DomeSettings value,
    String? etag,
  ) => api.putDomeSection(value, ifMatch: etag);
}

final domeSettingsProvider =
    NotifierProvider<DomeSettingsNotifier, LoadedSection<DomeSettings>>(
      DomeSettingsNotifier.new,
    );
