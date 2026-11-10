import 'package:fake_async/fake_async.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/filter_wheel_status.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/state/equipment/filter_wheel_command_state.dart';
import 'package:openastroara/state/equipment/filter_wheel_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';

FilterWheelStatus _wheel({int? slot = 0, String runtime = 'idle'}) => FilterWheelStatus(
  deviceId: 'fw',
  name: 'EFW',
  connectionState: EquipmentConnectionState.connected,
  runtimeState: runtime,
  currentSlot: slot,
  slots: const [
    FilterSlot(position: 0, name: 'L', focusOffset: 0),
    FilterSlot(position: 1, name: 'R', focusOffset: 0),
  ],
);

/// A wheel the test moves by hand; [answer] is what changeFilter returns.
class _Wheel extends FilterWheelNotifier {
  final sent = <int>[];
  bool answer = true;
  Object? error;
  int refreshes = 0;

  @override
  Future<FilterWheelStatus?> build() async => _wheel();

  @override
  Future<bool> changeFilter(int position) async {
    sent.add(position);
    if (error case final e?) throw e;
    return answer;
  }

  @override
  Future<void> refresh([client]) async => refreshes++;

  void report(FilterWheelStatus s) => state = AsyncData(s);
}

class _Run extends SequenceRunStateNotifier {
  _Run(this.running);
  final bool running;
  @override
  Future<SequenceRunStateInfo?> build() async =>
      running ? const SequenceRunStateInfo(runId: 'r', state: SequenceRunState.running) : null;
}

(ProviderContainer, _Wheel) _rig(FakeAsync async, {bool planRunning = false}) {
  final wheel = _Wheel();
  final c = ProviderContainer(overrides: [
    filterWheelProvider.overrideWith(() => wheel),
    sequenceRunStateProvider.overrideWith(() => _Run(planRunning)),
  ]);
  addTearDown(c.dispose);
  c.listen(sequenceRunStateProvider, (_, _) {});
  c.listen(filterWheelProvider, (_, _) {});
  c.listen(filterWheelCommandProvider, (_, _) {});
  async.flushMicrotasks();
  return (c, wheel);
}

void main() {
  test('a change with no plan running is sent at once and lands when the wheel parks on it', () {
    fakeAsync((async) {
      final (c, wheel) = _rig(async);
      c.read(filterWheelCommandProvider.notifier).request(1);
      async.flushMicrotasks();
      expect(wheel.sent, [1]);
      expect(c.read(filterWheelCommandProvider).phase, DeviceCommandPhase.moving);
      wheel.report(_wheel(slot: 0, runtime: 'moving'));
      wheel.report(_wheel(slot: 1));
      expect(c.read(filterWheelCommandProvider).phase, DeviceCommandPhase.landed);
    });
  });

  test('while a plan runs it asks first; Cancel sends nothing, Change anyway sends it', () {
    fakeAsync((async) {
      final (c, wheel) = _rig(async, planRunning: true);
      final cmd = c.read(filterWheelCommandProvider.notifier);
      cmd.request(1);
      expect(c.read(filterWheelCommandProvider).phase, DeviceCommandPhase.asking);
      cmd.cancel();
      async.flushMicrotasks();
      expect(wheel.sent, isEmpty);
      cmd.request(1);
      cmd.confirm();
      async.flushMicrotasks();
      expect(wheel.sent, [1]);
    });
  });

  test('a wheel never seen turning fails after 10 s', () {
    fakeAsync((async) {
      final (c, _) = _rig(async);
      c.read(filterWheelCommandProvider.notifier).request(1);
      async.elapse(const Duration(seconds: 9));
      expect(c.read(filterWheelCommandProvider).phase, DeviceCommandPhase.moving);
      async.elapse(const Duration(seconds: 2));
      expect(c.read(filterWheelCommandProvider).refusal, DeviceCommandRefusal.didntMove);
      expect(c.read(filterWheelCommandProvider).phase, DeviceCommandPhase.idle);
    });
  });

  test('a wheel seen turning gets the 45 s backstop before it fails', () {
    fakeAsync((async) {
      final (c, wheel) = _rig(async);
      c.read(filterWheelCommandProvider.notifier).request(1);
      async.flushMicrotasks();
      wheel.report(_wheel(slot: 0, runtime: 'moving'));
      async.elapse(const Duration(seconds: 50));
      expect(c.read(filterWheelCommandProvider).phase, DeviceCommandPhase.moving);
      async.elapse(const Duration(seconds: 6));
      expect(c.read(filterWheelCommandProvider).refusal, DeviceCommandRefusal.didntMove);
    });
  });

  test('a move the wheel drops (its last command running) is refused as busy', () {
    fakeAsync((async) {
      final (c, wheel) = _rig(async);
      wheel.answer = false;
      c.read(filterWheelCommandProvider.notifier).request(1);
      async.flushMicrotasks();
      expect(c.read(filterWheelCommandProvider).refusal, DeviceCommandRefusal.busy);
      expect(c.read(filterWheelCommandProvider).phase, DeviceCommandPhase.idle);
    });
  });

  test('a move that fails carries its error', () {
    fakeAsync((async) {
      final (c, wheel) = _rig(async);
      wheel.error = StateError('rig said no');
      c.read(filterWheelCommandProvider.notifier).request(1);
      async.flushMicrotasks();
      expect(c.read(filterWheelCommandProvider).refusal, DeviceCommandRefusal.failed);
      expect(c.read(filterWheelCommandProvider).error, isA<StateError>());
    });
  });

  test('while it moves the wheel is re-read every 1.5 s, not every 15 s', () {
    fakeAsync((async) {
      final (c, wheel) = _rig(async);
      c.read(filterWheelCommandProvider.notifier).request(1);
      async.elapse(const Duration(milliseconds: 4600));
      expect(wheel.refreshes, 3);
    });
  });
}
