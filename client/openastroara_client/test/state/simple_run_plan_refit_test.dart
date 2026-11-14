import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/filter_wheel_status.dart';
import 'package:openastroara/models/sequence/imaging_run_body.dart';
import 'package:openastroara/state/equipment/filter_wheel_state.dart';
import 'package:openastroara/state/settings/filter_set_state.dart';
import 'package:openastroara/state/simple_run_state.dart';

// #1875 slice 1: the Plan sheet refits a count to the dark hours left only
// while nobody has typed it. A count typed into Frames (a typed count since
// #1868) is kept, and a queued target's rows are the rig's plan: no refit
// may change them.

/// The picked target's dark hours left, set by the test.
class _Hours extends Notifier<double?> {
  @override
  double? build() => 2;
  void set(double? h) => state = h;
}

final _hours = NotifierProvider<_Hours, double?>(_Hours.new);

/// No wheel: one row with no filter name, so the counts are easy to read.
class _NoWheel extends FilterWheelNotifier {
  @override
  Future<FilterWheelStatus?> build() async => null;
}

class _NoFilters extends FilterSetNotifier {
  @override
  FilterSetSettings build() => const FilterSetSettings(filters: []);
}

Future<ProviderContainer> _sheet() async {
  final c = ProviderContainer(
    overrides: [
      filterWheelProvider.overrideWith(_NoWheel.new),
      filterSetProvider.overrideWith(_NoFilters.new),
      simpleDarkHoursProvider.overrideWith((ref) => ref.watch(_hours)),
    ],
  );
  addTearDown(c.dispose);
  c.listen(simpleRunPlanProvider, (_, _) {});
  await c.read(filterWheelProvider.future);
  await Future<void>.delayed(Duration.zero);
  return c;
}

int _frames(ProviderContainer c) =>
    c.read(simpleRunPlanProvider).single.frameCount;

void main() {
  test('an untyped count refits to the dark hours left', () async {
    // Guard for the rule below: the refit itself stays for counts nobody typed.
    final c = await _sheet();
    final before = _frames(c);
    c.read(_hours.notifier).set(0.05);
    expect(
      _frames(c),
      isNot(before),
      reason: 'a shorter window fits fewer frames',
    );
  });

  test(
    'a typed frame count is never refitted when the dark hours change',
    () async {
      final c = await _sheet();
      c.read(simpleRunPlanProvider.notifier).setFrames(0, 240);
      c.read(_hours.notifier).set(0.05);
      expect(
        _frames(c),
        240,
        reason: 'the count typed into Frames is kept (#1868)',
      );
    },
  );

  test(
    'a typed frame count stays when its row gets a new sub length',
    () async {
      final c = await _sheet();
      final rows = c.read(simpleRunPlanProvider.notifier);
      rows.setFrames(0, 240);
      rows.setExposure(0, 120);
      expect(c.read(simpleRunPlanProvider).single.exposureSeconds, 120);
      expect(
        _frames(c),
        240,
        reason: 'a new sub refits only a count nobody typed',
      );
    },
  );

  test('a queued target opened for an edit keeps its rows when the dark hours change', () async {
    // Edit saves these rows back to the rig: a refit here would change the queued plan.
    final c = await _sheet();
    c.read(simpleRunPlanProvider.notifier).load(const [
      FilterPlanStep(filterName: 'L', exposureSeconds: 60, frameCount: 240),
    ]);
    c.read(_hours.notifier).set(0.05);
    expect(_frames(c), 240, reason: "the rig's count, not a refit");
  });
}
