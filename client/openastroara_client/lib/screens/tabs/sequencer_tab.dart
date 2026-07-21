import 'dart:async';

import 'package:flutter/services.dart';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/draft_sequence.dart';
import '../../models/sequence/sequence_summary.dart';
import '../../state/sequencer/draft_sequences_state.dart';
import '../../state/sequencer/sequence_editor_state.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../theme/ara_colors.dart';
import '../../widgets/sequencer/sequence_editor_tree.dart';
import '../../widgets/sequencer/sequence_field_editor.dart';
import '../../widgets/sequencer/sequencer_palette.dart';
import '../../widgets/sequencer/run_completion_sheet.dart';
import '../../widgets/sequencer/run_dashboard_band.dart';
import '../../widgets/sequencer/sequence_load_dialog.dart';
import '../../widgets/sequencer/sequence_new_dialog.dart';
import '../../state/app_shell_state.dart';
import '../../state/settings/settings_nav.dart';
import '../../theme/ara_metrics.dart';
import '../../widgets/sequencer/sequencer_toolbar.dart';

/// Sequencer tab per playbook §25.5.3 / §38: toolbar + the NINA-style edit
/// surface — instruction palette, the raw-body tree, and the selected node's
/// field editor. Selecting a sequence loads its body into the editor
/// ([sequenceEditorProvider]); Run/Pause/Abort live on the toolbar.
class SequencerTab extends ConsumerStatefulWidget {
  const SequencerTab({super.key});

  @override
  ConsumerState<SequencerTab> createState() => _SequencerTabState();
}

class _SequencerTabState extends ConsumerState<SequencerTab> {
  // The sequence id whose body is currently loaded into the editor — guards
  // against re-fetching the same sequence on rebuilds and lets us detect an
  // already-selected sequence on (re)mount. Reset to null on a failed load so
  // the same sequence can be retried.
  String? _loadedId;

  @override
  void initState() {
    super.initState();
    // ref.listen only fires on *changes*, so an already-selected sequence (e.g.
    // the tab was navigated away from and back) wouldn't load. Pick it up here.
    final id = ref.read(selectedSequenceIdProvider);
    if (id != null) _load(id);
  }

  void _load(String id) {
    _loadedId = id;
    unawaited(_loadSelectedBody(id));
  }

  /// Fetch the sequence's raw body and load it into the editor. Best-effort: a
  /// missing client / transport failure surfaces a SnackBar, leaves the current
  /// editor in place, and clears [_loadedId] so the sequence can be retried.
  /// A `draft:` id loads from the local draft store instead of the daemon (§2
  /// offline planning), so drafts open with or without a server.
  Future<void> _loadSelectedBody(String id) async {
    if (isDraftSequenceId(id)) {
      await _loadDraftBody(id);
      return;
    }
    final api = ref.read(sequenceApiProvider);
    if (api == null) {
      _onLoadFailed(id);
      return;
    }
    try {
      final detail = await api.getSequenceDetail(id);
      if (!mounted) return;
      // Drop a stale response: a newer selection landed while this was in flight,
      // so loading now would clobber the editor with the wrong (older) sequence.
      if (ref.read(selectedSequenceIdProvider) != id) return;
      ref.read(sequenceEditorProvider.notifier).load(detail);
    } catch (e) {
      debugPrint('[sequencer] failed to load sequence body for $id: $e');
      _onLoadFailed(id);
    }
  }

  Future<void> _loadDraftBody(String id) async {
    try {
      final drafts = await ref.read(draftSequencesProvider.future);
      if (!mounted) return;
      if (ref.read(selectedSequenceIdProvider) != id) return; // stale — see above
      final draft = drafts.where((d) => d.id == id).firstOrNull;
      if (draft == null) {
        _onLoadFailed(id);
        return;
      }
      ref.read(sequenceEditorProvider.notifier).load(SequenceDetail(
            id: draft.id,
            name: draft.name,
            body: draft.body,
          ));
    } catch (e) {
      debugPrint('[sequencer] failed to load draft body for $id: $e');
      _onLoadFailed(id);
    }
  }

  void _onLoadFailed(String id) {
    if (!mounted) return;
    // Ignore a superseded load entirely: if `id` is no longer the one we last
    // kicked off, the user has already moved to another sequence (which may have
    // loaded fine), so neither clear their selection nor nag them with an error.
    // (The success path uses the provider value for the same race; here the field
    // is authoritative because a failed load never reaches the provider read.)
    if (_loadedId != id) return;
    _loadedId = null;
    // Clear the selection: nothing actually loaded, so it shouldn't claim a
    // sequence. This also makes retry work — re-picking the same sequence then
    // emits a fresh change (null → id), which a same-value `select(id)` alone
    // would not (Riverpod skips equal values).
    ref.read(selectedSequenceIdProvider.notifier).select(null);
    ScaffoldMessenger.maybeOf(context)?.showSnackBar(const SnackBar(
      content: Text(
          "Couldn't load the sequence. Check the connection and try again."),
      backgroundColor: AraColors.accentError,
    ));
  }

