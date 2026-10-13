import 'package:flutter/material.dart';
import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/wizard/wizard_shell.dart';
import 'package:openastroara/state/settings/storage_settings_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';
import 'package:openastroara/state/settings/plate_solve_settings_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/imaging_defaults_state.dart';
import 'package:openastroara/state/settings/filter_set_state.dart';
import 'package:openastroara/state/settings/camera_electronics_state.dart';
import 'package:openastroara/state/settings/autofocus_settings_state.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/profile_meta.dart';
import 'package:openastroara/services/pending_profile_draft_service.dart';
import 'package:openastroara/state/pending_profile_drafts_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/wizard_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';

/// §2 / #1137 — with no server in reach, Save keeps the profile on this device
/// instead of refusing, and the wizard closes.
/// In-memory store: file I/O never completes inside testWidgets' fake-async
/// zone, so the production service's temp-dir path cannot be used here.
class _MemStore extends PendingProfileDraftService {
  final Map<String, PendingProfileDraft> store = {};
  @override
  Future<List<PendingProfileDraft>> loadAll() async =>
      store.values.toList()..sort((a, b) => a.savedUtc.compareTo(b.savedUtc));
  @override
  Future<void> save(PendingProfileDraft draft) async => store[draft.id] = draft;
  @override
  Future<void> delete(String id) async => store.remove(id);
}

/// Creates the profile, then every section PUT dies in transit (no response).
class _UnreachableAfterCreate extends ProfileApi {
  _UnreachableAfterCreate()
    : super(const AraServer(hostname: 'rig-a', port: 5555));
  int creates = 0;
  @override
  Future<ProfileMeta> createProfile(String name) async {
    creates++;
    return ProfileMeta(id: 'profile-a', name: name);
  }

  Never _gone() => throw DioException(
    requestOptions: RequestOptions(path: '/api/v1/profile'),
    type: DioExceptionType.connectionError,
    message: 'Connection reset',
  );
  @override
  Future<SiteSettings> getSiteSettings() async => _gone();
  @override
  Future<OpticsSettings> getOptics() async => _gone();
  @override
  Future<ImagingDefaults> getImagingDefaults() async => _gone();
  @override
  Future<Phd2Settings> getPhd2Settings() async => _gone();
  @override
  Future<PlateSolveSettings> getPlateSolveSettings() async => _gone();
  @override
  Future<AutofocusSettings> getAutofocusSettings() async => _gone();
  @override
  Future<CameraElectronics> getCameraElectronics() async => _gone();
  @override
  Future<FilterSetSettings> getFilterSet() async => _gone();
  @override
  Future<StorageSettings> getStorageSettings() async => _gone();
  @override
  Future<SafetyPolicies> getSafetyPolicies() async => _gone();
}

void main() {
  testWidgets(
    'Save Profile with no server stores a pending draft and leaves the wizard',
    (tester) async {
      final store = _MemStore();
      var completed = 0;
      final container = ProviderContainer(
        overrides: [
          activeServerProvider.overrideWithValue(null),
          pendingProfileDraftServiceProvider.overrideWithValue(store),
          wsEventStreamProvider.overrideWithValue(null),
        ],
      );
      addTearDown(container.dispose);

      await tester.pumpWidget(
        UncontrolledProviderScope(
          container: container,
          child: MaterialApp(
            home: Builder(
              builder: (context) => Scaffold(
                body: Center(
                  child: ElevatedButton(
                    onPressed: () => Navigator.of(context).push(
                      MaterialPageRoute(
                        builder: (_) =>
                            WizardShell(onComplete: (_) => completed++),
                      ),
                    ),
                    child: const Text('open'),
                  ),
                ),
              ),
            ),
          ),
        ),
      );
      await tester.tap(find.text('open'));
      // Bounded pumps: wizard screens carry indeterminate animations, so
      // pumpAndSettle would never return.
      for (var i = 0; i < 10; i++) {
        await tester.pump(const Duration(milliseconds: 100));
      }
      // The controller is autoDispose: drive it only once the shell is mounted
      // and listening, or the jump lands on a provider about to be reset.
      container
          .read(wizardControllerProvider.notifier)
          .jumpTo(ProfileWizard.totalSteps);
      container.read(wizardControllerProvider).draft.profileName =
          'Dark site rig';
      // Bounded pumps: wizard screens carry indeterminate animations, so
      // pumpAndSettle would never return.
      for (var i = 0; i < 10; i++) {
        await tester.pump(const Duration(milliseconds: 100));
      }
      expect(find.text('Save Profile'), findsOneWidget);

      await tester.tap(find.text('Save Profile'));
      // Bounded pumps: wizard screens carry indeterminate animations, so
      // pumpAndSettle would never return.
      for (var i = 0; i < 10; i++) {
        await tester.pump(const Duration(milliseconds: 100));
      }

      final pending = await store.loadAll();
      expect(pending.single.name, 'Dark site rig');
      expect(pending.single.finalSave, isTrue);
      expect(pending.single.draft.profileName, 'Dark site rig');
      expect(
        completed,
        1,
        reason: 'the wizard exits as after a connected save',
      );
      expect(find.text('Save Profile'), findsNothing);
      expect(find.textContaining('saved on this device'), findsOneWidget);
    },
  );

  testWidgets(
    'a connection lost after the profile was created keeps the draft pinned to this rig',
    (tester) async {
      final store = _MemStore();
      final api = _UnreachableAfterCreate();
      var completed = 0;
      final container = ProviderContainer(
        overrides: [
          activeServerProvider.overrideWithValue(
            const AraServer(hostname: 'rig-a', port: 5555),
          ),
          pendingProfileDraftServiceProvider.overrideWithValue(store),
          wsEventStreamProvider.overrideWithValue(null),
        ],
      );
      addTearDown(container.dispose);

      await tester.pumpWidget(
        UncontrolledProviderScope(
          container: container,
          child: MaterialApp(
            home: Builder(
              builder: (context) => Scaffold(
                body: Center(
                  child: ElevatedButton(
                    onPressed: () => Navigator.of(context).push(
                      MaterialPageRoute(
                        builder: (_) => WizardShell(
                          createApi: (_) => api,
                          onComplete: (_) => completed++,
                        ),
                      ),
                    ),
                    child: const Text('open'),
                  ),
                ),
              ),
            ),
          ),
        ),
      );
      await tester.tap(find.text('open'));
      for (var i = 0; i < 6; i++) {
        await tester.pump(const Duration(milliseconds: 100));
      }
      container
          .read(wizardControllerProvider.notifier)
          .jumpTo(ProfileWizard.totalSteps);
      container.read(wizardControllerProvider).draft.profileName = 'Half saved';
      for (var i = 0; i < 4; i++) {
        await tester.pump(const Duration(milliseconds: 100));
      }

      await tester.tap(find.text('Save Profile'));
      for (var i = 0; i < 10; i++) {
        await tester.pump(const Duration(milliseconds: 100));
      }

      expect(api.creates, 1);
      final pending = await store.loadAll();
      expect(pending.single.name, 'Half saved');
      expect(
        pending.single.serverKey,
        'rig-a:5555',
        reason: 'the profile exists on THIS rig',
      );
      expect(
        pending.single.draft.savedProfileId,
        'profile-a',
        reason: 'the retry re-applies onto it',
      );
      expect(
        completed,
        1,
        reason: 'the wizard exits as after a connected save',
      );
      expect(
        find.byType(CircularProgressIndicator),
        findsNothing,
        reason: 'the spinner is gone',
      );
      expect(find.textContaining('saved on this device'), findsOneWidget);
    },
  );
}
