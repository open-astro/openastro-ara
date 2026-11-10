import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/filter_wheel_status.dart';
import '../sequencer/sequence_list_state.dart';
import 'filter_wheel_state.dart';

/// Where a commanded device move stands (ADR 0013): asked about while a plan
/// runs, sent and not yet landed, or landed on its target.
enum DeviceCommandPhase { idle, asking, moving, landed }

/// Why a move came to nothing: the device was still on its last command, the
/// call failed, or the device accepted it and never moved.
enum DeviceCommandRefusal { busy, failed, didntMove }

@immutable
class DeviceCommand {
  const DeviceCommand({
    this.phase = DeviceCommandPhase.idle,
    this.target,
    this.refusal,
    this.error,
    this.outcomes = 0,
    this.by,
  });

  final DeviceCommandPhase phase;

  /// The slot asked about or moving to.
  final int? target;

  /// The last move's refusal, with the error behind [DeviceCommandRefusal.failed].
  final DeviceCommandRefusal? refusal;
  final Object? error;

  /// Counts finished moves (landed, refused or reverted), so a presentation
  /// can react to each once.
  final int outcomes;

  /// Who asked for this move (a picker passes itself): only it renders the
  /// question and reacts to the outcome, so two pickers on screen never both
  /// ask or both report.
  final Object? by;

  DeviceCommand copyWith({
    DeviceCommandPhase? phase,
    int? target,
    DeviceCommandRefusal? refusal,
    Object? error,
    bool outcome = false,
    Object? by,
  }) => DeviceCommand(
    phase: phase ?? this.phase,
    target: target,
    refusal: refusal,
    error: error,
    outcomes: outcomes + (outcome ? 1 : 0),
    by: by ?? this.by,
  );
}

/// A filter change, for both presentations (ADR 0013, the device-command
/// pilot): ask first while a plan runs, the move in flight, stall detection
/// and the refused or busy outcome. Widgets render [DeviceCommand] and hold
/// no timer, pending target or asking state of their own.
class FilterWheelCommandNotifier extends Notifier<DeviceCommand> {
  /// No motion seen in this long fails the move; motion seen arms [backstop].
  static const firstStall = Duration(seconds: 10);
  static const backstop = Duration(seconds: 45);

  /// The wheel's live poll runs every 15 s; a move re-reads it this often
  /// (up to [_reconcileTicks] times) so a landing shows within seconds.
  static const _reconcileEvery = Duration(milliseconds: 1500);
  static const _reconcileTicks = 40;

  Timer? _stall;
  Timer? _reconcile;
  bool _sawMove = false;

  @override
  DeviceCommand build() {
    ref.onDispose(_stopTimers);
    // No picker showing: no timers either; one coming back picks up the move.
    ref.onCancel(_stopTimers);
    ref.onResume(() {
      if (state.phase == DeviceCommandPhase.moving) {
        _armStall();
        _armReconcile();
      }
    });
    ref.listen(filterWheelProvider, (_, next) => _follow(next.value));
    // Kept live while a picker shows, so a tap knows whether a plan runs.
    ref.listen(sequenceRunStateProvider, (_, _) {});
    return const DeviceCommand();
  }

  /// Change to [position] for [by]; while a plan runs this asks first, and
  /// [confirm] sends it.
  void request(int position, {Object? by}) {
    if (state.phase == DeviceCommandPhase.moving) return;
    final run = ref.read(sequenceRunStateProvider);
    if (run.hasValue) return _decide(position, by, run.value?.state?.isActive ?? false);
    // Not read yet: wait for it rather than send without asking. A read that
    // fails asks, the safe side.
    unawaited(
      ref
          .read(sequenceRunStateProvider.future)
          .then((info) => info?.state?.isActive ?? false, onError: (_) => true)
          .then((running) {
            if (ref.mounted) _decide(position, by, running);
          }),
    );
  }

  void _decide(int position, Object? by, bool planRunning) {
    if (state.phase == DeviceCommandPhase.moving) return;
    if (planRunning) {
      state = state.copyWith(
        phase: DeviceCommandPhase.asking,
        target: position,
        by: by,
      );
      return;
    }
    unawaited(_send(position, by: by));
  }

  /// The question's Change anyway.
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

  Future<void> _send(int position, {Object? by}) async {
    _sawMove = false;
    state = state.copyWith(
      phase: DeviceCommandPhase.moving,
      target: position,
      by: by,
    );
    _armStall();
    _armReconcile();
    final bool performed;
    try {
      performed = await ref
          .read(filterWheelProvider.notifier)
          .changeFilter(position);
    } catch (e) {
      if (ref.mounted) _end(DeviceCommandRefusal.failed, error: e);
      return;
    }
    if (!performed && ref.mounted) _end(DeviceCommandRefusal.busy);
  }

  void _follow(FilterWheelStatus? wheel) {
    if (state.phase != DeviceCommandPhase.moving) return;
    final target = state.target;
    if (wheel == null || !wheel.isConnected || wheel.isAwaitingUser) {
      // Gone, or a manual wheel now waiting on the user: nothing to watch.
      _end(null);
    } else if (wheel.isMoving) {
      _sawMove = true;
    } else if (wheel.currentSlot == target) {
      _land();
    } else if (_sawMove) {
      // Turned and parked elsewhere: the pickers show where it is.
      _end(null);
    }
  }

  void _armStall() {
    _stall?.cancel();
    _stall = Timer(firstStall, () {
      if (_sawMove) {
        _stall = Timer(backstop, () => unawaited(_stallHard()));
      } else {
        unawaited(_stallHard());
      }
    });
  }

  void _armReconcile() {
    _reconcile?.cancel();
    var ticks = 0;
    _reconcile = Timer.periodic(_reconcileEvery, (t) {
      if (state.phase != DeviceCommandPhase.moving || ++ticks > _reconcileTicks) {
        t.cancel();
        return;
      }
      unawaited(ref.read(filterWheelProvider.notifier).refresh());
    });
  }

  /// A landing no poll saw is still a landing: ask the wheel before failing.
  Future<void> _stallHard() async {
    if (state.phase != DeviceCommandPhase.moving) return;
    try {
      await ref.read(filterWheelProvider.notifier).refresh();
    } catch (_) {
      // The read failed; the move counts as not landed.
    }
    if (!ref.mounted || state.phase != DeviceCommandPhase.moving) return;
    if (ref.read(filterWheelProvider).value?.currentSlot == state.target) {
      _land();
    } else {
      _end(DeviceCommandRefusal.didntMove);
    }
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

/// Lives while a picker shows it; a move in flight ends with the last one.
final filterWheelCommandProvider =
    NotifierProvider.autoDispose<FilterWheelCommandNotifier, DeviceCommand>(
      FilterWheelCommandNotifier.new,
    );
