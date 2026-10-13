import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/sequence_summary.dart';
import '../../models/tracking_calibration.dart';
import '../ws/ws_providers.dart';
import '../tracking/tracking_probe_live_state.dart';
import 'sequence_list_state.dart';

/// §Run-redesign S11 — the run's heartbeat: a quiet reverse-chronological
/// strip of events, built ONLY from signals the client already receives
/// (sequence.* state transitions, current-instruction changes,
/// frame.complete/analyzed). Autofocus/dither appear naturally as
/// instruction transitions ("▶ Run Autofocus").
enum RunTickerKind { lifecycle, instruction, frame, attention }

class RunTickerEvent {
  final DateTime at;
  final RunTickerKind kind;
  final String label;
  const RunTickerEvent(
      {required this.at, required this.kind, required this.label});
}

class RunEventTickerNotifier extends Notifier<List<RunTickerEvent>> {
  static const int cap = 50;
  String? _runId;
  SequenceRunState? _lastState;
  String? _lastInstruction;

  void _push(RunTickerKind kind, String label) {
    state = [
      RunTickerEvent(at: DateTime.now().toUtc(), kind: kind, label: label),
      ...state.take(cap - 1),
    ];
  }

  @override
  List<RunTickerEvent> build() {
    ref.listen(sequenceRunStateProvider, (prev, next) {
      final run = next.value;
      if (run == null) return;
      // A fresh run clears the buffer — the ticker is run-scoped.
      if (run.runId != _runId) {
        _runId = run.runId;
        _lastState = null;
        _lastInstruction = null;
        state = const [];
      }
      final s = run.state;
      if (s != null && s != _lastState) {
        _lastState = s;
        final label = switch (s) {
          SequenceRunState.starting || SequenceRunState.running =>
            'Run started',
          SequenceRunState.paused => 'Paused',
          SequenceRunState.pausedAwaitingUser => 'Waiting for you',
          SequenceRunState.aborting => 'Aborting…',
          SequenceRunState.stopped => 'Stopped',
          SequenceRunState.completed => 'Run complete',
          SequenceRunState.failed => 'Run failed',
          SequenceRunState.idle => null,
        };
        if (label != null) {
          // Resumed reads better than a second "Run started".
          final resumed = label == 'Run started' &&
              state.any((e) => e.label == 'Paused' || e.label == 'Waiting for you');
          _push(
              s == SequenceRunState.pausedAwaitingUser
                  ? RunTickerKind.attention
                  : RunTickerKind.lifecycle,
              resumed ? 'Resumed' : label);
        }
      }
      final instruction = run.currentInstructionDescription;
      if (instruction != null &&
          instruction.isNotEmpty &&
          instruction != _lastInstruction &&
          (s?.isActive ?? false)) {
        _lastInstruction = instruction;
        _push(RunTickerKind.instruction, '▶ $instruction');
      }
    });
    ref.listen(wsEventsProvider, (prev, next) {
      final event = next.asData?.value;
      if (event == null) return;
      if (ref.read(sequenceRunStateProvider).value?.state?.isActive != true) {
        return;
      }
      // #1264 — the Tracking Probe's rungs and verdict read like the
      // autofocus lines: one entry per rung, one for the chosen ceiling.
      if (event.type == 'tracking_probe.rung_complete') {
        final rung = TrackingProbeRung.fromJson(event.payload);
        if (rung != null) _push(RunTickerKind.instruction, describeTrackingRung(rung));
        return;
      }
      if (event.type == 'tracking_probe.completed') {
        _push(
          RunTickerKind.instruction,
          describeTrackingCeiling(foldTrackingProbeEvent(null, event)!),
        );
        return;
      }
      if (event.type != 'frame.complete') return;
      final filter = event.payload['filter_name'];
      _push(
          RunTickerKind.frame,
          filter is String && filter.isNotEmpty
              ? 'Frame captured · $filter'
              : 'Frame captured');
    });
    return const [];
  }
}

final runEventTickerProvider =
    NotifierProvider<RunEventTickerNotifier, List<RunTickerEvent>>(
        RunEventTickerNotifier.new);
