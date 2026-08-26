import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/flat_panel_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/settings/panels/equipment_flat_panel.dart';
import 'package:openastroara/services/equipment_device_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/equipment/flat_panel_state.dart';
import 'package:openastroara/state/saved_server_state.dart';

class _FakeSavedServerService implements SavedServerService {
  _FakeSavedServerService(this._stored);
  final List<AraServer> _stored;
  @override
  Future<List<AraServer>> loadAll() async => List.unmodifiable(_stored);
  @override
  Future<void> saveAll(List<AraServer> servers) async {}
  @override
  Future<void> add(AraServer server) async {}
}

class _FakeFlatApi implements EquipmentDeviceClient<FlatPanelStatus> {
  _FakeFlatApi(this.status);
  FlatPanelStatus? status;
  final List<String> calls = [];
  final List<Map<String, dynamic>?> bodies = [];
  Object? commandError; // when set, `command` throws it (a failing device)

  @override
  Future<FlatPanelStatus?> getStatus() async => status;
  @override
  Future<void> connect(DiscoveredDevice device) async =>
      calls.add('connect:${device.name}');
  @override
  Future<void> disconnect() async => calls.add('disconnect');
  @override
  Future<void> command(String subpath, [Map<String, dynamic>? body]) async {
    calls.add('command:$subpath');
    bodies.add(body);
    if (commandError != null) throw commandError!;
  }
  @override
  void close() {}
}

FlatPanelStatus _status({
  EquipmentConnectionState state = EquipmentConnectionState.connected,
  bool lightOn = false,
  int brightness = 0,
  bool coverOpen = false,
  String runtimeState = 'cover_closed',
  int maxBrightness = 255,
  bool hasCover = true,
  bool hasCalibrator = true,
  bool lightWarming = false,
}) =>
    FlatPanelStatus(
      deviceId: 'flat-0',
      name: 'FlatMaster',
      connectionState: state,
      runtimeState: runtimeState,
      coverOpen: coverOpen,
      lightOn: lightOn,
      brightness: brightness,
      maxBrightness: maxBrightness,
      hasCover: hasCover,
      hasCalibrator: hasCalibrator,
      lightWarming: lightWarming,
    );

// The settings panels are designed for the wide right-hand pane; give the test a
// generous surface so the shared SettingsSwitchRow doesn't overflow at 800px.
Future<void> _wideSurface(WidgetTester tester) async {
  await tester.binding.setSurfaceSize(const Size(1200, 900));
  addTearDown(() => tester.binding.setSurfaceSize(null));
}

