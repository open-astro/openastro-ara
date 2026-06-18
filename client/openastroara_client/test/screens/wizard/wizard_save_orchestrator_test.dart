import 'package:flutter_test/flutter_test.dart';
// Hide the draft's ImagingDefaults; the section model of the same name comes from
// imaging_defaults_state.
import 'package:openastroara/models/profile_draft.dart'
    hide ImagingDefaults, PlateSolveSettings, AutofocusSettings;
import 'package:openastroara/models/profile_meta.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/wizard/wizard_save.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/settings/autofocus_settings_state.dart';
import 'package:openastroara/state/settings/imaging_defaults_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';
import 'package:openastroara/state/settings/plate_solve_settings_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/settings/storage_settings_state.dart';

/// In-memory ProfileApi double — overrides only the methods saveWizardProfile
/// calls. The inherited Dio is never used (all calls are overridden), so the
/// dummy server is harmless.
class _FakeProfileApi extends ProfileApi {
  _FakeProfileApi() : super(const AraServer(hostname: 'test', port: 1));

  int createCalls = 0;
  String createReturnsId = 'profile-1';
  final Set<String> failSections = {}; // section keys whose PUT throws
  final List<String> putCalls = [];

  @override
  Future<ProfileMeta> createProfile(String name) async {
    createCalls++;
    return ProfileMeta(id: createReturnsId, name: name);
  }

  Future<T> _put<T>(String key, T value) async {
    if (failSections.contains(key)) throw StateError('$key PUT failed');
    putCalls.add(key);
    return value;
  }

  @override
  Future<SiteSettings> getSiteSettings() async => const SiteSettings();
  @override
  Future<SiteSettings> putSiteSettings(SiteSettings v) => _put('site', v);
  @override
  Future<OpticsSettings> getOptics() async => const OpticsSettings();
  @override
  Future<OpticsSettings> putOptics(OpticsSettings v) => _put('optics', v);
  @override
  Future<ImagingDefaults> getImagingDefaults() async => const ImagingDefaults();
  @override
  Future<ImagingDefaults> putImagingDefaults(ImagingDefaults v) => _put('imaging', v);
  @override
  Future<Phd2Settings> getPhd2Settings() async => const Phd2Settings();
  @override
  Future<Phd2Settings> putPhd2Settings(Phd2Settings v) => _put('phd2', v);
  @override
  Future<PlateSolveSettings> getPlateSolveSettings() async =>
      const PlateSolveSettings();
  @override
  Future<PlateSolveSettings> putPlateSolveSettings(PlateSolveSettings v) =>
      _put('platesolve', v);
  @override
  Future<AutofocusSettings> getAutofocusSettings() async =>
      const AutofocusSettings();
  @override
  Future<AutofocusSettings> putAutofocusSettings(AutofocusSettings v) =>
      _put('autofocus', v);
  @override
  Future<StorageSettings> getStorageSettings() async => const StorageSettings();
  @override
  Future<StorageSettings> putStorageSettings(StorageSettings v) =>
      _put('storage', v);
}

void main() {
  group('saveWizardProfile orchestration', () {
    test('happy path creates once and PUTs every section', () async {
      final api = _FakeProfileApi();
      final draft = ProfileDraft()..profileName = 'Rig';
      await saveWizardProfile(api, draft);

      expect(api.createCalls, 1);
      expect(
          api.putCalls,
          containsAll([
            'site',
            'optics',
            'imaging',
            'phd2',
            'platesolve',
            'autofocus',
            'storage'
          ]));
      expect(draft.savedProfileId, 'profile-1');
    });

    test('a section failure throws but still records the created id', () async {
      final api = _FakeProfileApi()..failSections.add('optics');
      final draft = ProfileDraft()..profileName = 'Rig';

      await expectLater(saveWizardProfile(api, draft), throwsA(isA<Exception>()));
      expect(api.createCalls, 1);
      expect(draft.savedProfileId, 'profile-1',
          reason: 'id is stamped so a retry re-uses the same profile');
    });

    test('a retry after failure does NOT create a second profile', () async {
      final api = _FakeProfileApi()..failSections.add('optics');
      final draft = ProfileDraft()..profileName = 'Rig';
      await expectLater(saveWizardProfile(api, draft), throwsA(isA<Exception>()));

      api.failSections.clear(); // the transient failure clears
      await saveWizardProfile(api, draft); // retry

      expect(api.createCalls, 1, reason: 'createProfile is skipped once savedProfileId is set');
      expect(api.putCalls, containsAll(['site', 'optics', 'imaging', 'phd2']));
    });

    test('aggregates every concurrent section failure into one error', () async {
      final api = _FakeProfileApi()..failSections.addAll({'site', 'phd2'});
      final draft = ProfileDraft()..profileName = 'Rig';

      await expectLater(
        saveWizardProfile(api, draft),
        throwsA(predicate((e) => e.toString().contains('2 profile section'))),
      );
    });
  });
}
