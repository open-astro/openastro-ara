import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/profile_list.dart';
import 'package:openastroara/models/profile_meta.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/wizard/wizard_shell.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/autofocus_settings_state.dart';
import 'package:openastroara/state/settings/imaging_defaults_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';
import 'package:openastroara/state/settings/plate_solve_settings_state.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/settings/storage_settings_state.dart';

/// In-memory ProfileApi double whose create call can be held in-flight (so the
/// blocking spinner is observable) and which records how many times the profile
/// was created — the regression hooks for the navigator/spinner + double-tap
/// guard the bot flagged on PR #486.
class _FakeProfileApi extends ProfileApi {
  _FakeProfileApi() : super(const AraServer(hostname: 'test', port: 1));

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
  Future<PlateSolveSettings> putPlateSolveSettings(PlateSolveSettings v) async =>
      v;
  @override
  Future<AutofocusSettings> getAutofocusSettings() async =>
      const AutofocusSettings();
  @override
  Future<AutofocusSettings> putAutofocusSettings(AutofocusSettings v) async => v;
  @override
  Future<StorageSettings> getStorageSettings() async => const StorageSettings();
  @override
  Future<StorageSettings> putStorageSettings(StorageSettings v) async => v;
  @override
  Future<SafetyPolicies> getSafetyPolicies() async => const SafetyPolicies();
  @override
  Future<SafetyPolicies> putSafetyPolicies(SafetyPolicies v) async => v;
}

Widget _host(_FakeProfileApi api, {required void Function() onComplete}) {
  return ProviderScope(
    overrides: [
      activeServerProvider.overrideWithValue(
        const AraServer(hostname: 'daemon', port: 5555),
      ),
    ],
    child: MaterialApp(
      home: WizardShell(createApi: (_) => api, onComplete: (_) => onComplete()),
    ),
  );
}

void main() {
  testWidgets('Save & Exit shows the blocking spinner while the save is in-flight',
      (tester) async {
    final api = _FakeProfileApi();
    await tester.pumpWidget(_host(api, onComplete: () {}));

    await tester.tap(find.text('Save & Exit'));
    await tester.pump(); // kick off the async save (create is held by the gate)

    expect(find.byType(CircularProgressIndicator), findsOneWidget,
        reason: 'a blocking spinner is shown during the round-trip');
    expect(api.createCalls, 1);

    api.releaseCreate();
    await tester.pumpAndSettle();
    // Spinner dismissed via the captured navigator once the save settles.
    expect(find.byType(CircularProgressIndicator), findsNothing);
  });

  testWidgets('a rapid double-tap on Save creates the profile only once',
      (tester) async {
    final api = _FakeProfileApi();
    await tester.pumpWidget(_host(api, onComplete: () {}));

    // Two taps before the save settles — the _isSaving guard + the modal
    // barrier must keep this to a single createProfile call.
    await tester.tap(find.text('Save & Exit'));
    await tester.pump();
    await tester.tap(find.text('Save & Exit'), warnIfMissed: false);
    await tester.pump();

    expect(api.createCalls, 1, reason: 'double-tap must not start a second save');

    api.releaseCreate();
    await tester.pumpAndSettle();
  });

  testWidgets('on success the wizard pops and onComplete fires', (tester) async {
    final api = _FakeProfileApi();
    var completed = false;
    await tester.pumpWidget(_host(api, onComplete: () => completed = true));

    await tester.tap(find.text('Save & Exit'));
    await tester.pump();
    api.releaseCreate();
    await tester.pumpAndSettle();

    expect(completed, isTrue);
  });

  testWidgets('a successful save refreshes the profile list (so the new profile shows)',
      (tester) async {
    // The bug: the wizard saved the profile on the daemon but the cached
    // profile-list provider was never invalidated, so the new profile didn't
    // appear and it looked like nothing saved. The wizard now invalidates it.
    final api = _FakeProfileApi();
    final container = ProviderContainer(overrides: [
      activeServerProvider
          .overrideWithValue(const AraServer(hostname: 'daemon', port: 5555)),
      // The list provider reads the API through profileApiProvider — point it at
      // the same fake so we can count list re-fetches.
      profileApiProvider.overrideWithValue(api),
    ]);
    addTearDown(container.dispose);
    // Keep the list provider alive so an invalidate triggers a real re-fetch.
    container.listen(profileManagementProvider, (_, _) {});
    await container.read(profileManagementProvider.future);
    expect(api.listCalls, 1, reason: 'initial load fetched the list once');

    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
        home: WizardShell(createApi: (_) => api, onComplete: (_) {}),
      ),
    ));
    await tester.tap(find.text('Save & Exit'));
    await tester.pump();
    api.releaseCreate();
    await tester.pumpAndSettle();

    expect(api.listCalls, 2,
        reason: 'a successful save invalidates + re-fetches the profile list');
  });
}
