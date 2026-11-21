import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/settings/loaded_section.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';

/// The profile's site for a widget test, with no HTTP (a Dio request leaves a
/// timer pending in a test that only pumps). The section loads itself as in
/// the app once a rig is there: [initial] is the rig's copy, or null for a
/// rig that does not answer. Every save that reached "the rig" is in [puts];
/// [refuse] fails each save instead (nothing written).
class FakeSiteSection extends SiteSettingsNotifier {
  FakeSiteSection({this.initial = const SiteSettings()});

  SiteSettings? initial;
  final List<SiteSettings> puts = [];
  Object? refuse;

  @override
  Future<Tagged<SiteSettings>> fetch(ProfileApi api) async =>
      (value: initial ?? (throw StateError('rig unreachable')), etag: '"v1"');

  @override
  Future<Tagged<SiteSettings>> send(
    ProfileApi api,
    SiteSettings value,
    String? etag,
  ) async {
    if (refuse case final e?) throw e;
    puts.add(value);
    initial = value;
    return (value: value, etag: '"v${puts.length + 1}"');
  }
}

/// The site as the rig's copy from the start (no load, no HTTP), for a test
/// that reads or edits it with no rig to load it from.
Override siteFromRig([SiteSettings site = const SiteSettings()]) =>
    siteSettingsProvider.overrideWith(() => _RigSite(site));

class _RigSite extends SiteSettingsNotifier {
  _RigSite(this.initial);
  final SiteSettings initial;
  @override
  LoadedSection<SiteSettings> build() =>
      LoadedSection(value: initial, source: SectionSource.rig, etag: '"v1"');
}
