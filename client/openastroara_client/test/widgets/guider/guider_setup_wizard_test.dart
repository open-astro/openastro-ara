import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/calibration_status.dart';
import 'package:openastroara/models/guider_equipment_choices.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/alpaca_device_names_api.dart';
import 'package:openastroara/services/guider_api.dart';
import 'package:openastroara/services/guider_calibration_api.dart';
import 'package:openastroara/services/guider_equipment_api.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/guider/guider_calibration_state.dart';
import 'package:openastroara/state/guider/guider_equipment_state.dart';
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/widgets/guider/guider_setup_wizard.dart';

const _server = AraServer(hostname: 'h', port: 5555);

const _daemonSettings = Phd2Settings(
  host: 'rc91.lan',
  port: 4400,
  guiderCamera: 'Old Camera',
);

const _choices = GuiderEquipmentChoices(
  cameras: ['Alpaca Camera [rc91.lan:6800/0]', 'None'],
  mounts: ['Alpaca Mount [rc91.lan:6800/0]', 'None'],
  auxMounts: [],
  adaptiveOptics: [],
  rotators: ['None', 'Simulator'],
);

class _FakeSavedServerService implements SavedServerService {
  @override
  Future<List<AraServer>> loadAll() async => const [_server];
  @override
  Future<void> saveAll(List<AraServer> servers) async {}
  @override
  Future<void> add(AraServer server) async {}
}

class _FakeProfileApi extends ProfileApi {
  _FakeProfileApi() : super(_server);
  Phd2Settings stored = _daemonSettings;
  // Unconfigured first-run shape: no camera/mount ever selected.
  void makeUnconfigured() =>
      stored = const Phd2Settings(host: 'rc91.lan', port: 4400);
  Phd2Settings? lastPut;
  @override
  Future<Phd2Settings> getPhd2Settings() async => stored;
  @override
  Future<Phd2Settings> putPhd2Settings(Phd2Settings value) async {
    lastPut = value;
    stored = value;
    return value;
  }
}

class _FakeGuiderApi implements GuiderClient {
  GuiderStatus? status;
  final calls = <String>[];
  @override
  Future<GuiderStatus?> getStatus() async => status;
  @override
  Future<void> connect(
      {String host = kDefaultGuiderHost,
      int port = kDefaultGuiderPort}) async {
    calls.add('connect:$host:$port');
    status = const GuiderStatus(
      name: 'OpenAstro Guider',
      connectionState: GuiderConnectionState.connected,
      runtimeState: GuiderRuntimeState.stopped,
    );
  }

  @override
  Future<void> disconnect() async => calls.add('disconnect');
  @override
  void close() {}
}

class _FakeEquipmentApi implements GuiderEquipmentClient {
  final calls = <String>[];
  @override
  Future<GuiderEquipmentChoicesResponse> getChoices() async {
    calls.add('choices');
    return const GuiderEquipmentChoicesResponse(
        connected: true, choices: _choices);
  }
  @override
  Future<List<String>> discoverAlpaca(
          {int? numQueries, int? timeoutSeconds}) async {
    calls.add('discover');
    return const ['bridge.local:11111'];
  }
  @override
  Future<void> pushProfile() async => calls.add('push');
  @override
  Future<double?> getAlpacaCameraPixelSize(
      {String? host, int? port, int? device}) async {
    calls.add('pixelsize:$host:$port:$device');
    return 2.9;
  }

  @override
  void close() {}
}

class _FakeAlpacaNames implements AlpacaDeviceNamesClient {
  final calls = <String>[];
  @override
  Future<Map<String, String>> fetchNames(String host, int port) async {
    calls.add('$host:$port');
    if (host == 'bridge.local') {
      // The AlpacaBridge server — only reachable once discovery names it.
      return {'camera/2': 'QHY5III-200M', 'telescope/5': 'Bridge Mount'};
    }
    return {
      'camera/0': 'ZWO ASI290MM Mini',
      'telescope/0': 'iOptron HAE29C EQ',
      'rotator/0': 'CAA',
    };
  }

