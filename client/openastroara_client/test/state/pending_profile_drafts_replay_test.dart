import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/profile_draft.dart' show ProfileDraft;
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
  bool failSite = false;
  int n = 0;

  @override
  Future<ProfileMeta> createProfile(String name) async {
    created.add(name);
    return ProfileMeta(id: 'profile-${++n}', name: name);
  }

  @override
  Future<SiteSettings> getSiteSettings() async => const SiteSettings();
  @override
  Future<SiteSettings> putSiteSettings(SiteSettings v) async {
    if (failSite) throw StateError('site PUT failed');
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

  Future<void> pend(String name) => store.save(
    PendingProfileDraft(
      id: store.newId(),
      name: name,
      savedUtc: DateTime.now().toUtc(),
      draft: ProfileDraft()..profileName = name,
      finalSave: true,
    ),
  );

  test(
    '#1137: every pending draft is created on the daemon and then forgotten',
    () async {
      await pend('Dark site rig');
      await pend('Backyard');
      final api = _FakeProfileApi();

      final (applied, failed) = await replayPendingProfileDrafts(store, api);

      expect((applied, failed), (2, 0));
      expect(api.created, containsAll(['Dark site rig', 'Backyard']));
      expect(await store.loadAll(), isEmpty);
    },
  );

  test('#1137: a draft whose sections failed stays pending with its created profile id', () async {
    await pend('Flaky');
    final api = _FakeProfileApi()..failSite = true;

    final (applied, failed) = await replayPendingProfileDrafts(store, api);

    expect((applied, failed), (0, 1));
    final left = await store.loadAll();
    expect(left.single.name, 'Flaky');
    expect(
      left.single.draft.savedProfileId,
      'profile-1',
      reason: 'the retry re-applies onto the profile already created instead of orphaning another',
    );

    api.failSite = false;
    final second = await replayPendingProfileDrafts(store, api);
    expect(second, (1, 0));
    expect(api.created, ['Flaky'], reason: 'no second create on the retry');
    expect(await store.loadAll(), isEmpty);
  });
}
