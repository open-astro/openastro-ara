import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/camera_status.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/filter_wheel_status.dart';
import 'package:openastroara/services/equipment_device_api.dart';
import 'package:openastroara/state/equipment/camera_state.dart';
import 'package:openastroara/state/equipment/filter_wheel_state.dart';
import 'package:openastroara/state/imaging/exposure_state.dart';
import 'package:openastroara/state/settings/filter_wheel_labels_state.dart';
import 'package:openastroara/widgets/imaging/exposure_controls_panel.dart';

class _FakeWheelNotifier extends FilterWheelNotifier {
  final List<int> changeCalls = [];
  int refreshCalls = 0;
  bool failMoves = false;
  bool failInFlight = false;
  // How long a successful move takes before the wheel reports the landing
  // (so a test can observe the busy state mid-flight).
  Duration moveDelay = Duration.zero;
  // What the wheel reports AFTER a move lands (default: the named _wheelAt
  // slots). Set to an unnamed-slot list to simulate a driver that never names
  // a position.
  List<FilterSlot>? slotsOverride;
  @override
  Future<FilterWheelStatus?> build() async => null;
  @override
  Future<void> refresh([EquipmentDeviceClient<FilterWheelStatus>? client]) async {
    refreshCalls++;
    // No-op by design: the fake manages state via park(). The base refresh
    // would set state to AsyncData(null) — no API client exists in tests —
    // which the exposure controller reads as "wheel gone".
  }

  @override
  Future<bool> changeFilter(int position) async {
    changeCalls.add(position); // the driver was asked
    if (failMoves) throw StateError('driver rejected the move');
    if (failInFlight) {
      // Accepted (returns true) but the wheel never reaches the target: it
      // starts turning, then stalls and reports back where it was.
      final current = state.asData?.value;
      final oldSlot = current?.currentSlot;
      final slots = current?.slots ?? const [];
      park(FilterWheelStatus(
        deviceId: 'fw',
        name: 'FILTERWHEEL',
        connectionState: EquipmentConnectionState.connected,
        runtimeState: 'moving',
        currentSlot: null,
        slots: slots,
      ));
      await Future<void>.delayed(Duration.zero);
      park(FilterWheelStatus(
        deviceId: 'fw',
        name: 'FILTERWHEEL',
        connectionState: EquipmentConnectionState.connected,
        runtimeState: 'idle',
        currentSlot: oldSlot,
        slots: slots,
      ));
      return true;
    }
    if (moveDelay > Duration.zero) {
      await Future<void>.delayed(moveDelay);
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
    {List<String>? labels,
    CameraStatus? cameraStatus,
    int initialBin = 1,
    bool awaitCamera = true}) async {
  final container = ProviderContainer(overrides: [
    if (labels != null)
      filterWheelLabelsProvider.overrideWith(_FixedLabels.new),
    cameraStatusProvider.overrideWith(() => _FixedCameraStatus(cameraStatus)),
    exposureControllerProvider.overrideWith(() => _FixedExposure(initialBin)),
  ]);
  if (labels != null) _FixedLabels.labels = labels;
  // Resolve the camera status before the first frame so the dropdown's first
  // build already sees the camera's real bin range. Pass awaitCamera: false
  // to model the real mount order (provider still loading at first build,
  // capabilities arriving on a later frame).
  if (awaitCamera) await container.read(cameraStatusProvider.future);
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

class _FixedCameraStatus extends CameraStatusNotifier {
  final CameraStatus? status;
  _FixedCameraStatus(this.status);
  @override
  Future<CameraStatus?> build() async => status;
}

class _FixedExposure extends ExposureController {
  final int initialBin;
  _FixedExposure(this.initialBin);
  @override
  ExposureParams build() => ExposureParams(bin: initialBin);
}

/// A connected camera whose (symmetric) bin range tops out at [max].
CameraStatus _cameraWithMaxBin(int max) => CameraStatus.fromJson({
      'state': 'connected',
      'capabilities': {
        'sensor_width': 100,
        'sensor_height': 100,
        'min_bin_x': 1,
        'max_bin_x': max,
        'min_bin_y': 1,
        'max_bin_y': max,
      },
      'runtime': {'state': 'idle'},
    });

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

  testWidgets('a pick during the confirm flash does not drop the busy state',
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
    wheel.park(_wheelAt(0)); // on L
    wheel.moveDelay = const Duration(milliseconds: 400);
    await tester.pump();

    DropdownButtonFormField<String> field() => tester
        .widget<DropdownButtonFormField<String>>(
            find.byType(DropdownButtonFormField<String>));

    // First pick: long move (400 ms) so the confirm flash (600 ms) is still
    // playing after the menu cycles through close + reopen.
    await tester.tap(find.text('L').last);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Ha').last);
    await tester.pump(const Duration(milliseconds: 50)); // busy mid-flight
    await tester.pump(const Duration(milliseconds: 400)); // move lands -> flash
    expect(field().onChanged, isNotNull,
        reason: 'enabled during the confirm flash');

    // Pick OIII DURING the flash — the busy state must survive the
    // interrupted flash's whenComplete.
    await tester.tap(find.text('Ha').last); // open (value is now Ha)
    await tester.pump(const Duration(milliseconds: 300)); // menu fully open
    await tester.tap(find.text('OIII').last);
    await tester.pump(const Duration(milliseconds: 50));
    expect(wheel.changeCalls, [1, 2], reason: 'the second pick was commanded');
    expect(field().onChanged, isNull,
        reason: 'the interrupted confirm flash must not drop the new busy state');
    expect(field().decoration.enabledBorder, isNotNull,
        reason: 'busy border stays while the new move is in flight');

    // Let everything settle and finish the pending timers.
    await tester.pump(const Duration(milliseconds: 500)); // second move lands
    await tester.pumpAndSettle();
    expect(container.read(exposureControllerProvider).filterSlot, 'OIII');
    await tester.pump(const Duration(seconds: 10));
    await tester.pumpAndSettle();
  });

  testWidgets('the reconcile poll re-reads the daemon while a move is pending',
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
    wheel.park(_wheelAt(0));
    wheel.moveDelay = const Duration(seconds: 10); // landing arrives late
    await tester.pump();

    await tester.tap(find.text('L').last);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Ha').last);
    await tester.pump(const Duration(milliseconds: 100));

    final before = wheel.refreshCalls;
    await tester.pump(const Duration(milliseconds: 1600)); // one reconcile tick
    expect(wheel.refreshCalls, greaterThan(before),
        reason: 'the picker re-reads the daemon at the fast cadence while '
            'waiting for the landing');

    // The daemon catches up — the move lands.
    wheel.park(_wheelAt(1));
    await tester.pumpAndSettle();
    expect(container.read(exposureControllerProvider).filterSlot, 'Ha');
    // Let the fake's delayed move complete so no timers outlive the test.
    await tester.pump(const Duration(seconds: 10));
    await tester.pumpAndSettle();
  });

