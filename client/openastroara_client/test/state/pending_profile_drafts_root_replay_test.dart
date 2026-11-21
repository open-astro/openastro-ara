import '../support/storage_fixture.dart';

import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/profile_draft.dart' show ProfileDraft;
import 'package:openastroara/models/profile_meta.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/pending_profile_draft_service.dart';
import 'package:openastroara/services/ws_event_stream.dart';
import 'package:openastroara/state/pending_profile_drafts_state.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
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
import '../support/settings_values.dart';

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

class _Api extends FakeStorageApi {
  final created = <String>[];
  @override
  Future<ProfileMeta> createProfile(String name) async {
    created.add(name);
    return ProfileMeta(id: 'p-${created.length}', name: name);
  }

  // Each section is served with a tag and written back with it; Storage is
  // the fixture's, whose tag moves on the write.
  @override
  Future<OpticsSettings> getOptics() async => const TestOpticsSettings();
  @override
  Future<({SiteSettings value, String? etag})> getSiteSection() async =>
      (value: const TestSiteSettings(), etag: '"t"');
  @override
  Future<({SiteSettings value, String? etag})> putSiteSection(
    SiteSettings v, {
    String? ifMatch,
  }) async => (value: v, etag: ifMatch);
  @override
  Future<({OpticsSettings value, String? etag})> getOpticsSection() async =>
      (value: const TestOpticsSettings(), etag: '"t"');
  @override
  Future<({OpticsSettings value, String? etag})> putOpticsSection(
    OpticsSettings v, {
    String? ifMatch,
  }) async => (value: v, etag: ifMatch);
  @override
  Future<({ImagingDefaults value, String? etag})> getImagingDefaultsSection() async =>
      (value: const TestImagingDefaults(), etag: '"t"');
  @override
  Future<({ImagingDefaults value, String? etag})> putImagingDefaultsSection(
    ImagingDefaults v, {
    String? ifMatch,
  }) async => (value: v, etag: ifMatch);
  @override
  Future<({Phd2Settings value, String? etag})> getPhd2Section() async =>
      (value: const TestPhd2Settings(), etag: '"t"');
  @override
  Future<({Phd2Settings value, String? etag})> putPhd2Section(
    Phd2Settings v, {
    String? ifMatch,
  }) async => (value: v, etag: ifMatch);
  @override
  Future<({PlateSolveSettings value, String? etag})> getPlateSolveSection() async =>
      (value: const TestPlateSolveSettings(), etag: '"t"');
  @override
  Future<({PlateSolveSettings value, String? etag})> putPlateSolveSection(
    PlateSolveSettings v, {
    String? ifMatch,
  }) async => (value: v, etag: ifMatch);
  @override
  Future<({AutofocusSettings value, String? etag})> getAutofocusSection() async =>
      (value: const TestAutofocusSettings(), etag: '"t"');
  @override
  Future<({AutofocusSettings value, String? etag})> putAutofocusSection(
    AutofocusSettings v, {
    String? ifMatch,
  }) async => (value: v, etag: ifMatch);
  @override
  Future<({CameraElectronics value, String? etag})> getCameraElectronicsSection() async =>
      (value: const TestCameraElectronics(), etag: '"t"');
  @override
  Future<({CameraElectronics value, String? etag})> putCameraElectronicsSection(
    CameraElectronics v, {
    String? ifMatch,
  }) async => (value: v, etag: ifMatch);
  @override
  Future<({FilterSetSettings value, String? etag})> getFilterSetSection() async =>
      (value: const FilterSetSettings(), etag: '"t"');
  @override
  Future<({FilterSetSettings value, String? etag})> putFilterSetSection(
    FilterSetSettings v, {
    String? ifMatch,
  }) async => (value: v, etag: ifMatch);
  @override
  Future<({SafetyPolicies value, String? etag})> getSafetySection() async =>
      (value: const TestSafetyPolicies(), etag: '"t"');
  @override
  Future<({SafetyPolicies value, String? etag})> putSafetySection(
    SafetyPolicies v, {
    String? ifMatch,
  }) async => (value: v, etag: ifMatch);
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
          activeServerProvider.overrideWithValue(
            const AraServer(hostname: 'test', port: 1),
          ),
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

  test('an applied draft sends a loaded section back to not loaded, so its '
      'next Save is not refused', () async {
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
        activeServerProvider.overrideWithValue(
          const AraServer(hostname: 'test', port: 1),
        ),
        wsConnectionStateProvider.overrideWith((ref) => link.stream),
      ],
    );
    addTearDown(container.dispose);
    addTearDown(link.close);
    final storage = container.read(storageSettingsProvider.notifier);
    await storage.load(api);
    container.listen(pendingProfileDraftReplayProvider, (_, _) {});
    link.add(WsConnectionState.connected);
    for (var i = 0; i < 20 && store.store.isNotEmpty; i++) {
      await Future<void>.delayed(const Duration(milliseconds: 10));
    }
    expect(container.read(storageSettingsProvider).value, isNull);
    await storage.save(api);
    expect(
      api.ifMatches,
      ['"v1"', '"v2"'],
      reason: 'the replay wrote with the tag it loaded; the Save sends the new one',
    );
  });
}
