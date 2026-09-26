import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/draft_sequence.dart';
import '../../models/sequence/sequence_summary.dart';
import '../../services/sequence_api.dart';
import '../../state/sequencer/draft_sequences_state.dart';
import '../../state/sequencer/sequence_editor_state.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../state/app_shell_state.dart';
import '../../state/settings/settings_nav.dart' show kSetupTabIndex;
import '../../state/setup/setup_readiness.dart';
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

    // Every action as data, so the toolbar can decide per-width which ones
    // sit inline and which fold into the overflow menu (see _ToolbarLayout).
    final utilities = <_ToolAction>[
      _ToolAction(
        icon: Icons.note_add_outlined,
        label: 'New',
        // Enabled once connected; opens the template picker.
        onPressed: connected ? () => SequenceNewDialog.show(context) : null,
      ),
      _ToolAction(
        icon: Icons.folder_open_outlined,
        label: 'Load',
        // Always enabled: offline the picker still lists the local
        // drafts (§2); the server section shows its no-server state.
        onPressed: () => SequenceLoadDialog.show(context),
      ),
      _ToolAction(
        icon: Icons.file_download_outlined,
        label: 'Import',
        // Browse to a NINA-exported .json and import it via the §38
        // import path (file pick → read → POST /sequences/import). The
        // helper handles errors, lossy-translation warnings, and
        // selecting the imported sequence. Disabled while another
        // command is in-flight, and brackets the busy fence like Save.
        onPressed: (connected && !busy) ? () => _import(context, ref) : null,
      ),
      _ToolAction(
        icon: Icons.save_outlined,
        label: 'Save',
        onPressed: canSave ? () => _save(context, ref) : null,
      ),
      _ToolAction(
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
      _ToolAction(
        icon: Icons.fact_check_outlined,
        label: 'Validate',
        // Dry-run the working body through the daemon's schema
        // validator and report valid / the first problem.
        onPressed: canValidate ? () => _validate(context, ref) : null,
      ),
      _ToolAction(
        icon: Icons.delete_outline,
        label: 'Delete',
        // Delete the OPEN sequence right from the tab (the Load
        // dialog's per-row trash covers the rest). The shared flow
        // confirms, stop-and-deletes an active run, and clears the
        // selection + editor.
        // An open offline DRAFT is deletable too (it lives on this device,
        // no daemon involved) — before, Delete was dead for drafts and the
        // only way out was the Load dialog's per-row trash.
        onPressed: hasSelection
            ? () => _delete(context, ref, selectedId, selectedName)
            : (isDraft && selectedId != null && !busy)
                ? () => _deleteDraft(context, ref, selectedId, selectedName)
                : null,
      ),
    ];

    // ── Lifecycle cluster (run-redesign S2): the run verbs get
    // semantic colour + weight so the tab's most important action
    // reads as one — filled green Run/Resume, amber Pause, and a
    // destructive red-outline Abort behind a confirm. Labels stay
    // identical so test finders and muscle memory survive.
    final lifecycle = <_ToolAction>[
      _ToolAction(
        icon: Icons.play_arrow,
        label: isPaused ? 'Resume' : 'Run',
        kind: _LifecycleKind.primary,
        onPressed: canRunOrResume
            ? () => isPaused
                // §38.10 — resuming offers the pointing/focus
                // refinement choice before imaging continues.
                ? promptAndResumeSequence(context, ref)
                : preflightAndRunSequence(context, ref)
            : null,
      ),
      _ToolAction(
        icon: Icons.pause,
        label: 'Pause',
        kind: _LifecycleKind.caution,
        onPressed: canPause
            ? () => runSequenceLifecycle(
                context, ref, (api, id) => api.pause(id))
            : null,
      ),
      _ToolAction(
        icon: Icons.skip_next,
        label: 'Skip',
        // Skip the current target/item (e.g. one that's dropped below the
        // horizon) so the run advances to the next without aborting.
        onPressed: canSkip
            ? () => runSequenceLifecycle(
                context, ref, (api, id) => api.skipCurrent(id))
            : null,
      ),
      _ToolAction(
        icon: Icons.stop,
        label: 'Abort',
        kind: _LifecycleKind.destructive,
        onPressed: canAbort ? () => _confirmAbort(context, ref) : null,
      ),
    ];

    return Container(
      height: 44,
      padding: const EdgeInsets.symmetric(horizontal: 8),
      decoration: const BoxDecoration(
        color: AraColors.bgPanel,
        border: Border(bottom: BorderSide(color: AraColors.border)),
      ),
      child: LayoutBuilder(
        builder: (context, constraints) => _ToolbarLayout(
          width: constraints.maxWidth,
          utilities: utilities,
          lifecycle: lifecycle,
          status: _statusLine(connected, selectedId, selectedName, runInfo),
        ),
      ),
    );
  }
}

