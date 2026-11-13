import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/camera_status.dart';
import 'package:openastroara/models/filter_wheel_status.dart';
import 'package:openastroara/models/guide_focus.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/screens/simple/simple_device_windows.dart' show SimpleGuideGainSlider;
import 'package:openastroara/screens/simple/simple_focus_panel.dart' show SimpleGuideLiveShutter;
import 'package:openastroara/screens/simple/simple_preview_panel.dart';
import 'package:openastroara/services/equipment_driver_api.dart';
import 'package:openastroara/state/equipment/camera_state.dart';
import 'package:openastroara/state/equipment/filter_wheel_state.dart';
import 'package:openastroara/state/focus/autofocus_live_state.dart';
import 'package:openastroara/state/focus/guide_focus_state.dart';
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/state/imaging/capture_progress_state.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/widgets/imaging/exposure_controls_panel.dart' show FilterDropdown;

class _Camera extends CameraStatusNotifier {
  @override
  Future<CameraStatus?> build() async => CameraStatus.fromJson(
      {'device_id': 'c', 'name': 'ZWO ASI533MC Pro', 'state': 'connected', 'capabilities': <String, dynamic>{}});
}

class _Guider extends GuiderStatusNotifier {
  _Guider(this.connection, this.runtime);
  final String connection;
  final String runtime;
  @override
  Future<GuiderStatus?> build() async => connection == 'none'
      ? null
      : GuiderStatus.fromJson(
          {'device_id': 'g', 'name': 'PHD2', 'state': connection, 'runtime': {'state': runtime}});
}

class _Wheel extends FilterWheelNotifier {
  _Wheel(this.connection);
  final String connection;
  @override
  Future<FilterWheelStatus?> build() async => FilterWheelStatus.fromJson(
      {'device_id': 'w', 'name': 'Wheel', 'state': connection, 'slots': <dynamic>[]});
}

class _Live extends AutofocusLiveNotifier {
  @override
  AutofocusLive build() => const AutofocusLive();
}

class _Capture extends CaptureProgressNotifier {
  _Capture(this.phase);
  final CapturePhase phase;
  @override
  CaptureProgress build() => CaptureProgress(phase: phase);
}

class _Run extends SequenceRunStateNotifier {
  @override
  Future<SequenceRunStateInfo?> build() async => null;
}

class _GuideFocus extends GuideFocusNotifier {
  _GuideFocus(this.live0);
  final GuideFocusLive live0;
  final calls = <String>[];
  @override
  GuideFocusLive build() => live0;
  @override
  Future<void> start({required double exposureSec, int? binning}) async => calls.add('start $exposureSec');
  @override
  Future<void> stop() async => calls.add('stop');
}

late ProviderContainer _c;
late _GuideFocus _g;

Future<void> _pump(WidgetTester tester, Widget child,
    {String guider = 'connected',
    String runtime = 'stopped',
    String wheel = 'connected',
    bool guideRunning = false,
    CapturePhase capture = CapturePhase.idle,
    bool guideSample = false,
    bool oag = false}) async {
  _g = _GuideFocus(guideRunning
      ? GuideFocusLive(
          status: GuideFocusStatus(
              active: true,
              state: 'running',
              exposureSec: 2,
              seq: 1,
              latest: guideSample ? const GuideFocusSample(seq: 1, hfr: 2.34, stars: 12, peakAdu: 900, fwhm: 3) : null))
      : GuideFocusLive.idle);
  _c = ProviderContainer(overrides: [
    cameraStatusProvider.overrideWith(_Camera.new),
    guiderStatusProvider.overrideWith(() => _Guider(guider, runtime)),
    filterWheelProvider.overrideWith(() => _Wheel(wheel)),
    guideFocusProvider.overrideWith(() => _g),
    captureProgressProvider.overrideWith(() => _Capture(capture)),
    sequenceRunStateProvider.overrideWith(_Run.new),
    autofocusLiveProvider.overrideWith(_Live.new),
    deviceDriverProvider.overrideWith(
        (ref, type) async => type == 'guidecamera' ? const DeviceDriver(name: 'ZWO ASI120MM Mini') : null),
  ]);
  addTearDown(_c.dispose);
  if (oag) _c.read(phd2SettingsProvider.notifier).setGuiderSetupType('oag');
  // Keep the autoDispose driver alive, as the panel's listeners do on a running app.
  _c.listen(deviceDriverProvider('guidecamera'), (_, _) {});
  await tester.pumpWidget(UncontrolledProviderScope(
    key: ObjectKey(_c), // a test that pumps twice gets a fresh scope for its new container
    container: _c,
    child: MaterialApp(home: Scaffold(body: Center(child: child is SimpleShutter ? child : SingleChildScrollView(child: child)))),
  ));
  await tester.pump();
  await tester.pump();
}

