import 'dart:convert';
import 'dart:typed_data';

import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/sequence_summary.dart';
import '../../services/profile_share_file.dart' show shareFileName;
import '../../state/sequencer/draft_sequences_state.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../theme/ara_colors.dart';
import 'sequence_delete.dart';
import 'sequence_import.dart';

/// §38 "Load sequence" picker. Lists the active server's saved sequences
/// (newest-first) from [sequenceListProvider]; tapping one records it as the
/// [selectedSequenceIdProvider] and pops. Handles loading / error / no-server /
/// empty states. The actual body→tree population is a later slice — this slice
/// only selects which sequence is loaded.
class SequenceLoadDialog extends ConsumerWidget {
  const SequenceLoadDialog({super.key});

  /// Show the dialog. Returns the selected sequence id, or null if dismissed.
  static Future<String?> show(BuildContext context) =>
      showDialog<String>(context: context, builder: (_) => const SequenceLoadDialog());

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(sequenceListProvider);
    return AlertDialog(
      title: const Text('Load sequence'),
      content: SizedBox(
        width: 420,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            // §2 offline planning — local drafts, shown above the server list
            // (present with or without a connection; hidden when empty).
            const _DraftsSection(),
            _serverList(context, ref, async),
          ],
        ),
      ),
      actions: [
        // Import a NINA sequence file; on success it selects the new sequence
        // (loaded into the tree) and closes this picker. On a cancel/error the
        // dialog stays open so the user can retry or pick from the list.
        const _ImportNinaButton(),
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
      ],
    );
  }

  Widget _serverList(BuildContext context, WidgetRef ref,
      AsyncValue<List<SequenceListItem>?> async) {
    return async.when(
          loading: () => const SizedBox(
            height: 120,
            child: Center(child: CircularProgressIndicator()),
          ),
          error: (e, st) {
            // Keep the raw error in the logs (diagnosable in dev); show the user
            // a clean, actionable message.
            debugPrint('[sequencer] sequence list load failed: $e\n$st');
            return const _Message(
                'Couldn\'t load sequences from the server. Check the connection and try again.');
          },
          data: (list) {
            if (list == null) {
              return const _Message('Connect to a daemon to load saved sequences.');
            }
            if (list.isEmpty) {
              return const _Message('No saved sequences yet.');
            }
            // Cap the height so a server with dozens of sequences can't grow the
            // dialog past the screen; the list scrolls within the cap.
            return ConstrainedBox(
              constraints: const BoxConstraints(maxHeight: 360),
              child: ListView.builder(
              // No shrinkWrap: the ConstrainedBox bounds the viewport, so the
              // builder can lazily build only visible rows.
              itemCount: list.length,
              itemBuilder: (context, i) {
                final s = list[i];
                return ListTile(
                  contentPadding: EdgeInsets.zero,
                  title: Text(s.name.isEmpty ? '(untitled)' : s.name),
                  subtitle: Text(
                    '${s.instructionCount} instruction(s) · ${s.targetCount} target(s)',
                    style: const TextStyle(color: AraColors.textSecondary),
                  ),
                  trailing: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      _RunStateBadge(s.currentRunState),
                      // §70.5: export this sequence to a shareable .araseq.json
                      // file. Doesn't select/pop — the user may export several.
                      _ExportSequenceButton(id: s.id, name: s.name),
                      // Delete (with confirm). Doesn't pop either — the list
                      // refreshes in place so several can be cleaned up in one go.
                      _DeleteSequenceButton(id: s.id, name: s.name),
                    ],
                  ),
                  onTap: () {
                    ref.read(selectedSequenceIdProvider.notifier).select(s.id);
                    Navigator.of(context).pop(s.id);
                  },
                );
              },
              ),
            );
          },
        );
  }
}

/// §2 offline drafts — the client-managed drafts, listed above the server's
/// sequences. Tapping one loads it into the editor (works offline). Each row
/// offers Push-to-server (enabled only while connected; creates the real
/// sequence, deletes the local copy, and selects the new id) and Delete.
class _DraftsSection extends ConsumerStatefulWidget {
  const _DraftsSection();