/// One toolbar action, kept as data so the same button can render inline or
/// as an overflow-menu row depending on the width the toolbar gets.
class _ToolAction {
  final IconData icon;
  final String label;
  final VoidCallback? onPressed;
  /// Null for a utility button; set for the run verbs.
  final _LifecycleKind? kind;
  const _ToolAction({
    required this.icon,
    required this.label,
    required this.onPressed,
    this.kind,
  });
}

/// Width-aware arrangement of the toolbar. The old horizontal scroll view hid
/// whatever didn't fit — on a phone or a half-width window Delete, Validate
/// and Export simply weren't there, with no hint that the row scrolled. Now:
///
/// * the lifecycle cluster (Run / Pause / Skip / Abort) is ALWAYS on screen —
///   labelled when there's room, icon-only with tooltips when not;
/// * utility buttons fill from the left in their usual order, and whichever
///   don't fit fold into a trailing "More" (⋯) menu, so every action stays
///   one tap away at any width;
/// * the status line takes what's left and simply disappears on the
///   narrowest layouts (the run band above the tree carries the run state).
///
/// Widths are estimated from the label text (see [_estimateWidth]) rather
/// than measured, so the fit is greedy-but-safe: a small slack per button
/// keeps the row from ever overflowing.
class _ToolbarLayout extends StatelessWidget {
  final double width;
  final List<_ToolAction> utilities;
  final List<_ToolAction> lifecycle;
  final String status;
  const _ToolbarLayout({
    required this.width,
    required this.utilities,
    required this.lifecycle,
    required this.status,
  });

  /// Nominal width of the "More" (⋯) overflow button.
  static const double _moreWidth = 48;
  /// Width of the divider between the utility and lifecycle clusters.
  static const double _dividerWidth = 16;
  /// The status line only earns space once the buttons have theirs; below
  /// this reserve it's dropped rather than squeezed to an unreadable stub.
  static const double _statusReserve = 140;

