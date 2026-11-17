import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show ProviderListenable;

import '../sequencer/sequence_list_state.dart';

/// Where a commanded device move stands (ADR 0013): asked about while a plan
/// runs, sent and not yet landed, or landed on its target.
enum DeviceCommandPhase { idle, asking, moving, landed }

/// Why a move came to nothing: the device was still on its last command, the
/// call failed, or the device accepted it and never moved.
enum DeviceCommandRefusal { busy, failed, didntMove }

@immutable
class DeviceCommand<T> {
  const DeviceCommand({
    this.phase = DeviceCommandPhase.idle,
    this.target,
    this.refusal,
    this.error,
    this.outcomes = 0,
    this.byRef,
  });

  final DeviceCommandPhase phase;

  /// What was asked about or is moving to.
  final T? target;

  /// The last move's refusal, with the error behind [DeviceCommandRefusal.failed].
  final DeviceCommandRefusal? refusal;
  final Object? error;

  /// Counts finished moves (landed, refused or reverted), so a presentation
  /// can react to each once.
  final int outcomes;

  /// Who asked for this move (a presentation passes itself): only it renders the
  /// question and reacts to the outcome, so two pickers on screen never both
  /// ask or both report. Held weakly: a presentation that closed is not kept alive.
  final WeakReference<Object>? byRef;
  Object? get by => byRef?.target;

  DeviceCommand<T> copyWith({
    DeviceCommandPhase? phase,
    T? target,
    DeviceCommandRefusal? refusal,
    Object? error,
    bool outcome = false,
    Object? by,
  }) => DeviceCommand<T>(
    phase: phase ?? this.phase,
    target: target,
    refusal: refusal,
    error: error,
    outcomes: outcomes + (outcome ? 1 : 0),
    byRef: by == null ? byRef : WeakReference(by),
  );
}

/// The shared part of a commanded device move (ADR 0013): ask first while a
/// plan runs, the move in flight, stall detection and the refused or busy
/// outcome. A device supplies the hooks; widgets render [DeviceCommand] and
/// hold no timer, pending target or asking state of their own. [T] is what is
/// asked for, [S] the device's status.
abstract class DeviceCommandNotifier<T, S> extends Notifier<DeviceCommand<T>> {
  /// How long a tap waits for an unread run state before it asks anyway.
  static const runStateWait = Duration(seconds: 3);

  /// A live poll is slow; a move re-reads the device this often (up to
  /// [_reconcileTicks] times) so a landing shows within seconds.
  static const _reconcileEvery = Duration(milliseconds: 1500);
  static const _reconcileTicks = 40;

  /// The device's status, listened in [build] and read when a stall fires.
  ProviderListenable<AsyncValue<S?>> get device;

  /// Send [target]: false means the device is busy, a throw means it failed.
  Future<bool> send(T target);

  /// Re-read the device.
  Future<void> refresh();

  /// Nothing left to watch: the move ends with no refusal.
  bool gone(S? s);

  /// The device reads as moving.
  bool moving(S s);

  /// The device's own "motion seen" rule; moving by default.
  bool motionSeen(S s, T target) => moving(s);

  /// On target. [motionSeen] is true once motion was seen during this move.
  bool landed(S s, T target, {required bool motionSeen});

  /// No motion seen in this long fails the move; motion seen arms [backstop].
  Duration firstStall(T target);
  Duration backstop(T target);

  /// False: the move lands as soon as [send] returns true, with no timers.
  bool expectsMotion(T target) => true;

  /// True: timers stop while nothing listens and resume with a listener.
  bool get pausesWithoutListeners => true;

  Timer? _stall;
  Timer? _reconcile;
  bool _sawTurning = false;
  bool _sawMotion = false;

  @override
  DeviceCommand<T> build() {
    ref.onDispose(_stopTimers);
    if (pausesWithoutListeners) {
      ref.onCancel(_stopTimers);
      ref.onResume(() {
        final target = state.target;
        if (state.phase == DeviceCommandPhase.moving && target != null) {
          _armStall(target);
          _armReconcile();
        }
      });
    }
    ref.listen(device, (_, next) => _follow(next.value));
    // Kept live while a presentation shows, so a tap knows whether a plan runs.
    ref.listen(sequenceRunStateProvider, (_, _) {});
    return DeviceCommand<T>();
  }

