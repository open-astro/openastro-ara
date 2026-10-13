import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sky_model_calibration.dart';
import 'package:openastroara/state/planning/sky_model_calibration_state.dart';
import 'package:openastroara/state/settings/camera_electronics_state.dart';
import 'package:openastroara/state/settings/filter_set_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/widgets/sequencer/target_plan_dialog.dart';

class _SeededOptics extends OpticsSettingsNotifier {
  _SeededOptics(this._v);
  final OpticsSettings _v;
  @override
  OpticsSettings build() => _v;
}

class _SeededElectronics extends CameraElectronicsNotifier {
  _SeededElectronics(this._v);
  final CameraElectronics _v;
  @override
  CameraElectronics build() => _v;
}

class _SeededSite extends SiteSettingsNotifier {
  _SeededSite(this._v);
  final SiteSettings _v;
  @override
  SiteSettings build() => _v;
}

class _SeededSkyModel extends SkyModelCalibrationNotifier {
  _SeededSkyModel(this._v);
  final SkyModelCalibration? _v;
  @override
  Future<SkyModelCalibration?> build() async => _v;
}

void main() {
  group('planOptionsAvailable', () {
    test('mono narrowband or broadband filters offer a choice', () {
      expect(
        planOptionsAvailable(
            const [PlanningFilter(name: 'Ha', kind: FilterKind.ha)]),
        isTrue,
      );
      expect(
        planOptionsAvailable(
            const [PlanningFilter(name: 'L', kind: FilterKind.l)]),
        isTrue,
      );
    });

    test('OSC / dual-band only (or empty) offers none', () {
      expect(planOptionsAvailable(const []), isFalse);
      expect(
        planOptionsAvailable(const [
          PlanningFilter(name: 'OSC', kind: FilterKind.osc),
          PlanningFilter(name: 'L-eXtreme', kind: FilterKind.duo),
        ]),
        isFalse,
      );
    });
  });

  group('showTargetPlanDialog', () {
    Future<TargetPlanChoice?> open(
      WidgetTester tester, {
      required List<PlanningFilter> filters,
      required Future<void> Function(WidgetTester) interact,
      bool guider = true,
      List<Object> overrides = const [],
    }) async {
      TargetPlanChoice? result;
      final container = ProviderContainer(overrides: overrides.cast());
      addTearDown(container.dispose);
      for (final f in filters) {
        container.read(filterSetProvider.notifier).addFilter(f);
      }
      await tester.pumpWidget(UncontrolledProviderScope(
        container: container,
        child: MaterialApp(
          home: Builder(
            builder: (context) => Center(
              child: ElevatedButton(
                onPressed: () async {
                  result = await showTargetPlanDialog(
                    context,
                    targetName: 'Veil',
                    raDeg: 311.0,
                    decDeg: 31.7,
                    remainingDarkHours: 6,
                    guiderAvailable: guider,
                  );
                },
                child: const Text('go'),
              ),
            ),
          ),
        ),
      ));
      await tester.tap(find.text('go'));
      await tester.pumpAndSettle();
      await interact(tester);
      await tester.pumpAndSettle();
      return result;
    }

    const shoSet = [
      PlanningFilter(name: 'Ha', kind: FilterKind.ha),
      PlanningFilter(name: 'OIII', kind: FilterKind.oiii),
      PlanningFilter(name: 'SII', kind: FilterKind.sii),
      PlanningFilter(name: 'L', kind: FilterKind.l),
    ];

    testWidgets('offers SHO, broadband, per-filter and Basic options',
        (tester) async {
      await open(tester, filters: shoSet, interact: (t) async {
        expect(find.text('Narrowband · SHO'), findsOneWidget);
        expect(find.textContaining('Broadband ·'), findsOneWidget);
        expect(find.text('Ha only'), findsOneWidget);
        expect(find.text('Basic'), findsOneWidget);
        expect(find.text('Guide with OpenAstro Guider'), findsOneWidget);
        await t.tap(find.text('Cancel'));
      });
    });

    testWidgets('no guider on the rig: no toggle, no guiding', (tester) async {
      final choice = await open(tester, filters: shoSet, guider: false,
          interact: (t) async {
        expect(find.text('Guide with OpenAstro Guider'), findsNothing);
        expect(find.textContaining('No guider on this rig'), findsOneWidget);
        await t.tap(find.text('Create run'));
      });
      expect(choice, isNotNull);
      expect(choice!.guide, isFalse);
    });

    testWidgets('SHO pick returns a 3-step plan with guiding', (tester) async {
      final choice = await open(tester, filters: shoSet, interact: (t) async {
        await t.tap(find.text('Narrowband · SHO'));
        await t.pump();
        await t.tap(find.text('Create run'));
      });
      expect(choice, isNotNull);
      expect(choice!.guide, isTrue); // the rig has a guider → guide by default
      expect(choice.filterPlan!.map((s) => s.filterName), ['Ha', 'OIII', 'SII']);
      for (final step in choice.filterPlan!) {
        expect(step.exposureSeconds, greaterThan(0));
        expect(step.frameCount, greaterThanOrEqualTo(12));
      }
    });

    testWidgets('#1140 P2 a fitted sky-model k for Hα lengthens the Hα step\'s exposure only', (tester) async {
      // A configured rig; the plan clamps the Glover floor to [30 s, 10 min] and
      // under the saturation ceiling, so pick the sky (Bortle) where the Hα floor
      // itself sets the step — then halving the measured sky rate must move it.
      List<Object> rig(int bortle) => <Object>[
        opticsSettingsProvider.overrideWith(() => _SeededOptics(const OpticsSettings(
            focalLengthMm: 447, reducerFactor: 1.0, sensorWidthPx: 6248, sensorHeightPx: 4176, pixelSizeUm: 3.76, apertureMm: 91))),
        cameraElectronicsProvider.overrideWith(() => _SeededElectronics(const CameraElectronics(
            readNoiseE: 0.7, fullWellE: 50000, electronsPerAdu: 0.8, quantumEfficiencyPeak: 0.8))),
        siteSettingsProvider.overrideWith(() => _SeededSite(SiteSettings(bortleClass: bortle))),
      ];
      Future<Map<String, double>> exposures(int bortle, SkyModelCalibration? record) async {
        final choice = await open(tester, filters: shoSet, overrides: [
          ...rig(bortle),
          skyModelCalibrationProvider.overrideWith(() => _SeededSkyModel(record)),
        ], interact: (t) async {
          await t.tap(find.text('Narrowband · SHO'));
          await t.pump();
          await t.tap(find.text('Create run'));
        });
        await tester.pumpWidget(const SizedBox());
        return {for (final s in choice!.filterPlan!) s.filterName: s.exposureSeconds};
      }
      final halfHa = SkyModelCalibration(
        fittedUtc: DateTime.utc(2026, 10, 13), framesConsidered: 9,
        filters: const [SkyModelFilterCalibration(kind: 'ha', bin: 1, k: 0.5, frameCount: 9, passed: true)]);
      Map<String, double>? spec;
      Map<String, double>? fitted;
      for (var bortle = 4; bortle <= 9 && spec == null; bortle++) {
        final s = await exposures(bortle, null);
        if (s['Ha']! > 30 && s['Ha']! < 600) {
          spec = s;
          fitted = await exposures(bortle, halfHa);
        }
      }
      expect(spec, isNotNull, reason: 'some sky puts the Hα floor inside the plan\'s clamp');
      expect(fitted!['Ha']!, greaterThan(spec!['Ha']!), reason: 'half the measured sky rate moves the Hα floor out');
      expect(fitted['OIII'], spec['OIII'], reason: 'OIII has no fitted k');
    });

    testWidgets('Basic pick returns no plan; cancel returns null',
        (tester) async {
      final choice = await open(tester, filters: shoSet, interact: (t) async {
        await t.ensureVisible(find.text('Basic'));
        await t.pumpAndSettle();
        await t.tap(find.text('Basic'));
        await t.pump();
        await t.tap(find.text('Create run'));
      });
      expect(choice!.filterPlan, isNull);

      final cancelled = await open(tester, filters: shoSet,
          interact: (t) async {
        await t.tap(find.text('Cancel'));
      });
      expect(cancelled, isNull);
    });
  });
}
