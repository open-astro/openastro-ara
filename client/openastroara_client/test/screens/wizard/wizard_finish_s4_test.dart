import '../../support/storage_fixture.dart';

import 'dart:convert';
import 'dart:io';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/profile_meta.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/wizard/wizard_shell.dart';
import 'package:openastroara/services/guider_calibration_api.dart';
import 'package:openastroara/models/guider_equipment_choices.dart';
import 'package:openastroara/services/guider_equipment_api.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/guider/guider_calibration_state.dart';
import 'package:openastroara/state/guider/guider_equipment_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
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
import 'package:openastroara/state/wizard_state.dart';

/// Instant in-memory ProfileApi double (no gate — the finish flow is what's
/// under test, not the spinner).
class _FastProfileApi extends ProfileApi {
  _FastProfileApi() : super(const AraServer(hostname: 'test', port: 1));
  Phd2Settings? lastPhd2Put;

  @override
  Future<ProfileMeta> createProfile(String name) async =>
      ProfileMeta(id: 'profile-1', name: name);
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
  Future<Phd2Settings> putPhd2Settings(Phd2Settings v) async {
    lastPhd2Put = v;
    return v;
  }

  @override
  Future<PlateSolveSettings> getPlateSolveSettings() async =>
      const PlateSolveSettings();
  @override
  Future<PlateSolveSettings> putPlateSolveSettings(
          PlateSolveSettings v) async =>
      v;
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
  @override
  Future<StorageSettings> putStorageSettings(StorageSettings v) async => v;
  @override
  Future<SafetyPolicies> getSafetyPolicies() async => const SafetyPolicies();
  @override
  Future<SafetyPolicies> putSafetyPolicies(SafetyPolicies v) async => v;
}

class _FakeGuiderEquipment implements GuiderEquipmentClient {
  int pushes = 0;
  bool pushThrows = false;
  Object? pushError;
  @override
  Future<void> pushProfile({bool tuningOnly = false}) async {
    pushes++;
    if (pushError != null) throw pushError!;
    if (pushThrows) throw Exception('guider unreachable');
  }

  @override
  Future<GuiderEquipmentChoicesResponse> getChoices() async =>
      const GuiderEquipmentChoicesResponse(connected: false);
  @override
  void close() {}
  @override
  void noSuchMethod(Invocation invocation) =>
      throw UnimplementedError('${invocation.memberName}');
}

class _FakeCalibration implements GuiderCalibrationClient {
  int builds = 0;
  bool buildThrows = false;
  int? lastFrames;
  int? lastMinMs;
  int? lastMaxMs;

  @override
  Future<void> buildDarkLibrary({
    int frameCount = 5,
    int? minExposureMs,
    int? maxExposureMs,
    bool clearExisting = false,
    String? notes,
    bool loadAfter = true,
  }) async {
    builds++;
    lastFrames = frameCount;
    lastMinMs = minExposureMs;
    lastMaxMs = maxExposureMs;
    if (buildThrows) throw Exception('guider busy');
  }

  @override
  void close() {}
  @override
  void noSuchMethod(Invocation invocation) =>
      throw UnimplementedError('${invocation.memberName}');
}

Future<ProviderContainer> _pump(
  WidgetTester tester, {
  required _FastProfileApi api,
  required _FakeGuiderEquipment guider,
  required _FakeCalibration calibration,
  AraServer server = const AraServer(hostname: 'daemon', port: 5555),
}) async {
  // Tall surface: the Done view stacks notes under the progress line.
  tester.view.physicalSize = const Size(800, 1600);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.reset);
  final container = ProviderContainer(overrides: [
    activeServerProvider.overrideWithValue(server),
    guiderEquipmentApiProvider.overrideWithValue(guider),
    guiderCalibrationApiFactoryProvider.overrideWithValue((_) => calibration),
    // The Done view watches guiderBuildActivityProvider, which would open a
    // real WS to 'daemon' and leave reconnect timers pending at teardown —
    // inert it (null stream = empty activity map, the pre-first-tick state).
    wsEventStreamProvider.overrideWithValue(null),
  ]);
  addTearDown(container.dispose);
  // Jump to the final step so the bottom-nav button is "Save Profile" (the
  // finalSave path that provisions the guider + kicks darks).
  container
      .read(wizardControllerProvider.notifier)
      .jumpTo(ProfileWizard.totalSteps);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: container,
    child: MaterialApp(
      home: WizardShell(createApi: (_) => api, onComplete: (_) {}),
    ),
  ));
  await tester.pump();
  return container;
}

