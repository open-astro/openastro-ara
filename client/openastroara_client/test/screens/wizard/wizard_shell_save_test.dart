import '../../support/storage_fixture.dart';

import 'dart:async';

import 'package:flutter/material.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/filter_wheel_status.dart';
import 'package:openastroara/models/profile_list.dart';
import 'package:openastroara/models/profile_meta.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/wizard/wizard_shell.dart';
import 'package:openastroara/state/equipment/filter_wheel_state.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/sky_atlas/data_manager_state.dart';
import 'package:openastroara/state/wizard_state.dart';

import '../../helpers/fake_data_manager_client.dart';

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

import '../../support/site_section.dart';

/// In-memory ProfileApi double whose create call can be held in-flight (so the
/// blocking spinner is observable) and which records how many times the profile
/// was created — the regression hooks for the navigator/spinner + double-tap
/// guard the bot flagged on PR #486.
class _FakeProfileApi extends FakeStorageApi {
  int createCalls = 0;
  final Completer<void> _gate = Completer<void>();
  void releaseCreate() => _gate.complete();

  @override
  Future<ProfileMeta> createProfile(String name) async {
    createCalls++;
    await _gate.future; // hold the save in-flight until the test releases it
    return ProfileMeta(id: 'profile-1', name: name);
  }

