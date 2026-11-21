import '../../support/fake_section_rig.dart';
import '../../support/storage_fixture.dart';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
// Hide the draft's ImagingDefaults; the section model of the same name comes from
// imaging_defaults_state.
import 'package:openastroara/models/profile_draft.dart'
    hide ImagingDefaults, PlateSolveSettings, AutofocusSettings, SafetyPolicies;
import 'package:openastroara/models/profile_meta.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/switch_device.dart' show CoolingFan;
import 'package:openastroara/screens/wizard/wizard_save.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/settings/autofocus_settings_state.dart';
import 'package:openastroara/state/settings/camera_electronics_state.dart';
import 'package:openastroara/state/settings/filter_set_state.dart';
import 'package:openastroara/state/settings/imaging_defaults_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';
import 'package:openastroara/state/settings/plate_solve_settings_state.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/settings/storage_settings_state.dart';
import '../../support/settings_values.dart';

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

  bool failTransport = false; // every PUT dies with no response (link gone)

  Future<({T value, String? etag})> _put<T>(
    String key,
    T value,
    String? ifMatch,
  ) async {
    if (failTransport) {
      throw DioException(
        requestOptions: RequestOptions(path: '/api/v1/profile/$key'),
        type: DioExceptionType.connectionError,
      );
    }
    if (failSections.contains(key)) throw StateError('$key PUT failed');
    putCalls.add(key);
    return (value: value, etag: ifMatch);
  }

  static Future<({T value, String? etag})> _get<T>(T value) async =>
      (value: value, etag: '"v1"');

  @override
  Future<({CoolingFan value, String? etag})> getCoolingFanSection() =>
      _get(const CoolingFan());
  @override
  Future<({CoolingFan value, String? etag})> putCoolingFanSection(
    CoolingFan v, {
    String? ifMatch,
  }) => _put('cooling-fan', v, ifMatch);
  @override
  Future<({SiteSettings value, String? etag})> getSiteSection() =>
      _get(const TestSiteSettings());
  @override
  Future<({SiteSettings value, String? etag})> putSiteSection(
    SiteSettings v, {
    String? ifMatch,
  }) => _put('site', v, ifMatch);
  @override
  Future<OpticsSettings> getOptics() async => const TestOpticsSettings();
  @override
  Future<({OpticsSettings value, String? etag})> getOpticsSection() =>
      _get(const TestOpticsSettings());
  @override
  Future<({OpticsSettings value, String? etag})> putOpticsSection(
    OpticsSettings v, {
    String? ifMatch,
  }) => _put('optics', v, ifMatch);
  @override
  Future<({ImagingDefaults value, String? etag})> getImagingDefaultsSection() =>
      _get(const TestImagingDefaults());
  @override
  Future<({ImagingDefaults value, String? etag})> putImagingDefaultsSection(
    ImagingDefaults v, {
    String? ifMatch,
  }) => _put('imaging', v, ifMatch);
  @override
  Future<({Phd2Settings value, String? etag})> getPhd2Section() =>
      _get(const TestPhd2Settings());
  @override
  Future<({Phd2Settings value, String? etag})> putPhd2Section(
    Phd2Settings v, {
    String? ifMatch,
  }) => _put('phd2', v, ifMatch);
  @override
  Future<({PlateSolveSettings value, String? etag})> getPlateSolveSection() =>
      _get(const TestPlateSolveSettings());
  @override
  Future<({PlateSolveSettings value, String? etag})> putPlateSolveSection(
    PlateSolveSettings v, {
    String? ifMatch,
  }) => _put('platesolve', v, ifMatch);
  @override
  Future<({AutofocusSettings value, String? etag})> getAutofocusSection() =>
      _get(const TestAutofocusSettings());
  @override
  Future<({AutofocusSettings value, String? etag})> putAutofocusSection(
    AutofocusSettings v, {
    String? ifMatch,
  }) => _put('autofocus', v, ifMatch);
  @override
  Future<({CameraElectronics value, String? etag})>
  getCameraElectronicsSection() => _get(const TestCameraElectronics());
  @override
  Future<({CameraElectronics value, String? etag})> putCameraElectronicsSection(
    CameraElectronics v, {
    String? ifMatch,
  }) => _put('electronics', v, ifMatch);
  @override
  Future<({FilterSetSettings value, String? etag})> getFilterSetSection() =>
      _get(const FilterSetSettings());
  @override
  Future<({FilterSetSettings value, String? etag})> putFilterSetSection(
    FilterSetSettings v, {
    String? ifMatch,
  }) => _put('filterset', v, ifMatch);
  @override
  Future<({StorageSettings value, String? etag})> getStorageSection() =>
      _get(kTestStorage);
  @override
  Future<({StorageSettings value, String? etag})> putStorageSection(
    StorageSettings v, {
    String? ifMatch,
  }) => _put('storage', v, ifMatch);
  @override
  Future<({SafetyPolicies value, String? etag})> getSafetySection() =>
      _get(const TestSafetyPolicies());
  @override
  Future<({SafetyPolicies value, String? etag})> putSafetySection(
    SafetyPolicies v, {
    String? ifMatch,
  }) => _put('safety', v, ifMatch);
}

