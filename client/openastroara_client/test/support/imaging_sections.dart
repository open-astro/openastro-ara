import 'package:dio/dio.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/settings/autofocus_settings_state.dart';
import 'package:openastroara/state/settings/imaging_defaults_state.dart';
import 'package:openastroara/state/settings/loaded_section.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/unguided_settings_state.dart';
import 'settings_values.dart';

/// The profile's imaging defaults for a widget test, with no HTTP (a Dio
/// request leaves a timer pending in a test that only pumps). The section
/// loads itself as in the app: [initial] is the rig's copy, or null for a rig
/// that does not answer (set it later for a rig that answers again). Every save that reached "the rig" is in [puts];
/// [refuse] fails each save instead (nothing written).
class FakeImagingDefaultsSection extends ImagingDefaultsNotifier {
  FakeImagingDefaultsSection({this.initial = const TestImagingDefaults()});

  ImagingDefaults? initial;
  final List<ImagingDefaults> puts = [];
  Object? refuse;

  @override
  Future<Tagged<ImagingDefaults>> fetch(ProfileApi api) async =>
      (value: initial ?? (throw StateError('rig unreachable')), etag: '"v1"');

  @override
  Future<Tagged<ImagingDefaults>> send(
    ProfileApi api,
    ImagingDefaults value,
    String? etag,
  ) async {
    if (refuse case final e?) throw e;
    puts.add(value);
    return (value: value, etag: '"v${puts.length + 1}"');
  }
}

/// [FakeImagingDefaultsSection] for the autofocus section.
class FakeAutofocusSection extends AutofocusSettingsNotifier {
  FakeAutofocusSection({this.initial = const TestAutofocusSettings()});

  AutofocusSettings? initial;
  final List<AutofocusSettings> puts = [];
  Object? refuse;

  @override
  Future<Tagged<AutofocusSettings>> fetch(ProfileApi api) async =>
      (value: initial ?? (throw StateError('rig unreachable')), etag: '"v1"');

  @override
  Future<Tagged<AutofocusSettings>> send(
    ProfileApi api,
    AutofocusSettings value,
    String? etag,
  ) async {
    if (refuse case final e?) throw e;
    puts.add(value);
    return (value: value, etag: '"v${puts.length + 1}"');
  }
}

/// [FakeImagingDefaultsSection] for the optics section.
class FakeOpticsSection extends OpticsSettingsNotifier {
  FakeOpticsSection({this.initial = const TestOpticsSettings()});

  OpticsSettings? initial;
  final List<OpticsSettings> puts = [];
  Object? refuse;

  @override
  Future<Tagged<OpticsSettings>> fetch(ProfileApi api) async =>
      (value: initial ?? (throw StateError('rig unreachable')), etag: '"v1"');

  @override
  Future<Tagged<OpticsSettings>> send(
    ProfileApi api,
    OpticsSettings value,
    String? etag,
  ) async {
    if (refuse case final e?) throw e;
    puts.add(value);
    return (value: value, etag: '"v${puts.length + 1}"');
  }
}

/// The rig's refusal of a save as stale (412): set as a fake's `refuse`.
DioException staleSave() {
  final req = RequestOptions(path: '/profile/section');
  return DioException(
    requestOptions: req,
    response: Response(requestOptions: req, statusCode: 412),
  );
}

/// The sections a run is built from as the rig's copies from the start (no
/// load, no HTTP), for a test that builds a run: the create refuses without
/// them (ADR 0015). Loads and saves still go through the real API. A null
/// [unguided] leaves that section not loaded (only a rig with no guider needs
/// it).
List<Override> imagingSectionsFromRig({
  ImagingDefaults defaults = const TestImagingDefaults(),
  AutofocusSettings autofocus = const TestAutofocusSettings(),
  UnguidedSettings? unguided = const TestUnguidedSettings(),
}) => [
  imagingDefaultsFromRig(defaults),
  autofocusSettingsProvider.overrideWith(() => _RigAutofocus(autofocus)),
  if (unguided != null)
    unguidedSettingsProvider.overrideWith(() => _RigUnguided(unguided)),
];

/// The imaging defaults alone as the rig's copy (Simple Mode's plan rows take
/// their sub from it; with none loaded there are no rows).
Override imagingDefaultsFromRig([
  ImagingDefaults defaults = const TestImagingDefaults(),
]) => imagingDefaultsProvider.overrideWith(() => _RigImagingDefaults(defaults));

class _RigUnguided extends UnguidedSettingsNotifier {
  _RigUnguided(this.initial);
  final UnguidedSettings initial;
  @override
  LoadedSection<UnguidedSettings> build() =>
      LoadedSection(value: initial, source: SectionSource.rig, etag: '"v1"');
}

class _RigImagingDefaults extends ImagingDefaultsNotifier {
  _RigImagingDefaults(this.initial);
  final ImagingDefaults initial;
  @override
  LoadedSection<ImagingDefaults> build() =>
      LoadedSection(value: initial, source: SectionSource.rig, etag: '"v1"');
}

class _RigAutofocus extends AutofocusSettingsNotifier {
  _RigAutofocus(this.initial);
  final AutofocusSettings initial;
  @override
  LoadedSection<AutofocusSettings> build() =>
      LoadedSection(value: initial, source: SectionSource.rig, etag: '"v1"');
}