  /// Ask for [target] on behalf of [by]; while a plan runs this asks first,
  /// and [confirm] sends it.
  void request(T target, {Object? by}) {
    if (state.phase == DeviceCommandPhase.moving) return;
    final run = ref.read(sequenceRunStateProvider);
    if (run.hasValue)
      return _decide(target, by, run.value?.state?.isActive ?? false);
    // Not read yet: wait for it rather than send without asking. A read that
    // fails, or takes longer than [runStateWait], asks: the safe side.
    unawaited(
      ref
          .read(sequenceRunStateProvider.future)
          .timeout(runStateWait)
          .then((info) => info?.state?.isActive ?? false, onError: (_) => true)
          .then((running) {
            if (ref.mounted) _decide(target, by, running);
          }),
    );
  }

  void _decide(T target, Object? by, bool planRunning) {
    if (state.phase == DeviceCommandPhase.moving) return;
    if (planRunning) {
      state = state.copyWith(
        phase: DeviceCommandPhase.asking,
        target: target,
        by: by,
      );
      return;
    }
    unawaited(_send(target, by: by));
  }

  /// The question's "anyway".
  void confirm() {
    final target = state.target;
    if (state.phase != DeviceCommandPhase.asking || target == null) return;
    unawaited(_send(target));
  }

  /// The question's Cancel.
  void cancel() {
    if (state.phase == DeviceCommandPhase.asking) {
      state = state.copyWith(phase: DeviceCommandPhase.idle);
    }
  }

  Future<void> _send(T target, {Object? by}) async {
    _sawTurning = false;
    _sawMotion = false;
    state = state.copyWith(
      phase: DeviceCommandPhase.moving,
      target: target,
      by: by,
    );
    final expects = expectsMotion(target);
    if (expects) {
      _armStall(target);
      _armReconcile();
    }
    final bool performed;
    try {
      performed = await send(target);
    } catch (e) {
      if (ref.mounted) _end(DeviceCommandRefusal.failed, error: e);
      return;
    }
    if (!ref.mounted) return;
    if (!performed) {
      _end(DeviceCommandRefusal.busy);
    } else if (!expects) {
      _land();
    }
  }

  void _follow(S? s) {
    if (state.phase != DeviceCommandPhase.moving) return;
    final target = state.target;
    if (target == null) return;
    if (gone(s)) {
      _end(null);
      return;
    }
    if (s == null) return;
    final isMoving = moving(s);
    if (isMoving) _sawTurning = true;
    if (motionSeen(s, target)) _sawMotion = true;
    if (isMoving) return;
    if (landed(s, target, motionSeen: _sawMotion)) {
      _land();
    } else if (_sawTurning) {
      // Turned and stopped elsewhere: the presentations show where.
      _end(null);
    }
  }

  void _armStall(T target) {
    _stall?.cancel();
    _stall = Timer(firstStall(target), () {
      if (_sawMotion) {
        _stall = Timer(
          backstop(target),
          () => unawaited(_stallHard(afterBackstop: true)),
        );
      } else {
        unawaited(_stallHard(afterBackstop: false));
      }
    });
  }

  void _armReconcile() {
    _reconcile?.cancel();
    var ticks = 0;
    _reconcile = Timer.periodic(_reconcileEvery, (t) {
      if (state.phase != DeviceCommandPhase.moving ||
          ++ticks > _reconcileTicks) {
        t.cancel();
        return;
      }
      unawaited(refresh());
    });
  }

  /// A landing no poll saw is still a landing: ask the device before failing.
  Future<void> _stallHard({required bool afterBackstop}) async {
    if (state.phase != DeviceCommandPhase.moving) return;
    try {
      await refresh();
    } catch (_) {
      // The read failed; no news.
    }
    final target = state.target;
    if (!ref.mounted ||
        state.phase != DeviceCommandPhase.moving ||
        target == null) {
      return;
    }
    final s = ref.read(device).value;
    if (s != null) {
      if (motionSeen(s, target)) _sawMotion = true;
      if (landed(s, target, motionSeen: _sawMotion)) {
        _land();
        return;
      }
    }
    if (!afterBackstop && _sawMotion) {
      // The re-read found motion no poll had seen: give it the backstop.
      _stall = Timer(
        backstop(target),
        () => unawaited(_stallHard(afterBackstop: true)),
      );
      return;
    }
    _end(DeviceCommandRefusal.didntMove);
  }

  void _land() {
    _stopTimers();
    state = state.copyWith(
      phase: DeviceCommandPhase.landed,
      target: state.target,
      outcome: true,
    );
  }

  void _end(DeviceCommandRefusal? refusal, {Object? error}) {
    _stopTimers();
    state = state.copyWith(
      phase: DeviceCommandPhase.idle,
      refusal: refusal,
      error: error,
      outcome: true,
    );
  }

  void _stopTimers() {
    _stall?.cancel();
    _reconcile?.cancel();
  }
}
