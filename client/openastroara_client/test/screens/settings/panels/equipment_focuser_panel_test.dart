import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/focuser_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/settings/panels/equipment_focuser_panel.dart';
import 'package:openastroara/models/autofocus_run.dart';
import 'package:openastroara/services/autofocus_api.dart';
import 'package:openastroara/services/equipment_device_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/equipment/focuser_state.dart';
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

class _FakeAutofocusApi implements AutofocusApi {
  @override
  Future<void> recalibrate() async {}

  _FakeAutofocusApi({this.terminalState = 'complete', this.errorMessage, this.failFirstPolls = 0});
  final String terminalState;
  final String? errorMessage;
  final int failFirstPolls; // simulate transient poll blips before answering
  bool vanish = false; // simulate the daemon losing the job (restart mid-sweep)
  int startCalls = 0;
  int polls = 0;
  @override
  Future<AutofocusJob> start() async {
    startCalls++;
    return const AutofocusJob(jobId: 'j1', state: 'running');
  }

  @override
  Future<AutofocusJob?> job(String jobId) async {
    polls++;
    if (polls <= failFirstPolls) throw Exception('transient blip');
    if (vanish) return null;
    return AutofocusJob(jobId: jobId, state: terminalState, errorMessage: errorMessage);
  }

  @override
  Future<AutofocusRun> state() async => AutofocusRun.idle;

  @override
  Future<void> cancel() async {}

  @override
  Future<AutofocusFrame?> fetchFrame() async => null;

  @override
  void close() {}
}

class _FakeFocuserApi implements EquipmentDeviceClient<FocuserStatus> {
  @override
  Future<void> forget() async {
    calls.add('forget');
  }
  _FakeFocuserApi(this.status);
  FocuserStatus? status;
  final List<String> calls = [];
  @override
  Future<FocuserStatus?> getStatus() async => status;
  @override
  Future<void> connect(DiscoveredDevice device) async =>
      calls.add('connect:${device.name}');
  @override
  Future<void> disconnect() async => calls.add('disconnect');
  @override
  Future<void> command(String subpath, [Map<String, dynamic>? body]) async =>
      calls.add('command:$subpath:${body?['target_position']}');
  @override
  void close() {}
}

FocuserStatus _status({
  EquipmentConnectionState state = EquipmentConnectionState.connected,
  int? position = 1000,
  String runtimeState = 'idle',
  bool zero = false,
  bool calibrate = false,
  String? calibration,
}) =>
    FocuserStatus(
      deviceId: 'foc-0',
      name: 'MoonLite',
      connectionState: state,
      capabilities: FocuserCapabilities(
        minPosition: 0,
        maxPosition: 50000,
        stepSizeUm: 1.5,
        canTempComp: true,
        absoluteFocuser: true,
        canSetZero: zero,
        canCalibrate: calibrate,
      ),
      runtimeState: runtimeState,
      calibration: calibration,
      position: position,
      temperature: 3.2,
      tempCompEnabled: false,
    );

Future<void> _wideSurface(WidgetTester tester) async {
  await tester.binding.setSurfaceSize(const Size(1200, 1000));
  addTearDown(() => tester.binding.setSurfaceSize(null));
}