Finder _row(String id) => find.byKey(Key('simple-preview-camera-$id'));

void main() {
  testWidgets('Preview lists the connected cameras, each with its model', (tester) async {
    await _pump(tester, const SimpleSettingsPopover());
    expect(find.text('Camera'), findsOneWidget);
    expect(find.descendant(of: _row('main'), matching: find.text('ZWO ASI533MC Pro')), findsOneWidget);
    expect(find.descendant(of: _row('guide'), matching: find.text('ZWO ASI120MM Mini')), findsOneWidget);
  });

  testWidgets('with the guider off only the main camera is offered', (tester) async {
    await _pump(tester, const SimpleSettingsPopover(), guider: 'disconnected');
    expect(_row('main'), findsOneWidget);
    expect(_row('guide'), findsNothing);
  });

  group('an off-axis guider waiting on main focus', () {
    const key = Key('simple-preview-already-focused');
    Future<void> oagOnGuide(WidgetTester tester) async {
      await _pump(tester, const SimpleSettingsPopover(), oag: true);
      _c.read(previewCameraProvider.notifier).set(PreviewCamera.guide);
      await tester.pump();
    }

    testWidgets('Preview offers Already in focus, which clears the wait', (tester) async {
      await oagOnGuide(tester);
      await tester.tap(find.byKey(key));
      await tester.pump();
      expect(_c.read(autofocusLiveProvider).focusedThisSession, isTrue);
      expect(find.byKey(key), findsNothing, reason: 'nothing is waiting any more');
    });

    testWidgets('the button is not offered on the main camera', (tester) async {
      await _pump(tester, const SimpleSettingsPopover(), oag: true);
      expect(find.byKey(key), findsNothing);
    });

    testWidgets('a guide scope has nothing to wait for, so no button', (tester) async {
      await _pump(tester, const SimpleSettingsPopover());
      _c.read(previewCameraProvider.notifier).set(PreviewCamera.guide);
      await tester.pump();
      expect(find.byKey(key), findsNothing);
    });
  });

  testWidgets('a running guide loop keeps its Stop while the guider status is briefly gone', (tester) async {
    await _pump(tester, const SimpleShutter(), guider: 'none', guideRunning: true);
    _c.read(previewCameraProvider.notifier).set(PreviewCamera.guide);
    await tester.pump();
    await tester.tap(find.byType(SimpleShutter));
    expect(_g.calls, ['stop']);
  });

  testWidgets('while guiding the guide camera is greyed out and says why', (tester) async {
    await _pump(tester, const SimpleSettingsPopover(), runtime: 'guiding');
    await tester.tap(_row('guide'), warnIfMissed: false);
    await tester.pump();
    expect(_c.read(previewCameraProvider), PreviewCamera.main);
    expect(find.descendant(of: _row('guide'), matching: find.textContaining('Stop the guider first')), findsOneWidget);
    await tester.pump(kSimpleResultLife); // the tap's pill fades
  });

  testWidgets('while the main camera is taking a frame the Guide row is locked and says why', (tester) async {
    await _pump(tester, const SimpleSettingsPopover(), capture: CapturePhase.exposing);
    await tester.tap(_row('guide'), warnIfMissed: false);
    await tester.pump();
    expect(_c.read(previewCameraProvider), PreviewCamera.main);
    expect(find.descendant(of: _row('guide'), matching: find.textContaining('main camera is busy')), findsOneWidget);
    expect(find.descendant(of: _row('guide'), matching: find.text('ZWO ASI120MM Mini')), findsNothing);
    await tester.pump(kSimpleResultLife); // the tap's pill fades
  });

  testWidgets('tapping a locked Guide row says why in a pill', (tester) async {
    await _pump(tester, const SimpleSettingsPopover(), capture: CapturePhase.exposing);
    await tester.tap(_row('guide'), warnIfMissed: false);
    await tester.pump();
    expect(_c.read(simpleMessagesProvider).map((m) => m.title), contains(contains('main camera is busy')));
    await tester.pump(kSimpleResultLife);
  });

  testWidgets('with a guide loop running the Main row is locked, and says why on the row and in a pill', (tester) async {
    await _pump(tester, const SimpleSettingsPopover(), guideRunning: true);
    const why = "Stop the guide camera's frames first.";
    expect(find.descendant(of: _row('main'), matching: find.text(why)), findsOneWidget);
    await tester.tap(_row('main'), warnIfMissed: false);
    await tester.pump();
    expect(_c.read(simpleMessagesProvider).map((m) => m.title), contains(why));
    await tester.pump(kSimpleResultLife);
  });

  testWidgets("on Guide the Info tiles describe the guide camera's frames, with no main-camera facts", (tester) async {
    await _pump(tester, const SimpleInfoPanel(), guideRunning: true, guideSample: true);
    _c.read(previewCameraProvider.notifier).set(PreviewCamera.guide);
    await tester.pump();
    await tester.pump();
    for (final text in ['2.34', '12', '2 s', 'Gain']) {
      expect(find.text(text), findsOneWidget, reason: text);
    }
    expect(find.text('Clipped'), findsNothing);
    expect(find.text('Gain · offset'), findsNothing);
  });

  testWidgets('a guide loop found running stays reachable while the main camera is taking a frame', (tester) async {
    await _pump(tester, const SimpleSettingsPopover(), guideRunning: true, capture: CapturePhase.exposing);
    await tester.tap(_row('guide'));
    await tester.pump();
    expect(_c.read(previewCameraProvider), PreviewCamera.guide, reason: 'its stop is on that row');
  });

  testWidgets('picking Guide swaps to its own exposure and gain, with no Filter or Loop / Single', (tester) async {
    await _pump(tester, const SimpleSettingsPopover());
    await tester.tap(_row('guide'));
    await tester.pump();
    expect(_c.read(previewCameraProvider), PreviewCamera.guide);
    expect(find.byType(SimpleGuideGainSlider), findsOneWidget);
    expect(find.byType(FilterDropdown), findsNothing);
    expect(find.text('Loop'), findsNothing);
    expect(find.text('Single'), findsNothing);
    expect(find.text('2 s'), findsOneWidget, reason: 'the guide exposure, 2 s to start');
  });

  testWidgets("with Guide picked the shutter starts the guide camera's frames", (tester) async {
    await _pump(tester, const SimpleShutter());
    _c.read(previewCameraProvider.notifier).set(PreviewCamera.guide);
    await tester.pump();
    await tester.pump(); // the guider's status loads once the pick first asks for it
    await tester.tap(find.byType(SimpleShutter));
    await tester.pump();
    expect(_g.calls, ['start 2.0']);
  });

  testWidgets('a Guide pick falls back to Main once guiding takes the camera', (tester) async {
    await _pump(tester, const SimpleShutter(), runtime: 'guiding');
    _c.read(previewCameraProvider.notifier).set(PreviewCamera.guide);
    await tester.pump();
    await tester.pump(); // the guider's status loads once the pick first asks for it
    expect(find.byType(SimpleGuideLiveShutter), findsNothing, reason: 'the shutter is the main camera again');
  });

  testWidgets('a guide loop found running: the Guide row can still be picked, to reach its stop', (tester) async {
    await _pump(tester, const SimpleSettingsPopover(), guideRunning: true);
    await tester.tap(_row('guide'));
    await tester.pump();
    expect(_c.read(previewCameraProvider), PreviewCamera.guide);
    await tester.tap(_row('main'), warnIfMissed: false);
    await tester.pump();
    expect(_c.read(previewCameraProvider), PreviewCamera.guide, reason: 'leaving would strand it with no stop');
    await tester.pump(kSimpleResultLife); // the tap's pill fades
  });

  testWidgets('Filter shows only with a filter wheel connected', (tester) async {
    await _pump(tester, const SimpleSettingsPopover(), wheel: 'disconnected');
    expect(find.byType(FilterDropdown), findsNothing);
    await _pump(tester, const SimpleSettingsPopover());
    expect(find.byType(FilterDropdown), findsOneWidget);
  });

  testWidgets('Filter picks in place: no dropdown route', (tester) async {
    await _pump(tester, const SimpleSettingsPopover());
    expect(find.byType(DropdownButtonFormField<String>), findsNothing);
    await tester.tap(find.byKey(const Key('simple-filter')));
    await tester.pumpAndSettle();
    expect(tester.state<NavigatorState>(find.byType(Navigator)).canPop(), isFalse);
    expect(find.byType(ListWheelScrollView), findsOneWidget, reason: 'the choices are on a wheel in place');
  });
}