/// The wizard against the rig at the HTTP level (ADR 0015): it serves every
/// section with a tag and refuses a PUT with none (428) or a stale one (412).
class _RigApi extends ProfileApi {
  _RigApi(FakeSectionRig rig)
    : super(
        const AraServer(hostname: 'rig.test', port: 1),
        dio: Dio(BaseOptions(baseUrl: 'http://rig.test:1'))
          ..httpClientAdapter = rig,
      );

  @override
  Future<ProfileMeta> createProfile(String name) async =>
      ProfileMeta(id: 'profile-1', name: name);
}

void main() {
  test('the wizard sends every section the tag of the copy it loaded', () async {
    final rig = FakeSectionRig({...rigDefaults, 'storage': rigStorage});
    await saveWizardProfile(
      _RigApi(rig),
      ProfileDraft()
        ..profileName = 'Rig'
        ..coolingFan = const CoolingFan(deviceId: 'box', portId: 3),
    );
    expect(rig.ifMatches, isNot(contains(null)));
    expect({for (final p in rig.puts) p.section: p.ifMatch}, {
      for (final s in [
        'site',
        'optics',
        'imaging-defaults',
        'phd2',
        'plate-solve',
        'autofocus',
        'camera-electronics',
        'filter-set',
        'storage',
        'safety',
        'cooling-fan',
      ])
        s: '"$s-v1"',
    });
  });

  test('a focal length change on a rig with a confirmed first flip saves safety with the tag after the optics write', () async {
    final rig = FakeSectionRig({
      ...rigDefaults,
      'storage': rigStorage,
      'safety': {
        ...rigDefaults['safety']!,
        'first_flip_confirmed': true,
        'first_flip_completed_utc': '2026-11-01T22:14:00Z',
        'first_flip_pier_side_changed': true,
      },
    });
    final d = ProfileDraft()..profileName = 'Rig';
    d.telescope.focalLengthMm = 400;
    await saveWizardProfile(_RigApi(rig), d);
    expect(rig.ifMatches, isNot(contains(null)));
    final tags = {for (final p in rig.puts) p.section: p.ifMatch};
    expect(tags.keys, containsAll(['optics', 'safety']));
    // The optics write moved safety to v2; the safety PUT carries that tag.
    expect(tags['safety'], '"safety-v2"');
    expect((rig.sectionOf('optics') as Map)['focal_length_mm'], 400);
    expect((rig.sectionOf('safety') as Map)['first_flip_confirmed'], isFalse);
  });

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
          'electronics',
          'filterset',
          'storage',
          'safety',
        ]),
      );
      expect(draft.savedProfileId, 'profile-1');
    });

    test('a section failure throws but still records the created id', () async {
      final api = _FakeProfileApi()..failSections.add('optics');
      final draft = ProfileDraft()..profileName = 'Rig';

      await expectLater(
        saveWizardProfile(api, draft),
        throwsA(isA<Exception>()),
      );
      expect(api.createCalls, 1);
      expect(
        draft.savedProfileId,
        'profile-1',
        reason: 'id is stamped so a retry re-uses the same profile',
      );
    });

    test('a retry after failure does NOT create a second profile', () async {
      final api = _FakeProfileApi()..failSections.add('optics');
      final draft = ProfileDraft()..profileName = 'Rig';
      await expectLater(
        saveWizardProfile(api, draft),
        throwsA(isA<Exception>()),
      );

      api.failSections.clear(); // the transient failure clears
      await saveWizardProfile(api, draft); // retry

      expect(
        api.createCalls,
        1,
        reason: 'createProfile is skipped once savedProfileId is set',
      );
      expect(api.putCalls, containsAll(['site', 'optics', 'imaging', 'phd2']));
    });

    test(
      'aggregates every concurrent section failure into one error',
      () async {
        final api = _FakeProfileApi()..failSections.addAll({'site', 'phd2'});
        final draft = ProfileDraft()..profileName = 'Rig';

        await expectLater(
          saveWizardProfile(api, draft),
          throwsA(predicate((e) => e.toString().contains('2 profile section'))),
        );
      },
    );
  });

  test('#1137: every section dying in transit is a ProfileSaveTransportFailure; a rejected section is not', () async {
    final api = _FakeProfileApi()..failTransport = true;
    final draft = ProfileDraft()..profileName = 'Lost';
    await expectLater(
      saveWizardProfile(api, draft),
      throwsA(isA<ProfileSaveTransportFailure>()),
    );
    expect(
      draft.savedProfileId,
      'profile-1',
      reason: 'the create landed before the link dropped',
    );

    final mixed = _FakeProfileApi()..failSections.add('site');
    await expectLater(
      saveWizardProfile(mixed, ProfileDraft()..profileName = 'Rejected'),
      throwsA(
        isA<Exception>().having(
          (e) => e.toString(),
          'message',
          contains('1 profile section'),
        ),
      ),
    );
  });

  test('#1700: a chosen cooling fan is saved; a wizard that left it alone sends nothing', () async {
    final api = _FakeProfileApi();
    await saveWizardProfile(
      api,
      ProfileDraft()
        ..profileName = 'P'
        ..coolingFan = const CoolingFan(deviceId: 'box', portId: 3),
    );
    expect(api.putCalls, contains('cooling-fan'));
    final untouched = _FakeProfileApi();
    await saveWizardProfile(untouched, ProfileDraft()..profileName = 'P');
    expect(untouched.putCalls, isNot(contains('cooling-fan')));
  });
}
