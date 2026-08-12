import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/filter_wheel_status.dart';
import 'package:openastroara/state/equipment/filter_wheel_state.dart';
import 'package:openastroara/state/imaging/exposure_state.dart';
import 'package:openastroara/state/settings/filter_wheel_labels_state.dart';
import 'package:openastroara/widgets/imaging/exposure_controls_panel.dart';

class _FakeWheelNotifier extends FilterWheelNotifier {
  final List<int> changeCalls = [];
  bool failMoves = false;
  @override
  Future<FilterWheelStatus?> build() async => null;
  @override
  Future<bool> changeFilter(int position) async {
    changeCalls.add(position); // the driver was asked, then it failed
    if (failMoves) throw StateError('driver rejected the move');
    return true;
  }

  void park(FilterWheelStatus status) => state = AsyncData(status);
}

FilterWheelStatus _wheelAt(int position, {bool connected = true}) =>
    FilterWheelStatus(
      deviceId: 'fw',
      name: 'FILTERWHEEL',
      connectionState: connected
          ? EquipmentConnectionState.connected
          : EquipmentConnectionState.disconnected,
      runtimeState: 'idle',
      currentSlot: position,
      slots: const [
        FilterSlot(position: 0, name: 'L', focusOffset: 0),
        FilterSlot(position: 1, name: 'Ha', focusOffset: 0),
        FilterSlot(position: 2, name: 'OIII', focusOffset: 0),
      ],
    );

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

  testWidgets('picking a filter also moves the connected wheel to that slot',
      (tester) async {
    final container = ProviderContainer(overrides: [
      filterWheelLabelsProvider.overrideWith(_FixedLabels.new),
      filterWheelProvider.overrideWith(_FakeWheelNotifier.new),
    ]);
    _FixedLabels.labels = ['L', 'Ha', 'OIII', '', ''];
    addTearDown(container.dispose);

    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: ExposureControlsPanel())),
    ));
    await tester.pumpAndSettle();

    final wheel = container.read(filterWheelProvider.notifier)
        as _FakeWheelNotifier;
    wheel.park(_wheelAt(0)); // wheel parked on L
    await tester.pump();

    await tester.tap(find.text('L').last);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Ha').last);
    await tester.pumpAndSettle();

    expect(container.read(exposureControllerProvider).filterSlot, 'Ha');
    expect(wheel.changeCalls, [1],
        reason: 'Ha is slot 1 — picking it must command the wheel there');
  });

  testWidgets('picking the already-current slot does not re-move the wheel',
      (tester) async {
    final container = ProviderContainer(overrides: [
      filterWheelLabelsProvider.overrideWith(_FixedLabels.new),
      filterWheelProvider.overrideWith(_FakeWheelNotifier.new),
    ]);
    _FixedLabels.labels = ['L', 'Ha', 'OIII', '', ''];
    addTearDown(container.dispose);

    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: ExposureControlsPanel())),
    ));
    await tester.pumpAndSettle();

    final wheel = container.read(filterWheelProvider.notifier)
        as _FakeWheelNotifier;
    wheel.park(_wheelAt(1)); // wheel already on Ha
    await tester.pump();

    // The dropdown mirrors the wheel (value = Ha); pick Ha again.
    await tester.tap(find.text('Ha').last); // open the dropdown
    await tester.pumpAndSettle();
    await tester.tap(find.text('Ha').last); // select Ha from the menu
    await tester.pumpAndSettle();

    expect(container.read(exposureControllerProvider).filterSlot, 'Ha');
    expect(wheel.changeCalls, isEmpty,
        reason: 'already on Ha — nothing to move');
  });

  testWidgets('picking a filter with no connected wheel only tags the capture',
      (tester) async {
    final container = ProviderContainer(overrides: [
      filterWheelLabelsProvider.overrideWith(_FixedLabels.new),
      filterWheelProvider.overrideWith(_FakeWheelNotifier.new),
    ]);
    _FixedLabels.labels = ['L', 'Ha', 'OIII', '', ''];
    addTearDown(container.dispose);

    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: ExposureControlsPanel())),
    ));
    await tester.pumpAndSettle();

    final wheel = container.read(filterWheelProvider.notifier)
        as _FakeWheelNotifier;
    wheel.park(_wheelAt(0, connected: false)); // wheel disconnected
    await tester.pump();

    await tester.tap(find.text('L').last);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Ha').last);
    await tester.pumpAndSettle();

    expect(container.read(exposureControllerProvider).filterSlot, 'Ha');
    expect(wheel.changeCalls, isEmpty,
        reason: 'no wheel to move — the picker only tags the capture');
  });

  testWidgets('a failed wheel move does not tag the capture and shows an error',
      (tester) async {
    final container = ProviderContainer(overrides: [
      filterWheelLabelsProvider.overrideWith(_FixedLabels.new),
      filterWheelProvider.overrideWith(_FakeWheelNotifier.new),
    ]);
    _FixedLabels.labels = ['L', 'Ha', 'OIII', '', ''];
    addTearDown(container.dispose);

    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: ExposureControlsPanel())),
    ));
    await tester.pumpAndSettle();

    final wheel = container.read(filterWheelProvider.notifier)
        as _FakeWheelNotifier;
    wheel.park(_wheelAt(0)); // wheel on L
    wheel.failMoves = true; // driver rejects the move
    await tester.pump();

    await tester.tap(find.text('L').last);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Ha').last);
    await tester.pumpAndSettle();

    // The failed move must NOT tag the capture with a filter that isn't in
    // the light path — the picker stays truthful to the wheel (L).
    expect(container.read(exposureControllerProvider).filterSlot, 'L');
    expect(wheel.changeCalls, [1], reason: 'the move was attempted');
    expect(find.text('driver rejected the move'), findsOneWidget,
        reason: 'the failure surfaces as a snackbar (StateError passes through '
            'friendlyError verbatim), not a silent drop');
  });
}