  testWidgets('the first-launch home-to-L shows the same busy state',
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

    DropdownButtonFormField<String> field() => tester
        .widget<DropdownButtonFormField<String>>(
            find.byType(DropdownButtonFormField<String>));

    container.read(exposureControllerProvider.notifier).setHoming(true);
    await tester.pump();
    expect(field().onChanged, isNull, reason: 'disabled while homing to L');
    expect(field().decoration.enabledBorder, isNotNull,
        reason: 'busy border while homing to L');

    container.read(exposureControllerProvider.notifier).setHoming(false);
    await tester.pumpAndSettle();
    expect(field().onChanged, isNotNull, reason: 'enabled once homed');
  });

  testWidgets('the picker disables + pulses while the wheel is moving',
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

    DropdownButtonFormField<String> field() => tester
        .widget<DropdownButtonFormField<String>>(
            find.byType(DropdownButtonFormField<String>));
    expect(field().onChanged, isNotNull, reason: 'enabled while parked');

    // Wheel starts turning — the picker disables and the busy border shows.
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
    expect(field().onChanged, isNull, reason: 'disabled while the wheel turns');
    expect(field().decoration.enabledBorder, isNotNull,
        reason: 'busy border pulse is active');

    // Wheel lands on Ha — re-enabled, value shows.
    wheel.park(_wheelAt(1));
    await tester.pumpAndSettle();
    expect(field().onChanged, isNotNull, reason: 're-enabled after landing');
    expect(field().decoration.enabledBorder, isNull,
        reason: 'busy border cleared after landing');
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
    // Connect at L (first-connect home-to-L is a no-op there), then the wheel
    // is moved to Ha via the panel — the picker follows.
    wheel.park(_wheelAt(0));
    await tester.pump();
    wheel.park(_wheelAt(1)); // wheel now on Ha
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

  testWidgets('the busy pulse re-arms on every move (not just the first)',
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
    wheel.park(_wheelAt(0)); // on L
    wheel.moveDelay = const Duration(milliseconds: 400);
    await tester.pump();

    DropdownButtonFormField<String> field() => tester
        .widget<DropdownButtonFormField<String>>(
            find.byType(DropdownButtonFormField<String>));

    Future<void> pick(String name, String landedName) async {
      await tester.tap(find.text(container
              .read(exposureControllerProvider)
              .filterSlot)
          .last); // open
      await tester.pumpAndSettle();
      await tester.tap(find.text(name).last);
      await tester.pump(const Duration(milliseconds: 50)); // mid-flight
      expect(field().onChanged, isNull,
          reason: 'busy -> disabled while the move is in flight ($name)');
      expect(field().decoration.enabledBorder, isNotNull,
          reason: 'busy border pulse is active ($name)');
      await tester.pumpAndSettle(); // landing + green flash
      expect(field().onChanged, isNotNull,
          reason: 're-enabled after landing ($name)');
      expect(container.read(exposureControllerProvider).filterSlot, landedName);
    }

    // First move — and then a SECOND move: the pulse must re-arm both times
    // (a previous run resumed the controller at its end value, showing no red).
    await pick('Ha', 'Ha');
    await pick('OIII', 'OIII');
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

  testWidgets(
      'Option A: bin renders as a symmetric dropdown from the camera range',
      (tester) async {
    final container = await _pump(tester, cameraStatus: _cameraWithMaxBin(4));
    expect(find.text('Bin'), findsOneWidget);
    // Default bin 1 is shown as 1x1 and the camera's max (4) is the cap.
    expect(find.text('1x1'), findsOneWidget);
    await tester.tap(find.text('1x1').last);
    await tester.pumpAndSettle();
    await tester.tap(find.text('3x3').last);
    await tester.pumpAndSettle();
    expect(container.read(exposureControllerProvider).bin, 3);
    // 5x5 is beyond the camera's 4x4 cap and must not be offered.
    expect(find.text('5x5'), findsNothing);
  });

  testWidgets('Option A: bin dropdown falls back to 1..8 with no camera',
      (tester) async {
    final container = await _pump(tester); // no camera connected
    expect(find.text('1x1'), findsOneWidget);
    await tester.tap(find.text('1x1').last);
    await tester.pumpAndSettle();
    await tester.tap(find.text('8x8').last);
    await tester.pumpAndSettle();
    expect(container.read(exposureControllerProvider).bin, 8);
    // Beyond the fallback cap nothing is offered.
    expect(find.text('9x9'), findsNothing);
  });

  testWidgets(
      'Option A: an out-of-range stored bin clamps to a valid selection '
      'AND the clamp is written back to state', (tester) async {
    // Camera caps at 2x2 but the stored bin is 8 (e.g. a camera swap) — the
    // picker must clamp rather than crash on a value with no matching item.
    final container =
        await _pump(tester, cameraStatus: _cameraWithMaxBin(2), initialBin: 8);
    expect(tester.takeException(), isNull);
    expect(find.text('2x2'), findsOneWidget);
    expect(find.text('8x8'), findsNothing);
    // The correction must reach ExposureParams.bin — a display-only clamp
    // would still submit bin 8 on capture.
    await tester.pump(); // post-frame write-back
    expect(container.read(exposureControllerProvider).bin, 2);
  });

  testWidgets(
      'Option A: capabilities arriving after the first build re-clamp the '
      'selection (no exactly-one-item-per-value crash)', (tester) async {
    // Real mount order: the camera provider is still loading on the first
    // frame (dropdown shows the 1..8 fallback with the stored bin 8), then
    // the capabilities resolve and narrow the range to 2.
    final container = await _pump(tester,
        cameraStatus: _cameraWithMaxBin(2), initialBin: 8, awaitCamera: false);
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);
    expect(find.text('2x2'), findsOneWidget);
    expect(find.text('8x8'), findsNothing);
    expect(container.read(exposureControllerProvider).bin, 2);
  });

  testWidgets(
      'Option A: malformed bin capabilities (min above max) keep the 1..8 '
      'fallback', (tester) async {
    final broken = CameraStatus.fromJson({
      'state': 'connected',
      'capabilities': {
        'sensor_width': 100,
        'sensor_height': 100,
        'min_bin_x': 6,
        'max_bin_x': 2,
        'min_bin_y': 6,
        'max_bin_y': 2,
      },
      'runtime': {'state': 'idle'},
    });
    await _pump(tester, cameraStatus: broken);
    expect(tester.takeException(), isNull);
    expect(find.text('1x1'), findsOneWidget);
    await tester.tap(find.text('1x1').last);
    await tester.pumpAndSettle();
    expect(find.text('8x8'), findsOneWidget); // full fallback range offered
  });
}