Future<_FakeFocuserApi> _pump(WidgetTester tester, FocuserStatus? status,
    {_FakeAutofocusApi? autofocus}) async {
  // A working device's status blinks (DESIGN.md status rule) and never settles: Reduce Motion holds it.
  tester.platformDispatcher.accessibilityFeaturesTestValue = const FakeAccessibilityFeatures(disableAnimations: true);
  addTearDown(tester.platformDispatcher.clearAccessibilityFeaturesTestValue);
  await _wideSurface(tester);
  final api = _FakeFocuserApi(status);
  await tester.pumpWidget(ProviderScope(
    overrides: [
      serverLinkUpProvider.overrideWith((ref) => true),
      savedServerServiceProvider.overrideWithValue(
          _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
      focuserApiFactoryProvider.overrideWithValue((_) => api),
      autofocusApiFactoryProvider
          .overrideWithValue((_) => autofocus ?? _FakeAutofocusApi()),
    ],
    child: const MaterialApp(home: Scaffold(body: EquipmentFocuserPanel())),
  ));
  await tester.pumpAndSettle();
  return api;
}

void main() {
  testWidgets('renders live position + temperature + move control', (tester) async {
    await _pump(tester, _status(position: 1234));
    expect(find.text('MoonLite'), findsOneWidget);
    expect(find.text('Connected'), findsOneWidget);
    // Shown twice: the live Position row + the Target field seeded from it.
    expect(find.text('1234'), findsNWidgets(2));
    expect(find.text('Position'), findsOneWidget);
    expect(find.text('3.2 °C'), findsOneWidget);
    expect(find.widgetWithText(FilledButton, 'Move'), findsOneWidget);
  });

  // ADR 0009 rule 1: the full UI reaches the same focuser actions as Simple Mode.
  testWidgets('Set zero asks first, then sends setzero', (tester) async {
    final api = await _pump(tester, _status(position: 1234, zero: true));
    expect(find.byKey(const Key('focuser-calibrate')), findsNothing, reason: 'only what the driver offers');
    await tester.tap(find.byKey(const Key('focuser-set-zero')));
    await tester.pumpAndSettle();
    expect(api.calls.where((c) => c.startsWith('command:setzero')), isEmpty, reason: 'asks first');
    await tester.tap(find.byKey(const Key('focuser-set-zero-confirm')));
    await tester.pumpAndSettle();
    expect(api.calls, contains('command:setzero:null'));
  });

  testWidgets('Calibrate travel starts it; while it runs Stop aborts it', (tester) async {
    final api = await _pump(tester, _status(calibrate: true, calibration: 'not_calibrated'));
    expect(find.byKey(const Key('focuser-set-zero')), findsNothing);
    await tester.tap(find.byKey(const Key('focuser-calibrate')));
    await tester.pumpAndSettle();
    expect(api.calls, contains('command:calibrate:null'));
  });

  testWidgets('a calibrating focuser shows Stop, which aborts', (tester) async {
    final api = await _pump(tester, _status(zero: true, calibrate: true, calibration: 'calibrating'));
    final zero = tester.widget<OutlinedButton>(find.byKey(const Key('focuser-set-zero')));
    expect(zero.onPressed, isNull, reason: 'no zero while it calibrates');
    await tester.tap(find.byKey(const Key('focuser-calibrate-stop')));
    await tester.pumpAndSettle();
    expect(api.calls, contains('command:calibrate/abort:null'));
  });

  testWidgets('Move sends the target position command', (tester) async {
    final api = await _pump(tester, _status(position: 1000));
    await tester.enterText(find.byType(TextField), '2500');
    await tester.tap(find.widgetWithText(FilledButton, 'Move'));
    await tester.pumpAndSettle();
    expect(api.calls, contains('command:move:2500'));
  });

  testWidgets('Move clamps a past-max target to the device range', (tester) async {
    final api = await _pump(tester, _status());
    await tester.enterText(find.byType(TextField), '99999'); // > max 50000
    await tester.tap(find.widgetWithText(FilledButton, 'Move'));
    await tester.pumpAndSettle();
    expect(api.calls, contains('command:move:50000'));
  });

  testWidgets('a moving focuser shows Moving… and disables Move', (tester) async {
    await _pump(tester, _status(runtimeState: 'moving'));
    expect(find.text('Moving…'), findsOneWidget);
    final btn = tester.widget<FilledButton>(find.widgetWithText(FilledButton, 'Move'));
    expect(btn.onPressed, isNull);
  });

  testWidgets('a relative focuser allows a negative step and does not clamp',
      (tester) async {
    final api = await _pump(
        tester,
        FocuserStatus(
          deviceId: 'foc-1',
          name: 'Relative',
          connectionState: EquipmentConnectionState.connected,
          capabilities: const FocuserCapabilities(
            minPosition: 0,
            maxPosition: 0, // relative focusers report no absolute range
            stepSizeUm: 1.0,
            canTempComp: false,
            absoluteFocuser: false,
          ),
          runtimeState: 'idle',
          position: 0,
          temperature: null,
          tempCompEnabled: false,
        ));
    expect(find.text('Steps (±)'), findsOneWidget);
    await tester.enterText(find.byType(TextField), '-300'); // inward, negative
    await tester.tap(find.widgetWithText(FilledButton, 'Move'));
    await tester.pumpAndSettle();
    // Sent as-is (no clamp to the 0..0 absolute range).
    expect(api.calls, contains('command:move:-300'));
  });

  testWidgets('no device connected shows the empty state + Connect…',
      (tester) async {
    await _pump(tester, null);
    expect(find.text('No focuser connected.'), findsOneWidget);
    expect(find.widgetWithText(TextButton, 'Connect…'), findsOneWidget);
  });

  testWidgets('Run autofocus polls the job to completion', (tester) async {
    final af = _FakeAutofocusApi(terminalState: 'complete');
    await _pump(tester, _status(), autofocus: af);
    await tester.tap(find.widgetWithText(FilledButton, 'Run autofocus'));
    await tester.pump(); // start() resolves; row flips to running
    expect(find.text('Autofocusing…'), findsOneWidget);
    await tester.pump(const Duration(seconds: 3)); // first poll → terminal
    await tester.pumpAndSettle();
    expect(af.startCalls, 1);
    expect(find.textContaining('Autofocus complete'), findsOneWidget);
  });

  testWidgets('failed sweep surfaces the daemon reason inline', (tester) async {
    final af = _FakeAutofocusApi(
        terminalState: 'failed', errorMessage: 'Autofocus sweep failed — curve fit unusable');
    await _pump(tester, _status(), autofocus: af);
    await tester.tap(find.widgetWithText(FilledButton, 'Run autofocus'));
    await tester.pump();
    await tester.pump(const Duration(seconds: 3));
    await tester.pumpAndSettle();
    expect(find.textContaining('curve fit unusable'), findsOneWidget);
  });

  testWidgets('transient poll blips do not abort the tracking', (tester) async {
    // The daemon job keeps running through a dropped request — one blip must
    // not declare failure (the mid-sweep-outage convention from the device
    // liveness polls).
    final af = _FakeAutofocusApi(terminalState: 'complete', failFirstPolls: 2);
    await _pump(tester, _status(), autofocus: af);
    await tester.tap(find.widgetWithText(FilledButton, 'Run autofocus'));
    await tester.pump();
    // Two failing polls + the successful third (2s apart each).
    await tester.pump(const Duration(seconds: 3));
    await tester.pump(const Duration(seconds: 2));
    await tester.pump(const Duration(seconds: 2));
    await tester.pumpAndSettle();
    expect(find.textContaining('Autofocus complete'), findsOneWidget);
    expect(af.polls, greaterThanOrEqualTo(3));
  });

  testWidgets('a vanished job reads as lost-track, never as finished', (tester) async {
    // The daemon's job store never evicts — a 404 means it LOST STATE (restart
    // mid-sweep) and the focuser may sit at a probe position. The row must warn,
    // not report success.
    final af = _FakeAutofocusApi()..vanish = true;
    await _pump(tester, _status(), autofocus: af);
    await tester.tap(find.widgetWithText(FilledButton, 'Run autofocus'));
    await tester.pump();
    await tester.pump(const Duration(seconds: 3));
    await tester.pumpAndSettle();
    expect(find.textContaining('Lost track of the sweep'), findsOneWidget);
    expect(find.textContaining('finished'), findsNothing);
  });

  testWidgets('Run autofocus disabled without a connected focuser', (tester) async {
    await _pump(tester, _status(state: EquipmentConnectionState.disconnected));
    final button = tester.widget<FilledButton>(
        find.widgetWithText(FilledButton, 'Run autofocus'));
    expect(button.onPressed, isNull);
    expect(find.text('Connect a focuser to run autofocus.'), findsOneWidget);
  });
}