  @override
  ConsumerState<_DraftsSection> createState() => _DraftsSectionState();
}

class _DraftsSectionState extends ConsumerState<_DraftsSection> {
  /// One busy fence for ALL draft rows: push deletes the local file and
  /// refreshes state, so a second overlapping push/delete (double-tap or a tap
  /// on another row mid-flight) could act on a stale list (review #845).
  bool _busy = false;

  @override
  Widget build(BuildContext context) {
    final drafts = ref.watch(draftSequencesProvider).asData?.value;
    if (drafts == null || drafts.isEmpty) return const SizedBox.shrink();
    final connected = ref.watch(sequenceApiProvider) != null;
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const Padding(
          padding: EdgeInsets.only(bottom: 4),
          child: Text('Offline drafts',
              style: TextStyle(
                  color: AraColors.textSecondary,
                  fontWeight: FontWeight.bold)),
        ),
        ConstrainedBox(
          constraints: const BoxConstraints(maxHeight: 180),
          child: ListView.builder(
            shrinkWrap: true,
            itemCount: drafts.length,
            itemBuilder: (context, i) {
              final d = drafts[i];
              return ListTile(
                contentPadding: EdgeInsets.zero,
                leading:
                    const Icon(Icons.cloud_off_outlined, size: 18),
                title: Text(d.name.isEmpty ? '(untitled draft)' : d.name),
                trailing: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    IconButton(
                      icon: const Icon(Icons.cloud_upload_outlined, size: 18),
                      tooltip: connected
                          ? 'Push to server'
                          : 'Connect to a server to push this draft',
                      onPressed: (connected && !_busy)
                          ? () => _push(context, d.id)
                          : null,
                    ),
                    IconButton(
                      icon: const Icon(Icons.delete_outline, size: 18),
                      tooltip: 'Delete draft',
                      onPressed:
                          _busy ? null : () => _delete(context, d.id, d.name),
                    ),
                  ],
                ),
                onTap: () {
                  ref.read(selectedSequenceIdProvider.notifier).select(d.id);
                  Navigator.of(context).pop(d.id);
                },
              );
            },
          ),
        ),
        const Divider(color: AraColors.border),
      ],
    );
  }

  Future<void> _push(BuildContext context, String id) async {
    if (_busy) return;
    setState(() => _busy = true);
    final messenger = ScaffoldMessenger.of(context);
    try {
      // Was the pushed draft the one open in the editor? Read BEFORE the push
      // (the await is a gap the selection could change under).
      final wasOpen = ref.read(selectedSequenceIdProvider) == id;
      final newId =
          await ref.read(draftSequencesProvider.notifier).push(id);
      // Swap the editor to the pushed copy ONLY when this draft was already
      // the open one — pushing an unrelated draft must not clobber whatever
      // (possibly dirty) sequence the user is editing (review #845).
      if (wasOpen && context.mounted) {
        ref.read(selectedSequenceIdProvider.notifier).select(newId);
      }
      messenger.showSnackBar(
          const SnackBar(content: Text('Draft pushed to the server.')));
    } catch (e) {
      debugPrint('[sequencer] draft push failed: $e');
      messenger.showSnackBar(const SnackBar(
        content: Text(
            "Couldn't push the draft. Check the connection and try again."),
        backgroundColor: AraColors.accentError,
      ));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _delete(BuildContext context, String id, String name) async {
    if (_busy) return;
    final messenger = ScaffoldMessenger.of(context);
    final ok = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Delete draft?'),
        content: Text(
            '"${name.isEmpty ? '(untitled draft)' : name}" will be removed from this device. This can\'t be undone.'),
        actions: [
          TextButton(
              onPressed: () => Navigator.of(ctx).pop(false),
              child: const Text('Cancel')),
          FilledButton(
            style:
                FilledButton.styleFrom(backgroundColor: AraColors.accentError),
            onPressed: () => Navigator.of(ctx).pop(true),
            child: const Text('Delete'),
          ),
        ],
      ),
    );
    if (ok != true || !context.mounted) return;
    setState(() => _busy = true);
    try {
      await ref.read(draftSequencesProvider.notifier).delete(id);
    } catch (e) {
      debugPrint('[sequencer] draft delete failed: $e');
      messenger.showSnackBar(const SnackBar(
        content: Text("Couldn't delete the draft."),
        backgroundColor: AraColors.accentError,
      ));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }
}

