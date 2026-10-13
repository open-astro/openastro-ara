import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/settings/panels/imaging_defaults_panel.dart';
import 'package:openastroara/services/planning_prefs_service.dart';
import 'package:openastroara/state/planning/planning_prefs_state.dart';
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
}
