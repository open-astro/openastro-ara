import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/polar_align.dart';
import '../../services/profile_api.dart';
import 'loaded_section.dart';

/// The profile's polar-align settings (`/api/v1/profile/polar-align`: exposure,
/// Loop/Single, tolerance, the turns, the Polar Aligner's motors). ADR 0015: a
/// value only once the rig has answered, never a client default. Loads itself:
/// the Settings panel, the Imaging tab, Simple Mode's Polar align screen and
/// its Polar Aligner row read it with no panel opened first. The running
/// routine re-reads these settings on the rig before every frame, so a save
/// applies to the next one.
class PolarAlignSettingsNotifier
    extends SelfLoadingSection<PolarAlignSettings> {
  /// A knob that saves on change: [change] applied to the rig's copy and
  /// saved whole. Not loaded from the rig: loaded once more first, and refused
  /// with nothing sent when that fails. A failed save puts the rig's copy
  /// back; a 412 keeps the change and flags the conflict (rule 6). Only for
  /// the Settings panel, whose conflict banner offers Reload: the Imaging tab
  /// and Simple Mode save through [changeAndSave], which reloads on a 412.
  Future<void> saveKnob(
    PolarAlignSettings Function(PolarAlignSettings) change,
  ) {
    final v = state.value;
    return v == null ? editAndSave(change) : replaceAndSave(change(v));
  }

  @override
  Future<Tagged<PolarAlignSettings>> fetch(ProfileApi api) =>
      api.getPolarAlignSection();

  @override
  Future<Tagged<PolarAlignSettings>> send(
    ProfileApi api,
    PolarAlignSettings value,
    String? etag,
  ) => api.putPolarAlignSection(value, ifMatch: etag);
}

final polarAlignSettingsProvider =
    NotifierProvider<
      PolarAlignSettingsNotifier,
      LoadedSection<PolarAlignSettings>
    >(PolarAlignSettingsNotifier.new);