  @override
  Widget build(BuildContext context) {
    // When the picked sequence changes, fetch its body into the editor.
    // Deselecting (next == null) intentionally leaves the current editor in
    // place — this slice is load-only; clearing the editor is a later slice.
    ref.listen<String?>(selectedSequenceIdProvider, (prev, next) {
      if (next != null && next != _loadedId) _load(next);
    });
    // §Run-redesign S6 — a finished run is an event: on the active→terminal
    // transition, present the completion sheet (replacing the old silent
    // status-line flip). Abort is excluded — the user just confirmed it.
    ref.listen(sequenceRunStateProvider, (prev, next) {
      final was = prev?.value?.state;
      final now = next.value?.state;
      if (was == null || now == null || was == now) return;
      final wasActive = was.isActive && now != SequenceRunState.aborting;
      final terminal = now == SequenceRunState.completed ||
          now == SequenceRunState.failed;
      if (wasActive && terminal && mounted) {
        RunCompletionSheet.show(context, next.value!);
      }
    });
    final hasSequence = ref.watch(sequenceEditorProvider) != null;
    // §Run-redesign S13 — live mood: while a run is active the palette and
    // inspector recede (dim, still interactive) so the tree + dashboard carry
    // the stage. Reduced-motion honours the platform setting.
    final runActive =
        ref.watch(sequenceRunStateProvider).value?.state?.isActive ?? false;
    final motionOff = MediaQuery.of(context).disableAnimations;
    final dimDuration =
        motionOff ? Duration.zero : const Duration(milliseconds: 250);
    Widget dimmed(Widget child) => AnimatedOpacity(
        opacity: runActive ? 0.55 : 1.0, duration: dimDuration, child: child);

    // §Run-redesign S12 — editor keyboard: ⌘Z/⌘⇧Z undo-redo, Delete removes
    // the selected node, ↑/↓ walk the tree, ⌘R runs/resumes and Space
    // pauses/resumes — the lifecycle keys drive the toolbar's OWN fenced path
    // (runSequenceLifecycle: busy re-entrancy guard + state re-read), gated by
    // the same conditions as its buttons. Abort stays mouse-only: destructive,
    // behind its confirm dialog. All guarded to skip while a text field has
    // focus so typing in the inspector never fires tree/run actions.
    return CallbackShortcuts(
      bindings: {
        const SingleActivator(LogicalKeyboardKey.arrowUp): () {
          if (_textFieldFocused) return;
          ref.read(sequenceEditorProvider.notifier).selectAdjacent(next: false);
        },
        const SingleActivator(LogicalKeyboardKey.arrowDown): () {
          if (_textFieldFocused) return;
          ref.read(sequenceEditorProvider.notifier).selectAdjacent(next: true);
        },
        const SingleActivator(LogicalKeyboardKey.keyR, meta: true):
            _runOrResumeKey,
        const SingleActivator(LogicalKeyboardKey.keyR, control: true):
            _runOrResumeKey,
        const SingleActivator(LogicalKeyboardKey.space): _spaceKey,
        const SingleActivator(LogicalKeyboardKey.keyZ, meta: true): () {
          if (_textFieldFocused) return;
          ref.read(sequenceEditorProvider.notifier).undo();
        },
        const SingleActivator(LogicalKeyboardKey.keyZ, meta: true, shift: true):
            () {
          if (_textFieldFocused) return;
          ref.read(sequenceEditorProvider.notifier).redo();
        },
        const SingleActivator(LogicalKeyboardKey.keyZ, control: true): () {
          if (_textFieldFocused) return;
          ref.read(sequenceEditorProvider.notifier).undo();
        },
        const SingleActivator(LogicalKeyboardKey.keyZ,
            control: true, shift: true): () {
          if (_textFieldFocused) return;
          ref.read(sequenceEditorProvider.notifier).redo();
        },
        const SingleActivator(LogicalKeyboardKey.delete): _deleteSelected,
        const SingleActivator(LogicalKeyboardKey.backspace): _deleteSelected,
      },
      child: Focus(
        autofocus: true,
        child: Column(
      children: [
        const SequencerToolbar(),
        // §Run-redesign S5 — the live dashboard band renders only while a run
        // is active (SizedBox.shrink otherwise), so compose-mood layout and
        // its tests are untouched.
        const RunDashboardBand(),
        Expanded(
          // §Run-redesign S6 — with nothing loaded, the tab invites instead of
          // presenting three empty grey panes.
          child: !hasSequence
              ? const _ZeroState()
              : Row(
                  children: [
                    _pane(flex: 2, child: dimmed(const SequencerPalette())),
                    _pane(flex: 3, child: const SequenceEditorTree()),
                    // Rightmost pane: same bg as the others, no trailing divider.
                    _pane(
                        flex: 2,
                        border: false,
                        child: dimmed(const SequenceFieldEditor())),
                  ],
                ),
        ),
      ],
        ),
      ),
    );
  }

