import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/sequence_summary.dart';
import '../../services/sequence_api.dart';
import '../../state/sequencer/sequence_editor_state.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../theme/ara_colors.dart';
import 'sequence_export.dart';
import 'sequence_load_dialog.dart';
import 'sequence_new_dialog.dart';

/// §25.5.3 sequencer toolbar. New opens the §38.7 template picker; Load opens
/// the §38 sequence picker; Run / Pause / Abort drive the lifecycle endpoints on
/// the selected sequence, gated by its live run state. Save / Validate stay
/// disabled pending later slices (the tree-editing + serialization path).
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

    // A command in flight disables all controls so a double-tap can't fire two
    // concurrent lifecycle calls.
    final busy = ref.watch(sequenceCommandBusyProvider);
    final hasSelection = connected && selectedId != null && !busy;
    // Save is enabled only when the open sequence has unsaved edits.
    final dirty = ref.watch(sequenceEditorProvider.select((s) => s?.isDirty ?? false));
    final canSave = hasSelection && dirty;
    final isRunning = runState == SequenceRunState.running;
    final isPaused = runState == SequenceRunState.paused;
    final isActive = runState?.isActive ?? false;
    // Run = start when there's no active run; resume when paused. Disabled only
    // while running/starting/aborting. Pause only while running; Abort while any
    // run is active. A null run state (no active run / unknown) → Run only.
    final isAborting = runState == SequenceRunState.aborting;
    final canRunOrResume = hasSelection && (!isActive || isPaused);
    final canPause = hasSelection && isRunning;
    // Abort while a run is active, but not when it's already aborting.
    final canAbort = hasSelection && isActive && !isAborting;

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
                _ToolButton(
                  icon: Icons.note_add_outlined,
                  label: 'New',
                  // Enabled once connected; opens the template picker.
                  onPressed:
                      connected ? () => SequenceNewDialog.show(context) : null,
                ),
                _ToolButton(
                  icon: Icons.folder_open_outlined,
                  label: 'Load',
                  // Enabled once a server is connected; opens the picker.
                  onPressed:
                      connected ? () => SequenceLoadDialog.show(context) : null,
                ),
                _ToolButton(
                  icon: Icons.save_outlined,
                  label: 'Save',
                  onPressed: canSave ? () => _save(context, ref) : null,
                ),
                _ToolButton(
                  icon: Icons.ios_share,
                  label: 'Export',
                  // Export the selected sequence to a NINA-compatible .json.
                  // Enabled whenever a sequence is selected (independent of run
                  // state — exporting is read-only).
                  onPressed: (connected && selectedId != null)
                      ? () => exportSequence(context, ref,
                          id: selectedId, name: selectedName ?? selectedId)
                      : null,
                ),
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
  // Re-entrancy guard: ignore a second command while one is already running.
  if (ref.read(sequenceCommandBusyProvider)) return;
  final id = ref.read(selectedSequenceIdProvider);
  final api = ref.read(sequenceApiProvider);
  if (id == null || api == null) return;
  // Capture refs/messenger before the await — usable even if the widget unmounts.
  final messenger = ScaffoldMessenger.of(context);
  final busy = ref.read(sequenceCommandBusyProvider.notifier);
  final runState = ref.read(sequenceRunStateProvider.notifier);

  busy.setBusy(true);
  try {
    try {
      await op(api, id);
    } on DioException catch (e) {
      final code = e.response?.statusCode;
      // 409 is an expected business error (the run already moved past this
      // action), so give it a clearer message than a raw status code.
      messenger.showSnackBar(SnackBar(
        content: Text(code == 409
            ? "Command not valid in the sequence's current state."
            : 'Sequence command failed (${code ?? e.message ?? 'network error'}).'),
        backgroundColor: AraColors.accentError,
      ));
    } catch (e) {
      // e.g. a FormatException if the 202 lacked an operation_id — still surface
      // it rather than let it propagate as an unhandled exception.
      messenger.showSnackBar(const SnackBar(
        content: Text('Sequence command failed.'),
        backgroundColor: AraColors.accentError,
      ));
    }
    // Re-read run state BEFORE re-enabling the controls (the busy flag drops in
    // the finally below), so a fast re-click can't act on the stale pre-command
    // state. refresh() swallows its own errors, so this won't throw.
    await runState.refresh();
  } finally {
    busy.setBusy(false);
  }
}

/// PATCH the editor's working body back to the daemon (verbatim, so it
/// round-trips), then rebaseline dirty-tracking. A 422 surfaces the validator's
/// rejection; any other failure is a generic transport error.
Future<void> _save(BuildContext context, WidgetRef ref) async {
  if (ref.read(sequenceCommandBusyProvider)) return;
  final editor = ref.read(sequenceEditorProvider);
  final api = ref.read(sequenceApiProvider);
  if (editor == null || api == null) return;
  // Capture before the await — usable even if the widget unmounts.
  final messenger = ScaffoldMessenger.of(context);
  final busy = ref.read(sequenceCommandBusyProvider.notifier);

  // The exact body sent over the wire — rebaseline against THIS, not live state
  // re-read after the await, so an edit landing mid-flight stays dirty.
  final sentBody = editor.body;
  busy.setBusy(true);
  try {
    await api.updateSequence(editor.id, body: sentBody);
    ref.read(sequenceEditorProvider.notifier).markSaved(sentBody);
    messenger.showSnackBar(const SnackBar(content: Text('Sequence saved.')));
  } on DioException catch (e) {
    final code = e.response?.statusCode;
    messenger.showSnackBar(SnackBar(
      content: Text(code == 422
          ? 'Save rejected: ${_validationMessage(e) ?? 'the sequence is invalid.'}'
          : "Couldn't save the sequence. Check the connection and try again."),
      backgroundColor: AraColors.accentError,
    ));
  } catch (e) {
    // Don't let a programming error masquerade as a network failure in dev.
    debugPrint('[sequencer] unexpected save error: $e');
    messenger.showSnackBar(const SnackBar(
      content: Text("Couldn't save the sequence. Check the connection and try again."),
      backgroundColor: AraColors.accentError,
    ));
  } finally {
    busy.setBusy(false);
  }
}

/// Best-effort extraction of the validator's message from a 422 body
/// (`{detail|message|error: "..."}` or a bare string); null if none readable.
/// Capped so a pathologically long server message can't blow out the SnackBar.
String? _validationMessage(DioException e) {
  final data = e.response?.data;
  String? msg;
  if (data is String && data.trim().isNotEmpty) {
    msg = data.trim();
  } else if (data is Map) {
    for (final key in const ['detail', 'message', 'error']) {
      final v = data[key];
      if (v is String && v.trim().isNotEmpty) {
        msg = v.trim();
        break;
      }
    }
  }
  if (msg == null) return null;
  return msg.length > 200 ? '${msg.substring(0, 200)}…' : msg;
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
