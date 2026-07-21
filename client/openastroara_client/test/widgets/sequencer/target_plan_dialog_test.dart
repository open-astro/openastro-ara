import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/settings/filter_set_state.dart';
import 'package:openastroara/widgets/sequencer/target_plan_dialog.dart';

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
    }) async {
      TargetPlanChoice? result;
      final container = ProviderContainer();
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
        expect(find.text('Guide with PHD2'), findsOneWidget);
        await t.tap(find.text('Cancel'));
      });
    });

    testWidgets('SHO pick returns a 3-step plan with guiding', (tester) async {
      final choice = await open(tester, filters: shoSet, interact: (t) async {
        await t.tap(find.text('Narrowband · SHO'));
        await t.pump();
        await t.tap(find.text('Create run'));
      });
      expect(choice, isNotNull);
      expect(choice!.guide, isTrue); // PHD2 dither default is on
      expect(choice.filterPlan!.map((s) => s.filterName), ['Ha', 'OIII', 'SII']);
      for (final step in choice.filterPlan!) {
        expect(step.exposureSeconds, greaterThan(0));
        expect(step.frameCount, greaterThanOrEqualTo(12));
      }
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
