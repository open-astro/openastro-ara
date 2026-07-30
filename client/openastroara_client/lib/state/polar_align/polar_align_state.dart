import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/polar_align.dart';
import '../../models/server.dart';
import '../../models/ws_event.dart';
import '../../services/polar_align_api.dart';
import '../saved_server_state.dart';
import '../ws/ws_providers.dart';

/// §45 `polar_align.*` WS event tokens (mirrors `WsEventCatalog` on the server).
abstract final class PolarAlignWsEvents {
  static const started = 'polar_align.started';
  static const stopped = 'polar_align.stopped';
  static const progress = 'polar_align.progress';
  static const frameComplete = 'polar_align.frame_complete';
  static const paused = 'polar_align.paused';
  static const error = 'polar_align.error';
}

/// Live routine view folded from the `polar_align.*` WS stream — what the
/// bullseye panel renders between (or instead of) status polls. [phase] tracks
/// the server state strings ([PolarAlignStates]); error fields are null until
/// the first solved live iteration.
class PolarAlignLive {
  final String phase;
  final int iteration;
  final double? altErrorArcmin;
  final double? azErrorArcmin;
  final double? totalErrorArcmin;

  /// `red` | `yellow` | `green` from the last progress event.
  final String? zone;

  /// Consecutive failed solves from the last `frame_complete` (0 after a good
  /// solve) — drives the §45.11 "no solve — check sky" retry counter.
  final int consecutiveSolveFailures;

  /// Machine-readable reason + message from a fatal `polar_align.error`.
  final String? errorReason;
  final String? errorMessage;

  const PolarAlignLive({
    this.phase = PolarAlignStates.idle,
    this.iteration = 0,
    this.altErrorArcmin,
    this.azErrorArcmin,
    this.totalErrorArcmin,
    this.zone,
    this.consecutiveSolveFailures = 0,
    this.errorReason,
    this.errorMessage,
  });

  PolarAlignLive copyWith({
    String? phase,
    int? iteration,
    double? altErrorArcmin,
    double? azErrorArcmin,
    double? totalErrorArcmin,
    String? zone,
    int? consecutiveSolveFailures,
    String? errorReason,
    String? errorMessage,
  }) =>
      PolarAlignLive(
        phase: phase ?? this.phase,
        iteration: iteration ?? this.iteration,
        altErrorArcmin: altErrorArcmin ?? this.altErrorArcmin,
        azErrorArcmin: azErrorArcmin ?? this.azErrorArcmin,
        totalErrorArcmin: totalErrorArcmin ?? this.totalErrorArcmin,
        zone: zone ?? this.zone,
        consecutiveSolveFailures:
            consecutiveSolveFailures ?? this.consecutiveSolveFailures,
        errorReason: errorReason ?? this.errorReason,
        errorMessage: errorMessage ?? this.errorMessage,
      );
}

double? _num(Map<String, dynamic> payload, String key) =>
    (payload[key] as num?)?.toDouble();

/// Pure fold of one WS event into the live routine view. Returns null when the
/// event is not a `polar_align.*` event (no state write). Exposed for unit
/// tests.
PolarAlignLive? foldPolarAlignEvent(PolarAlignLive current, WsEvent event) {
  switch (event.type) {
    case PolarAlignWsEvents.started:
      // A fresh routine: drop everything from the previous run.
      return const PolarAlignLive(phase: PolarAlignStates.seeding);
    case PolarAlignWsEvents.stopped:
      return current.copyWith(phase: PolarAlignStates.stopped);
    case PolarAlignWsEvents.paused:
      return current.copyWith(phase: PolarAlignStates.paused);
    case PolarAlignWsEvents.progress:
      return current.copyWith(
        phase: PolarAlignStates.adjusting,
        iteration: (event.payload['iteration'] as num?)?.toInt() ?? current.iteration,
        altErrorArcmin: _num(event.payload, 'altitude_error_arcmin'),
        azErrorArcmin: _num(event.payload, 'azimuth_error_arcmin'),
        totalErrorArcmin: _num(event.payload, 'total_error_arcmin'),
        zone: event.payload['zone'] is String ? event.payload['zone'] as String : current.zone,
        consecutiveSolveFailures: 0,
      );
    case PolarAlignWsEvents.frameComplete:
      final failures =
          (event.payload['consecutive_solve_failures'] as num?)?.toInt() ?? 0;
      return current.copyWith(consecutiveSolveFailures: failures);
    case PolarAlignWsEvents.error:
      return current.copyWith(
        phase: PolarAlignStates.failed,
        errorReason: event.payload['reason'] is String
            ? event.payload['reason'] as String
            : 'internal_error',
        errorMessage: event.payload['message'] is String
            ? event.payload['message'] as String
            : null,
      );
    default:
      return null;
  }
}

/// Builds a [PolarAlignClient] for a server. Overridable in tests.
final polarAlignApiFactoryProvider =
    Provider<PolarAlignClient Function(AraServer)>((ref) => PolarAlignApi.new);

/// [PolarAlignClient] bound to the active server, or `null` when none is saved.
final polarAlignApiProvider = Provider<PolarAlignClient?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(polarAlignApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// Live routine view for the active server, folded from `polar_align.*` WS
/// events. NOT autoDispose: a paused/failed routine must still be visible when
/// the user navigates back to the Imaging tab. A server switch resets it (the
/// stream provider rebuilds).
class PolarAlignLiveNotifier extends Notifier<PolarAlignLive> {
  @override
  PolarAlignLive build() {
    final stream = ref.watch(wsEventStreamProvider);
    if (stream == null) return const PolarAlignLive();
    ref.listen(wsEventsProvider, (prev, next) {
      final event = next.asData?.value;
      if (event == null) return;
      final folded = foldPolarAlignEvent(state, event);
      if (folded != null) state = folded;
    });
    return const PolarAlignLive();
  }

  /// Hydrate [state] from a REST status snapshot (on panel open / reconnect,
  /// where the WS resume may have skipped past routine events).
  void hydrateFromStatus(PolarAlignStatus status) {
    state = state.copyWith(
      phase: status.state,
      altErrorArcmin: status.altitudeAdjustmentArcmin,
      azErrorArcmin: status.azimuthAdjustmentArcmin,
      totalErrorArcmin: status.currentErrorArcmin,
    );
  }
}

final polarAlignLiveProvider =
    NotifierProvider<PolarAlignLiveNotifier, PolarAlignLive>(
        PolarAlignLiveNotifier.new);