  @override
  Widget build(BuildContext context) {
    final style = Theme.of(context).textTheme.labelLarge;
    final scaler = MediaQuery.textScalerOf(context);
    double labelWidth(String label) => _estimateWidth(label, style, scaler);

    // Utility TextButton.icon: 12 leading + 16 icon + 8 gap + text + 16
    // trailing (52px measured) plus slack.
    double utilityWidth(_ToolAction a) => labelWidth(a.label) + 52 + 4;
    // Lifecycle buttons, measured in a widget test: the filled / text
    // variants and the plain Skip come to label + 52 (14+14 or 12+16 padding,
    // 16 icon, 8 gap); the outlined Abort is label + 64 (its border adds a
    // 1px stroke plus the outlined default padding). Both carry ±2 outer
    // padding; +4 slack on top so a font-hinting rounding can't overflow.
    double lifecycleWidth(_ToolAction a) =>
        labelWidth(a.label) +
        (a.kind == _LifecycleKind.destructive ? 64 : 52) +
        4 +
        4;
    // Icon-only lifecycle: a 40×40 IconButton with the same ±2 outer padding.
    const double lifecycleIconWidth = 44;

    final labelledLifecycle =
        lifecycle.fold<double>(0, (sum, a) => sum + lifecycleWidth(a));
    final compactLifecycle = lifecycleIconWidth * lifecycle.length;

    // Step 1 — can the lifecycle cluster keep its labels? It needs room for
    // itself plus at least the More button (the utilities' minimum footprint).
    final lifecycleLabelled =
        width >= labelledLifecycle + _dividerWidth + _moreWidth;
    final lifecycleSpan =
        lifecycleLabelled ? labelledLifecycle : compactLifecycle;

    // Step 2 — greedily place utilities from the left. Reserve the More
    // button's width unless EVERY utility fits without it.
    final available = width - lifecycleSpan - _dividerWidth;
    final allWidth =
        utilities.fold<double>(0, (sum, a) => sum + utilityWidth(a));
    final List<_ToolAction> inline;
    final List<_ToolAction> overflow;
    if (allWidth <= available) {
      inline = utilities;
      overflow = const [];
    } else {
      var used = _moreWidth;
      var count = 0;
      for (final a in utilities) {
        final w = utilityWidth(a);
        if (used + w > available) break;
        used += w;
        count++;
      }
      inline = utilities.sublist(0, count);
      overflow = utilities.sublist(count);
    }

    // Step 3 — the status line gets the remainder, if it's worth showing.
    final usedByButtons =
        inline.fold<double>(0, (s, a) => s + utilityWidth(a)) +
            (overflow.isEmpty ? 0 : _moreWidth) +
            _dividerWidth +
            lifecycleSpan;
    final showStatus = width - usedByButtons >= _statusReserve;

    return Row(
      children: [
        for (final a in inline)
          _ToolButton(icon: a.icon, label: a.label, onPressed: a.onPressed),
        if (overflow.isNotEmpty) _MoreMenu(actions: overflow),
        const VerticalDivider(width: _dividerWidth, indent: 8, endIndent: 8),
        for (final a in lifecycle)
          if (a.kind != null)
            _LifecycleButton(
              icon: a.icon,
              label: a.label,
              kind: a.kind!,
              onPressed: a.onPressed,
              compact: !lifecycleLabelled,
            )
          else if (lifecycleLabelled)
            _ToolButton(icon: a.icon, label: a.label, onPressed: a.onPressed)
          else
            _CompactToolButton(
                icon: a.icon, label: a.label, onPressed: a.onPressed),
        if (showStatus)
          Expanded(
            child: Text(
              status,
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                    color: AraColors.textDisabled,
                  ),
              overflow: TextOverflow.ellipsis,
              maxLines: 1,
              textAlign: TextAlign.right,
            ),
          )
        else
          const Spacer(),
      ],
    );
  }

  static double _estimateWidth(
      String label, TextStyle? style, TextScaler scaler) {
    final painter = TextPainter(
      text: TextSpan(text: label, style: style),
      textDirection: TextDirection.ltr,
      textScaler: scaler,
      maxLines: 1,
    )..layout();
    final w = painter.width;
    painter.dispose();
    return w;
  }
}

/// The ⋯ overflow menu holding the utility actions that didn't fit inline.
/// Rows keep the button's icon + label and its enabled state, so a disabled
/// Delete reads the same way in the menu as it would on the bar.
class _MoreMenu extends StatelessWidget {
  final List<_ToolAction> actions;
  const _MoreMenu({required this.actions});

  @override
  Widget build(BuildContext context) {
    return PopupMenuButton<_ToolAction>(
      tooltip: 'More actions',
      icon: const Icon(Icons.more_horiz, size: 20, color: AraColors.textPrimary),
      color: AraColors.bgPanel,
      onSelected: (a) => a.onPressed?.call(),
      itemBuilder: (context) => [
        for (final a in actions)
          PopupMenuItem<_ToolAction>(
            value: a,
            enabled: a.onPressed != null,
            child: Row(children: [
              Icon(a.icon,
                  size: 18,
                  color: a.onPressed != null
                      ? AraColors.textPrimary
                      : AraColors.textDisabled),
              const SizedBox(width: 12),
              Text(a.label),
            ]),
          ),
      ],
    );
  }
}

