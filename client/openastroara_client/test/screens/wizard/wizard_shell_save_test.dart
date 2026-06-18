import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/profile_meta.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/wizard/wizard_shell.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/imaging_defaults_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';

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
}
