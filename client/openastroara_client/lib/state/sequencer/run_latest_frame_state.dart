import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../ws/ws_providers.dart';
import 'sequence_list_state.dart';

/// §Run-redesign S10 — the newest frame captured during the ACTIVE run.
class RunLatestFrame {
  final String frameId;
  final String? filterName;
  final DateTime at;
  const RunLatestFrame(
      {required this.frameId, this.filterName, required this.at});
}

/// Fed directly from the `frame.complete` WS payload (`frame_id` /
/// `filter_name`) — lower latency and simpler than deriving from the
/// library's 2s-debounced list refresh. Frames landing while no run is
/// active are ignored (the Run tab's thumbnail is run-scoped); reset when
/// the run id changes so a new run never opens showing last night's frame.
class RunLatestFrameNotifier extends Notifier<RunLatestFrame?> {
  String? _runId;

  @override
  RunLatestFrame? build() {
    ref.listen(sequenceRunStateProvider, (prev, next) {
      final id = next.value?.runId;
      if (id != _runId) {
        _runId = id;
        state = null;
      }
    });
    ref.listen(wsEventsProvider, (prev, next) {
      final event = next.asData?.value;
      if (event == null || event.type != 'frame.complete') return;
      final run = ref.read(sequenceRunStateProvider).value;
      if (run?.state?.isActive != true) return;
      final payload = event.payload;
      final frameId = payload['frame_id'];
      if (frameId is! String || frameId.isEmpty) return;
      state = RunLatestFrame(
        frameId: frameId,
        filterName: payload['filter_name'] as String?,
        at: DateTime.now().toUtc(),
      );
    });
    return null;
  }
}

final runLatestFrameProvider =
    NotifierProvider<RunLatestFrameNotifier, RunLatestFrame?>(
        RunLatestFrameNotifier.new);