/// Icon-only utility button for the narrowest layouts (tooltip carries the
/// label so the action is still discoverable).
class _CompactToolButton extends StatelessWidget {
  final IconData icon;
  final String label;
  final VoidCallback? onPressed;
  const _CompactToolButton({
    required this.icon,
    required this.label,
    required this.onPressed,
  });

  @override
  Widget build(BuildContext context) {
    // Same ±2 outer padding as the compact _LifecycleButton so Skip sits on
    // the same rhythm as its neighbours in the icon-only cluster.
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 2),
      child: IconButton(
        onPressed: onPressed,
        tooltip: label,
        icon: Icon(icon, size: 18),
        visualDensity: VisualDensity.compact,
        style: IconButton.styleFrom(
          foregroundColor: AraColors.textPrimary,
          disabledForegroundColor: AraColors.textDisabled,
        ),
      ),
    );
  }
}

/// Start the selected sequence, with the soft pre-flight gate (§25 flow
/// redesign): when polar alignment hasn't reached the green zone this
/// session, confirm before starting — the gate SUGGESTS, it never blocks
/// (planning-only rigs, permanent piers and daytime tests all run fine
/// unaligned). "Go to Setup" jumps to the checklist instead. Public: the
/// tab's keyboard shortcuts drive the same path as the Run button.
Future<void> preflightAndRunSequence(BuildContext context, WidgetRef ref) async {
  final aligned =
      ref.read(setupPolarAlignStateProvider) == SetupStepState.done;
  if (!aligned) {
    final choice = await showDialog<_PreflightChoice>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        backgroundColor: AraColors.bgPanel,
        title: const Text('Not polar aligned — run anyway?'),
        content: const Text(
            'Polar alignment hasn\'t been completed this session. On a '
            'freshly set up mount, unguided pointing and field rotation will '
            'suffer. If the mount is already aligned (permanent pier), just '
            'run.'),
        actions: [
          TextButton(
            onPressed: () =>
                Navigator.of(dialogContext).pop(_PreflightChoice.cancel),
            child: const Text('Cancel'),
          ),
          TextButton(
            onPressed: () =>
                Navigator.of(dialogContext).pop(_PreflightChoice.goToSetup),
            child: const Text('Go to Setup'),
          ),
          FilledButton(
            onPressed: () =>
                Navigator.of(dialogContext).pop(_PreflightChoice.runAnyway),
            child: const Text('Run anyway'),
          ),
        ],
      ),
    );
    if (choice == null || choice == _PreflightChoice.cancel) return;
    if (choice == _PreflightChoice.goToSetup) {
      ref.read(selectedTabIndexProvider.notifier).select(kSetupTabIndex);
      return;
    }
    if (!context.mounted) return;
  }
  await runSequenceLifecycle(context, ref, (api, id) => api.start(id));
}

enum _PreflightChoice { cancel, goToSetup, runAnyway }