  int listCalls = 0;
  @override
  Future<ProfileList> listProfiles() async {
    listCalls++;
    return const ProfileList(activeId: null, profiles: []);
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
  Future<StorageSettings> getStorageSettings() async => kTestStorage;
  // The wizard writes Storage with no tag; the rig takes it and its tag moves.
  @override
  Future<StorageSettings> putStorageSettings(StorageSettings v) async =>
      (await putStorageSection(v)).value;
  @override
  Future<SafetyPolicies> getSafetyPolicies() async => const SafetyPolicies();
  @override
  Future<SafetyPolicies> putSafetyPolicies(SafetyPolicies v) async => v;
}

/// The wizard's reset builds the filter wheel labels, which watch the wheel's
/// status: a wheel with no stream, so no reconnect timer outlives the test.
class _NoWheel extends FilterWheelNotifier {
  @override
  Future<FilterWheelStatus?> build() async => null;
}

Widget _host(_FakeProfileApi api, {required void Function() onComplete}) {
  return ProviderScope(
    overrides: [
      activeServerProvider.overrideWithValue(
        const AraServer(hostname: 'daemon', port: 5555),
      ),
      filterWheelProvider.overrideWith(_NoWheel.new),
      // The dome settings listen to the dome, which would dial a real stream.
      wsEventStreamProvider.overrideWith((ref) => null),
    ],
    child: MaterialApp(
      home: WizardShell(createApi: (_) => api, onComplete: (_) => onComplete()),
    ),
  );
}

void main() {
  testWidgets(
    'Save & Exit shows the blocking spinner while the save is in-flight',
    (tester) async {
      final api = _FakeProfileApi();
      await tester.pumpWidget(_host(api, onComplete: () {}));

      await tester.tap(find.text('Save & Exit'));
      await tester
          .pump(); // kick off the async save (create is held by the gate)

      expect(
        find.byType(CircularProgressIndicator),
        findsOneWidget,
        reason: 'a blocking spinner is shown during the round-trip',
      );
      expect(api.createCalls, 1);

      api.releaseCreate();
      await tester.pumpAndSettle();
      // Spinner dismissed via the captured navigator once the save settles.
      expect(find.byType(CircularProgressIndicator), findsNothing);
    },
  );

  testWidgets('a rapid double-tap on Save creates the profile only once', (
    tester,
  ) async {
    final api = _FakeProfileApi();
    await tester.pumpWidget(_host(api, onComplete: () {}));

    // Two taps before the save settles — the _isSaving guard + the modal
    // barrier must keep this to a single createProfile call.
    await tester.tap(find.text('Save & Exit'));
    await tester.pump();
    await tester.tap(find.text('Save & Exit'), warnIfMissed: false);
    await tester.pump();

    expect(
      api.createCalls,
      1,
      reason: 'double-tap must not start a second save',
    );

    api.releaseCreate();
    await tester.pumpAndSettle();
  });

  testWidgets('on success the wizard pops and onComplete fires', (
    tester,
  ) async {
    final api = _FakeProfileApi();
    var completed = false;
    await tester.pumpWidget(_host(api, onComplete: () => completed = true));

    await tester.tap(find.text('Save & Exit'));
    await tester.pump();
    api.releaseCreate();
    await tester.pumpAndSettle();

    expect(completed, isTrue);
  });

  testWidgets('a saved profile sends a loaded section back to not loaded, so '
      'its next Save is not refused', (tester) async {
    final api = _FakeProfileApi()..releaseCreate();
    await tester.pumpWidget(_host(api, onComplete: () {}));
    final container = ProviderScope.containerOf(
      tester.element(find.byType(WizardShell)),
      listen: false,
    );
    final storage = container.read(storageSettingsProvider.notifier);
    await storage.load(api);
    await tester.tap(find.text('Save & Exit'));
    await tester.pumpAndSettle();
    expect(container.read(storageSettingsProvider).value, isNull);
    await storage.save(api);
    expect(api.ifMatches, [null, '"v2"'], reason: 'the wizard wrote untagged');
  });

  testWidgets(
    'a successful save refreshes the profile list (so the new profile shows)',
    (tester) async {
      // The bug: the wizard saved the profile on the daemon but the cached
      // profile-list provider was never invalidated, so the new profile didn't
      // appear and it looked like nothing saved. The wizard now invalidates it.
      final api = _FakeProfileApi();
      final container = ProviderContainer(
        overrides: [
          activeServerProvider.overrideWithValue(
            const AraServer(hostname: 'daemon', port: 5555),
          ),
          // The list provider reads the API through profileApiProvider — point it at
          // the same fake so we can count list re-fetches.
          profileApiProvider.overrideWithValue(api),
          filterWheelProvider.overrideWith(_NoWheel.new),
          wsEventStreamProvider.overrideWith((ref) => null),
        ],
      );
      addTearDown(container.dispose);
      // Keep the list provider alive so an invalidate triggers a real re-fetch.
      container.listen(profileManagementProvider, (_, _) {});
      await container.read(profileManagementProvider.future);
      expect(api.listCalls, 1, reason: 'initial load fetched the list once');

      await tester.pumpWidget(
        UncontrolledProviderScope(
          container: container,
          child: MaterialApp(
            home: WizardShell(createApi: (_) => api, onComplete: (_) {}),
          ),
        ),
      );
      await tester.tap(find.text('Save & Exit'));
      await tester.pump();
      api.releaseCreate();
      await tester.pumpAndSettle();

      expect(
        api.listCalls,
        2,
        reason: 'a successful save invalidates + re-fetches the profile list',
      );
    },
  );

  for (final tick in [true, false]) {
    testWidgets(
      'a saved profile queues the chosen star databases on the rig (ticked=$tick, §36.10)',
      (tester) async {
        final api = _FakeProfileApi();
        final dm = FakeDataManagerClient();
        final container = ProviderContainer(
          overrides: [
            activeServerProvider.overrideWithValue(
              const AraServer(hostname: 'daemon', port: 5555),
            ),
            dataManagerApiProvider.overrideWithValue(dm),
            filterWheelProvider.overrideWith(_NoWheel.new),
            wsEventStreamProvider.overrideWith((ref) => null),
          ],
        );
        addTearDown(container.dispose);
        final draft = container.read(wizardControllerProvider).draft;
        draft.plateSolve.solverDatabasePackageIds = ['astap-d05', 'astap-d50'];
        draft.plateSolve.downloadSolverDatabase = tick;
        await tester.pumpWidget(
          UncontrolledProviderScope(
            container: container,
            child: MaterialApp(
              home: WizardShell(createApi: (_) => api, onComplete: (_) {}),
            ),
          ),
        );
        await tester.tap(find.text('Save & Exit'));
        await tester.pump();
        api.releaseCreate();
        await tester.pumpAndSettle();
        expect(
          dm.downloaded,
          tick ? ['astap-d05', 'astap-d50'] : isEmpty,
          reason: 'ticked → both queued after the save; unticked → nothing',
        );
      },
    );
  }

  testWidgets(
    'the bottom-nav Skip is gated like Next while the step is invalid',
    (tester) async {
      // A skippable gate is no gate: §68.2 (and the field-validation screens,
      // whose invalid values are already in the draft) must block BOTH advance
      // buttons. Back stays available.
      final api = _FakeProfileApi();
      final container = ProviderContainer(
        overrides: [
          activeServerProvider.overrideWithValue(
            const AraServer(hostname: 'daemon', port: 5555),
          ),
          // The basics step reads the saved site: the rig's copy, no network.
          siteFromRig(),
        ],
      );
      addTearDown(container.dispose);
      await tester.pumpWidget(
        UncontrolledProviderScope(
          container: container,
          child: MaterialApp(
            home: WizardShell(createApi: (_) => api, onComplete: (_) {}),
          ),
        ),
      );

      final skip = find.widgetWithText(TextButton, 'Skip — use defaults');
      expect(
        tester.widget<TextButton>(skip).onPressed,
        isNotNull,
        reason: 'a valid step skips normally',
      );

      container.read(wizardStepValidProvider.notifier).setValid(false);
      await tester.pump();
      expect(
        tester.widget<TextButton>(skip).onPressed,
        isNull,
        reason: 'an invalid/gated step blocks Skip exactly like Next',
      );
      final next = find.widgetWithText(FilledButton, 'Next');
      expect(tester.widget<FilledButton>(next).onPressed, isNull);
      // (Back is null here only because step 1 has nothing to go back to —
      // the gate itself never disables it.)
    },
  );
}
