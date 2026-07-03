import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/imaging/exposure_state.dart';
import 'package:openastroara/state/settings/filter_wheel_labels_state.dart';
import 'package:openastroara/widgets/imaging/exposure_controls_panel.dart';

Future<ProviderContainer> _pump(WidgetTester tester,
    {List<String>? labels}) async {
  final container = ProviderContainer(overrides: [
    if (labels != null)
      filterWheelLabelsProvider.overrideWith(_FixedLabels.new)
  ]);
  if (labels != null) _FixedLabels.labels = labels;
  addTearDown(container.dispose);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: container,
    child: const MaterialApp(home: Scaffold(body: ExposureControlsPanel())),
  ));
  return container;
}

class _FixedLabels extends FilterWheelLabelsNotifier {
  static List<String> labels = const [];
  @override
  FilterWheelLabels build() => FilterWheelLabels(labels: labels);
}

void main() {
  testWidgets('PR #71: the filter picker exists and drives filterSlot',
      (tester) async {
    final container =
        await _pump(tester, labels: ['L', 'Ha', 'OIII', '', '']);
    expect(find.text('Filter'), findsOneWidget);

    await tester.tap(find.text('L').last);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Ha').last);
    await tester.pumpAndSettle();

    expect(container.read(exposureControllerProvider).filterSlot, 'Ha',
        reason: 'the picker mutates the exposure params sent as filter_name');
    // Empty slots never appear as choices.
    expect(find.text(''), findsNothing);
  });

  testWidgets('r1: duplicate slot labels dedupe instead of crashing the picker',
      (tester) async {
    // Two slots labelled 'Ha' would trip DropdownButtonFormField's
    // exactly-one-item-per-value assertion without the keep-first dedupe.
    final container = await _pump(tester, labels: ['L', 'Ha', 'Ha', 'OIII']);
    expect(tester.takeException(), isNull);
    await tester.tap(find.text('L').last);
    await tester.pumpAndSettle();
    expect(find.text('Ha'), findsOneWidget, reason: 'the duplicate collapses to one choice');
    await tester.tap(find.text('Ha').last);
    await tester.pumpAndSettle();
    expect(container.read(exposureControllerProvider).filterSlot, 'Ha');
  });

  testWidgets('a stored filter not among the labels stays selectable',
      (tester) async {
    final container = await _pump(tester, labels: ['Ha', 'OIII']);
    // The default filterSlot 'L' isn't in the labels — it must still render
    // as the current selection rather than being silently dropped.
    expect(container.read(exposureControllerProvider).filterSlot, 'L');
    expect(find.text('L'), findsOneWidget);
  });
}
