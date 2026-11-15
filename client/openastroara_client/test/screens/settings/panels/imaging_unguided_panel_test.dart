import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/mount_spec_sheet.dart';
import 'package:openastroara/models/mount_status.dart';
import 'package:openastroara/state/equipment/mount_state.dart';
import 'package:openastroara/models/tracking_calibration.dart';
import 'package:openastroara/screens/settings/panels/imaging_unguided_panel.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/tracking/tracking_calibration_state.dart';
import 'package:openastroara/state/settings/unguided_settings_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/widgets/settings/editable_field.dart';

/// #1264 Imaging → Unguided: the stored ceiling, its ladder and Recalibrate.
class _Fixed extends TrackingCalibrationNotifier {
  _Fixed(this._v);
  final TrackingCalibration? _v;
  int recalibrated = 0;
  @override
  Future<TrackingCalibration?> build() async => _v;
  @override
  Future<void> recalibrate() async {
    recalibrated++;
    state = const AsyncData(null);
  }
}

class _SeededMount extends MountNotifier {
  _SeededMount(this._v, {this.neverLoads = false});
  final MountStatus? _v;
  final bool neverLoads;
  @override
  Future<MountStatus?> build() => neverLoads ? Completer<MountStatus?>().future : Future.value(_v);
}

MountStatus _mount(MountSpecSheet? specs) => MountStatus(
      deviceId: 'mnt', name: 'EQM-35', connectionState: EquipmentConnectionState.connected,
      capabilities: MountCapabilities(
          canSlew: true, canSync: true, canPark: true, canUnpark: true,
          canSetTracking: true, canFindHome: false, specs: specs),
      runtimeState: 'tracking', rightAscensionHours: 5, declinationDegrees: 20,
      tracking: true, parked: false, atHome: false,
    );

Future<_Fixed> _pump(
  WidgetTester tester, {
  required bool connected,
  TrackingCalibration? cal,
  MountSpecSheet? specs,
  bool mountKnown = false,
  bool mountLoading = false,
}) async {
  tester.view.physicalSize = const Size(1600, 1600);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.reset);
  final notifier = _Fixed(cal);
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        activeServerProvider.overrideWithValue(
          connected ? const AraServer(hostname: 'rig', port: 5555) : null,
        ),
        trackingCalibrationProvider.overrideWith(() => notifier),
        mountProvider.overrideWith(() => _SeededMount(mountKnown ? _mount(specs) : null, neverLoads: mountLoading)),
      ],
      child: MaterialApp(
        builder: (context, child) => MediaQuery(
          data: MediaQuery.of(context)
              .copyWith(textScaler: const TextScaler.linear(0.5)),
          child: child!,
        ),
        home: const Scaffold(body: ImagingUnguidedPanel()),
      ),
    ),
  );
  await tester.pumpAndSettle();
  return notifier;
}