  @override
  void close() {}
}

class _FakeCalibrationApi implements GuiderCalibrationClient {
  final calls = <String>[];
  @override
  Future<CalibrationStatusResponse> getStatus() async =>
      const CalibrationStatusResponse(connected: true);
  @override
  Future<void> buildDarkLibrary({
    int frameCount = 5,
    int? minExposureMs,
    int? maxExposureMs,
    bool clearExisting = false,
    String? notes,
    bool loadAfter = true,
  }) async =>
      calls.add('darks:$frameCount:$minExposureMs:$maxExposureMs');
  @override
  Future<void> buildDefectMap({
    int exposureMs = 3000,
    int frameCount = 10,
    String? notes,
    bool loadAfter = true,
  }) async {}
  @override
  Future<void> setDarkLibraryEnabled(bool enabled) async {}
  @override
  Future<void> setDefectMapEnabled(bool enabled) async {}
  @override
  Future<CalibrationStatusResponse> deleteCalibrationFiles(
          {bool darks = false, bool defectmap = false}) async =>
      const CalibrationStatusResponse(connected: true);
  @override
  void close() {}
}

typedef _Harness = ({
  ProviderContainer container,
  _FakeProfileApi profile,
  _FakeGuiderApi guider,
  _FakeEquipmentApi equipment,
  _FakeCalibrationApi calibration,
});

Future<_Harness> _pump(WidgetTester tester, {bool connected = true}) async {
  final profile = _FakeProfileApi();
  final guider = _FakeGuiderApi();
  if (connected) {
    guider.status = const GuiderStatus(
      name: 'OpenAstro Guider',
      connectionState: GuiderConnectionState.connected,
      runtimeState: GuiderRuntimeState.stopped,
    );
  }
  final equipment = _FakeEquipmentApi();
  final calibration = _FakeCalibrationApi();
  final names = _FakeAlpacaNames();
  final container = ProviderContainer(overrides: [
    savedServerServiceProvider
        .overrideWithValue(_FakeSavedServerService()),
    profileApiProvider.overrideWithValue(profile),
    guiderApiFactoryProvider.overrideWithValue((_) => guider),
    guiderEquipmentApiFactoryProvider.overrideWithValue((_) => equipment),
    guiderCalibrationApiFactoryProvider.overrideWithValue((_) => calibration),
    alpacaDeviceNamesApiProvider.overrideWithValue(names),
    // The darks step watches the build-activity fold, which would otherwise
    // open a real WS connection (leaking reconnect timers into the test).
    wsEventStreamProvider.overrideWithValue(null),
  ]);
  addTearDown(container.dispose);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: container,
    child: const MaterialApp(home: Scaffold(body: GuiderSetupWizard())),
  ));
  await tester.pumpAndSettle();
  return (
    container: container,
    profile: profile,
    guider: guider,
    equipment: equipment,
    calibration: calibration,
  );
}