Future<_FakeFlatApi> _pump(WidgetTester tester, FlatPanelStatus? status) async {
  await _wideSurface(tester);
  final api = _FakeFlatApi(status);
  await tester.pumpWidget(ProviderScope(
    overrides: [
      serverLinkUpProvider.overrideWith((ref) => true),
      savedServerServiceProvider.overrideWithValue(
          _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
      flatPanelApiFactoryProvider.overrideWithValue((_) => api),
    ],
    child: const MaterialApp(home: Scaffold(body: EquipmentFlatPanel())),
  ));
  await tester.pumpAndSettle();
  return api;
}

/// Let the notifier's post-apply confirm poll run to completion (it re-reads
/// every 500 ms until the device reflects the command, or its ~4 s budget runs
/// out) so no timer outlives the test.
Future<void> _drainConfirm(WidgetTester tester) async {
  for (var i = 0; i < 10; i++) {
    await tester.pump(const Duration(milliseconds: 500));
  }
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('no flat panel connected shows the empty state + Connect…',
      (tester) async {
    await _pump(tester, null);
    expect(find.text('No flat panel connected.'), findsOneWidget);
    expect(find.widgetWithText(TextButton, 'Connect…'), findsOneWidget);
  });

  testWidgets('offers Reconnect while disconnected', (tester) async {
    // The shared card surfaces Reconnect only in the disconnected state.
    await _pump(tester, null);
    expect(find.widgetWithText(TextButton, 'Reconnect'), findsOneWidget);
  });

  testWidgets('a disconnected (non-null) status still offers Reconnect',
      (tester) async {
    // After a session disconnect the daemon keeps the device and reports
    // state=disconnected (a non-null status, not a 404). The card must still show
    // the disconnected layout with Reconnect — not treat the lingering device as
    // connected (which would hide Reconnect behind a Disconnect button).
    await _pump(tester, _status(state: EquipmentConnectionState.disconnected));
    expect(find.text('No flat panel connected.'), findsOneWidget);
    expect(find.widgetWithText(TextButton, 'Reconnect'), findsOneWidget);
    expect(find.byIcon(Icons.link_off), findsNothing); // not the connected layout
  });

  testWidgets('a connected panel with the light on shows its readout, no Reconnect',
      (tester) async {
    await _pump(tester,
        _status(lightOn: true, brightness: 128, runtimeState: 'light_on'));
    expect(find.text('FlatMaster'), findsOneWidget);
    expect(find.text('Connected'), findsOneWidget);
    expect(find.text('Light on · brightness 128'), findsOneWidget);
    // Reconnect is hidden while connected (Disconnect is offered instead).
    expect(find.widgetWithText(TextButton, 'Reconnect'), findsNothing);
  });

  testWidgets('a connected panel with the light off shows "Light off"',
      (tester) async {
    await _pump(tester, _status(lightOn: false));
    expect(find.text('Light off'), findsOneWidget);
  });

  testWidgets('a moving cover shows "Cover moving…"', (tester) async {
    await _pump(tester, _status(runtimeState: 'cover_moving'));
    expect(find.text('Cover moving…'), findsOneWidget);
  });

  testWidgets('connecting then settling to connected via the poll turns live',
      (tester) async {
    // The daemon's connect is 202 + background, so the first read shows
    // `connecting`. The generic engine polls while connecting; once the daemon
    // finishes, the card settles to Connected without the user re-opening.
    await _wideSurface(tester);
    final api =
        _FakeFlatApi(_status(state: EquipmentConnectionState.connecting));
    await tester.pumpWidget(ProviderScope(
      overrides: [
        serverLinkUpProvider.overrideWith((ref) => true),
        savedServerServiceProvider.overrideWithValue(
            _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
        flatPanelApiFactoryProvider.overrideWithValue((_) => api),
      ],
      child: const MaterialApp(home: Scaffold(body: EquipmentFlatPanel())),
    ));
    await tester.pump(); // build
    await tester.pump(); // resolve the initial getStatus
    expect(find.text('Connecting'), findsOneWidget);

    api.status = _status(lightOn: true, brightness: 64, runtimeState: 'light_on');
    await tester.pump(const Duration(milliseconds: 1600)); // settle tick → refresh
    await tester.pump(); // resolve the refresh getStatus
    expect(find.text('Connected'), findsOneWidget);
    expect(find.text('Light on · brightness 64'), findsOneWidget);
  });

  testWidgets('disconnect targets the device', (tester) async {
    final api = await _pump(tester, _status());
    await tester.tap(find.byIcon(Icons.link_off));
    await tester.pumpAndSettle();
    expect(api.calls, contains('disconnect'));
  });

  testWidgets('Reconnect dispatches the reconnect command', (tester) async {
    final api = await _pump(tester, null);
    await tester.tap(find.widgetWithText(TextButton, 'Reconnect'));
    await tester.pumpAndSettle();
    expect(api.calls, contains('command:reconnect'));
  });

  testWidgets('Open / Close drive the cover through apply', (tester) async {
    final api = await _pump(tester, _status());
    await tester.tap(find.widgetWithText(OutlinedButton, 'Open'));
    await tester.pumpAndSettle();
    expect(api.calls, contains('command:apply'));
    expect(api.bodies.last, {'open_cover': true});

    await tester.tap(find.widgetWithText(OutlinedButton, 'Close'));
    await tester.pumpAndSettle();
    expect(api.bodies.last, {'open_cover': false});
  });

  testWidgets('both cover buttons are disabled while the cover moves',
      (tester) async {
    await _pump(tester, _status(runtimeState: 'cover_moving'));
    for (final label in ['Open', 'Close']) {
      final button = tester.widget<OutlinedButton>(
          find.widgetWithText(OutlinedButton, label));
      expect(button.onPressed, isNull, reason: '$label must be dead mid-travel');
    }
  });

  testWidgets('the light switch turns the calibrator on', (tester) async {
    final api = await _pump(tester, _status(lightOn: false));
    // What the device becomes once the daemon's background apply lands.
    api.status = _status(lightOn: true, brightness: 255, runtimeState: 'light_on');
    await tester.tap(find.byKey(const Key('flat-light-switch')));
    await tester.pump();
    expect(api.bodies.last, {'light_on': true});
    // The switch moves under the finger — it does NOT wait for the device.
    expect(tester.widget<Switch>(
            find.byKey(const Key('flat-light-switch'))).value, isTrue);
    expect(find.text('Turning the light on…'), findsOneWidget);
    await _drainConfirm(tester);
    // …and the confirm poll lands the real reading well inside the 15 s
    // liveness poll that used to be the only update.
    expect(find.text('Light on · brightness 255'), findsOneWidget);
  });

  testWidgets('the light switch turns the calibrator off again', (tester) async {
    // The switch reflects the DEVICE's state, not a local toggle: pumped with the
    // light already on, tapping it must command off.
    final api = await _pump(tester,
        _status(lightOn: true, brightness: 255, runtimeState: 'light_on'));
    api.status = _status(lightOn: false);
    await tester.tap(find.byKey(const Key('flat-light-switch')));
    await tester.pump();
    expect(api.bodies.last, {'light_on': false});
    await _drainConfirm(tester);
    expect(find.text('Light off'), findsOneWidget);
  });

  testWidgets('the brightness slider commits once, on release', (tester) async {
    final api = await _pump(tester,
        _status(lightOn: true, brightness: 0, runtimeState: 'light_on'));
    await tester.drag(find.byType(Slider), const Offset(200, 0));
    await tester.pump();
    await _drainConfirm(tester);
    // One apply for the whole drag (onChangeEnd), never one per frame.
    expect(api.calls.where((c) => c == 'command:apply').length, 1);
    final sent = api.bodies.last!['brightness'] as int;
    expect(sent, greaterThan(0));
    expect(sent, lessThanOrEqualTo(255));
  });

  testWidgets('the brightness slider is disabled until the max is known',
      (tester) async {
    await _pump(tester, _status(lightOn: true, maxBrightness: 0));
    expect(tester.widget<Slider>(find.byType(Slider)).onChanged, isNull);
    expect(find.textContaining('brightness range'), findsOneWidget);
  });

  testWidgets('a device with no cover hides the cover row', (tester) async {
    await _pump(tester, _status(hasCover: false));
    expect(find.widgetWithText(OutlinedButton, 'Open'), findsNothing);
    expect(find.text('Cover closed'), findsNothing);
    expect(find.byKey(const Key('flat-light-switch')), findsOneWidget); // light row stays
  });

  testWidgets('a device with no calibrator hides the light + brightness rows',
      (tester) async {
    await _pump(tester, _status(hasCalibrator: false));
    expect(find.byType(Slider), findsNothing);
    expect(find.byKey(const Key('flat-light-switch')), findsNothing);
    expect(find.text('Light off'), findsNothing);
    expect(find.widgetWithText(OutlinedButton, 'Open'), findsOneWidget);
  });

  testWidgets('a light command waits out a moving cover instead of giving up',
      (tester) async {
    // Panels refuse a calibrator change while the cover travels, so the daemon
    // holds the command until the cover settles. The pending state must survive
    // that wait — well past the 4 s confirm budget — rather than snapping back.
    final api = await _pump(tester, _status(runtimeState: 'cover_moving'));
    await tester.tap(find.byKey(const Key('flat-light-switch')));
    await tester.pump();
    expect(api.bodies.last, {'light_on': true});
    for (var i = 0; i < 30; i++) {
      await tester.pump(const Duration(milliseconds: 500)); // 15 s of travel
    }
    expect(find.textContaining('Waiting for the cover'), findsOneWidget);
    expect(tester.widget<Switch>(
            find.byKey(const Key('flat-light-switch'))).value, isTrue);

    // Cover settles, the daemon applies the held command, the panel confirms.
    api.status = _status(lightOn: true, brightness: 255, runtimeState: 'light_on');
    await _drainConfirm(tester);
    expect(find.text('Light on · brightness 255'), findsOneWidget);
  });

  testWidgets('a warming light reads as in-progress, not as a failure',
      (tester) async {
    // EL panels report NotReady while ramping to the commanded level. That is the
    // command still working, so the pending state must hold through it rather
    // than time out and snap the switch back.
    final api = await _pump(tester, _status(lightWarming: true));
    await tester.tap(find.byKey(const Key('flat-light-switch')));
    await tester.pump();
    for (var i = 0; i < 30; i++) {
      await tester.pump(const Duration(milliseconds: 500)); // 15 s of warm-up
    }
    expect(find.text('Light warming up…'), findsOneWidget);
    expect(tester.widget<Switch>(
            find.byKey(const Key('flat-light-switch'))).value, isTrue);

    api.status = _status(lightOn: true, brightness: 255, runtimeState: 'light_on');
    await _drainConfirm(tester);
    expect(find.text('Light on · brightness 255'), findsOneWidget);
  });

  testWidgets('a failing apply surfaces the error instead of failing silently',
      (tester) async {
    final api = await _pump(tester, _status());
    api.commandError = Exception('driver said no');
    await tester.tap(find.widgetWithText(OutlinedButton, 'Open'));
    await tester.pumpAndSettle();
    expect(find.textContaining("Couldn't drive the flat panel"), findsOneWidget);
  });
}
