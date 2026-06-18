import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/sequence_summary.dart';
import '../../services/sequence_api.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../theme/ara_colors.dart';
import 'sequence_load_dialog.dart';

/// §25.5.3 sequencer toolbar. Load opens the §38 sequence picker; Run / Pause /
/// Abort drive the lifecycle endpoints on the selected sequence, gated by its
/// live run state. Save / Validate stay disabled pending later slices (the
/// editor body→tree load + validation path).
class SequencerToolbar extends ConsumerWidget {
  const SequencerToolbar({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connected = ref.watch(sequenceApiProvider) != null;
    final selectedId = ref.watch(selectedSequenceIdProvider);
    final runInfo = ref.watch(sequenceRunStateProvider).asData?.value;
    final runState = runInfo?.state;

    // Resolve the picked sequence's name from the loaded list so the status line
    // can confirm WHICH sequence is selected (not just "one is").
    String? selectedName;
    if (selectedId != null) {
      final list = ref.watch(sequenceListProvider).asData?.value;
      if (list != null) {
        for (final s in list) {
          if (s.id == selectedId) {
            selectedName = s.name.isEmpty ? '(untitled)' : s.name;
            break;
          }
        }
      }
    }

    final hasSelection = connected && selectedId != null;
    final isRunning = runState == SequenceRunState.running;
    final isPaused = runState == SequenceRunState.paused;
    final isActive = runState?.isActive ?? false;
    // Run = start when there's no active run; resume when paused. Disabled only
    // while running/starting/aborting. Pause only while running; Abort while any
    // run is active. A null run state (no active run / unknown) → Run only.
    final canRunOrResume = hasSelection && (!isActive || isPaused);
    final canPause = hasSelection && isRunning;
    final canAbort = hasSelection && isActive;

    return Container(
      height: 44,
      padding: const EdgeInsets.symmetric(horizontal: 8),
      decoration: const BoxDecoration(
        color: AraColors.bgPanel,
        border: Border(bottom: BorderSide(color: AraColors.border)),
      ),
      child: Row(
        children: [
          // Buttons in a horizontally-scrollable row so the toolbar stays
          // usable on narrow window widths (≤ ~700px). The status line
          // gets the remaining flexible space on the right.
          Flexible(
            child: SingleChildScrollView(
              scrollDirection: Axis.horizontal,
              child: Row(children: [
                const _ToolButton(
                    icon: Icons.note_add_outlined, label: 'New', onPressed: null),
                _ToolButton(
                  icon: Icons.folder_open_outlined,
                  label: 'Load',
                  // Enabled once a server is connected; opens the picker.
                  onPressed:
                      connected ? () => SequenceLoadDialog.show(context) : null,
                ),
                const _ToolButton(
                    icon: Icons.save_outlined, label: 'Save', onPressed: null),
                const _ToolButton(
                    icon: Icons.fact_check_outlined,
                    label: 'Validate',
                    onPressed: null),
                const VerticalDivider(width: 16, indent: 8, endIndent: 8),
                _ToolButton(
                  icon: Icons.play_arrow,
                  label: isPaused ? 'Resume' : 'Run',
                  onPressed: canRunOrResume
                      ? () => _lifecycle(context, ref,
                          (api, id) => isPaused ? api.resume(id) : api.start(id))
                      : null,
                ),
                _ToolButton(
                  icon: Icons.pause,
                  label: 'Pause',
                  onPressed: canPause
                      ? () => _lifecycle(context, ref, (api, id) => api.pause(id))
                      : null,
                ),
                _ToolButton(
                  icon: Icons.stop,
                  label: 'Abort',
                  onPressed: canAbort
                      ? () => _lifecycle(context, ref, (api, id) => api.abort(id))
                      : null,
                ),
              ]),
            ),
          ),
          Expanded(
            child: Text(
              _statusLine(connected, selectedId, selectedName, runInfo),
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                    color: AraColors.textDisabled,
                  ),
              overflow: TextOverflow.ellipsis,
              maxLines: 1,
              textAlign: TextAlign.right,
            ),
          ),
        ],
      ),
    );
  }
}

/// Run a lifecycle transition on the selected sequence, surface a transport
/// failure as a SnackBar, then re-read the run state so the buttons re-gate.
Future<void> _lifecycle(
  BuildContext context,
  WidgetRef ref,
  Future<String> Function(SequenceClient api, String id) op,
) async {
  final id = ref.read(selectedSequenceIdProvider);
  final api = ref.read(sequenceApiProvider);
  if (id == null || api == null) return;
  final messenger = ScaffoldMessenger.of(context); // captured before the await
  try {
    await op(api, id);
  } on DioException catch (e) {
    // 409 = illegal transition for the current state; others = transport.
    messenger.showSnackBar(SnackBar(
      content: Text(
          'Sequence command failed (${e.response?.statusCode ?? e.message ?? 'network error'}).'),
      backgroundColor: AraColors.accentError,
    ));
  } catch (e) {
    // e.g. a FormatException if the 202 lacked an operation_id — still surface it
    // rather than let it propagate as an unhandled exception.
    messenger.showSnackBar(const SnackBar(
      content: Text('Sequence command failed.'),
      backgroundColor: AraColors.accentError,
    ));
  }
  // Guard ref use across the async gap — the widget may have been disposed (the
  // user navigated away mid-call), where ref.read would throw.
  if (!context.mounted) return;
  // Re-read run state regardless — the daemon may have advanced it even on error.
  await ref.read(sequenceRunStateProvider.notifier).refresh();
}

String _statusLine(bool connected, String? selectedId, String? selectedName,
    SequenceRunStateInfo? runInfo) {
  if (!connected) return 'Idle — connect to a server to load saved sequences';
  if (selectedId == null) return 'Idle — Load a saved sequence';
  final name = selectedName ?? selectedId;
  final state = runInfo?.state;
  if (state == null) return 'Selected: $name';
  final frames = runInfo!.framesTotal > 0
      ? ' — ${runInfo.framesCompleted}/${runInfo.framesTotal} frames'
      : '';
  return '$name — ${_runStateLabel(state)}$frames';
}

String _runStateLabel(SequenceRunState s) => switch (s) {
      SequenceRunState.idle => 'Idle',
      SequenceRunState.starting => 'Starting',
      SequenceRunState.running => 'Running',
      SequenceRunState.paused => 'Paused',
      SequenceRunState.aborting => 'Aborting',
      SequenceRunState.stopped => 'Stopped',
      SequenceRunState.completed => 'Completed',
      SequenceRunState.failed => 'Failed',
    };

class _ToolButton extends StatelessWidget {
  final IconData icon;
  final String label;
  final VoidCallback? onPressed;
  const _ToolButton({
    required this.icon,
    required this.label,
    required this.onPressed,
  });

  @override
  Widget build(BuildContext context) {
    return TextButton.icon(
      onPressed: onPressed,
      icon: Icon(icon, size: 16),
      label: Text(label),
      style: TextButton.styleFrom(
        foregroundColor: AraColors.textPrimary,
        disabledForegroundColor: AraColors.textDisabled,
      ),
    );
  }
}
