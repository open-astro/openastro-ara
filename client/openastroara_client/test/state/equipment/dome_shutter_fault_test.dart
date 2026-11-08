import 'package:clock/clock.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/dome_status.dart';
import 'package:openastroara/state/equipment/dome_shutter_state.dart';
import 'package:openastroara/state/equipment/dome_state.dart';

/// A dome whose reading the test sets.
class _Dome extends DomeNotifier {
  @override
  Future<DomeStatus?> build() async => _status('idle', true);

  void report(String runtime, {required bool open}) =>
      state = AsyncData(_status(runtime, open));
}

DomeStatus _status(String runtime, bool open) => DomeStatus.fromJson({
  'device_id': 'd',
  'name': 'Dome',
  'state': 'connected',
  'runtime': {'state': runtime, 'shutter_open': open},
});

void main() {
  late ProviderContainer c;
  late List<DomeShutterMove?> seen;

  Future<_Dome> setUp0() async {
    c = ProviderContainer(overrides: [domeProvider.overrideWith(_Dome.new)]);
    addTearDown(c.dispose);
    await c.read(domeProvider.future);
    seen = [];
    c.listen(domeShutterProvider, (_, m) => seen.add(m), fireImmediately: true);
    return c.read(domeProvider.notifier) as _Dome;
  }

  test(
    'after a fault on an open dome, a Close move is never read as opening',
    () async {
      final dome = await setUp0();
      // The daemon reports a faulted shutter as error with shutter_open false.
      dome.report('error', open: false);
      dome.report('shutter_moving', open: false);
      final move = c.read(domeShutterProvider)!;
      expect(move.opening, isNull, reason: 'direction unknown after a fault');
      expect(
        seen.whereType<DomeShutterMove>().any((m) => m.opening == true),
        isFalse,
      );
    },
  );

  test(
    'a refused open or close leaves no ask to time a later move from',
    () async {
      final dome = await setUp0();
      final t0 = DateTime(2026, 11, 8, 21);
      await withClock(Clock.fixed(t0), () async {
        final n = c.read(domeShutterProvider.notifier)..asked();
        n.cancelAsk();
      });
      final t1 = t0.add(const Duration(seconds: 3));
      await withClock(Clock.fixed(t1), () async {
        dome.report('shutter_moving', open: true);
      });
      expect(
        c.read(domeShutterProvider)!.startedAt,
        t1,
        reason: 'a plan-started move is timed from when it was seen',
      );
    },
  );
}
