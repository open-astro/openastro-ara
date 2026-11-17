import 'package:fake_async/fake_async.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/rotator_status.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/state/equipment/device_command_state.dart';
import 'package:openastroara/state/equipment/rotator_command_state.dart';
import 'package:openastroara/state/equipment/rotator_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';

RotatorStatus _rotator({
  double? sky = 92,
  double? mech = 88,
  String runtime = 'idle',
}) => RotatorStatus(
  deviceId: 'rot',
  name: 'Falcon',
  connectionState: EquipmentConnectionState.connected,
  capabilities: const RotatorCapabilities(canReverse: true, stepSize: 0.1),
  runtimeState: runtime,
  mechanicalAngleDeg: mech,
  skyAngleDeg: sky,
  reverse: false,
);

/// A rotator the test moves by hand; [answer] is what move and sync return.
class _Rotator extends RotatorNotifier {
  final sent = <String>[];
  bool answer = true;
  Object? error;
  int refreshes = 0;

  /// What a refresh reads, 100 ms after it is asked; null reads nothing new.
  RotatorStatus? onRefresh;

  @override
  Future<RotatorStatus?> build() async => _rotator();

  @override
  Future<bool> move(double targetAngleDeg, {bool useSkyAngle = false}) async {
    sent.add(
      'move ${targetAngleDeg.toStringAsFixed(1)}${useSkyAngle ? ' sky' : ''}',
    );
    if (error case final e?) throw e;
    return answer;
  }

  @override
  Future<bool> sync(double skyAngleDeg) async {
    sent.add('sync ${skyAngleDeg.toStringAsFixed(1)}');
    if (error case final e?) throw e;
    return answer;
  }

  @override
  Future<bool> halt() async {
    sent.add('halt');
    return true;
  }

  @override
  Future<void> refresh([client]) async {
    refreshes++;
    if (onRefresh case final s?) {
      await Future<void>.delayed(const Duration(milliseconds: 100));
      state = AsyncData(s);
    }
  }

  void report(RotatorStatus s) => state = AsyncData(s);
}

class _Run extends SequenceRunStateNotifier {
  _Run(this.running, {this.delay = Duration.zero, this.fails = false});
  final bool running;
  final Duration delay;
  final bool fails;
  @override
  Future<SequenceRunStateInfo?> build() async {
    if (delay > Duration.zero) await Future<void>.delayed(delay);
    if (fails) throw StateError('run state unreadable');
    return running
        ? const SequenceRunStateInfo(
            runId: 'r',
            state: SequenceRunState.running,
          )
        : null;
  }
}

(ProviderContainer, _Rotator) _rig(
  FakeAsync async, {
  bool planRunning = false,
  Duration runReadTakes = Duration.zero,
  bool runReadFails = false,
}) {
  final rotator = _Rotator();
  final c = ProviderContainer(
    overrides: [
      rotatorProvider.overrideWith(() => rotator),
      sequenceRunStateProvider.overrideWith(
        () => _Run(planRunning, delay: runReadTakes, fails: runReadFails),
      ),
    ],
  );
  addTearDown(c.dispose);
  c.listen(sequenceRunStateProvider, (_, _) {});
  c.listen(rotatorProvider, (_, _) {});
  c.listen(rotatorCommandProvider, (_, _) {});
  async.flushMicrotasks();
  return (c, rotator);
}

const _to75 = RotatorTarget.move(75, useSkyAngle: true);

