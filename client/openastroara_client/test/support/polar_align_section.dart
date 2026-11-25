import 'dart:async';

import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/loaded_section.dart';
import 'package:openastroara/state/settings/polar_align_settings_state.dart';

import 'fake_section_rig.dart';

/// The daemon's polar-align section on a fresh profile (its section defaults,
/// [rigDefaults]'s `polar-align` copy): the client holds none of its own.
const rigPolarAlign = PolarAlignSettings(
  exposureSeconds: 1.0,
  binning: 0,
  targetToleranceArcmin: 1.0,
  seedRotationDeg: 10.0,
  loopCadenceMs: 1000,
  settleSeconds: 2.0,
  loopMode: PolarAlignLoopModes.loop,
  checkStepDeg: 10.0,
  coarseExposureSeconds: 3.0,
  polarAlignerAuto: true,
  stopTrackingWhenDone: true,
  camera: PolarAlignCameras.guide,
  framesPerPoint: 3,
  eastDirection: null,
);

/// The profile's polar-align section for a widget test, with no HTTP (a Dio
/// request leaves a timer pending in a test that only pumps). The section
/// loads itself as in the app: [initial] is the rig's copy, or null for a rig
/// that does not answer (set it later for a rig that answers again). Every
/// save sent is in [sent], and every one that reached "the rig" in [puts];
/// [refuse] fails each save instead (nothing written), and [gate] holds each
/// one's answer until completed.
///
/// [loaded]: the rig's copy is in from the first frame (no load), for a test
/// about what the screen does with it rather than how it arrives.
class FakePolarAlignSection extends PolarAlignSettingsNotifier {
  FakePolarAlignSection({this.initial = rigPolarAlign, this.loaded = false});

  final bool loaded;

  @override
  LoadedSection<PolarAlignSettings> build() {
    final notLoaded = super.build();
    final copy = initial;
    return loaded && copy != null
        ? LoadedSection(value: copy, source: SectionSource.rig, etag: '"v1"')
        : notLoaded;
  }

  PolarAlignSettings? initial;
  final List<PolarAlignSettings> sent = [];
  final List<PolarAlignSettings> puts = [];
  Object? refuse;
  Completer<void>? gate;

  /// Every load the rig was asked for.
  int loads = 0;

  @override
  Future<Tagged<PolarAlignSettings>> fetch(ProfileApi api) async {
    loads++;
    return (
      value: initial ?? (throw StateError('rig unreachable')),
      etag: '"v1"',
    );
  }

  @override
  Future<Tagged<PolarAlignSettings>> send(
    ProfileApi api,
    PolarAlignSettings value,
    String? etag,
  ) async {
    sent.add(value);
    await gate?.future;
    if (refuse case final e?) throw e;
    puts.add(value);
    return (value: initial = value, etag: '"v${puts.length + 1}"');
  }
}

/// [section] as the profile's polar-align section, on a rig the profile API
/// reaches (the fake never sends a request through it).
List<Override> polarAlignSectionOverrides(FakePolarAlignSection section) => [
  profileApiProvider.overrideWithValue(FakeSectionRig({}).api()),
  polarAlignSettingsProvider.overrideWith(() => section),
];
