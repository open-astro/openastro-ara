import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/tracking_calibration.dart';
import '../../models/ws_event.dart';
import '../sequencer/sequence_list_state.dart';
import '../ws/ws_providers.dart';

/// #1264 — the Tracking Probe as it runs: the planned ladder, each rung's
/// verdict as it lands, and the chosen ceiling. Fed straight from the
/// `tracking_probe.*` WebSocket events (there is no state endpoint to poll; the
/// final record is `GET /tracking/calibration`).
class TrackingProbeLive {
  final String? target;
  final List<double> ladder;
  final double? priorSeconds;
  final List<TrackingProbeRung> rungs;
  final bool done;
  final double? ceilingSeconds;
  final String? source;
  final String? summary;

  const TrackingProbeLive({
    this.target,
    this.ladder = const [],
    this.priorSeconds,
    this.rungs = const [],
    this.done = false,
    this.ceilingSeconds,
    this.source,
    this.summary,
  });

  TrackingProbeLive copyWith({
    List<TrackingProbeRung>? rungs,
    bool? done,
    double? ceilingSeconds,
    String? source,
    String? summary,
  }) => TrackingProbeLive(
    target: target,
    ladder: ladder,
    priorSeconds: priorSeconds,
    rungs: rungs ?? this.rungs,
    done: done ?? this.done,
    ceilingSeconds: ceilingSeconds ?? this.ceilingSeconds,
    source: source ?? this.source,
    summary: summary ?? this.summary,
  );
}

/// Pure reducer — unit-tested. Unrelated events return [current] unchanged; a
/// `started` begins a fresh probe (the ladder restarts, so an old readout never
/// mixes with a new one); a `rung_complete` appends; a `completed` closes it.
TrackingProbeLive? foldTrackingProbeEvent(
  TrackingProbeLive? current,
  WsEvent event,
) {
  final p = event.payload;
  switch (event.type) {
    case 'tracking_probe.started':
      final ladder = p['rungs'];
      return TrackingProbeLive(
        target: p['target'] is String ? p['target'] as String : null,
        ladder: ladder is List
            ? ladder.whereType<num>().map((n) => n.toDouble()).toList()
            : const [],
        priorSeconds: (p['prior_seconds'] as num?)?.toDouble(),
      );
    case 'tracking_probe.rung_complete':
      final rung = TrackingProbeRung.fromJson(p);
      if (rung == null) return current;
      final base = current ?? const TrackingProbeLive();
      return base.copyWith(rungs: [...base.rungs, rung]);
    case 'tracking_probe.completed':
      final base = current ?? const TrackingProbeLive();
      return base.copyWith(
        done: true,
        ceilingSeconds: (p['ceiling_seconds'] as num?)?.toDouble(),
        source: p['source'] is String ? p['source'] as String : null,
        summary: p['summary'] is String ? p['summary'] as String : null,
      );
    default:
      return current;
  }
}

/// Run-scoped: cleared when the run id changes, so the Run tab never opens on
/// last night's ladder.
class TrackingProbeLiveNotifier extends Notifier<TrackingProbeLive?> {
  String? _runId;

  @override
  TrackingProbeLive? build() {
    ref.listen(sequenceRunStateProvider, (prev, next) {
      final id = next.value?.runId;
      if (id != _runId) {
        _runId = id;
        state = null;
      }
    });
    ref.listen(wsEventsProvider, (prev, next) {
      final event = next.asData?.value;
      if (event == null || !event.type.startsWith('tracking_probe.')) return;
      state = foldTrackingProbeEvent(state, event);
    });
    return null;
  }
}

final trackingProbeLiveProvider =
    NotifierProvider<TrackingProbeLiveNotifier, TrackingProbeLive?>(
      TrackingProbeLiveNotifier.new,
    );

/// One line for the ticker / band: "Tracking probe 30 s · ecc 0.31 · FWHM 2.4 px · pass".
String describeTrackingRung(TrackingProbeRung r) {
  final parts = <String>[
    'Tracking probe ${formatExposureSeconds(r.exposureSeconds)}',
    if (r.eccentricity != null) 'ecc ${r.eccentricity!.toStringAsFixed(2)}',
    if (r.fwhmPx != null) 'FWHM ${r.fwhmPx!.toStringAsFixed(1)} px',
    !r.usable
        ? 'too few stars'
        : r.passed
        ? 'pass'
        : 'trailed',
  ];
  return parts.join(' · ');
}

/// The band's verdict line once the probe is done.
String describeTrackingCeiling(TrackingProbeLive live) {
  final c = live.ceilingSeconds;
  if (c == null) {
    return 'Tracking probe: ${live.summary ?? 'no ceiling found'}';
  }
  final how = live.source == 'prior' ? 'prior kept' : 'measured';
  return 'Unguided ceiling ${formatExposureSeconds(c)} ($how)';
}
