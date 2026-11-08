import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/camera_status.dart';
import 'package:openastroara/models/filter_wheel_status.dart';
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
  Future<GuiderStatus?> build() async => GuiderStatus.fromJson(
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

class _GuideFocus extends GuideFocusNotifier {
  final calls = <String>[];
  @override
  GuideFocusLive build() => GuideFocusLive.idle;
  @override
  Future<void> start({required double exposureSec, int? binning}) async => calls.add('start $exposureSec');
  @override
  Future<void> stop() async => calls.add('stop');
}

late ProviderContainer _c;
late _GuideFocus _g;

Future<void> _pump(WidgetTester tester, Widget child,
    {String guider = 'connected', String runtime = 'stopped', String wheel = 'connected'}) async {
  _g = _GuideFocus();
  _c = ProviderContainer(overrides: [
    cameraStatusProvider.overrideWith(_Camera.new),
    guiderStatusProvider.overrideWith(() => _Guider(guider, runtime)),
    filterWheelProvider.overrideWith(() => _Wheel(wheel)),
    guideFocusProvider.overrideWith(() => _g),
    autofocusLiveProvider.overrideWith(_Live.new),
    deviceDriverProvider.overrideWith(
        (ref, type) async => type == 'guidecamera' ? const DeviceDriver(name: 'ZWO ASI120MM Mini') : null),
  ]);
  addTearDown(_c.dispose);
  // Keep the autoDispose driver alive, as the panel's listeners do on a running app.
  _c.listen(deviceDriverProvider('guidecamera'), (_, _) {});
  await tester.pumpWidget(UncontrolledProviderScope(
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

  testWidgets('while guiding the guide camera is greyed out and says why', (tester) async {
    await _pump(tester, const SimpleSettingsPopover(), runtime: 'guiding');
    await tester.tap(_row('guide'), warnIfMissed: false);
    await tester.pump();
    expect(_c.read(previewCameraProvider), PreviewCamera.main);
    expect(find.descendant(of: _row('guide'), matching: find.textContaining('Stop the guider first')), findsOneWidget);
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

  testWidgets('Filter shows only with a filter wheel connected', (tester) async {
    await _pump(tester, const SimpleSettingsPopover(), wheel: 'disconnected');
    expect(find.byType(FilterDropdown), findsNothing);
    await _pump(tester, const SimpleSettingsPopover());
    expect(find.byType(FilterDropdown), findsOneWidget);
  });
}
