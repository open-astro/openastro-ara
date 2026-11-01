import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/imaging_run_body.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/screens/simple/simple_run_panel.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/settings/filter_set_state.dart';
import 'package:openastroara/state/sky_atlas/planetarium_selection_state.dart';

const _m31 = PlanetariumSelection(
    name: 'M31', kind: 'Galaxy', raHours: 0.73, decDeg: 41.4, raJ2000Hours: 0.71, decJ2000Deg: 41.27, altDeg: 62.4, maxAltDeg: 74.2);

const _lrgb = [
  PlanningFilter(name: 'L', kind: FilterKind.l),
  PlanningFilter(name: 'R', kind: FilterKind.r),
  PlanningFilter(name: 'G', kind: FilterKind.g),
  PlanningFilter(name: 'B', kind: FilterKind.b),
  PlanningFilter(name: 'Ha', kind: FilterKind.ha),
];

/// The daemon as the Run mode sees it: create, start and the lifecycle calls
/// are recorded; the run state is whatever the test sets.
class _Seq implements SequenceClient {
  final calls = <String>[];
  SequenceRunStateInfo? runState;
  @override
  Future<String> create(String name, Map<String, dynamic> body,
      {String? description, String? idempotencyKey}) async {
    calls.add('create $name');
    return 'seq-1';
  }

  @override
  Future<String> start(String id) async => _op('start $id');
  @override
  Future<String> stop(String id) async => _op('stop $id');
  @override
  Future<String> pause(String id) async => _op('pause $id');
  @override
  Future<String> resume(String id, {bool recenter = true, bool refocus = false}) async => _op('resume $id');
  String _op(String c) {
    calls.add(c);
    return 'op';
  }

  @override
  Future<SequenceRunStateInfo?> getRunState(String id) async => runState;
  @override
  Future<SequencePage> list({int limit = 50, String? cursor}) async => const SequencePage(items: []);
  @override
  void close() {}
  @override
  void noSuchMethod(Invocation invocation) => throw UnimplementedError('${invocation.memberName}');
}

SequenceRunStateInfo _imaging(int frames, {SequenceRunState state = SequenceRunState.running}) =>
    SequenceRunStateInfo(sequenceId: 'seq-1', state: state, framesCaptured: frames);

class _Sel extends PlanetariumSelectionNotifier {
  _Sel(this.value);
  final PlanetariumSelection? value;
  @override
  PlanetariumSelection? build() => value;
}

class _Filters extends FilterSetNotifier {
  @override
  FilterSetSettings build() => const FilterSetSettings(filters: _lrgb);
}

Future<ProviderContainer> _pump(WidgetTester tester, _Seq seq, Widget child,
    {PlanetariumSelection? sel = _m31}) async {
  final container = ProviderContainer(overrides: [
    sequenceApiProvider.overrideWith((ref) => seq),
    planetariumSelectionProvider.overrideWith(() => _Sel(sel)),
    filterSetProvider.overrideWith(_Filters.new),
  ]);
  addTearDown(container.dispose);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: container,
    child: MaterialApp(home: Scaffold(body: Center(child: child))),
  ));
  await tester.pumpAndSettle();
  return container;
}

InkResponse _shutter(WidgetTester t) => t.widget<InkResponse>(find.byKey(const Key('simple-shutter')));

