import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/profile_draft.dart' show ProfileDraft;
import 'package:openastroara/models/profile_meta.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/pending_profile_draft_service.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/services/ws_event_stream.dart';
import 'package:openastroara/state/pending_profile_drafts_state.dart';
import 'package:openastroara/state/profile_management_state.dart';
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
import 'package:openastroara/state/ws/ws_providers.dart';

/// §2 / #1137 — the root listener: the WebSocket reaching `connected` applies
/// the drafts waiting on this device and posts one notice.
class _MemStore extends PendingProfileDraftService {
  final Map<String, PendingProfileDraft> store = {};
  @override
  Future<List<PendingProfileDraft>> loadAll() async => store.values.toList();
  @override
  Future<void> save(PendingProfileDraft draft) async => store[draft.id] = draft;
  @override
  Future<void> delete(String id) async => store.remove(id);
}

class _Api extends ProfileApi {
  _Api() : super(const AraServer(hostname: 'test', port: 1));
  final created = <String>[];
  @override
  Future<ProfileMeta> createProfile(String name) async {
    created.add(name);
    return ProfileMeta(id: 'p-${created.length}', name: name);
  }

  @override
  Future<SiteSettings> getSiteSettings() async => const SiteSettings();
  @override
  Future<SiteSettings> putSiteSettings(SiteSettings v) async => v;
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
  test(
    'reaching connected applies the pending drafts once and posts a notice',
    () async {
      final store = _MemStore();
      await store.save(
        PendingProfileDraft(
          id: 'd1',
          name: 'Dark site rig',
          savedUtc: DateTime.utc(2026, 10, 12),
          draft: ProfileDraft()..profileName = 'Dark site rig',
          finalSave: true,
        ),
      );
      final api = _Api();
      final link = StreamController<WsConnectionState>.broadcast();
      final container = ProviderContainer(
        overrides: [
          pendingProfileDraftServiceProvider.overrideWithValue(store),
          profileApiProvider.overrideWithValue(api),
          wsConnectionStateProvider.overrideWith((ref) => link.stream),
        ],
      );
      addTearDown(container.dispose);
      addTearDown(link.close);

      // Mount the root listener the way main.dart does.
      container.listen(pendingProfileDraftReplayProvider, (_, _) {});
      container.read(pendingProfileDraftReplayProvider);
      await Future<void>.delayed(Duration.zero);

      link.add(WsConnectionState.connecting);
      await Future<void>.delayed(Duration.zero);
      expect(api.created, isEmpty, reason: 'only a live link replays');

      link.add(WsConnectionState.connected);
      for (var i = 0; i < 20 && store.store.isNotEmpty; i++) {
        await Future<void>.delayed(const Duration(milliseconds: 10));
      }

      expect(api.created, ['Dark site rig']);
      expect(store.store, isEmpty, reason: 'an applied draft is forgotten');
      expect(
        container.read(pendingProfileReplayNoticeProvider),
        contains('1 profile saved offline was applied'),
      );
      expect(
        container.read(pendingProfileReplayNoticeProvider.notifier).consume(),
        isNotNull,
      );
      expect(
        container.read(pendingProfileReplayNoticeProvider),
        isNull,
        reason: 'consumed once',
      );

      // A reconnect with nothing pending is quiet.
      link.add(WsConnectionState.reconnecting);
      link.add(WsConnectionState.connected);
      await Future<void>.delayed(const Duration(milliseconds: 20));
      expect(api.created, hasLength(1));
      expect(container.read(pendingProfileReplayNoticeProvider), isNull);
    },
  );
}