void main() {
  testWidgets('offline: explains it needs the rig; Recalibrate disabled', (
    tester,
  ) async {
    await _pump(tester, connected: false);
    expect(find.textContaining('Connect to your rig'), findsOneWidget);
    expect(
      tester.widget<OutlinedButton>(find.byType(OutlinedButton)).onPressed,
      isNull,
    );
  });

  testWidgets(
    'connected with nothing measured says the first run measures it',
    (tester) async {
      await _pump(tester, connected: true);
      expect(find.textContaining('Not measured yet'), findsOneWidget);
      expect(
        tester.widget<OutlinedButton>(find.byType(OutlinedButton)).onPressed,
        isNull,
      );
    },
  );

  testWidgets(
    'a record shows the ceiling, the ladder and clears on Recalibrate',
    (tester) async {
      final n = await _pump(
        tester,
        connected: true,
        cal: TrackingCalibration(
          ceilingSeconds: 90,
          measuredUtc: DateTime.utc(2026, 10, 11, 22),
          priorSeconds: 60,
          source: 'measured',
          rungs: const [
            TrackingProbeRung(
              exposureSeconds: 60,
              eccentricity: 0.4,
              fwhmPx: 2.2,
              stars: 130,
              usable: true,
              passed: true,
            ),
            TrackingProbeRung(
              exposureSeconds: 120,
              eccentricity: 0.7,
              fwhmPx: 3.9,
              stars: 120,
              usable: true,
              passed: false,
            ),
          ],
        ),
      );
      expect(
        find.textContaining('90 s · measured on 2026-10-1'),
        findsOneWidget,
      );
      expect(
        find.text('ecc 0.70 · FWHM 3.9 px · 120 stars · trailed'),
        findsOneWidget,
      );
      expect(
        find.text('ecc 0.40 · FWHM 2.2 px · 130 stars · pass'),
        findsOneWidget,
      );

      await tester.tap(find.text('Recalibrate on the next run'));
      await tester.pumpAndSettle();
      expect(n.recalibrated, 1);
      expect(find.textContaining('Not measured yet'), findsOneWidget);
      expect(
        find.textContaining('the next unguided run measures it again'),
        findsOneWidget,
      );
    },
  );

  testWidgets('#1264 the spec-sheet row names the sheet, or says there is none', (tester) async {
    await _pump(tester, connected: true, mountKnown: true,
        specs: const MountSpecSheet(model: 'EQM-35 Pro', source: 'manufacturer', wormPeriodS: 638, periodicErrorPpArcsec: 25, hasPec: true));
    expect(find.textContaining('EQM-35 Pro: Periodic error 25″ p-p over 638 s · manufacturer · PEC available'), findsOneWidget);

    await tester.pumpWidget(const SizedBox());
    await _pump(tester, connected: true, mountKnown: true);
    expect(find.textContaining('No spec sheet for this mount'), findsOneWidget);
  });

  testWidgets('#1264 the knobs edit the Unguided settings (guard switch, ladder text)', (tester) async {
    await _pump(tester, connected: true, mountKnown: true);
    final element = tester.element(find.byType(ImagingUnguidedPanel));
    final container = ProviderScope.containerOf(element);

    await tester.tap(find.descendant(
        of: find.widgetWithText(SettingsSwitchRow, 'Unguided exposure guard'), matching: find.byType(Switch)));
    await tester.pumpAndSettle();
    expect(container.read(unguidedSettingsProvider).guardEnabled, isFalse);
    expect(find.textContaining("Off: the probe's ceiling holds"), findsOneWidget);

    final ladder = find.widgetWithText(EditableTextRow, 'Probe ladder (s, shortest first)');
    await tester.scrollUntilVisible(ladder, 200, scrollable: find.byType(Scrollable).first);
    await tester.enterText(find.descendant(of: ladder, matching: find.byType(TextField)), '5, 20, 80');
    await tester.testTextInput.receiveAction(TextInputAction.done);
    await tester.pumpAndSettle();
    expect(container.read(unguidedSettingsProvider).probeRungsSeconds, [5, 20, 80]);

    await tester.enterText(find.descendant(of: ladder, matching: find.byType(TextField)), '80, 5');
    await tester.testTextInput.receiveAction(TextInputAction.done);
    await tester.pumpAndSettle();
    expect(container.read(unguidedSettingsProvider).probeRungsSeconds, [5, 20, 80], reason: 'a descending ladder is rejected');
  });

  testWidgets('#1264 the spec-sheet row tells a missing mount and a loading one apart from "no sheet"', (tester) async {
    await _pump(tester, connected: true);
    expect(find.text('Mount not connected.'), findsOneWidget);
    expect(find.textContaining('No spec sheet'), findsNothing);

    await tester.pumpWidget(const SizedBox());
    await _pump(tester, connected: true, mountLoading: true);
    expect(find.text('Reading the mount…'), findsOneWidget);
  });

  test('#1264 basisGapFor names the missing figure, and only then', () {
    const noOptics = OpticsSettings();
    const optics = OpticsSettings(focalLengthMm: 447, pixelSizeUm: 3.76);
    expect(basisGapFor('max', optics: noOptics, seeingArcsec: 0), isNull);
    expect(basisGapFor('pixel', optics: noOptics, seeingArcsec: 3), contains('optics'));
    expect(basisGapFor('pixel', optics: optics, seeingArcsec: 0), isNull);
    expect(basisGapFor('seeing', optics: optics, seeingArcsec: 0), contains('typical seeing'));
    expect(basisGapFor('seeing', optics: noOptics, seeingArcsec: 2.5), isNull);
  });

  testWidgets('#1264 the basis hint appears only while its figure is missing', (tester) async {
    await _pump(tester, connected: true, mountKnown: true);
    final element = tester.element(find.byType(ImagingUnguidedPanel));
    final container = ProviderScope.containerOf(element);
    expect(find.textContaining('gives no prior'), findsNothing, reason: 'max needs nothing');

    container.read(unguidedSettingsProvider.notifier).setPriorToleranceBasis('pixel');
    await tester.pumpAndSettle();
    expect(find.textContaining('Needs the optics'), findsOneWidget, reason: 'default optics are unset');

    container.read(opticsSettingsProvider.notifier).setFocalLengthMm(447);
    container.read(opticsSettingsProvider.notifier).setPixelSizeUm(3.76);
    await tester.pumpAndSettle();
    expect(find.textContaining('Needs the optics'), findsNothing);

    container.read(unguidedSettingsProvider.notifier).setPriorToleranceBasis('seeing');
    await tester.pumpAndSettle();
    expect(find.textContaining('typical seeing'), findsNothing, reason: 'the default site seeing (2.5") is set');
  });
}