void main() {
  test('the first plan: LRGB from the set, one unnamed row without a choice', () {
    final lrgb = simpleRunDefaultPlan(_lrgb, 300);
    expect(lrgb.map((s) => s.filterName), ['L', 'R', 'G', 'B']);
    expect(lrgb.map((s) => s.frameCount), [15, 15, 15, 15]);
    expect(lrgb.first.exposureSeconds, 300);

    final narrow = simpleRunDefaultPlan(const [
      PlanningFilter(name: 'Ha', kind: FilterKind.ha),
      PlanningFilter(name: 'OIII', kind: FilterKind.oiii),
    ], 180);
    expect(narrow.map((s) => s.filterName), ['Ha', 'OIII']);
    expect(narrow.first.frameCount, 30);

    final osc = simpleRunDefaultPlan(const [PlanningFilter(name: 'OSC', kind: FilterKind.osc)], 60);
    expect(osc, hasLength(1));
    expect(osc.single.filterName, '');
    expect(osc.single.frameCount, 60);
  });

  test('progress fills the plan rows in order', () {
    const steps = [
      FilterPlanStep(filterName: 'L', exposureSeconds: 300, frameCount: 40),
      FilterPlanStep(filterName: 'R', exposureSeconds: 180, frameCount: 20),
    ];
    expect(simpleRunProgress(steps, 0), [0, 0]);
    expect(simpleRunProgress(steps, 23), [23, 0]);
    expect(simpleRunProgress(steps, 45), [40, 5]);
    expect(simpleRunProgress(steps, 99), [40, 20]);
  });

  test('the chip: Plan, frames of the total, Paused, Done', () {
    const run = SimpleRun(sequenceId: 'seq-1', steps: [
      FilterPlanStep(filterName: 'L', exposureSeconds: 300, frameCount: 40),
      FilterPlanStep(filterName: 'R', exposureSeconds: 180, frameCount: 60),
    ]);
    expect(simpleRunSummary(const SimpleRun(), null), 'Plan');
    expect(simpleRunSummary(run, _imaging(23)), '23 / 100');
    expect(simpleRunSummary(run, _imaging(23, state: SequenceRunState.paused)), 'Paused');
    expect(simpleRunSummary(run, _imaging(100, state: SequenceRunState.completed)), 'Done');
    // A run started elsewhere: just its frames.
    expect(simpleRunSummary(const SimpleRun(), _imaging(7)), '7 frames');
  });

  test('durations read in hours and minutes', () {
    expect(simpleRunDuration(45 * 60), '45 min');
    expect(simpleRunDuration(6 * 3600 + 20 * 60), '6 h 20 min');
  });

  group('the shutter starts imaging', () {
    testWidgets('off with no target picked', (tester) async {
      await _pump(tester, _Seq(), SimpleRunShutter(onPanel: (_) {}), sel: null);
      expect(_shutter(tester).onTap, isNull);
    });

    testWidgets('creates the run from the plan, starts it and closes the sheet', (tester) async {
      final seq = _Seq();
      bool? panel;
      final c = await _pump(tester, seq, SimpleRunShutter(onPanel: (open) => panel = open));
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pumpAndSettle();
      expect(seq.calls, ['create M31', 'start seq-1']);
      expect(panel, isFalse);
      expect(c.read(simpleRunProvider).sequenceId, 'seq-1');
      expect(c.read(simpleRunProvider).steps.map((s) => s.filterName), ['L', 'R', 'G', 'B']);
    });
  });

  group('the red square asks before stopping', () {
    Future<(ProviderContainer, List<bool>)> imaging(WidgetTester tester, _Seq seq) async {
      final panels = <bool>[];
      seq.runState = _imaging(23);
      final c = await _pump(
          tester,
          seq,
          Row(mainAxisSize: MainAxisSize.min, children: [
            const SimpleRunPanel(),
            SimpleRunShutter(onPanel: panels.add),
          ]));
      c.read(selectedSequenceIdProvider.notifier).select('seq-1');
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pumpAndSettle();
      return (c, panels);
    }

    testWidgets('the tap opens the question; Keep going closes it', (tester) async {
      final seq = _Seq();
      final (_, panels) = await imaging(tester, seq);
      expect(panels, [true]);
      expect(find.text('Stop the run?'), findsOneWidget);
      await tester.tap(find.byKey(const Key('simple-run-keep-going')));
      await tester.pumpAndSettle();
      expect(find.text('Stop the run?'), findsNothing);
      expect(seq.calls, isEmpty);
    });

    testWidgets('Stop now stops the run', (tester) async {
      final seq = _Seq();
      await imaging(tester, seq);
      await tester.tap(find.byKey(const Key('simple-run-stop-now')));
      await tester.pumpAndSettle();
      expect(seq.calls, ['stop seq-1']);
    });

    testWidgets('Stop after this frame stops once the next frame is filed', (tester) async {
      final seq = _Seq();
      final (c, _) = await imaging(tester, seq);
      await tester.tap(find.byKey(const Key('simple-run-stop-after')));
      await tester.pumpAndSettle();
      expect(find.text('Stopping after this frame…'), findsOneWidget);
      await c.read(sequenceRunStateProvider.notifier).refresh();
      await tester.pumpAndSettle();
      expect(seq.calls, isEmpty, reason: 'frame 23 is still the last one');
      seq.runState = _imaging(24);
      await c.read(sequenceRunStateProvider.notifier).refresh();
      await tester.pumpAndSettle();
      expect(seq.calls, ['stop seq-1']);
    });
  });

  group('the sheet', () {
    testWidgets('with no target it sends you to Target', (tester) async {
      var opened = false;
      await _pump(tester, _Seq(), SimpleRunSheet(onOpenTarget: () => opened = true, onLibrary: () {}), sel: null);
      expect(find.text('No target yet'), findsOneWidget);
      await tester.tap(find.byKey(const Key('simple-run-open-target')));
      expect(opened, isTrue);
    });

    testWidgets('lists the plan for the target and says the shutter starts it', (tester) async {
      await _pump(tester, _Seq(), SimpleRunSheet(onOpenTarget: () {}, onLibrary: () {}));
      expect(find.text("Tonight's plan · M31"), findsOneWidget);
      expect(find.byKey(const Key('simple-run-exposure-3')), findsOneWidget);
      expect(find.text('60 frames'), findsOneWidget);
      expect(find.text('Tap the shutter to start imaging'), findsOneWidget);
      expect(find.text('Start imaging'), findsNothing);
    });

    testWidgets('a removed row leaves the plan; the last row stays', (tester) async {
      final c = await _pump(tester, _Seq(), SimpleRunSheet(onOpenTarget: () {}, onLibrary: () {}));
      final plan = c.read(simpleRunPlanProvider.notifier);
      for (var i = 0; i < 5; i++) {
        plan.remove(0);
      }
      expect(c.read(simpleRunPlanProvider).map((s) => s.filterName), ['B']);
    });
  });
}
