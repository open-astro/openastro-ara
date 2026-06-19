import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/sequencer/sequence_editor_state.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../theme/ara_colors.dart';
import '../../widgets/sequencer/sequence_editor_tree.dart';
import '../../widgets/sequencer/sequence_field_editor.dart';
import '../../widgets/sequencer/sequencer_palette.dart';
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
  Future<void> _loadSelectedBody(String id) async {
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

    return Column(
      children: [
        const SequencerToolbar(),
        Expanded(
          child: Row(
            children: [
              _pane(flex: 2, border: true, child: const SequencerPalette()),
              _pane(flex: 3, border: true, child: const SequenceEditorTree()),
              const Expanded(flex: 2, child: SequenceFieldEditor()),
            ],
          ),
        ),
      ],
    );
  }

  Widget _pane({required int flex, required bool border, required Widget child}) =>
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
