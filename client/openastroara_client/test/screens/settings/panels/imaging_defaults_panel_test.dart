import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/settings/panels/imaging_defaults_panel.dart';
import 'package:openastroara/services/planning_prefs_service.dart';
import 'package:openastroara/models/sky_model_calibration.dart';
import 'package:openastroara/state/planning/planning_prefs_state.dart';
import 'package:openastroara/state/planning/sky_model_calibration_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/widgets/settings/editable_field.dart';

/// #1140 — the Planning row writes the noise-tolerance knob (client-local).
class _MemPrefs extends PlanningPrefsService {
  PlanningPrefs stored = const PlanningPrefs();
  @override
  Future<PlanningPrefs> load() async => stored;
  @override
  Future<void> save(PlanningPrefs prefs) async => stored = prefs;
}

class _Seeded extends SkyModelCalibrationNotifier {
  _Seeded(this._v);
  final SkyModelCalibration? _v;
  @override
  Future<SkyModelCalibration?> build() async => _v;
}

void main() {
  testWidgets(
    'typing a tolerance and submitting updates and persists the knob',
    (tester) async {
      tester.view.physicalSize = const Size(1600, 1600);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      final prior = FlutterError.onError;
      FlutterError.onError = (d) {
        if (!d.exceptionAsString().contains('overflowed')) prior?.call(d);
      };
      addTearDown(() => FlutterError.onError = prior);
      final prefs = _MemPrefs();
      final container = ProviderContainer(
        overrides: [
          activeServerProvider.overrideWithValue(null),
          planningPrefsServiceProvider.overrideWithValue(prefs),
        ],
      );
      addTearDown(container.dispose);
      await tester.pumpWidget(
        UncontrolledProviderScope(
          container: container,
          child: MaterialApp(
            builder: (context, child) => MediaQuery(
              data: MediaQuery.of(context)
                  .copyWith(textScaler: const TextScaler.linear(0.5)),
              child: child!,
            ),
            home: const Scaffold(body: ImagingDefaultsPanel()),
          ),
        ),
      );
      await tester.pumpAndSettle();

      final row = find.widgetWithText(
        EditableNumberRow,
        'Optimal-sub noise tolerance (%)',
      );
      await tester.scrollUntilVisible(row, 200, scrollable: find.byType(Scrollable).first);
      expect(row, findsOneWidget);
      final field = find.descendant(of: row, matching: find.byType(TextField));
      await tester.enterText(field, '2.5');
      await tester.testTextInput.receiveAction(TextInputAction.done);
      await tester.pumpAndSettle();

      expect(container.read(planningPrefsProvider).noiseTolerancePct, 2.5);
      expect(
        prefs.stored.noiseTolerancePct,
        2.5,
        reason: 'persisted on this device',
      );

      // Out of range clamps; garbage is ignored.
      await tester.enterText(field, '99');
      await tester.testTextInput.receiveAction(TextInputAction.done);
      await tester.pumpAndSettle();
      expect(
        container.read(planningPrefsProvider).noiseTolerancePct,
        PlanningPrefs.maxNoiseTolerancePct,
      );
    },
  );

  testWidgets('#1140 P2 the sky-model section lists each filter kind\'s standing; offline it says to connect', (tester) async {
    tester.view.physicalSize = const Size(1600, 2400);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    final record = SkyModelCalibration(
      fittedUtc: DateTime.utc(2026, 10, 13, 6),
      framesConsidered: 42,
      filters: const [
        SkyModelFilterCalibration(kind: 'l', bin: 1, k: 0.82, scatterPct: 6.5, frameCount: 30, passed: true),
        SkyModelFilterCalibration(kind: 'ha', bin: 1, frameCount: 4, passed: false, note: 'All 4 frames share one exposure length (300 s) and the library has no bias frame to pin the pedestal; vary the exposure or shoot a bias.'),
      ],
    );
    // Offline: no server → the section asks to connect and the button is disabled.
    await tester.pumpWidget(ProviderScope(
      overrides: [
        activeServerProvider.overrideWithValue(null),
        planningPrefsServiceProvider.overrideWithValue(_MemPrefs()),
        skyModelCalibrationProvider.overrideWith(() => _Seeded(record)),
      ],
      child: const MaterialApp(home: Scaffold(body: ImagingDefaultsPanel())),
    ));
    await tester.pumpAndSettle();
    expect(find.text('Sky model calibration'), findsOneWidget);
    expect(find.textContaining('Connect to the rig'), findsOneWidget);
    final button = tester.widget<OutlinedButton>(find.widgetWithText(OutlinedButton, 'Recalibrate from recent frames'));
    expect(button.onPressed, isNull);

    // The record's rows (rendered on their own: the panel's hydration needs a live server).
    await tester.pumpWidget(const SizedBox());
    await tester.pumpWidget(MaterialApp(home: Scaffold(body: ListView(children: [
      for (final f in record.filters) Text(f.standing),
    ]))));
    expect(find.text('k 0.82 · scatter 7 % · 30 frames · applies'), findsOneWidget);
    expect(find.textContaining('All 4 frames share one exposure length'), findsOneWidget);
  });
}
