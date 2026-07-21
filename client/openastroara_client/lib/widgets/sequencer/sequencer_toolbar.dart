import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/draft_sequence.dart';
import '../../models/sequence/sequence_summary.dart';
import '../../services/sequence_api.dart';
import '../../state/sequencer/draft_sequences_state.dart';
import '../../state/sequencer/sequence_editor_state.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../theme/ara_colors.dart';
import 'sequence_delete.dart';
import 'sequence_export.dart';
import 'sequence_import.dart';
import 'sequence_load_dialog.dart';
import 'sequence_new_dialog.dart';

/// §25.5.3 sequencer toolbar. New opens the §38.7 template picker; Load opens
/// the §38 sequence picker; Run / Pause / Resume / Skip / Abort drive the
/// lifecycle endpoints on the selected sequence, gated by its live run state;
/// Save / Validate / Export / Import act on the loaded body. Pause is real
/// since the daemon grew its instruction-boundary pause gate (§38): the run
/// suspends between instructions (the in-flight instruction finishes first),
/// reports Paused, and Run relabels to Resume.
class SequencerToolbar extends ConsumerWidget {
  const SequencerToolbar({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connected = ref.watch(sequenceApiProvider) != null;
    final selectedId = ref.watch(selectedSequenceIdProvider);
    final runInfo = ref.watch(sequenceRunStateProvider).asData?.value;
    final runState = runInfo?.state;

    // Resolve the picked sequence's name from the loaded list so the status line
    // can confirm WHICH sequence is selected (not just "one is"). Drafts resolve
    // from the local store instead of the daemon list.
    String? selectedName;
    if (selectedId != null) {
      if (isDraftSequenceId(selectedId)) {
        final drafts = ref.watch(draftSequencesProvider).asData?.value;
        for (final d in drafts ?? const []) {
          if (d.id == selectedId) {
            selectedName =
                '${d.name.isEmpty ? '(untitled)' : d.name} (offline draft)';
            break;
          }
        }
        selectedName ??= '(offline draft)';
      } else {
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
    }

    // A command in flight disables all controls so a double-tap can't fire two
    // concurrent lifecycle calls.
    final busy = ref.watch(sequenceCommandBusyProvider);
    // §2 offline drafts live client-side: they save locally with no daemon,
    // and never expose actions that send the draft's id to the daemon
    // (run/pause/skip/abort/delete/export) even while connected — push the
    // draft to the server first. Validate is the exception: it sends only the
    // BODY (no id), so pre-push validation of a draft works while connected.
    final isDraft = isDraftSequenceId(selectedId);
    final hasSelection = connected && selectedId != null && !busy && !isDraft;
    // Save is enabled only when the open sequence has unsaved edits. A draft
    // saves to the local store, so it needs no connection.
    final dirty = ref.watch(sequenceEditorProvider.select((s) => s?.isDirty ?? false));
    final canSave = dirty && !busy && (hasSelection || (isDraft && selectedId != null));
    // Validate works on whatever's loaded in the editor (even if not dirty).
    final editorLoaded = ref.watch(sequenceEditorProvider.select((s) => s != null));
    final canValidate = connected && editorLoaded && !busy;
    final isActive = runState?.isActive ?? false;
    final isRunning = runState == SequenceRunState.running;
    // Both paused flavors: an awaiting-user suspension (§58.12, a failed flip
    // with the mount in safe rest) resumes through the same button and command.
    final isPaused = runState?.isAnyPaused ?? false;
    final isAborting = runState == SequenceRunState.aborting;
    // Run = start when no run is active (including re-running a finished one);
    // the same button relabels to Resume while paused. Pause only while
    // running — the request is honored at the next instruction boundary, so the
    // Paused state appears once the engine actually suspends (never on the
    // mere request).
    final canRunOrResume = hasSelection && (!isActive || isPaused);
    final canPause = hasSelection && isRunning;
    // Abort while a run is active, but not when it's already aborting.
    final canAbort = hasSelection && isActive && !isAborting;
    // Skip-current shares Abort's gate: both interrupt an active run. While
    // paused nothing is running to skip, so the daemon treats it as a harmless
    // accepted no-op.
    final canSkip = hasSelection && isActive && !isAborting;

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
                  // Always enabled: offline the picker still lists the local
                  // drafts (§2); the server section shows its no-server state.
                  onPressed: () => SequenceLoadDialog.show(context),
                ),
                _ToolButton(
                  icon: Icons.file_download_outlined,
                  label: 'Import',
                  // Browse to a NINA-exported .json and import it via the §38
                  // import path (file pick → read → POST /sequences/import). The
                  // helper handles errors, lossy-translation warnings, and
                  // selecting the imported sequence. Disabled while another
                  // command is in-flight, and brackets the busy fence like Save.
                  onPressed:
                      (connected && !busy) ? () => _import(context, ref) : null,
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
                  // Enabled whenever a DAEMON sequence is selected (independent
                  // of run state — exporting is read-only). Excludes drafts:
                  // exportSequence fetches by id from the daemon, which never
                  // saw a draft: id (review #845). Local draft export is a
                  // tracked follow-up.
                  onPressed: (connected && selectedId != null && !isDraft)
                      ? () => exportSequence(context, ref,
                          id: selectedId, name: selectedName ?? selectedId)
                      : null,
                ),
                _ToolButton(
                  icon: Icons.fact_check_outlined,
                  label: 'Validate',
                  // Dry-run the working body through the daemon's schema
                  // validator and report valid / the first problem.
                  onPressed:
                      canValidate ? () => _validate(context, ref) : null,
                ),
                _ToolButton(
                  icon: Icons.delete_outline,
                  label: 'Delete',
                  // Delete the OPEN sequence right from the tab (the Load
                  // dialog's per-row trash covers the rest). The shared flow
                  // confirms, stop-and-deletes an active run, and clears the
                  // selection + editor.
                  onPressed: hasSelection
                      ? () => _delete(context, ref, selectedId, selectedName)
                      : null,
                ),
                const VerticalDivider(width: 16, indent: 8, endIndent: 8),
                // ── Lifecycle cluster (run-redesign S2): the run verbs get
                // semantic colour + weight so the tab's most important action
                // reads as one — filled green Run/Resume, amber Pause, and a
                // destructive red-outline Abort behind a confirm. Labels stay
                // identical so test finders and muscle memory survive.
                _LifecycleButton(
                  icon: Icons.play_arrow,
                  label: isPaused ? 'Resume' : 'Run',
                  kind: _LifecycleKind.primary,
                  onPressed: canRunOrResume
                      ? () => _lifecycle(context, ref,
                          (api, id) => isPaused ? api.resume(id) : api.start(id))
                      : null,
                ),
                _LifecycleButton(
                  icon: Icons.pause,
                  label: 'Pause',
                  kind: _LifecycleKind.caution,
                  onPressed: canPause
                      ? () => _lifecycle(context, ref, (api, id) => api.pause(id))
                      : null,
                ),
                _ToolButton(
                  icon: Icons.skip_next,
                  label: 'Skip',
                  // Skip the current target/item (e.g. one that's dropped below the
                  // horizon) so the run advances to the next without aborting.
                  onPressed: canSkip
                      ? () => _lifecycle(
                          context, ref, (api, id) => api.skipCurrent(id))
                      : null,
                ),
                _LifecycleButton(
                  icon: Icons.stop,
                  label: 'Abort',
                  kind: _LifecycleKind.destructive,
                  onPressed: canAbort
                      ? () => _confirmAbort(context, ref)
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
  // editor == null is reachable (state could clear between tap and read).
  if (editor == null) return;
  // Capture before the await — usable even if the widget unmounts.
  final messenger = ScaffoldMessenger.of(context);
  final busy = ref.read(sequenceCommandBusyProvider.notifier);
  final editorNotifier = ref.read(sequenceEditorProvider.notifier);

  // The exact body sent over the wire — rebaseline against THIS, not live state
  // re-read after the await, so an edit landing mid-flight stays dirty.
  final sentBody = editor.body;

  // §2 offline drafts save to the local store, no daemon involved.
  if (isDraftSequenceId(editor.id)) {
    busy.setBusy(true);
    try {
      await ref
          .read(draftSequencesProvider.notifier)
          .saveBody(editor.id, sentBody);
      editorNotifier.markSaved(sentBody);
      messenger
          .showSnackBar(const SnackBar(content: Text('Draft saved locally.')));
    } catch (e) {
      debugPrint('[sequencer] draft save error: $e');
      messenger.showSnackBar(const SnackBar(
        content: Text("Couldn't save the draft to disk."),
        backgroundColor: AraColors.accentError,
      ));
    } finally {
      busy.setBusy(false);
    }
    return;
  }

  // The api == null half is the required null-safety check for
  // api.updateSequence below (canSave already implies a non-null api when Save
  // is tappable on a daemon sequence).
  if (api == null) return;
  busy.setBusy(true);
  try {
    await api.updateSequence(editor.id, body: sentBody);
    editorNotifier.markSaved(sentBody);
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

/// Dry-run the editor's working body through the daemon's schema validator
/// (`POST /validate`) and report the result in a SnackBar — green when valid,
/// red with the first problem reason otherwise. Bracketed by the busy fence like
/// [_save]; validation never persists, so it's safe regardless of run state.
Future<void> _validate(BuildContext context, WidgetRef ref) async {
  if (ref.read(sequenceCommandBusyProvider)) return;
  final editor = ref.read(sequenceEditorProvider);
  final api = ref.read(sequenceApiProvider);
  if (editor == null || api == null) return;
  final messenger = ScaffoldMessenger.of(context);
  final busy = ref.read(sequenceCommandBusyProvider.notifier);
  busy.setBusy(true);
  try {
    final result = await api.validate(editor.body);
    messenger.showSnackBar(SnackBar(
      content: Text(result.valid
          ? 'Sequence is valid.'
          : 'Invalid: ${result.reason ?? 'failed schema validation.'}'),
      backgroundColor:
          result.valid ? AraColors.accentConnected : AraColors.accentError,
    ));
  } catch (e) {
    debugPrint('[sequencer] validate error: $e');
    messenger.showSnackBar(const SnackBar(
      content: Text("Couldn't validate the sequence. Check the connection and try again."),
      backgroundColor: AraColors.accentError,
    ));
  } finally {
    busy.setBusy(false);
  }
}

/// Delete the open sequence via the shared confirm/stop-and-delete flow,
/// bracketing the busy fence so the other toolbar commands (Run above all)
/// can't fire against a sequence that's mid-deletion.
Future<void> _delete(
    BuildContext context, WidgetRef ref, String id, String? name) async {
  if (ref.read(sequenceCommandBusyProvider)) return;
  final busy = ref.read(sequenceCommandBusyProvider.notifier);
  busy.setBusy(true);
  try {
    await confirmAndDeleteSequence(context, ref, id: id, name: name ?? '');
  } finally {
    busy.setBusy(false);
  }
}

/// Browse to a NINA `.json` and import it, bracketing the shared busy fence so
/// the toolbar's other commands disable while the file picker / import is in
/// flight (mirrors [_save]). `pickAndImportSequence` owns the file pick, decode,
/// `POST /sequences/import`, lossy-warning dialog, and selecting the result.
Future<void> _import(BuildContext context, WidgetRef ref) async {
  if (ref.read(sequenceCommandBusyProvider)) return;
  final busy = ref.read(sequenceCommandBusyProvider.notifier);
  busy.setBusy(true);
  try {
    await pickAndImportSequence(context, ref);
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
  if (selectedId != null && isDraftSequenceId(selectedId)) {
    // A draft is fully local: show it whether or not a server is connected,
    // and never a run state (drafts can't run until pushed).
    return 'Selected: ${selectedName ?? selectedId}';
  }
  if (!connected) {
    return 'Offline — planning only; drafts push to the server when connected';
  }
  if (selectedId == null) return 'Idle — Load a saved sequence';
  final name = selectedName ?? selectedId;
  final state = runInfo?.state;
  if (state == null) return 'Selected: $name';
  // "instructions", NOT "frames" (r1 on the wire rename): the counters are
  // sequence-tree leaves — slews, filter changes and autofocus steps included —
  // and the §28.2 startup notification uses the same word.
  final progress = runInfo!.instructionsTotal > 0
      ? ' — ${runInfo.instructionsCompleted}/${runInfo.instructionsTotal} instructions'
      : '';
  return '$name — ${_runStateLabel(state)}$progress';
}

String _runStateLabel(SequenceRunState s) => switch (s) {
      SequenceRunState.idle => 'Idle',
      SequenceRunState.starting => 'Starting',
      SequenceRunState.running => 'Running',
      SequenceRunState.paused => 'Paused',
      // §58.12 — the run stopped itself after an urgent failure (e.g. a failed
      // meridian flip, mount in safe rest) and won't continue until resumed.
      SequenceRunState.pausedAwaitingUser => 'Paused — needs your attention',
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

/// Aborting mid-run is destructive (the night's remaining plan dies with it)
/// — confirm before dispatching, per the S2 design.
Future<void> _confirmAbort(BuildContext context, WidgetRef ref) async {
  final confirmed = await showDialog<bool>(
    context: context,
    builder: (dialogContext) => AlertDialog(
      backgroundColor: AraColors.bgPanel,
      title: const Text('Abort this run?'),
      content: const Text(
          'The sequence stops where it is — completed frames are kept, the '
          'rest of tonight\'s plan is cancelled.'),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(dialogContext).pop(false),
          child: const Text('Keep running'),
        ),
        FilledButton(
          style: FilledButton.styleFrom(backgroundColor: AraColors.accentError),
          onPressed: () => Navigator.of(dialogContext).pop(true),
          child: const Text('Abort run'),
        ),
      ],
    ),
  );
  if (confirmed != true || !context.mounted) return;
  await _lifecycle(context, ref, (api, id) => api.abort(id));
}

enum _LifecycleKind { primary, caution, destructive }

/// Run-verb button: primary = filled green (the tab's hero action), caution =
/// amber tint, destructive = red outline. Compact to sit inline with the
/// utility _ToolButtons.
class _LifecycleButton extends StatelessWidget {
  final IconData icon;
  final String label;
  final _LifecycleKind kind;
  final VoidCallback? onPressed;
  const _LifecycleButton({
    required this.icon,
    required this.label,
    required this.kind,
    required this.onPressed,
  });

  @override
  Widget build(BuildContext context) {
    final child = Padding(
      padding: const EdgeInsets.symmetric(horizontal: 2),
      child: switch (kind) {
        _LifecycleKind.primary => FilledButton.icon(
            onPressed: onPressed,
            icon: Icon(icon, size: 16),
            label: Text(label),
            style: FilledButton.styleFrom(
              backgroundColor: AraColors.accentConnected,
              foregroundColor: Colors.black,
              disabledBackgroundColor: AraColors.bgInput,
              disabledForegroundColor: AraColors.textDisabled,
              visualDensity: VisualDensity.compact,
              padding: const EdgeInsets.symmetric(horizontal: 14),
              shape: const StadiumBorder(),
            ),
          ),
        _LifecycleKind.caution => TextButton.icon(
            onPressed: onPressed,
            icon: Icon(icon, size: 16),
            label: Text(label),
            style: TextButton.styleFrom(
              foregroundColor: AraColors.accentBusy,
              disabledForegroundColor: AraColors.textDisabled,
              visualDensity: VisualDensity.compact,
            ),
          ),
        _LifecycleKind.destructive => OutlinedButton.icon(
            onPressed: onPressed,
            icon: Icon(icon, size: 16),
            label: Text(label),
            style: OutlinedButton.styleFrom(
              foregroundColor: AraColors.accentError,
              disabledForegroundColor: AraColors.textDisabled,
              side: BorderSide(
                  color: onPressed == null
                      ? AraColors.border
                      : AraColors.accentError.withValues(alpha: 0.6)),
              visualDensity: VisualDensity.compact,
              shape: const StadiumBorder(),
            ),
          ),
      },
    );
    return child;
  }
}
