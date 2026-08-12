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
  bool failInFlight = false;
  // What the wheel reports AFTER a move lands (default: the named _wheelAt
  // slots). Set to an unnamed-slot list to simulate a driver that never names
  // a position.
  List<FilterSlot>? slotsOverride;
  @override
  Future<FilterWheelStatus?> build() async => null;
  @override
  Future<bool> changeFilter(int position) async {
    changeCalls.add(position); // the driver was asked
    if (failMoves) throw StateError('driver rejected the move');
    if (failInFlight) {
      // Accepted (returns true) but the wheel never reaches the target — it
      // stays put, as if the motor stalled mid-move.
      return true;
    }
    park(FilterWheelStatus(
      deviceId: 'fw',
      name: 'FILTERWHEEL',
      connectionState: EquipmentConnectionState.connected,
      runtimeState: 'idle',
      currentSlot: position,
      slots: slotsOverride ??
          const [
            FilterSlot(position: 0, name: 'L', focusOffset: 0),
            FilterSlot(position: 1, name: 'Ha', focusOffset: 0),
            FilterSlot(position: 2, name: 'OIII', focusOffset: 0),
          ],
    ));
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

  testWidgets('shows a Changing indicator while the wheel is moving',
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
    wheel.park(_wheelAt(0)); // parked on L
    await tester.pump();
    expect(find.text('Changing…'), findsNothing);

    // Wheel starts turning — the picker shows progress instead of looking
    // stuck on the stale value.
    wheel.park(FilterWheelStatus(
      deviceId: 'fw',
      name: 'FILTERWHEEL',
      connectionState: EquipmentConnectionState.connected,
      runtimeState: 'moving',
      currentSlot: null,
      slots: const [
        FilterSlot(position: 0, name: 'L', focusOffset: 0),
        FilterSlot(position: 1, name: 'Ha', focusOffset: 0),
        FilterSlot(position: 2, name: 'OIII', focusOffset: 0),
      ],
    ));
    await tester.pump();
    expect(find.text('Changing…'), findsOneWidget);

    // Wheel lands on Ha — the indicator clears and the value shows.
    wheel.park(_wheelAt(1));
    await tester.pumpAndSettle();
    expect(find.text('Changing…'), findsNothing);
    expect(container.read(exposureControllerProvider).filterSlot, 'Ha');
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
    // The controlled button must now DISPLAY Ha (it follows the state).
    expect(
      find.descendant(
        of: find.byType(DropdownButton<String>),
        matching: find.text('Ha'),
      ),
      findsOneWidget,
      reason: 'the picker shows the wheel\'s new filter after a successful move',
    );
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

  testWidgets('a move accepted but failing in flight leaves no stale tag',
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
    wheel.failInFlight = true; // 202 accepted, but the motor never lands
    await tester.pump();

    await tester.tap(find.text('L').last);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Ha').last);
    await tester.pumpAndSettle();

    // No optimistic tag: the picker stays truthful to the wheel (L) even
    // though the move was ACCEPTED — the capture is never tagged with a
    // filter that isn't actually in the light path.
    expect(container.read(exposureControllerProvider).filterSlot, 'L');
    expect(wheel.changeCalls, [1], reason: 'the move was commanded');
    expect(
      find.descendant(
        of: find.byType(DropdownButton<String>),
        matching: find.text('L'),
      ),
      findsOneWidget,
      reason: 'the picker still displays the wheel\'s actual filter',
    );
  });

  testWidgets('a locally-labelled filter maps to its physical slot by position',
      (tester) async {
    final container = ProviderContainer(overrides: [
      filterWheelLabelsProvider.overrideWith(_FixedLabels.new),
      filterWheelProvider.overrideWith(_FakeWheelNotifier.new),
    ]);
    // Local labels use 'Ha'; the driver reports that same physical slot as
    // 'Hα' — the two lists have diverged.
    _FixedLabels.labels = ['L', 'Ha', 'OIII', '', ''];
    addTearDown(container.dispose);

    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: ExposureControlsPanel())),
    ));
    await tester.pumpAndSettle();

    final wheel = container.read(filterWheelProvider.notifier)
        as _FakeWheelNotifier;
    wheel.park(FilterWheelStatus(
      deviceId: 'fw',
      name: 'FILTERWHEEL',
      connectionState: EquipmentConnectionState.connected,
      runtimeState: 'idle',
      currentSlot: 0,
      slots: const [
        FilterSlot(position: 0, name: 'L', focusOffset: 0),
        FilterSlot(position: 1, name: 'Hα', focusOffset: 0),
        FilterSlot(position: 2, name: 'OIII', focusOffset: 0),
      ],
    ));
    await tester.pump();

    await tester.tap(find.text('L').last);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Ha').last);
    await tester.pumpAndSettle();

    // 'Ha' has no driver-name match, but profile slot 2 → wheel position 1:
    // the wheel must still move there.
    expect(wheel.changeCalls, [1],
        reason: 'the local label resolves to its physical slot by position');
  });

  testWidgets('an unresolvable filter name on a connected wheel says so',
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
    // A one-slot wheel: 'Ha' maps to no physical slot at all.
    wheel.park(FilterWheelStatus(
      deviceId: 'fw',
      name: 'FILTERWHEEL',
      connectionState: EquipmentConnectionState.connected,
      runtimeState: 'idle',
      currentSlot: 0,
      slots: const [FilterSlot(position: 0, name: 'L', focusOffset: 0)],
    ));
    await tester.pump();

    await tester.tap(find.text('L').last);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Ha').last);
    await tester.pumpAndSettle();

    // No silent lie: no tag, no move, and a snackbar says the name isn't on
    // the wheel.
    expect(container.read(exposureControllerProvider).filterSlot, 'L');
    expect(wheel.changeCalls, isEmpty);
    expect(
        find.textContaining("isn't a slot on the connected wheel"),
        findsOneWidget);
  });

  testWidgets('a pick landing on an unnamed device slot tags the local label',
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
    // The driver never names slot 1 — only local label 'Ha' covers it.
    wheel.slotsOverride = const [
      FilterSlot(position: 0, name: 'L', focusOffset: 0),
      FilterSlot(position: 1, name: '', focusOffset: 0),
      FilterSlot(position: 2, name: 'OIII', focusOffset: 0),
    ];
    wheel.park(FilterWheelStatus(
      deviceId: 'fw',
      name: 'FILTERWHEEL',
      connectionState: EquipmentConnectionState.connected,
      runtimeState: 'idle',
      currentSlot: 0,
      slots: wheel.slotsOverride!,
    ));
    await tester.pump();

    await tester.tap(find.text('L').last);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Ha').last);
    await tester.pumpAndSettle();

    // 'Ha' resolves by position to unnamed slot 1; the move lands, and the
    // picker tags the local label (the follow-logic can't — the slot is
    // unnamed), so a capture after the move isn't tagged with the previous
    // filter.
    expect(container.read(exposureControllerProvider).filterSlot, 'Ha');
    expect(wheel.changeCalls, [1]);
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
    // The controlled button re-syncs to state: it must still DISPLAY L, not
    // the just-tapped name (DropdownButtonFormField would have lied here).
    expect(
      find.descendant(
        of: find.byType(DropdownButton<String>),
        matching: find.text('L'),
      ),
      findsOneWidget,
      reason: 'the picker shows the wheel\'s actual filter after a failed move',
    );
  });
}