Future<void> _next(WidgetTester tester) async {
  await tester.tap(find.text('Next'));
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('hydrates the draft from the daemon profile', (tester) async {
    await _pump(tester);
    expect(find.text('Guider setup — Guider connection'), findsOneWidget);
    // The host field carries the daemon value, not the client default.
    expect(find.widgetWithText(TextFormField, 'rc91.lan'), findsOneWidget);
  });

  testWidgets('walks camera → optics → mount with live choices',
      (tester) async {
    await _pump(tester);
    await _next(tester); // camera
    expect(find.text('Guider setup — Guide camera'), findsOneWidget);
    // The daemon's choice strings are offered; the stale stored value is
    // still representable (panel rule) but the real device is pickable.
    await tester.tap(find.byKey(const ValueKey('wiz-Guide camera')));
    await tester.pumpAndSettle();
    await tester
        .tap(find.text('ZWO ASI290MM Mini (rc91.lan:6800/0)').last);
    await tester.pumpAndSettle();
    await _next(tester); // optics
    expect(find.text('Guider setup — Guide optics'), findsOneWidget);
    await _next(tester); // mount
    expect(find.text('Guider setup — Mount'), findsOneWidget);
  });

  testWidgets('Apply PUTs the merged draft and pushes the profile',
      (tester) async {
    final h = await _pump(tester);
    await _next(tester); // camera
    await tester.tap(find.byKey(const ValueKey('wiz-Guide camera')));
    await tester.pumpAndSettle();
    await tester
        .tap(find.text('ZWO ASI290MM Mini (rc91.lan:6800/0)').last);
    await tester.pumpAndSettle();
    await _next(tester); // optics
    await _next(tester); // mount
    await _next(tester); // apply
    // The shared provider is untouched until Apply (draft pattern).
    expect(h.container.read(phd2SettingsProvider).guiderCamera, '');
    await tester.tap(find.text('Apply to guider'));
    await tester.pumpAndSettle();
    expect(h.profile.lastPut?.guiderCamera,
        'Alpaca Camera [rc91.lan:6800/0]');
    // Untouched fields ride the server copy, not client defaults.
    expect(h.profile.lastPut?.host, 'rc91.lan');
    expect(h.equipment.calls, contains('push'));
    // Applied fields are reflected into the shared provider.
    expect(h.container.read(phd2SettingsProvider).guiderCamera,
        'Alpaca Camera [rc91.lan:6800/0]');
  });

  testWidgets('Next past Review is gated until Apply succeeds',
      (tester) async {
    await _pump(tester);
    await _next(tester); // camera
    await _next(tester); // optics
    await _next(tester); // mount
    await _next(tester); // apply
    final next = tester.widget<FilledButton>(
        find.widgetWithText(FilledButton, 'Next'));
    expect(next.onPressed, isNull);
    await tester.tap(find.text('Apply to guider'));
    await tester.pumpAndSettle();
    await _next(tester); // darks — reachable now
    expect(find.text('Guider setup — Dark library'), findsOneWidget);
  });

  testWidgets('darks step kicks off the dark-library build', (tester) async {
    final h = await _pump(tester);
    await _next(tester); // camera
    await _next(tester); // optics
    await _next(tester); // mount
    await _next(tester); // apply
    await tester.tap(find.text('Apply to guider'));
    await tester.pumpAndSettle();
    await _next(tester); // darks
    await tester.tap(find.text('Build dark library'));
    // No pumpAndSettle: the indeterminate progress bar animates forever.
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 100));
    expect(h.calibration.calls, contains('darks:5:1000:6000'));
    // Progress surface replaces the controls (indeterminate until a tick).
    expect(find.byType(LinearProgressIndicator), findsOneWidget);
    expect(find.textContaining('Capturing dark frames'), findsOneWidget);
  });

  testWidgets('Cancel discards the draft — provider untouched',
      (tester) async {
    final h = await _pump(tester);
    await tester.enterText(
        find.byKey(const ValueKey('wiz-Host')), 'edited.local');
    await tester.tap(find.text('Cancel'));
    await tester.pumpAndSettle();
    expect(h.container.read(phd2SettingsProvider).host, 'localhost');
    expect(h.profile.lastPut, isNull);
  });

  test('parseAlpacaChoiceEndpoint parses the [host:port/N] suffix', () {
    expect(parseAlpacaChoiceEndpoint('Alpaca Camera [rc91.lan:6800/1]'),
        (host: 'rc91.lan', port: 6800, device: 1));
    expect(parseAlpacaChoiceEndpoint('Alpaca Camera [rc91.lan:6800/0]  '),
        (host: 'rc91.lan', port: 6800, device: 0));
    expect(parseAlpacaChoiceEndpoint('None'), isNull);
    expect(parseAlpacaChoiceEndpoint('ZWO ASI290MM Mini'), isNull);
    expect(parseAlpacaChoiceEndpoint('Weird [rc91.lan:0/0]'), isNull);
  });

  testWidgets('picking an Alpaca camera autofills pixel size from the driver',
      (tester) async {
    final h = await _pump(tester);
    await _next(tester); // camera
    await tester.tap(find.byKey(const ValueKey('wiz-Guide camera')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('ZWO ASI290MM Mini (rc91.lan:6800/0)').last);
    await tester.pumpAndSettle();
    expect(h.equipment.calls, contains('pixelsize:rc91.lan:6800:0'));
    // The field rebuilt with the driver value + the provenance note.
    expect(find.widgetWithText(TextFormField, '2.9'), findsOneWidget);
    expect(find.textContaining('Read from the camera'), findsOneWidget);
  });

  testWidgets('mount step offers a rotator picker from the daemon list',
      (tester) async {
    // Tall surface so the whole mount step (rotator included) is hittable.
    tester.view.physicalSize = const Size(1400, 1800);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    final h = await _pump(tester);
    await _next(tester); // camera
    await _next(tester); // optics
    await _next(tester); // mount
    await tester.tap(find.byKey(const ValueKey('wiz-Rotator')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Simulator').last);
    await tester.pumpAndSettle();
    await _next(tester); // apply
    await tester.tap(find.text('Apply to guider'));
    await tester.pumpAndSettle();
    expect(h.profile.lastPut?.guiderRotator, 'Simulator');
  });

  testWidgets('entering a picker step re-fetches the device lists',
      (tester) async {
    final h = await _pump(tester);
    final before =
        h.equipment.calls.where((c) => c == 'choices').length;
    await _next(tester); // camera
    await _next(tester); // optics
    await _next(tester); // mount
    final after = h.equipment.calls.where((c) => c == 'choices').length;
    expect(after, greaterThanOrEqualTo(before + 2),
        reason: 'camera and mount steps each refresh the choices');
  });

  test('friendlyAlpacaChoiceLabel overlays real names, falls back verbatim', () {
    const names = {'rc91.lan:6800|camera/1': 'ZWO ASI290MM Mini'};
    expect(
        friendlyAlpacaChoiceLabel(
            'Alpaca Camera [rc91.lan:6800/1]', 'camera', names),
        'ZWO ASI290MM Mini (rc91.lan:6800/1)');
    // Unknown endpoint / non-Alpaca choices stay verbatim.
    expect(
        friendlyAlpacaChoiceLabel(
            'Alpaca Camera [rc91.lan:6800/0]', 'camera', names),
        'Alpaca Camera [rc91.lan:6800/0]');
    expect(friendlyAlpacaChoiceLabel('None', 'camera', names), 'None');
  });

  testWidgets('pickers show real Alpaca device names from the management API',
      (tester) async {
    await _pump(tester);
    await _next(tester); // camera
    await tester.tap(find.byKey(const ValueKey('wiz-Guide camera')));
    await tester.pumpAndSettle();
    // The daemon string "Alpaca Camera [rc91.lan:6800/0]" is labeled with the
    // real device name; the verbatim value still backs the selection.
    expect(find.text('ZWO ASI290MM Mini (rc91.lan:6800/0)'), findsWidgets);
  });

  testWidgets(
      'network discovery surfaces cameras on other Alpaca servers '
      'and picking one retargets the profile host', (tester) async {
    final h = await _pump(tester);
    await _next(tester); // camera
    await tester.tap(find.text('Search network for Alpaca servers'));
    await tester.pumpAndSettle();
    expect(h.equipment.calls, contains('discover'));
    await tester.tap(find.byKey(const ValueKey('wiz-Guide camera')));
    await tester.pumpAndSettle();
    // The bridge camera appears with its real name, synthesized in the
    // daemon's own choice format.
    await tester.tap(find.text('QHY5III-200M (bridge.local:11111/2)').last);
    await tester.pumpAndSettle();
    await _next(tester); // optics
    await _next(tester); // mount
    await _next(tester); // apply
    await tester.tap(find.text('Apply to guider'));
    await tester.pumpAndSettle();
    expect(h.profile.lastPut?.guiderCamera,
        'Alpaca Camera [bridge.local:11111/2]');
    // Picking a camera on another server retargets the daemon's Alpaca config.
    expect(h.profile.lastPut?.guiderAlpacaHost, 'bridge.local');
    expect(h.profile.lastPut?.guiderAlpacaPort, 11111);
  });

  testWidgets('final step offers a filled Finish, not a dead Next',
      (tester) async {
    await _pump(tester);
    await _next(tester); // camera
    await _next(tester); // optics
    await _next(tester); // mount
    await _next(tester); // apply
    await tester.tap(find.text('Apply to guider'));
    await tester.pumpAndSettle();
    await _next(tester); // darks (last)
    expect(find.text('Next'), findsNothing);
    expect(find.text('Cancel'), findsNothing);
    final finish =
        tester.widget<FilledButton>(find.widgetWithText(FilledButton, 'Finish'));
    expect(finish.onPressed, isNotNull);
    await tester.tap(find.text('Finish'));
    await tester.pumpAndSettle();
    expect(find.byType(GuiderSetupWizard), findsNothing); // dialog closed
  });

  testWidgets(
      'mount picker only offers devices on the camera\'s Alpaca server',
      (tester) async {
    await _pump(tester);
    await _next(tester); // camera
    await tester.tap(find.text('Search network for Alpaca servers'));
    await tester.pumpAndSettle();
    // Camera stays on rc91 (the daemon's server) — pick it explicitly.
    await tester.tap(find.byKey(const ValueKey('wiz-Guide camera')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('ZWO ASI290MM Mini (rc91.lan:6800/0)').last);
    await tester.pumpAndSettle();
    await _next(tester); // optics
    await _next(tester); // mount
    await tester.tap(find.byKey(const ValueKey('wiz-Mount')));
    await tester.pumpAndSettle();
    // The bridge's mount must NOT be offered: OpenAstro Guider has one Alpaca server
    // (the camera's), so a cross-server mount would be silently dropped.
    expect(find.textContaining('Bridge Mount'), findsNothing);
    expect(find.textContaining('iOptron HAE29C EQ'), findsWidgets);
  });

  testWidgets(
      'unconfigured guider shows a placeholder, never a phantom selection',
      (tester) async {
    final profile = _FakeProfileApi()..makeUnconfigured();
    final guider = _FakeGuiderApi()
      ..status = const GuiderStatus(
        name: 'OpenAstro Guider',
        connectionState: GuiderConnectionState.connected,
        runtimeState: GuiderRuntimeState.stopped,
      );
    final container = ProviderContainer(overrides: [
      savedServerServiceProvider.overrideWithValue(_FakeSavedServerService()),
      profileApiProvider.overrideWithValue(profile),
      guiderApiFactoryProvider.overrideWithValue((_) => guider),
      guiderEquipmentApiFactoryProvider
          .overrideWithValue((_) => _FakeEquipmentApi()),
      guiderCalibrationApiFactoryProvider
          .overrideWithValue((_) => _FakeCalibrationApi()),
      alpacaDeviceNamesApiProvider.overrideWithValue(_FakeAlpacaNames()),
      wsEventStreamProvider.overrideWithValue(null),
    ]);
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: GuiderSetupWizard())),
    ));
    await tester.pumpAndSettle();
    await _next(tester); // camera
    // The draft holds no camera — the field must SAY so, not display the
    // first discovered device as if it were picked (review r2).
    expect(find.text('Select a device…'), findsWidgets);
    expect(
        find.descendant(
            of: find.byKey(const ValueKey('wiz-Guide camera')),
            matching: find.text('ZWO ASI290MM Mini (rc91.lan:6800/0)')),
        findsNothing);
  });
}
