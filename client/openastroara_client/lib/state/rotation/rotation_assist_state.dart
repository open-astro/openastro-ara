import 'dart:async';
import 'dart:typed_data';

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
  final Uint8List? frame;
  final int frameSeq;
  final bool busy;
  final String? error;

  const RotationAssistLive({
    this.status = RotationAssistStatus.idle,
    this.frame,
    this.frameSeq = 0,
    this.busy = false,
    this.error,
  });

  static const idle = RotationAssistLive();

  RotationAssistLive copyWith({
    RotationAssistStatus? status,
    Uint8List? frame,
    int? frameSeq,
    bool? busy,
    String? error,
    bool clearError = false,
    bool clearFrame = false,
  }) => RotationAssistLive(
    status: status ?? this.status,
    frame: clearFrame ? null : (frame ?? this.frame),
    frameSeq: clearFrame ? 0 : (frameSeq ?? this.frameSeq),
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
/// while a readout runs (the Plan screen's Rotate camera panel starts it),
/// slowly otherwise so a readout started elsewhere shows up within seconds.
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
      if (status.hasFrame && status.frameSeq != state.frameSeq) {
        final frame = await api.fetchFrame();
        if (!ref.mounted || gen != _generation) return;
        if (frame != null) {
          state = state.copyWith(frame: frame.bytes, frameSeq: frame.seq);
        }
      }
    } catch (e) {
      if (!ref.mounted || gen != _generation) return;
      _consecutiveErrors++;
      state = state.copyWith(error: describeRequestError(e));
    }
    _schedule();
  }

  void _schedule() {
    _timer?.cancel();
    final base = state.status.busy ? _pollInterval : _idlePollInterval;
    final delay = _consecutiveErrors == 0
        ? base
        : Duration(
            milliseconds:
                (base.inMilliseconds * (1 << _consecutiveErrors.clamp(1, 4)))
                    .clamp(1000, 8000),
          );
    _timer = Timer(delay, () => unawaited(refresh()));
  }

  /// Start (or, in single mode, take one more frame of) the readout toward
  /// [positionAngleDeg]. The frame is kept: toward the same target the daemon
  /// keeps the history too, so the picture only changes when a new solve lands.
  Future<void> start({
    required double positionAngleDeg,
    double? exposureSeconds,
    String mode = RotationAssistModes.loop,
    int? binning,
  }) async {
    final api = ref.read(rotationAssistApiProvider);
    if (api == null || state.busy) return;
    state = state.copyWith(busy: true, clearError: true);
    try {
      await api.start(
        positionAngleDeg: positionAngleDeg,
        exposureSeconds: exposureSeconds,
        mode: mode,
        binning: binning,
      );
      if (!ref.mounted) return;
      state = state.copyWith(busy: false);
      await refresh();
    } catch (e) {
      if (!ref.mounted) return;
      state = state.copyWith(busy: false, error: describeRequestError(e));
    }
  }

  /// Done: the daemon stops the loop and checks the framing with one 1×1
  /// frame at the full plate-solve exposure; the status ends `confirmed`
  /// (approved) or `not_confirmed`.
  Future<void> confirm() async {
    final api = ref.read(rotationAssistApiProvider);
    if (api == null || state.busy) return;
    state = state.copyWith(busy: true, clearError: true);
    try {
      await api.confirm();
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

/// The planetarium `scopeBox` command for the readout's latest solve — where
/// the camera actually points and how it is turned, as a field the page draws
/// beside the planned framing box — or the `clear` command when no readout is
/// running / nothing has solved. Field size = frame pixels × solved pixel
/// scale. Pure — unit-tested.
Map<String, Object?> scopeBoxCommandFor(RotationAssistStatus status) {
  final latest = status.latest;
  if (!status.active ||
      latest == null ||
      latest.frameWidth <= 0 ||
      latest.frameHeight <= 0 ||
      latest.pixelScaleArcsec <= 0) {
    return const {'type': 'scopeBox', 'clear': true};
  }
  return {
    'type': 'scopeBox',
    'ra': latest.raDeg,
    'dec': latest.decDeg,
    'paDeg': latest.solvedPositionAngleDeg,
    'fovWDeg': latest.frameWidth * latest.pixelScaleArcsec / 3600,
    'fovHDeg': latest.frameHeight * latest.pixelScaleArcsec / 3600,
  };
}

/// What to do with the camera next, read from the delta trend. The app cannot
/// know which way "clockwise" turns the sky on this optical train (mirror
/// flips), so — like the guide-camera focus card — the advice is relative to
/// the user's last move: a move that shrank the delta is the right way, one
/// that grew it is the wrong way. Pure — unit-tested.
enum RotateAdvice {
  wait,
  onTarget,
  makeAMove,
  keepGoing,
  goBack,
  noSolve,
  confirming,
  confirmed,
  notConfirmed,
}

/// The loop exposure to suggest for a binning: a bin of b collects b² times
/// the light per pixel, so the full plate-solve exposure divided by b², never
/// under 0.2 s and never over the full exposure. Pure — unit-tested.
double suggestedLoopExposure(double fullExposureSeconds, int binning) {
  if (!(fullExposureSeconds > 0)) return 0;
  final b = binning.clamp(1, 16);
  final s = fullExposureSeconds / (b * b);
  final floor = fullExposureSeconds < 0.2 ? fullExposureSeconds : 0.2;
  return s.clamp(floor, fullExposureSeconds);
}

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
  // The Done check outranks the loop's advice: its verdict comes from the
  // full-resolution frame.
  if (status.confirming) {
    return const RotationHint(
      RotateAdvice.confirming,
      'Checking at full resolution',
      'One 1×1 frame at the full plate-solve exposure — hold the camera still.',
    );
  }
  if (status.state == RotationAssistStates.confirmed &&
      status.confirmation != null) {
    return RotationHint(
      RotateAdvice.confirmed,
      'Framing approved',
      'The full-resolution solve reads ${status.confirmation!.solvedPositionAngleDeg.toStringAsFixed(1)}°, within ±${status.toleranceDeg.toStringAsFixed(1)}° of the plan. Tighten the camera — you are done.',
    );
  }
  if (status.state == RotationAssistStates.notConfirmed &&
      status.confirmation != null) {
    final off = status.confirmation!.deltaDeg;
    return RotationHint(
      RotateAdvice.notConfirmed,
      'Not quite',
      'At full resolution the camera is ${off.abs().toStringAsFixed(1)}° off the plan. Keep adjusting, then press Done again.',
    );
  }
  if (status.state == RotationAssistStates.error) {
    return const RotationHint(
      RotateAdvice.noSolve,
      'No solve',
      'The field would not solve. Check the sky and the exposure, then try again.',
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
      'Tighten the camera here — the framing matches the plan.',
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
