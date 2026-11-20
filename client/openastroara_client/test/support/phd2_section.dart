import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/settings/loaded_section.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';

/// The profile's guider settings for a widget test, with no HTTP (a Dio
/// request leaves a timer pending in a test that only pumps). The section
/// loads itself as in the app once a rig is there: [initial] is the rig's
/// copy, or null for a rig that does not answer. Every save that reached "the
/// rig" is in [puts] with its `If-Match` in [ifMatches]; [refuse] fails each
/// save instead (nothing written).
class FakePhd2Section extends Phd2SettingsNotifier {
  FakePhd2Section({this.initial = const Phd2Settings()});

  Phd2Settings? initial;
  final List<Phd2Settings> puts = [];
  final List<String?> ifMatches = [];
  Object? refuse;
  int loads = 0;

  @override
  Future<Tagged<Phd2Settings>> fetch(ProfileApi api) async {
    loads++;
    return (
      value: initial ?? (throw StateError('rig unreachable')),
      etag: '"v${puts.length + 1}"',
    );
  }

  @override
  Future<Tagged<Phd2Settings>> send(
    ProfileApi api,
    Phd2Settings value,
    String? etag,
  ) async {
    if (refuse case final e?) throw e;
    puts.add(value);
    ifMatches.add(etag);
    initial = value;
    return (value: value, etag: '"v${puts.length + 1}"');
  }
}

/// The guider settings as the rig's copy from the start (no load, no HTTP),
/// for a test that reads or edits them with no rig to load them from.
Override phd2FromRig([Phd2Settings settings = const Phd2Settings()]) =>
    phd2SettingsProvider.overrideWith(() => _RigPhd2(settings));

class _RigPhd2 extends Phd2SettingsNotifier {
  _RigPhd2(this.initial);
  final Phd2Settings initial;
  @override
  LoadedSection<Phd2Settings> build() =>
      LoadedSection(value: initial, source: SectionSource.rig, etag: '"v1"');
}
