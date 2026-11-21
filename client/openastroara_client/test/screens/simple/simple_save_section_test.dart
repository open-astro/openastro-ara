import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/camera_status.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/focuser_status.dart';
import 'package:openastroara/models/switch_device.dart' show CoolingFan;
import 'package:openastroara/screens/simple/simple_device_windows.dart';
import 'package:openastroara/screens/simple/simple_focus_panel.dart';
import 'package:openastroara/services/equipment_driver_api.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/equipment/camera_state.dart';
import 'package:openastroara/state/equipment/focuser_state.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/autofocus_settings_state.dart';
import 'package:openastroara/state/settings/cooling_fan_state.dart';
import 'package:openastroara/state/settings/loaded_section.dart';
import 'package:openastroara/state/settings/migrated_sections.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';

import '../../support/fake_section_rig.dart';

// ADR 0015, slice 3 of #1807: what Simple Mode's shared write helper
// (simpleSaveSection) says and does around its load, through the windows that
// use it. The per-site refusal and keeps-the-edit cases are in
// simple_device_windows_load_first_test.dart and its neighbours.

const _route = 'autofocus';

final _rigCopy = ProfileApi.autofocusSettingsToJson(
  const AutofocusSettings(exposureSeconds: 7, steps: 9, stepSize: 80),
);

class _Focuser extends FocuserNotifier {
  @override
  Future<FocuserStatus?> build() async => FocuserStatus(
    deviceId: 'f',
    name: 'Focuser',
    connectionState: EquipmentConnectionState.connected,
    capabilities: FocuserCapabilities(
      minPosition: 0,
      maxPosition: 1100,
      stepSizeUm: 1,
      canTempComp: false,
      absoluteFocuser: true,
    ),
    runtimeState: 'idle',
    position: 1000,
    temperature: null,
    tempCompEnabled: false,
  );
}