void main() {
  test('a move with no plan running is sent at once and lands when the rotator stops on the angle', () {
    fakeAsync((async) {
      final (c, rotator) = _rig(async);
      c.read(rotatorCommandProvider.notifier).request(_to75);
      async.flushMicrotasks();
      expect(rotator.sent, ['move 75.0 sky']);
      expect(c.read(rotatorCommandProvider).phase, DeviceCommandPhase.moving);
      rotator.report(_rotator(sky: 85, runtime: 'moving'));
      rotator.report(_rotator(sky: 75));
      expect(c.read(rotatorCommandProvider).phase, DeviceCommandPhase.landed);
      expect(c.read(rotatorCommandProvider).refusal, isNull);
    });
  });

  test('a mechanical move lands on the mechanical angle, whatever the sky angle reads', () {
    fakeAsync((async) {
      final (c, rotator) = _rig(async);
      c
          .read(rotatorCommandProvider.notifier)
          .request(const RotatorTarget.move(40, useSkyAngle: false));
      async.flushMicrotasks();
      expect(rotator.sent, ['move 40.0']);
      rotator.report(_rotator(mech: 60, sky: 64, runtime: 'moving'));
      rotator.report(_rotator(mech: 40, sky: 44));
      expect(c.read(rotatorCommandProvider).phase, DeviceCommandPhase.landed);
    });
  });

  test('while a plan runs a move asks first and sends nothing', () {
    fakeAsync((async) {
      final (c, rotator) = _rig(async, planRunning: true);
      c.read(rotatorCommandProvider.notifier).request(_to75);
      async.flushMicrotasks();
      expect(c.read(rotatorCommandProvider).phase, DeviceCommandPhase.asking);
      expect(c.read(rotatorCommandProvider).target, _to75);
      expect(rotator.sent, isEmpty);
    });
  });

  test('Cancel sends nothing and the module goes back to idle', () {
    fakeAsync((async) {
      final (c, rotator) = _rig(async, planRunning: true);
      final cmd = c.read(rotatorCommandProvider.notifier);
      cmd.request(_to75);
      async.flushMicrotasks();
      cmd.cancel();
      async.elapse(const Duration(seconds: 5));
      expect(c.read(rotatorCommandProvider).phase, DeviceCommandPhase.idle);
      expect(c.read(rotatorCommandProvider).refusal, isNull);
      expect(rotator.sent, isEmpty);
    });
  });

  test('Rotate anyway sends exactly the angle asked, once', () {
    fakeAsync((async) {
      final (c, rotator) = _rig(async, planRunning: true);
      final cmd = c.read(rotatorCommandProvider.notifier);
      cmd.request(_to75);
      async.flushMicrotasks();
      cmd.confirm();
      cmd.confirm(); // a second tap on the spent question sends nothing more
      async.flushMicrotasks();
      expect(rotator.sent, ['move 75.0 sky']);
      expect(c.read(rotatorCommandProvider).phase, DeviceCommandPhase.moving);
    });
  });

  test('a new press while the question shows replaces what is asked', () {
    fakeAsync((async) {
      final (c, rotator) = _rig(async, planRunning: true);
      final cmd = c.read(rotatorCommandProvider.notifier);
      final window = Object();
      final panel = Object();
      cmd.request(_to75, by: window);
      async.flushMicrotasks();
      cmd.request(const RotatorTarget.sync(80), by: panel);
      async.flushMicrotasks();
      expect(
        c.read(rotatorCommandProvider).target,
        const RotatorTarget.sync(80),
      );
      expect(c.read(rotatorCommandProvider).by, same(panel));
      cmd.confirm();
      async.flushMicrotasks();
      expect(rotator.sent, ['sync 80.0']);
    });
  });

  test('while a plan runs Sync asks first; Sync anyway syncs the angle asked and lands with no motion', () {
    fakeAsync((async) {
      final (c, rotator) = _rig(async, planRunning: true);
      final cmd = c.read(rotatorCommandProvider.notifier);
      cmd.request(const RotatorTarget.sync(75));
      async.flushMicrotasks();
      expect(c.read(rotatorCommandProvider).phase, DeviceCommandPhase.asking);
      expect(rotator.sent, isEmpty);
      cmd.confirm();
      async.flushMicrotasks();
      expect(rotator.sent, ['sync 75.0']);
      expect(c.read(rotatorCommandProvider).phase, DeviceCommandPhase.landed);
      async.elapse(const Duration(seconds: 10));
      expect(
        c.read(rotatorCommandProvider).refusal,
        isNull,
        reason: 'a sync does not move, so it never stalls',
      );
    });
  });

  test('a press while the rotator moves sends nothing more', () {
    fakeAsync((async) {
      final (c, rotator) = _rig(async);
      final cmd = c.read(rotatorCommandProvider.notifier);
      cmd.request(_to75);
      async.flushMicrotasks();
      rotator.report(_rotator(sky: 85, runtime: 'moving'));
      cmd.request(const RotatorTarget.move(40, useSkyAngle: true));
      async.flushMicrotasks();
      expect(rotator.sent, ['move 75.0 sky']);
      expect(c.read(rotatorCommandProvider).target, _to75);
    });
  });

  test('a move lands within 0.5 degrees of the angle', () {
    fakeAsync((async) {
      final (c, rotator) = _rig(async);
      c.read(rotatorCommandProvider.notifier).request(_to75);
      async.flushMicrotasks();
      rotator.report(_rotator(sky: 85, runtime: 'moving'));
      rotator.report(_rotator(sky: 75.4));
      expect(c.read(rotatorCommandProvider).phase, DeviceCommandPhase.landed);
    });
  });

  test(
    'a move to 0 that stops at 359.8 has landed: the angle wraps through 0',
    () {
      fakeAsync((async) {
        final (c, rotator) = _rig(async);
        c
            .read(rotatorCommandProvider.notifier)
            .request(const RotatorTarget.move(0, useSkyAngle: true));
        async.flushMicrotasks();
        rotator.report(_rotator(sky: 10, runtime: 'moving'));
        rotator.report(_rotator(sky: 359.8));
        expect(c.read(rotatorCommandProvider).phase, DeviceCommandPhase.landed);
      });
    },
  );

  test('a rotator that turns and stops a degree off ends with no refusal, not landed', () {
    fakeAsync((async) {
      final (c, rotator) = _rig(async);
      c.read(rotatorCommandProvider.notifier).request(_to75);
      async.flushMicrotasks();
      rotator.report(_rotator(sky: 85, runtime: 'moving'));
      rotator.report(_rotator(sky: 76));
      expect(c.read(rotatorCommandProvider).phase, DeviceCommandPhase.idle);
      expect(c.read(rotatorCommandProvider).refusal, isNull);
    });
  });

  test('a rotator that stops with its angle unread has not landed', () {
    fakeAsync((async) {
      final (c, rotator) = _rig(async);
      c.read(rotatorCommandProvider.notifier).request(_to75);
      async.flushMicrotasks();
      rotator.report(_rotator(sky: 85, runtime: 'moving'));
      rotator.report(_rotator(sky: null));
      expect(
        c.read(rotatorCommandProvider).phase,
        isNot(DeviceCommandPhase.landed),
      );
    });
  });

  test('a move to the angle the rotator already holds is still sent and fails after 3 s', () {
    fakeAsync((async) {
      final (c, rotator) = _rig(async);
      c
          .read(rotatorCommandProvider.notifier)
          .request(const RotatorTarget.move(92, useSkyAngle: true));
      async.flushMicrotasks();
      expect(rotator.sent, ['move 92.0 sky']);
      rotator.report(_rotator(sky: 92));
      expect(
        c.read(rotatorCommandProvider).phase,
        DeviceCommandPhase.moving,
        reason: 'on the angle with no motion seen is not a landing',
      );
      async.elapse(const Duration(milliseconds: 3100));
      expect(c.read(rotatorCommandProvider).phase, DeviceCommandPhase.idle);
      expect(
        c.read(rotatorCommandProvider).refusal,
        DeviceCommandRefusal.didntMove,
      );
    });
  });

  test(
    'a fast move no poll sees turning lands when the angle reads on target',
    () {
      fakeAsync((async) {
        final (c, rotator) = _rig(async);
        c.read(rotatorCommandProvider.notifier).request(_to75);
        async.flushMicrotasks();
        rotator.report(_rotator(sky: 75));
        expect(c.read(rotatorCommandProvider).phase, DeviceCommandPhase.landed);
        expect(c.read(rotatorCommandProvider).refusal, isNull);
      });
    },
  );

  test('a rotator that never reads as moving but whose angle changes gets the backstop, not a 3 s failure', () {
    fakeAsync((async) {
      final (c, rotator) = _rig(async);
      c.read(rotatorCommandProvider.notifier).request(_to75);
      async.flushMicrotasks();
      rotator.report(_rotator(sky: 85));
      expect(
        c.read(rotatorCommandProvider).phase,
        DeviceCommandPhase.moving,
        reason: 'not seen turning, so not stopped off target either',
      );
      async.elapse(const Duration(milliseconds: 3100));
      expect(c.read(rotatorCommandProvider).phase, DeviceCommandPhase.moving);
      expect(c.read(rotatorCommandProvider).refusal, isNull);
      async.elapse(const Duration(seconds: 298));
      expect(c.read(rotatorCommandProvider).phase, DeviceCommandPhase.moving);
      rotator.report(_rotator(sky: 75));
      expect(c.read(rotatorCommandProvider).phase, DeviceCommandPhase.landed);
    });
  });

  test('a rotator the 3 s check finds turning gets the backstop instead of failing', () {
    fakeAsync((async) {
      final (c, rotator) = _rig(async);
      c.read(rotatorCommandProvider.notifier).request(_to75);
      async.elapse(const Duration(milliseconds: 2900));
      rotator.onRefresh = _rotator(sky: 85, runtime: 'moving');
      async.elapse(const Duration(milliseconds: 300));
      expect(c.read(rotatorCommandProvider).phase, DeviceCommandPhase.moving);
      expect(c.read(rotatorCommandProvider).refusal, isNull);
      rotator.onRefresh = null;
      rotator.report(_rotator(sky: 75));
      expect(c.read(rotatorCommandProvider).phase, DeviceCommandPhase.landed);
    });
  });

  test('a rotator never seen moving fails after 3 s', () {
    fakeAsync((async) {
      final (c, _) = _rig(async);
      c.read(rotatorCommandProvider.notifier).request(_to75);
      async.elapse(const Duration(milliseconds: 2900));
      expect(c.read(rotatorCommandProvider).phase, DeviceCommandPhase.moving);
      async.elapse(const Duration(milliseconds: 200));
      expect(
        c.read(rotatorCommandProvider).refusal,
        DeviceCommandRefusal.didntMove,
      );
      expect(c.read(rotatorCommandProvider).phase, DeviceCommandPhase.idle);
    });
  });

  test('a rotator seen moving gets the 300 s backstop before it fails', () {
    fakeAsync((async) {
      final (c, rotator) = _rig(async);
      c.read(rotatorCommandProvider.notifier).request(_to75);
      async.flushMicrotasks();
      rotator.report(_rotator(sky: 85, runtime: 'moving'));
      async.elapse(const Duration(seconds: 302));
      expect(c.read(rotatorCommandProvider).phase, DeviceCommandPhase.moving);
      async.elapse(const Duration(seconds: 2));
      expect(
        c.read(rotatorCommandProvider).refusal,
        DeviceCommandRefusal.didntMove,
      );
    });
  });

  test('while it moves the rotator is re-read every 1.5 s, not every 15 s', () {
    fakeAsync((async) {
      final (c, rotator) = _rig(async);
      c.read(rotatorCommandProvider.notifier).request(_to75);
      async.flushMicrotasks();
      final before = rotator.refreshes;
      async.elapse(const Duration(milliseconds: 2000));
      expect(rotator.refreshes - before, 1);
    });
  });

  test(
    'a move the rotator drops (its last command running) is refused as busy',
    () {
      fakeAsync((async) {
        final (c, rotator) = _rig(async);
        rotator.answer = false;
        c.read(rotatorCommandProvider.notifier).request(_to75);
        async.flushMicrotasks();
        expect(
          c.read(rotatorCommandProvider).refusal,
          DeviceCommandRefusal.busy,
        );
        expect(c.read(rotatorCommandProvider).phase, DeviceCommandPhase.idle);
      });
    },
  );

  test('a move that fails carries its error', () {
    fakeAsync((async) {
      final (c, rotator) = _rig(async);
      rotator.error = StateError('rig said no');
      c.read(rotatorCommandProvider.notifier).request(_to75);
      async.flushMicrotasks();
      expect(
        c.read(rotatorCommandProvider).refusal,
        DeviceCommandRefusal.failed,
      );
      expect(c.read(rotatorCommandProvider).error, isA<StateError>());
    });
  });

  test('a run state that cannot be read asks instead of sending', () {
    fakeAsync((async) {
      final (c, rotator) = _rig(async, runReadFails: true);
      c.read(rotatorCommandProvider.notifier).request(_to75);
      async.flushMicrotasks();
      expect(c.read(rotatorCommandProvider).phase, DeviceCommandPhase.asking);
      expect(rotator.sent, isEmpty);
    });
  });

  test('a tap before the run state is read waits for it, and asks while a plan runs', () {
    fakeAsync((async) {
      final (c, rotator) = _rig(
        async,
        planRunning: true,
        runReadTakes: const Duration(seconds: 1),
      );
      c.read(rotatorCommandProvider.notifier).request(_to75);
      async.flushMicrotasks();
      expect(
        rotator.sent,
        isEmpty,
        reason: 'not sent before the run state is known',
      );
      async.elapse(const Duration(seconds: 2));
      expect(c.read(rotatorCommandProvider).phase, DeviceCommandPhase.asking);
      expect(rotator.sent, isEmpty);
    });
  });

  test(
    'a run state that never answers asks after 3 s instead of doing nothing',
    () {
      fakeAsync((async) {
        final (c, rotator) = _rig(async, runReadTakes: const Duration(days: 1));
        c.read(rotatorCommandProvider.notifier).request(_to75);
        async.elapse(const Duration(seconds: 2));
        expect(c.read(rotatorCommandProvider).phase, DeviceCommandPhase.idle);
        async.elapse(const Duration(seconds: 2));
        expect(c.read(rotatorCommandProvider).phase, DeviceCommandPhase.asking);
        expect(rotator.sent, isEmpty);
      });
    },
  );

  test('a halt while a plan runs goes straight to the rotator; the move it stops ends with no question and no refusal', () {
    fakeAsync((async) {
      final (c, rotator) = _rig(async, planRunning: true);
      final cmd = c.read(rotatorCommandProvider.notifier);
      cmd.request(_to75);
      async.flushMicrotasks();
      cmd.confirm();
      async.flushMicrotasks();
      rotator.report(_rotator(sky: 85, runtime: 'moving'));
      final phases = <DeviceCommandPhase>[];
      c.listen(rotatorCommandProvider, (_, next) => phases.add(next.phase));
      rotator.halt();
      async.flushMicrotasks();
      rotator.report(_rotator(sky: 83));
      expect(rotator.sent, ['move 75.0 sky', 'halt']);
      expect(c.read(rotatorCommandProvider).phase, DeviceCommandPhase.idle);
      expect(c.read(rotatorCommandProvider).refusal, isNull);
      expect(phases, isNot(contains(DeviceCommandPhase.asking)));
    });
  });

  test('the move remembers who asked for it', () {
    fakeAsync((async) {
      final (c, _) = _rig(async);
      final window = Object();
      c.read(rotatorCommandProvider.notifier).request(_to75, by: window);
      async.flushMicrotasks();
      expect(c.read(rotatorCommandProvider).by, same(window));
    });
  });

  test('the module lives only while a window or panel shows it', () {
    fakeAsync((async) {
      final rotator = _Rotator();
      final c = ProviderContainer(
        overrides: [
          rotatorProvider.overrideWith(() => rotator),
          sequenceRunStateProvider.overrideWith(() => _Run(true)),
        ],
      );
      addTearDown(c.dispose);
      c.listen(rotatorProvider, (_, _) {});
      final shown = c.listen(rotatorCommandProvider, (_, _) {});
      async.flushMicrotasks();
      c.read(rotatorCommandProvider.notifier).request(_to75);
      async.flushMicrotasks();
      expect(c.read(rotatorCommandProvider).phase, DeviceCommandPhase.asking);
      shown.close();
      async.elapse(Duration.zero); // autoDispose runs once nothing listens
      expect(
        c.read(rotatorCommandProvider).phase,
        DeviceCommandPhase.idle,
        reason: 'the question ended with the window',
      );
    });
  });
}