  /// True while any editable text field owns focus — keyboard editing must
  /// win over tree shortcuts.
  bool get _textFieldFocused =>
      FocusManager.instance.primaryFocus?.context?.widget is EditableText;

  void _deleteSelected() {
    if (_textFieldFocused) return;
    final editor = ref.read(sequenceEditorProvider);
    final path = editor?.selectedPath;
    if (path == null || path.isEmpty) return;
    ref.read(sequenceEditorProvider.notifier).removeNode(path);
  }

  /// The toolbar buttons' gate, for the lifecycle keys: a real (non-draft)
  /// sequence selected on a connected daemon with no command in flight.
  bool get _lifecycleKeysArmed {
    final id = ref.read(selectedSequenceIdProvider);
    return ref.read(sequenceApiProvider) != null &&
        id != null &&
        !isDraftSequenceId(id) &&
        !ref.read(sequenceCommandBusyProvider);
  }

  /// ⌘R — Run when idle/finished, Resume when paused. Mirrors the toolbar's
  /// canRunOrResume gate exactly; a mid-run press is a silent no-op.
  void _runOrResumeKey() {
    if (_textFieldFocused || !_lifecycleKeysArmed) return;
    final state = ref.read(sequenceRunStateProvider).value?.state;
    final isPaused = state?.isAnyPaused ?? false;
    if ((state?.isActive ?? false) && !isPaused) return;
    unawaited(runSequenceLifecycle(
        context, ref, (api, id) => isPaused ? api.resume(id) : api.start(id)));
  }

  /// Space — Pause while running, Resume while paused; anything else no-ops
  /// (so Space can't START a run by surprise — starting is ⌘R's deliberate
  /// gesture). Skipped while a button owns focus: Space there activates the
  /// focused button, and firing both would double-command the daemon.
  void _spaceKey() {
    if (_textFieldFocused || _buttonFocused || !_lifecycleKeysArmed) return;
    final state = ref.read(sequenceRunStateProvider).value?.state;
    if (state == SequenceRunState.running) {
      unawaited(runSequenceLifecycle(context, ref, (api, id) => api.pause(id)));
    } else if (state?.isAnyPaused ?? false) {
      unawaited(
          runSequenceLifecycle(context, ref, (api, id) => api.resume(id)));
    }
  }

  /// True while a button-like control owns focus (Space would activate it).
  /// Material buttons attach their FocusNode inside InkWell's internal Focus
  /// widget, so the focused context's OWN widget is never the button — walk
  /// the ancestors to find one (review #864).
  bool get _buttonFocused {
    final ctx = FocusManager.instance.primaryFocus?.context;
    if (ctx == null) return false;
    var found = false;
    ctx.visitAncestorElements((e) {
      if (e.widget is ButtonStyleButton || e.widget is IconButton) {
        found = true;
        return false;
      }
      return true;
    });
    return found;
  }

  // An editor pane: shared bg, with a trailing divider before the next pane
  // ([border]) for all but the rightmost.
  Widget _pane({required int flex, required Widget child, bool border = true}) =>
      Expanded(
        flex: flex,
        child: Container(
          decoration: BoxDecoration(
            color: AraColors.bgPrimary,
            border: border
                ? const Border(right: BorderSide(color: AraColors.border))
                : null,
          ),
          child: child,
        ),
      );
}


/// Inviting zero state: what this tab is for + the three ways in. Replaces the
/// old bare "No sequence loaded" center-text (which still guards the tree for
/// the loaded-but-cleared edge).
class _ZeroState extends ConsumerWidget {
  const _ZeroState();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    return Container(
      color: AraColors.bgPrimary,
      child: Center(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const Icon(Icons.movie_filter_outlined,
                size: 44, color: AraColors.textDisabled),
            const SizedBox(height: AraSpace.s16),
            const Text('Ready to build tonight\'s run', style: AraText.title),
            const SizedBox(height: AraSpace.s8),
            const SizedBox(
              width: 380,
              child: Text(
                'Compose a sequence from instructions, or let the planner '
                'pick tonight\'s best targets and times for you.',
                textAlign: TextAlign.center,
                style: AraText.caption,
              ),
            ),
            const SizedBox(height: AraSpace.s24),
            Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                FilledButton.icon(
                  icon: const Icon(Icons.auto_awesome, size: 16),
                  label: const Text('Plan tonight'),
                  onPressed: () => ref
                      .read(selectedTabIndexProvider.notifier)
                      .select(kPlanningTabIndex),
                ),
                const SizedBox(width: AraSpace.s12),
                OutlinedButton.icon(
                  icon: const Icon(Icons.note_add_outlined, size: 16),
                  label: const Text('New sequence'),
                  onPressed: () => SequenceNewDialog.show(context),
                ),
                const SizedBox(width: AraSpace.s12),
                OutlinedButton.icon(
                  icon: const Icon(Icons.folder_open_outlined, size: 16),
                  label: const Text('Load'),
                  onPressed: () => SequenceLoadDialog.show(context),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}
