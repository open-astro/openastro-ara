import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/screens/simple/simple_device_windows.dart';
import 'package:openastroara/services/equipment_driver_api.dart';
import 'package:openastroara/services/guider_equipment_api.dart' show GuiderEquipmentClient;
import 'package:openastroara/state/guider/guider_equipment_state.dart' show guiderEquipmentApiProvider;
import 'package:openastroara/services/profile_api.dart' show ProfileApi;
import 'package:openastroara/state/profile_management_state.dart' show profileApiProvider;
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';

class _Guider extends GuiderStatusNotifier {
  _Guider(this.json);
  final Map<String, dynamic> json;
  @override
  Future<GuiderStatus?> build() async => GuiderStatus.fromJson(json);
}

Map<String, dynamic> _wire({String state = 'connected', String runtime = 'guiding'}) => {
      'device_id': 'g',
      'name': 'PHD2',
      'state': state,
      'runtime': {
        'state': runtime,
        'rms_total': 0.9,
        'rms_total_arcsec': 0.62,
        'rms_ra_arcsec': 0.41,
        'rms_dec_arcsec': 0.47,
      },
    };

class _Profile implements ProfileApi {
  @override
  Future<Phd2Settings> putPhd2Settings(Phd2Settings s) async => s;
  @override
  dynamic noSuchMethod(Invocation i) => throw UnimplementedError('${i.memberName}');
}

class _GuiderEquipment implements GuiderEquipmentClient {
  final pushes = <bool>[];
  @override
  Future<void> pushProfile({bool tuningOnly = false}) async => pushes.add(tuningOnly);
  @override
  dynamic noSuchMethod(Invocation i) => throw UnimplementedError('${i.memberName}');
}

Future<ProviderContainer> _pump(WidgetTester tester, Map<String, dynamic> wire,
    {DeviceDriver? camera, bool inSettings = false, _GuiderEquipment? equipment}) async {
  final c = ProviderContainer(overrides: [
    guiderStatusProvider.overrideWith(() => _Guider(wire)),
    deviceDriverProvider.overrideWith((ref, type) async => type == 'guidecamera' ? camera : null),
    if (equipment != null) ...[
      profileApiProvider.overrideWithValue(_Profile()),
      guiderEquipmentApiProvider.overrideWithValue(equipment),
    ],
  ]);
  addTearDown(c.dispose);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: c,
    child: MaterialApp(home: Scaffold(body: SizedBox(width: 360, child: SimpleGuiderWindow(inSettings: inSettings)))),
  ));
  await tester.pump();
  return c;
}

void main() {
  testWidgets('the Guide Camera window: its title, Gain; no State, RMS or dither (user, 2026-11-04)', (tester) async {
    await _pump(tester, _wire());
    expect(find.text('Guide Camera'), findsOneWidget, reason: 'no guide camera known: just Guide Camera');
    for (final gone in ['State', 'RMS', 'Dither', 'Dither by']) {
      expect(find.byKey(Key('simple-window-row-$gone')), findsNothing, reason: '$gone moved to Guide mode');
    }
    expect(find.byKey(const Key('simple-guider-gain')), findsOneWidget);
  });

  testWidgets('RA and Dec aggressiveness are sliders, 0-100 % in 5s, kept in the profile and sent to the guider live',
      (tester) async {
    final eq = _GuiderEquipment();
    final c = await _pump(tester, _wire(), equipment: eq);
    final ra = tester.widget<Slider>(find.byKey(const Key('simple-guide-aggressiveness-ra')));
    expect((ra.min, ra.max, ra.divisions), (0.0, 1.0, 20));
    ra.onChangeEnd!(0.55);
    await tester.pump();
    await tester.pump();
    expect(c.read(phd2SettingsProvider).raAggressiveness, closeTo(0.55, 1e-9));
    expect(eq.pushes, [true], reason: 'tuning only, so guiding goes on');
    tester.widget<Slider>(find.byKey(const Key('simple-guide-aggressiveness-dec'))).onChangeEnd!(0.4);
    await tester.pump();
    await tester.pump();
    expect(c.read(phd2SettingsProvider).decAggressiveness, closeTo(0.4, 1e-9));
    expect(eq.pushes, [true, true]);
  });

  testWidgets('Dec guide mode is picked in the window and sent to the guider live', (tester) async {
    final eq = _GuiderEquipment();
    final c = await _pump(tester, _wire(), equipment: eq);
    for (final m in ['auto', 'north', 'south', 'off']) {
      expect(find.byKey(Key('simple-guide-dec-mode-$m')), findsOneWidget, reason: m);
    }
    await tester.tap(find.byKey(const Key('simple-guide-dec-mode-north')));
    await tester.pump();
    await tester.pump();
    expect(c.read(phd2SettingsProvider).decGuideMode, 'north');
    expect(eq.pushes, [true]);
  });

  testWidgets('not connected says so', (tester) async {
    await _pump(tester, _wire(state: 'disconnected', runtime: 'stopped'));
    expect(find.text('Not connected'), findsOneWidget);
  });

  testWidgets('the title names the guide camera, like Main Camera\'s; Settings shows its versions', (tester) async {
    await _pump(tester, _wire(),
        camera: const DeviceDriver(name: 'ZWO ASI120MM Mini', driverVersion: '6.0.0', sdkVersion: '1.41'), inSettings: true);
    await tester.pump();
    expect(find.text('Guide Camera · ZWO ASI120MM Mini'), findsOneWidget);
    expect(find.text('Versions'), findsOneWidget);
    expect(find.text('1.41'), findsOneWidget);
  });

  testWidgets('the guide camera\'s gain is a slider, 0-100 % in 5s, kept in the profile', (tester) async {
    final c = await _pump(tester, _wire());
    final slider = tester.widget<Slider>(find.byKey(const Key('simple-guider-gain')));
    expect((slider.min, slider.max, slider.divisions, slider.value), (0.0, 100.0, 20, 50.0));
    slider.onChangeEnd!(70);
    await tester.pump();
    expect(c.read(phd2SettingsProvider).guideCameraGain, 70);
    expect(find.descendant(of: find.byKey(const Key('simple-window-row-Gain')), matching: find.text('70 %')), findsOneWidget);
  });
}