void main() {
  testWidgets(
      'final Save provisions the guider, kicks darks over the chosen range, '
      'and lands on the Done view instead of popping', (tester) async {
    final api = _FastProfileApi();
    final guider = _FakeGuiderEquipment();
    final calibration = _FakeCalibration();
    final container =
        await _pump(tester, api: api, guider: guider, calibration: calibration);

    final g = container.read(wizardControllerProvider).draft.guider;
    g.darkMinExposureMs = 500;
    g.darkMaxExposureMs = 2000;
    g.darkFrameCount = 8;

    await tester.tap(find.text('Save Profile'));
    // Bounded pumps: the Done view's indeterminate darks bar animates
    // forever, so pumpAndSettle would never settle.
    for (var i = 0; i < 8; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    expect(guider.pushes, 1, reason: 'guider twin gets the final config');
    expect(calibration.builds, 1);
    expect(calibration.lastMinMs, 500);
    expect(calibration.lastMaxMs, 2000);
    expect(calibration.lastFrames, 8);
    // The persisted phd2 section carries the same range (one choice, two
    // consumers).
    expect(api.lastPhd2Put?.guideExposureMinMs, 500);
    expect(api.lastPhd2Put?.guideExposureMaxMs, 2000);
    // Done view with live darks progress, not an exit.
    expect(find.text('You\'re all set'), findsOneWidget);
    expect(find.textContaining('dark library'), findsWidgets);
    expect(find.widgetWithText(FilledButton, 'Finish'), findsOneWidget);
  });

  testWidgets('darks toggle off: no build, wizard exits as before',
      (tester) async {
    final api = _FastProfileApi();
    final guider = _FakeGuiderEquipment();
    final calibration = _FakeCalibration();
    final container =
        await _pump(tester, api: api, guider: guider, calibration: calibration);
    container.read(wizardControllerProvider).draft.guider.buildDarksOnFinish =
        false;

    await tester.tap(find.text('Save Profile'));
    await tester.pumpAndSettle();

    expect(calibration.builds, 0);
    expect(find.text('You\'re all set'), findsNothing);
  });

  testWidgets('a failed darks kickoff degrades to an amber note and exits — '
      'never fails the wizard', (tester) async {
    final api = _FastProfileApi();
    final guider = _FakeGuiderEquipment();
    final calibration = _FakeCalibration()..buildThrows = true;
    await _pump(tester, api: api, guider: guider, calibration: calibration);

    await tester.tap(find.text('Save Profile'));
    for (var i = 0; i < 8; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    expect(find.text('You\'re all set'), findsNothing,
        reason: 'no Done view when the build never started');
    expect(find.textContaining('couldn\'t start'), findsOneWidget,
        reason: 'the degrade is surfaced in the exit snackbar');
  });

  testWidgets('a mid-wizard Save & Exit never provisions or shoots darks',
      (tester) async {
    final api = _FastProfileApi();
    final guider = _FakeGuiderEquipment();
    final calibration = _FakeCalibration();
    final container =
        await _pump(tester, api: api, guider: guider, calibration: calibration);
    container.read(wizardControllerProvider.notifier).jumpTo(1);
    await tester.pump();

    await tester.tap(find.text('Save & Exit'));
    await tester.pumpAndSettle();

    expect(guider.pushes, 0);
    expect(calibration.builds, 0);
  });

  DioException refusal(int status, String detail) => DioException(
        requestOptions: RequestOptions(path: '/x'),
        type: DioExceptionType.badResponse,
        response: Response(
          requestOptions: RequestOptions(path: '/x'),
          statusCode: status,
          data: {'title': 'refused', 'detail': detail},
        ),
        message: 'This exception was thrown because the response has a '
            'status code of $status and RequestOptions.validateStatus was '
            'configured to throw',
      );

  testWidgets('a refused guider push shows the server\'s reason, skips the '
      'dark-library build and says so', (tester) async {
    final guider = _FakeGuiderEquipment()
      ..pushError = refusal(422, 'The guide camera is the main imaging camera.');
    final calibration = _FakeCalibration();
    await _pump(tester,
        api: _FastProfileApi(), guider: guider, calibration: calibration);

    await tester.tap(find.text('Save Profile'));
    for (var i = 0; i < 8; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    expect(find.textContaining('The guide camera is the main imaging camera.'),
        findsOneWidget);
    expect(find.textContaining('when the guider is reachable'), findsNothing);
    expect(calibration.builds, 0,
        reason: 'darks would shoot with the previous profile\'s guide camera');
    expect(find.textContaining('Dark-library build skipped'), findsOneWidget);
    expect(find.text('You\'re all set'), findsNothing);
  });

  testWidgets('an unreachable guider keeps the "when the guider is reachable" '
      'advice and still skips the build', (tester) async {
    final guider = _FakeGuiderEquipment()
      ..pushError = DioException.connectionError(
          requestOptions: RequestOptions(path: '/x'), reason: 'refused');
    final calibration = _FakeCalibration();
    await _pump(tester,
        api: _FastProfileApi(), guider: guider, calibration: calibration);

    await tester.tap(find.text('Save Profile'));
    for (var i = 0; i < 8; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    expect(find.textContaining('when the guider is reachable'), findsOneWidget);
    expect(calibration.builds, 0);
  });

  testWidgets('a Main Camera connect refused with 409 shows the server\'s '
      'reason, not the Dio exception text', (tester) async {
    late HttpServer http;
    // The test binding swaps in a client that answers every request with 400.
    final overrides = HttpOverrides.current;
    HttpOverrides.global = null;
    addTearDown(() => HttpOverrides.global = overrides);
    await tester.runAsync(() async {
      http = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
      http.listen((req) async {
        req.response.headers.contentType = ContentType('application', 'json');
        if (req.uri.path == '/api/v1/equipment/discover/camera') {
          req.response.write(jsonEncode([
            {
              'unique_id': 'cam-1',
              'name': 'Sim camera',
              'type': 'camera',
              'host_name': 'localhost',
              'ip_address': '127.0.0.1',
              'ip_port': 32323,
              'alpaca_device_number': 0,
              'use_https': false,
            }
          ]));
        } else if (req.uri.path == '/api/v1/equipment/camera/connect') {
          req.response.statusCode = 409;
          req.response.headers.contentType =
              ContentType('application', 'problem+json');
          req.response.write(jsonEncode({
            'title': 'guide_camera_in_use',
            'detail': 'The guider is using this camera.',
          }));
        } else {
          req.response.write('{}');
        }
        await req.response.close();
      });
    });
    addTearDown(() => http.close(force: true));
    final container = await _pump(tester,
        api: _FastProfileApi(),
        guider: _FakeGuiderEquipment(),
        calibration: _FakeCalibration(),
        server: AraServer(hostname: '127.0.0.1', port: http.port));
    container.read(wizardControllerProvider).draft.equipment.cameraDeviceId =
        'cam-1';

    await tester.tap(find.text('Save Profile'));
    for (var i = 0; i < 40; i++) {
      await tester.runAsync(
          () => Future<void>.delayed(const Duration(milliseconds: 50)));
      await tester.pump(const Duration(milliseconds: 50));
    }

    expect(find.textContaining('Camera: The guider is using this camera.'),
        findsOneWidget);
    expect(find.textContaining('RequestOptions'), findsNothing);
    expect(find.textContaining('DioException'), findsNothing);
    // Elapse the Dio request timeouts armed inside the fake-async zone.
    await tester.pumpWidget(const SizedBox());
    await tester.pump(const Duration(minutes: 1));
  });
}
