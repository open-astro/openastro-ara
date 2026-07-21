import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/run_index_map.dart';
import 'sequence_editor_state.dart';
import 'sequence_list_state.dart';

/// §Run-redesign S3 — the live spotlight: which editor-tree node is executing
/// right now, resolved from the run state through [resolveSpotlight].
///
/// Null when: nothing is loaded, no run is active, the RUNNING sequence isn't
/// the LOADED one (separate documents — spotlighting the wrong tree would be
/// worse than nothing), or the index can't be mapped confidently.
///
/// A [Notifier] (not a derived Provider) because the description fallback is
/// stateful: [_lastMatchedLeaf] keeps repeated labels (ten TakeExposures)
/// advancing monotonically instead of sticking to the first match. Reset on
/// run change.
class RunSpotlightNotifier extends Notifier<RunSpotlight?> {
  int? _lastMatchedLeaf;
  String? _lastRunId;

  @override
  RunSpotlight? build() {
    final editor = ref.watch(sequenceEditorProvider);
    final run = ref.watch(sequenceRunStateProvider).value;
    if (editor == null || run == null) return null;
    if (run.sequenceId != editor.id) return null;
    final active = run.state?.isActive ?? false;
    if (!active) {
      _lastMatchedLeaf = null;
      return null;
    }
    if (run.runId != _lastRunId) {
      _lastRunId = run.runId;
      _lastMatchedLeaf = null; // a fresh run starts the monotonic match over
    }
    final spotlight = resolveSpotlight(
      editor.body,
      index: run.currentInstructionIndex,
      total: run.instructionsTotal == 0 ? null : run.instructionsTotal,
      description: run.currentInstructionDescription,
      lastMatchedLeaf: _lastMatchedLeaf,
    );
    _lastMatchedLeaf = spotlight.currentLeaf ?? _lastMatchedLeaf;
    return spotlight;
  }
}

final runSpotlightProvider =
    NotifierProvider<RunSpotlightNotifier, RunSpotlight?>(
        RunSpotlightNotifier.new);
