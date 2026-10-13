import 'dart:io';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/profile_draft.dart' show ProfileDraft;
import 'package:openastroara/models/profile_list.dart';
import 'package:openastroara/models/profile_meta.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/pending_profile_draft_service.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/pending_profile_drafts_state.dart';
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

/// A ProfileApi double for the replay: records creates, can fail one section.
class _FakeProfileApi extends ProfileApi {
  _FakeProfileApi() : super(const AraServer(hostname: 'test', port: 1));
  final created = <String>[];
  bool failSite = false; // in transit: no response at all
  bool rejectSite = false; // the daemon answers 422
  int n = 0;

  @override
  Future<ProfileMeta> createProfile(String name) async {
    created.add(name);
    activeId = 'profile-${++n}';
    known.add(activeId!);
    return ProfileMeta(id: activeId!, name: name);
  }

  // The daemon's profile list: which one is active, and which exist.
  String? activeId;
  final known = <String>[];
  final selected = <String>[];
  @override
  Future<ProfileList> listProfiles() async => ProfileList(
    activeId: activeId,
    profiles: [for (final id in known) ProfileMeta(id: id, name: id)],
  );
  @override
  Future<void> selectProfile(String id) async {
    selected.add(id);
    activeId = id;
  }

  @override
  Future<SiteSettings> getSiteSettings() async => const SiteSettings();
  @override
  Future<SiteSettings> putSiteSettings(SiteSettings v) async {
    if (failSite) {
      throw DioException(
        requestOptions: RequestOptions(path: '/api/v1/profile/site'),
        type: DioExceptionType.connectionError,
      );
    }
    if (rejectSite) {
      throw DioException(
        requestOptions: RequestOptions(path: '/api/v1/profile/site'),
        type: DioExceptionType.badResponse,
        response: Response(
          requestOptions: RequestOptions(path: '/api/v1/profile/site'),
          statusCode: 422,
        ),
      );
    }
    return v;
  }

  @override
  Future<OpticsSettings> getOptics() async => const OpticsSettings();
  @override
  Future<OpticsSettings> putOptics(OpticsSettings v) async => v;
  @override
  Future<ImagingDefaults> getImagingDefaults() async => const ImagingDefaults();
  @override
  Future<ImagingDefaults> putImagingDefaults(ImagingDefaults v) async => v;
  @override
  Future<Phd2Settings> getPhd2Settings() async => const Phd2Settings();
  @override
  Future<Phd2Settings> putPhd2Settings(Phd2Settings v) async => v;
  @override
  Future<PlateSolveSettings> getPlateSolveSettings() async =>
      const PlateSolveSettings();
  @override
  Future<PlateSolveSettings> putPlateSolveSettings(
    PlateSolveSettings v,
  ) async => v;
  @override
  Future<AutofocusSettings> getAutofocusSettings() async =>
      const AutofocusSettings();
  @override
  Future<AutofocusSettings> putAutofocusSettings(AutofocusSettings v) async =>
      v;
  @override
  Future<CameraElectronics> getCameraElectronics() async =>
      const CameraElectronics();
  @override
  Future<CameraElectronics> putCameraElectronics(CameraElectronics v) async =>
      v;
  @override
  Future<FilterSetSettings> getFilterSet() async => const FilterSetSettings();
  @override
  Future<FilterSetSettings> putFilterSet(FilterSetSettings v) async => v;
  @override
  Future<StorageSettings> getStorageSettings() async => const StorageSettings();
  @override
  Future<StorageSettings> putStorageSettings(StorageSettings v) async => v;
  @override
  Future<SafetyPolicies> getSafetyPolicies() async => const SafetyPolicies();
  @override
  Future<SafetyPolicies> putSafetyPolicies(SafetyPolicies v) async => v;
}