/// The "Import NINA…" action. Stateful so it can disable itself for the whole
/// async flow (picker → upload → warnings dialog) — without the busy guard a
/// second tap on a slow connection would open a second picker and fire a second
/// concurrent import. On a successful import it pops the Load dialog.
class _ImportNinaButton extends ConsumerStatefulWidget {
  const _ImportNinaButton();

  @override
  ConsumerState<_ImportNinaButton> createState() => _ImportNinaButtonState();
}

class _ImportNinaButtonState extends ConsumerState<_ImportNinaButton> {
  bool _busy = false;

  Future<void> _run() async {
    final navigator = Navigator.of(context);
    setState(() => _busy = true);
    var popped = false;
    try {
      final imported = await pickAndImportSequence(context, ref);
      // Gate on this State's `mounted`, not navigator.mounted: the dialog is
      // barrierDismissible, so the user can tap it away mid-import. Once the
      // dialog route is gone this State is disposed (mounted == false), but the
      // app-level navigator is still alive — popping it here would pop the wrong
      // route (the tab beneath). `mounted` false ⇒ skip the pop.
      if (imported && mounted) {
        navigator.pop();
        popped = true;
      }
    } catch (e, st) {
      // The inner flow already shows a SnackBar for expected failures; this is a
      // backstop so an unexpected error (e.g. a platform file-picker exception)
      // is logged rather than vanishing into Flutter's global handler.
      debugPrint('[sequencer] import error: $e\n$st');
    } finally {
      // Only clear the busy flag when we kept the dialog open; if we popped, the
      // dialog is gone and there's no state to reset (explicit flag rather than
      // relying on `mounted` flipping as a side-effect of pop()).
      if (!popped && mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return TextButton.icon(
      icon: _busy
          ? const SizedBox(
              width: 18,
              height: 18,
              child: CircularProgressIndicator(strokeWidth: 2),
            )
          : const Icon(Icons.upload_file, size: 18),
      label: const Text('Import NINA…'),
      onPressed: _busy ? null : _run,
    );
  }
}

/// §70.5 per-sequence "Export…" action. Stateful so it disables itself across
/// the whole async flow (share-export round-trip → save picker) — without the
/// busy guard a second tap could fire a second export / open a second picker.
/// Mirrors the profile export flow (`profile_management_screen`): fetch the
/// shareable manifest, pretty-print it, and let the OS save dialog write the
/// `.araseq.json` file. Never pops the Load dialog (the user may export several).
class _ExportSequenceButton extends ConsumerStatefulWidget {
  const _ExportSequenceButton({required this.id, required this.name});
  final String id;
  final String name;

  @override
  ConsumerState<_ExportSequenceButton> createState() => _ExportSequenceButtonState();
}

class _ExportSequenceButtonState extends ConsumerState<_ExportSequenceButton> {
  bool _busy = false;

  Future<void> _run() async {
    final api = ref.read(sequenceApiProvider);
    final messenger = ScaffoldMessenger.of(context);
    if (api == null) {
      messenger.showSnackBar(const SnackBar(
          content: Text('Connect to a daemon to export sequences.'),
          backgroundColor: AraColors.accentError));
      return;
    }
    setState(() => _busy = true);
    try {
      final share = await api.exportShare(widget.id);
      // Pretty-print so the shared file is human-readable; saveFile writes the
      // bytes itself on the desktop targets and returns the path (null on cancel).
      final bytes = Uint8List.fromList(
          utf8.encode(const JsonEncoder.withIndent('  ').convert(share.manifest)));
      final saved = await FilePicker.saveFile(
        dialogTitle: 'Save sequence share',
        fileName: shareFileName(
            share.sequenceName.isEmpty ? widget.name : share.sequenceName,
            fallbackBase: 'sequence',
            extension: 'araseq.json'),
        // Pre-filter the OS dialog to .json so the user can't accidentally save
        // over an unrelated file / drop the extension. The suggested name ends
        // in `.araseq.json`, which satisfies the `json` filter.
        type: FileType.custom,
        allowedExtensions: const ['json'],
        bytes: bytes,
      );
      if (saved == null || !mounted) return; // cancelled / dialog gone
      messenger.showSnackBar(SnackBar(
          content: Text('Exported "${widget.name.isEmpty ? 'sequence' : widget.name}" '
              '— share this file; the recipient imports it.')));
    } catch (e, st) {
      debugPrint('[sequencer] export error: $e\n$st');
      messenger.showSnackBar(const SnackBar(
          content: Text("Couldn't export that sequence."),
          backgroundColor: AraColors.accentError));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return IconButton(
      icon: _busy
          ? const SizedBox(
              width: 18, height: 18, child: CircularProgressIndicator(strokeWidth: 2))
          : const Icon(Icons.save_alt, size: 18),
      tooltip: 'Export…',
      onPressed: _busy ? null : _run,
    );
  }
}

/// Per-sequence trash action — [confirmAndDeleteSequence] owns the whole flow
/// (live run-state probe, Stop & Delete for an active run, selection/editor
/// cleanup, list refresh). Stateful only for the re-entrancy disable; no busy
/// spinner, because the busy window is mostly spent under the confirm dialog's
/// modal barrier where a spinner is invisible (and its endless animation would
/// wedge pumpAndSettle in tests).
class _DeleteSequenceButton extends ConsumerStatefulWidget {
  const _DeleteSequenceButton({required this.id, required this.name});
  final String id;
  final String name;

  @override
  ConsumerState<_DeleteSequenceButton> createState() =>
      _DeleteSequenceButtonState();
}

class _DeleteSequenceButtonState extends ConsumerState<_DeleteSequenceButton> {
  bool _busy = false;

  Future<void> _run() async {
    setState(() => _busy = true);
    try {
      await confirmAndDeleteSequence(context, ref,
          id: widget.id, name: widget.name);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return IconButton(
      icon: const Icon(Icons.delete_outline, size: 18),
      tooltip: 'Delete…',
      onPressed: _busy ? null : _run,
    );
  }
}

/// Small coloured chip for a sequence's current run state; nothing for idle/none.
class _RunStateBadge extends StatelessWidget {
  const _RunStateBadge(this.state);
  final SequenceRunState? state;

  @override
  Widget build(BuildContext context) {
    final s = state;
    if (s == null || s == SequenceRunState.idle) return const SizedBox.shrink();
    // Exhaustive (no wildcard) so a new SequenceRunState forces a compile error
    // here rather than silently defaulting — matching SequenceRunState.isActive.
    final color = switch (s) {
      SequenceRunState.running || SequenceRunState.starting => AraColors.accentBusy,
      SequenceRunState.paused => AraColors.accentInfo,
      // §58.12 awaiting-user reads as urgent (the rig needs a human), not as a
      // leisurely operator pause — error red, even though the run is resumable.
      SequenceRunState.pausedAwaitingUser ||
      SequenceRunState.failed ||
      SequenceRunState.aborting =>
        AraColors.accentError,
      SequenceRunState.stopped || SequenceRunState.completed => AraColors.textSecondary,
      SequenceRunState.idle => AraColors.textSecondary, // unreachable (early-returned above)
    };
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.18),
        borderRadius: BorderRadius.circular(4),
        border: Border.all(color: color),
      ),
      // The single-word states read fine as their enum names; the §58.12
      // multi-word state needs a human label ("pausedAwaitingUser" is not UI).
      child: Text(s == SequenceRunState.pausedAwaitingUser ? 'needs attention' : s.name,
          style: TextStyle(color: color, fontSize: 11, fontWeight: FontWeight.w600)),
    );
  }
}

class _Message extends StatelessWidget {
  const _Message(this.text);
  final String text;
  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 24),
        child: Text(text, style: const TextStyle(color: AraColors.textSecondary)),
      );
}