class _Camera extends CameraStatusNotifier {
  @override
  Future<CameraStatus?> build() async => CameraStatus.fromJson({
    'device_id': 'c',
    'name': 'ASI2600MM',
    'state': 'connected',
    'capabilities': {'has_cooler': true, 'can_set_temperature': true},
    'runtime': {'cooler_on': true, 'cooler_setpoint_c': -10},
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

Future<ProviderContainer> _pump(
  WidgetTester tester,
  Widget body, {
  FakeSectionRig? rig,
  List<Override> gear = const [],
  Locale? locale,
  double? width = 360,
}) async {
  final c = ProviderContainer(
    retry: (_, _) => null,
    overrides: [
      focuserProvider.overrideWith(_Focuser.new),
      deviceDriverProvider.overrideWith((ref, type) async => null),
      profileApiProvider.overrideWithValue(rig?.api()),
      simpleModeActiveProvider.overrideWithValue(true),
      ...gear,
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
        locale: locale,
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          body: Align(
            alignment: Alignment.topCenter,
            child: SizedBox(width: width, child: body),
          ),
        ),
      ),
    ),
  );
  await flushLoads(tester);
  return c;
}

List<(String, String?)> _pills(ProviderContainer c) => [
  for (final m in c.read(simpleMessagesProvider)) (m.title, m.detail),
];

Future<void> _tap(WidgetTester tester, String key) async {
  await tester.runAsync(() async {
    await tester.tap(find.byKey(Key(key)));
  });
  await tester.pump();
}

void main() {
  group('with no rig connected the write says so and keeps nothing', () {
    testWidgets('focuser window: autofocus during plans Off', (tester) async {
      final c = await _pump(tester, const SimpleFocuserWindow());
      await _tap(tester, 'simple-focuser-plans-off');
      expect(_pills(c), [("Couldn't save that", 'Rig not connected')]);
    });

    testWidgets('focus panel: an autofocus exposure pick', (tester) async {
      final c = await _pump(tester, const SimpleFocusPanel(), width: null);
      await _tap(tester, 'simple-focus-af-exposure-10');
      expect(_pills(c), [("Couldn't save that", 'Rig not connected')]);
    });

    testWidgets('camera window: a cooler target', (tester) async {
      final c = await _pump(
        tester,
        const SimpleCameraWindow(),
        gear: [
          cameraStatusProvider.overrideWith(_Camera.new),
          coolingFanProvider.overrideWith(_FixedFan.new),
        ],
      );
      tester
          .widget<Slider>(find.byKey(const Key('simple-camera-target')))
          .onChangeEnd!(-25);
      await flushLoads(tester);
      expect(
        _pills(c),
        contains(("Couldn't save the target", 'Rig not connected')),
      );
    });
  });

  testWidgets('a save the rig fails (not a 412) puts the control back on the '
      "rig's value", (tester) async {
    final rig = FakeSectionRig({_route: _rigCopy});
    final c = await _pump(tester, const SimpleFocusPanel(), width: null, rig: rig);
    expect(c.read(autofocusSettingsProvider).value!.exposureSeconds, 7);
    rig.failPut.add(_route);
    await _tap(tester, 'simple-focus-af-exposure-10');
    await tester.runAsync(() => settle(() => _pills(c).isNotEmpty));
    await tester.pump();
    expect(rig.puts, isEmpty);
    expect(_pills(c).single.$1, "Couldn't save that");
    expect(c.read(autofocusSettingsProvider).value!.exposureSeconds, 7);
  });

  testWidgets('a rig or profile change during the write\'s own load refuses '
      'with the pill and writes nothing', (tester) async {
    final rig = FakeSectionRig({_route: _rigCopy});
    final c = await _pump(tester, const SimpleFocusPanel(), width: null, rig: rig);
    final hold = rig.holdGet[_route] = Completer<void>();
    await _tap(tester, 'simple-focus-af-exposure-10');
    // The tap's GET is out once the rig has taken the hold.
    await tester.runAsync(() => settle(() => rig.holdGet.isEmpty));
    // The reset lands while the tap's GET is out; the section then fails to
    // load again, so it holds no copy when the tap's load returns.
    rig.failGet.add(_route);
    resetMigratedSections(c.read);
    await tester.runAsync(() async {
      await Future<void>.delayed(const Duration(milliseconds: 50));
      hold.complete();
    });
    await tester.runAsync(() => settle(() => _pills(c).isNotEmpty));
    await flushLoads(tester);
    expect(rig.puts, isEmpty);
    expect(_pills(c), [
      (
        "Couldn't save that",
        'Settings not loaded from the rig; nothing was saved',
      ),
    ]);
  });

  testWidgets('a second exposure pick while the first is still saving lands '
      'too, with no conflict', (tester) async {
    final rig = FakeSectionRig({_route: _rigCopy});
    final c = await _pump(tester, const SimpleFocusPanel(), width: null, rig: rig);
    final hold = rig.holdPut[_route] = Completer<void>();
    await _tap(tester, 'simple-focus-af-exposure-10');
    // The first pick's PUT is out once the rig has taken the hold.
    await tester.runAsync(() => settle(() => rig.holdPut.isEmpty));
    await _tap(tester, 'simple-focus-af-exposure-15');
    await tester.runAsync(() async {
      await Future<void>.delayed(const Duration(milliseconds: 50));
      hold.complete();
    });
    await tester.runAsync(
      () => settle(() => rig.puts.length == 2 || _pills(c).isNotEmpty),
    );
    await flushLoads(tester);
    expect(_pills(c), isEmpty);
    expect([
      for (final p in rig.puts) (p.body as Map)['exposure_seconds'],
    ], [10, 15]);
    expect(c.read(autofocusSettingsProvider).conflict, isFalse);
    expect(c.read(autofocusSettingsProvider).value!.exposureSeconds, 15);
  });

  testWidgets('the helper\'s two messages read in Portuguese', (tester) async {
    final rig = FakeSectionRig({_route: _rigCopy})..failGet.add(_route);
    final c = await _pump(
      tester,
      const SimpleFocusPanel(),
      width: null,
      rig: rig,
      locale: const Locale('pt', 'BR'),
    );
    await _tap(tester, 'simple-focus-af-exposure-10');
    await tester.runAsync(() => settle(() => _pills(c).isNotEmpty));
    await tester.pump();
    expect(
      _pills(c).single.$2,
      'Configurações não carregadas do setup; nada foi salvo',
    );
  });
}
