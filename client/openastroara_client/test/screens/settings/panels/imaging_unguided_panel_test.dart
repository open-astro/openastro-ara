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
  _SeededMount(this._v);
  final MountStatus? _v;
  @override
  Future<MountStatus?> build() async => _v;
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
        mountProvider.overrideWith(() => _SeededMount(mountKnown ? _mount(specs) : null)),
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
        specs: const MountSpecSheet(model: 'EQM-35 Pro', wormPeriodS: 638, periodicErrorPpArcsec: 25, hasPec: true));
    expect(find.textContaining('EQM-35 Pro: Periodic error 25″ p-p over 638 s · manufacturer · PEC available'), findsOneWidget);

    await tester.pumpWidget(const SizedBox());
    await _pump(tester, connected: true, mountKnown: true);
    expect(find.textContaining('No spec sheet for this mount'), findsOneWidget);
  });
}