void main() {
  late Directory tmp;
  late PendingProfileDraftService store;

  setUp(() async {
    tmp = await Directory.systemTemp.createTemp('pending_replay');
    store = PendingProfileDraftService(supportDir: () async => tmp);
  });
  tearDown(() => tmp.delete(recursive: true));

  Future<void> pend(String name, {String? serverKey, String? savedProfileId}) =>
      store.save(
        PendingProfileDraft(
          id: store.newId(),
          name: name,
          savedUtc: DateTime.now().toUtc(),
          draft: ProfileDraft()
            ..profileName = name
            ..savedProfileId = savedProfileId,
          finalSave: true,
          serverKey: serverKey,
        ),
      );

  test(
    '#1137: every pending draft is created on the daemon and then forgotten',
    () async {
      await pend('Dark site rig');
      await pend('Backyard');
      final api = _FakeProfileApi();

      final (applied, failed, held, _) = await replayPendingProfileDrafts(
        store,
        api,
        serverKey: 'rig-a:5555',
      );

      expect((applied, failed, held), (2, 0, 0));
      expect(api.created, containsAll(['Dark site rig', 'Backyard']));
      expect(await store.loadAll(), isEmpty);
    },
  );

  test('#1137: a draft whose sections failed IN TRANSIT stays pending with its created profile id', () async {
    await pend('Flaky');
    final api = _FakeProfileApi()..failSite = true;

    final (applied, failed, held, _) = await replayPendingProfileDrafts(
      store,
      api,
      serverKey: 'rig-a:5555',
    );

    expect((applied, failed, held), (0, 1, 0));
    final left = await store.loadAll();
    expect(left.single.name, 'Flaky');
    expect(
      left.single.draft.savedProfileId,
      'profile-1',
      reason: 'the retry re-applies onto the profile already created instead of orphaning another',
    );

    api.failSite = false;
    final second = await replayPendingProfileDrafts(
      store,
      api,
      serverKey: 'rig-a:5555',
    );
    expect(second, (1, 0, 0, 0));
    expect(api.created, ['Flaky'], reason: 'no second create on the retry');
    expect(await store.loadAll(), isEmpty);
  });

  test(
    '#1327: a draft saved against another rig is held, not applied here',
    () async {
      await pend('For rig B', serverKey: 'rig-b:5555');
      await pend('Anywhere');
      final api = _FakeProfileApi();

      final (applied, failed, held, _) = await replayPendingProfileDrafts(
        store,
        api,
        serverKey: 'rig-a:5555',
      );

      expect((applied, failed, held), (1, 0, 1));
      expect(api.created, ['Anywhere']);
      expect(
        (await store.loadAll()).single.name,
        'For rig B',
        reason: 'waits for rig B',
      );
    },
  );

  test('#1327: a half-created profile that is no longer active is re-selected before its sections land', () async {
    final api = _FakeProfileApi()
      ..known.addAll(['profile-old', 'other'])
      ..activeId = 'other';
    await pend(
      'Resumed',
      serverKey: 'rig-a:5555',
      savedProfileId: 'profile-old',
    );

    final result = await replayPendingProfileDrafts(
      store,
      api,
      serverKey: 'rig-a:5555',
    );

    expect(result, (1, 0, 0, 0));
    expect(api.created, isEmpty, reason: 'no second profile');
    expect(api.selected, [
      'profile-old',
    ], reason: 'the sections must not land on "other"');
  });

  test('#1327: a half-created profile that was deleted meanwhile starts a fresh one', () async {
    final api = _FakeProfileApi()
      ..known.add('other')
      ..activeId = 'other';
    await pend(
      'Vanished',
      serverKey: 'rig-a:5555',
      savedProfileId: 'profile-gone',
    );

    final result = await replayPendingProfileDrafts(
      store,
      api,
      serverKey: 'rig-a:5555',
    );

    expect(result, (1, 0, 0, 0));
    expect(api.created, ['Vanished']);
    expect(api.selected, isEmpty);
  });

  test('#1327: a draft the daemon rejects is dropped, not retried on every reconnect', () async {
    await pend('Bad value');
    final api = _FakeProfileApi()..rejectSite = true;

    final first = await replayPendingProfileDrafts(
      store,
      api,
      serverKey: 'rig-a:5555',
    );

    expect(first, (0, 0, 0, 1));
    expect(api.created, ['Bad value'], reason: 'the profile it did create stays on the rig');
    expect(await store.loadAll(), isEmpty, reason: 'nothing left to retry');

    // The user moves the rig to their real profile; a reconnect must not swap it back.
    api
      ..known.add('real')
      ..activeId = 'real';
    final second = await replayPendingProfileDrafts(
      store,
      api,
      serverKey: 'rig-a:5555',
    );
    expect(second, (0, 0, 0, 0));
    expect(api.selected, isEmpty, reason: 'no re-select of the rejected profile');
    expect(api.activeId, 'real');
  });
}