/// Run a lifecycle transition on the selected sequence, surface a transport
/// failure as a SnackBar, then re-read the run state so the buttons re-gate.
/// Public: the tab-level keyboard shortcuts (Space, R — run-redesign S12)
/// drive the same fenced path as the toolbar buttons.
Future<void> runSequenceLifecycle(
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

/// Dry-run the editor's working body through Ara's schema validator
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

/// Confirm-then-delete for the open offline draft: removes the local file and
/// clears the selection + editor so the Run tab isn't editing a ghost.
Future<void> _deleteDraft(
    BuildContext context, WidgetRef ref, String id, String? name) async {
  if (ref.read(sequenceCommandBusyProvider)) return;
  final messenger = ScaffoldMessenger.of(context);
  final display = (name == null || name.isEmpty) ? '(untitled draft)' : name;
  final ok = await showDialog<bool>(
    context: context,
    builder: (ctx) => AlertDialog(
      title: const Text('Delete draft?'),
      content: Text('"$display" will be removed from this device. '
          "This can't be undone."),
      actions: [
        TextButton(
            onPressed: () => Navigator.of(ctx).pop(false),
            child: const Text('Cancel')),
        TextButton(
          style: TextButton.styleFrom(foregroundColor: AraColors.accentError),
          onPressed: () => Navigator.of(ctx).pop(true),
          child: const Text('Delete'),
        ),
      ],
    ),
  );
  if (ok != true || !context.mounted) return;
  final container = ProviderScope.containerOf(context, listen: false);
  final busy = container.read(sequenceCommandBusyProvider.notifier);
  busy.setBusy(true);
  try {
    await container.read(draftSequencesProvider.notifier).delete(id);
    if (container.read(selectedSequenceIdProvider) == id) {
      container.read(selectedSequenceIdProvider.notifier).select(null);
    }
    if (container.read(sequenceEditorProvider)?.id == id) {
      container.read(sequenceEditorProvider.notifier).clear();
    }
  } catch (e) {
    debugPrint('[sequencer] draft delete failed: $e');
    messenger.showSnackBar(const SnackBar(
      content: Text("Couldn't delete the draft."),
      backgroundColor: AraColors.accentError,
    ));
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

/// §38.10 — the resume choice: while the rig sat paused on this target,
/// pointing (and focus) may have drifted. Offers [Resume & re-center]
/// (default), [Re-center + refocus], and [Just resume]; dismissing the dialog
/// cancels (no resume — the user backed out). Public: the tab's keyboard
/// shortcuts (⌘R / Space on a paused run) drive the same path as the button.
/// The daemon skips the refinement gracefully when the run moved past the
/// paused target or no plate solver is configured, so offering it is always safe.
Future<void> promptAndResumeSequence(BuildContext context, WidgetRef ref) async {
  final choice = await showDialog<(bool recenter, bool refocus)>(
    context: context,
    builder: (dialogContext) => AlertDialog(
      backgroundColor: AraColors.bgPanel,
      title: const Text('Resume imaging?'),
      content: const Text(
          'The target can be re-centered with a plate solve before imaging '
          'continues — recommended after a pause. You can also refocus first.'),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(dialogContext).pop((false, false)),
          child: const Text('Just resume'),
        ),
        TextButton(
          onPressed: () => Navigator.of(dialogContext).pop((true, true)),
          child: const Text('Re-center + refocus'),
        ),
        FilledButton(
          onPressed: () => Navigator.of(dialogContext).pop((true, false)),
          child: const Text('Resume & re-center'),
        ),
      ],
    ),
  );
  if (choice == null || !context.mounted) return; // dismissed — user backed out
  await runSequenceLifecycle(
      context,
      ref,
      (api, id) =>
          api.resume(id, recenter: choice.$1, refocus: choice.$2));
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
  await runSequenceLifecycle(context, ref, (api, id) => api.abort(id));
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
  /// Icon-only (tooltip carries the label) for the narrowest toolbar widths.
  final bool compact;
  const _LifecycleButton({
    required this.icon,
    required this.label,
    required this.kind,
    required this.onPressed,
    this.compact = false,
  });

  @override
  Widget build(BuildContext context) {
    if (compact) {
      final enabled = onPressed != null;
      final (Color fg, Color? bg) = switch (kind) {
        _LifecycleKind.primary => (Colors.black, AraColors.accentConnected),
        _LifecycleKind.caution => (AraColors.accentBusy, null),
        _LifecycleKind.destructive => (AraColors.accentError, null),
      };
      return Padding(
        padding: const EdgeInsets.symmetric(horizontal: 2),
        child: IconButton(
          onPressed: onPressed,
          tooltip: label,
          icon: Icon(icon, size: 18),
          visualDensity: VisualDensity.compact,
          style: IconButton.styleFrom(
            foregroundColor: fg,
            backgroundColor: bg,
            disabledForegroundColor: AraColors.textDisabled,
            disabledBackgroundColor: bg == null ? null : AraColors.bgInput,
            side: kind == _LifecycleKind.destructive
                ? BorderSide(
                    color: enabled
                        ? AraColors.accentError.withValues(alpha: 0.6)
                        : AraColors.border)
                : null,
          ),
        ),
      );
    }
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
