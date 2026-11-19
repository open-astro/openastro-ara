import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/camera_status.dart';
import 'package:openastroara/models/focuser_status.dart';
import 'package:openastroara/models/switch_device.dart' show CoolingFan;
import 'package:openastroara/screens/simple/simple_device_windows.dart';
import 'package:openastroara/services/equipment_driver_api.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/equipment/camera_state.dart';
import 'package:openastroara/state/equipment/focuser_state.dart';
import 'package:openastroara/state/focus/focus_presets.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/autofocus_settings_state.dart';
import 'package:openastroara/state/settings/cooling_fan_state.dart';
import 'package:openastroara/state/settings/imaging_defaults_state.dart';
import 'package:openastroara/state/settings/loaded_section.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';

import '../../support/fake_section_rig.dart';

// ADR 0015, slice 3 of #1807: the Simple Mode device windows save a profile
// section they never loaded (setter, then persistToServer). Each write must
// load the rig's copy first, refuse (no PUT, a pill) when that load fails, and
// otherwise apply the edit to the rig's copy. Assertions are on the rig's side
// (FakeSectionRig.puts) and the pill the person sees, so they hold whatever
// shape the section notifiers take.

// ── The rig's copies: the field each write sets, and others it does not (a
// client default for any of them would show in the PUT). ──────────────────

final _imagingCopy = ProfileApi.imagingDefaultsToJson(
  const ImagingDefaults(
    defaultExposure: Duration(seconds: 120),
    defaultGain: 200,
    defaultOffset: 30,
    coolerTargetC: -20,
  ),
);

/// Autofocus during plans on (the default triggers), so Off is a change.
const _autofocus = AutofocusSettings(
  steps: 9,
  stepSize: 80,
  exposureSeconds: 7,
  afFilter: 'Ha',
);
final _autofocusCopy = ProfileApi.autofocusSettingsToJson(_autofocus);

// ── Fake devices: each window shows its control, nothing reaches a rig. ────

class _Camera extends CameraStatusNotifier {
  @override
  Future<CameraStatus?> build() async => CameraStatus.fromJson({
    'device_id': 'c',
    'name': 'ASI2600MM',
    'state': 'connected',
    'capabilities': {'has_cooler': true, 'can_set_temperature': true},
    'runtime': {
      'ccd_temperature': -9.8,
      'cooler_power_pct': 34,
      'cooler_on': true,
      'cooler_setpoint_c': -10,
    },
  });

  @override
  Future<bool> setCooler(bool enabled, {double? targetTemperatureC}) async =>
      true;
}

class _FixedFan extends CoolingFanNotifier {
  @override
  LoadedSection<CoolingFan> build() =>
      LoadedSection(value: CoolingFan.none, source: SectionSource.rig);
}

class _Focuser extends FocuserNotifier {
  @override
  Future<FocuserStatus?> build() async => FocuserStatus.fromJson({
    'device_id': 'f',
    'name': 'ZWO EAF',
    'state': 'connected',
    'capabilities': {
      'min_position': 0,
      'max_position': 60000,
      'absolute_focuser': true,
    },
    'runtime': {'state': 'idle', 'position': 12000, 'temperature': 8.4},
  });
}

// ── The write sites.. ──────────────────────────────────────────────────

typedef _Site = ({
  String window,
  String write,
  String route,
  Map<String, dynamic> copy,
  Widget body,
  List<Override> overrides,
  Future<void> Function(WidgetTester) act,
  Map<String, dynamic> saved,
  String pill,
});

void _slide(WidgetTester tester, String key, double v) =>
    tester.widget<Slider>(find.byKey(Key(key))).onChangeEnd!(v);

final _sites = <_Site>[
  (
    window: 'camera window',
    // −25: neither the client default (−10) nor the rig's (−20), so the
    // window's "unchanged" early return can't hide the write.
    write: 'a cooler target',
    route: 'imaging-defaults',
    copy: _imagingCopy,
    body: const SimpleCameraWindow(),
    overrides: [
      cameraStatusProvider.overrideWith(_Camera.new),
      coolingFanProvider.overrideWith(_FixedFan.new),
    ],
    act: (t) async => _slide(t, 'simple-camera-target', -25),
    saved: {..._imagingCopy, 'cooler_target_c': -25.0},
    pill: "Couldn't save the target",
  ),
  (
    window: 'focuser window',
    write: 'autofocus during plans Off',
    route: 'autofocus',
    copy: _autofocusCopy,
    body: const SimpleFocuserWindow(),
    overrides: [focuserProvider.overrideWith(_Focuser.new)],
    act: (t) => t.tap(find.byKey(const Key('simple-focuser-plans-off'))),
    saved: ProfileApi.autofocusSettingsToJson(
      withAutofocusDuringPlansOff(_autofocus),
    ),
    pill: "Couldn't save that",
  ),
];

Future<ProviderContainer> _pump(
  WidgetTester tester,
  _Site site,
  FakeSectionRig rig,
) async {
  final c = ProviderContainer(
    overrides: [
      ...site.overrides,
      deviceDriverProvider.overrideWith((ref, type) async => null),
      profileApiProvider.overrideWithValue(rig.api()),
      simpleModeActiveProvider.overrideWithValue(true),
    ],
  );
  addTearDown(c.dispose);
  tester.view.physicalSize = const Size(800, 1600);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: c,
      child: MaterialApp(
        home: Scaffold(
          body: Align(
            alignment: Alignment.topCenter,
            child: SizedBox(width: 360, child: site.body),
          ),
        ),
      ),
    ),
  );
  await tester.pump();
  return c;
}

/// Real time for the rest of a write to finish (the camera's cooler follows
/// a saved target) before the test ends and its container goes.
Future<void> _drain(WidgetTester tester) async {
  await tester.runAsync(
    () => Future<void>.delayed(const Duration(milliseconds: 100)),
  );
  await tester.pump();
}

void main() {
  for (final site in _sites) {
    testWidgets('${site.window}: ${site.write} with the section not loaded and '
        "the rig unreachable writes nothing and says it couldn't save", (
      tester,
    ) async {
      final rig = FakeSectionRig({site.route: site.copy})
        ..failGet.add(site.route);
      final c = await _pump(tester, site, rig);
      await tester.runAsync(() => site.act(tester));
      await tester.runAsync(
        () => settle(
          () =>
              rig.puts.isNotEmpty || c.read(simpleMessagesProvider).isNotEmpty,
        ),
      );
      await _drain(tester);
      expect(rig.puts, isEmpty);
      expect(c.read(simpleMessagesProvider).map((m) => m.title), [site.pill]);
    });

    testWidgets("${site.window}: ${site.write} keeps the rig's other values "
        'and the edit', (tester) async {
      // Not loaded at the tap: any load the window fires on its own fails.
      final rig = FakeSectionRig({site.route: site.copy})
        ..failGet.add(site.route);
      await _pump(tester, site, rig);
      await _drain(tester);
      rig.failGet.remove(site.route);
      await tester.runAsync(() => site.act(tester));
      await tester.runAsync(() => settle(() => rig.puts.isNotEmpty));
      await _drain(tester);
      expect(rig.puts, hasLength(1));
      expect(rig.puts.single.body, site.saved);
    });
  }
}
