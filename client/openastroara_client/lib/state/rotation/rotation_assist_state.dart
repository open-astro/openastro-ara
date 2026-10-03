import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/rotation_assist.dart';
import '../../models/server.dart';
import '../../services/rotation_assist_api.dart';
import '../focus/autofocus_live_state.dart';
import '../saved_server_state.dart';

/// What the rotation card renders: the readout's status, a start/stop in
/// flight and the last request error.
class RotationAssistLive {
  final RotationAssistStatus status;
  final bool busy;
  final String? error;

  const RotationAssistLive({
    this.status = RotationAssistStatus.idle,
    this.busy = false,
    this.error,
  });

  static const idle = RotationAssistLive();

  RotationAssistLive copyWith({
    RotationAssistStatus? status,
    bool? busy,
    String? error,
    bool clearError = false,
  }) => RotationAssistLive(
    status: status ?? this.status,
    busy: busy ?? this.busy,
    error: clearError ? null : (error ?? this.error),
  );
}

/// Builds a [RotationAssistClient] for a server. Overridable in tests.
final rotationAssistApiFactoryProvider =
    Provider<RotationAssistClient Function(AraServer)>(
      (ref) => RotationAssistApi.new,
    );

/// Client bound to the active server, or null when none.
final rotationAssistApiProvider = Provider<RotationAssistClient?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(rotationAssistApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// Follows the daemon's by-hand rotation readout: polled on a short timer
/// while a readout runs (a run's Rotate camera by hand step starts it), slowly
/// otherwise so a readout started by the daemon shows up within seconds.
class RotationAssistNotifier extends Notifier<RotationAssistLive> {
  Timer? _timer;
  int _generation = 0;
  int _consecutiveErrors = 0;
  static const _pollInterval = Duration(seconds: 1);
  static const _idlePollInterval = Duration(seconds: 4);

  @override
  RotationAssistLive build() {
    _generation++;
    _timer?.cancel();
    _timer = null;
    ref.onDispose(() => _timer?.cancel());
    final api = ref.watch(rotationAssistApiProvider);
    if (api == null) return RotationAssistLive.idle;
    Future<void>.microtask(refresh);
    return RotationAssistLive.idle;
  }

  Future<void> refresh() async {
    final api = ref.read(rotationAssistApiProvider);
    if (api == null) return;
    final gen = _generation;
    try {
      final status = await api.status();
      if (!ref.mounted || gen != _generation) return;
      _consecutiveErrors = 0;
      state = state.copyWith(status: status, clearError: true);
    } catch (e) {
      if (!ref.mounted || gen != _generation) return;
      _consecutiveErrors++;
      state = state.copyWith(error: describeRequestError(e));
    }
    _schedule();
  }

  void _schedule() {
    _timer?.cancel();
    final base = state.status.active ? _pollInterval : _idlePollInterval;
    final delay = _consecutiveErrors == 0
        ? base
        : Duration(
            milliseconds:
                (base.inMilliseconds * (1 << _consecutiveErrors.clamp(1, 4)))
                    .clamp(1000, 8000),
          );
    _timer = Timer(delay, () => unawaited(refresh()));
  }

  Future<void> start({required double positionAngleDeg}) async {
    final api = ref.read(rotationAssistApiProvider);
    if (api == null || state.busy) return;
    state = state.copyWith(busy: true, clearError: true);
    try {
      await api.start(positionAngleDeg: positionAngleDeg);
      if (!ref.mounted) return;
      state = state.copyWith(busy: false);
      await refresh();
    } catch (e) {
      if (!ref.mounted) return;
      state = state.copyWith(busy: false, error: describeRequestError(e));
    }
  }

  Future<void> stop() async {
    final api = ref.read(rotationAssistApiProvider);
    if (api == null || state.busy) return;
    state = state.copyWith(busy: true, clearError: true);
    try {
      await api.stop();
      if (!ref.mounted) return;
      state = state.copyWith(busy: false);
      await refresh();
    } catch (e) {
      if (!ref.mounted) return;
      state = state.copyWith(busy: false, error: describeRequestError(e));
    }
  }
}

final rotationAssistProvider =
    NotifierProvider<RotationAssistNotifier, RotationAssistLive>(
      RotationAssistNotifier.new,
    );

/// What to do with the camera next, read from the delta trend. The app cannot
/// know which way "clockwise" turns the sky on this optical train (mirror
/// flips), so — like the guide-camera focus card — the advice is relative to
/// the user's last move: a move that shrank the delta is the right way, one
/// that grew it is the wrong way. Pure — unit-tested.
enum RotateAdvice { wait, onTarget, makeAMove, keepGoing, goBack, noSolve }

class RotationHint {
  final RotateAdvice advice;
  final String title;
  final String detail;
  const RotationHint(this.advice, this.title, this.detail);
}

/// A change smaller than this between two solves is solver jitter, not a move.
const double rotationMoveThresholdDeg = 0.4;

RotationHint rotationHint(RotationAssistStatus status) {
  final latest = status.latest;
  if (status.state == RotationAssistStates.error) {
    return const RotationHint(
      RotateAdvice.noSolve,
      'No solve',
      'The field would not solve. Check the sky and the exposure, then start again.',
    );
  }
  if (latest == null) {
    return const RotationHint(
      RotateAdvice.wait,
      'Solving',
      'The first frame is on its way.',
    );
  }
  final size = '${latest.deltaDeg.abs().toStringAsFixed(1)}°';
  if (status.withinTolerance || latest.deltaDeg.abs() <= status.toleranceDeg) {
    return const RotationHint(
      RotateAdvice.onTarget,
      'On target',
      'Hold the camera here and press Resume.',
    );
  }
  // The user's last move: the newest earlier sample whose delta differs by
  // more than jitter from the latest.
  final recent = status.recent;
  RotationAssistSample? before;
  for (var i = recent.length - 1; i >= 0; i--) {
    final s = recent[i];
    if (s.seq >= latest.seq) continue;
    if ((s.deltaDeg - latest.deltaDeg).abs() >= rotationMoveThresholdDeg) {
      before = s;
      break;
    }
  }
  if (before == null) {
    return RotationHint(
      RotateAdvice.makeAMove,
      'Make a move',
      'Turn the camera about $size either way, then watch which way the number goes.',
    );
  }
  // A sign flip means the turn went PAST the target: the remaining delta is
  // smaller, but the way back is the other way.
  final passed = (latest.deltaDeg > 0) != (before.deltaDeg > 0);
  if (passed) {
    return RotationHint(
      RotateAdvice.goBack,
      'Go back',
      'You went past it — back the other way, about $size.',
    );
  }
  if (latest.deltaDeg.abs() < before.deltaDeg.abs()) {
    return RotationHint(
      RotateAdvice.keepGoing,
      'Keep going',
      'Same way you just turned — about $size more.',
    );
  }
  return RotationHint(
    RotateAdvice.goBack,
    'Go back',
    'The other way from your last turn — about $size.',
  );
}
