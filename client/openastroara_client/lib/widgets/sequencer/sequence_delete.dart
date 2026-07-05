import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/sequence_api.dart';
import '../../state/sequencer/sequence_editor_state.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../theme/ara_colors.dart';

/// Confirm-then-delete for a saved sequence — the one flow behind BOTH delete
/// surfaces (the toolbar's Delete for the open sequence and the Load dialog's
/// per-row trash). A sequence with an active run stays deletable: the confirm
/// turns into "Stop & Delete" (abort, wait for the run to end, then delete),
/// because a freshly-started test run would otherwise lock its sequence with
/// no obvious way out. On success the list refreshes, and when the deleted
/// sequence is the one open in the Run tab its selection + editor are cleared
/// so the tab doesn't keep editing a record that no longer exists.
///
/// Returns true when the sequence was deleted. [context] anchors the confirm
/// dialog and SnackBars; after each await, [ref] is only touched behind a
/// `context.mounted` check.
Future<bool> confirmAndDeleteSequence(
  BuildContext context,
  WidgetRef ref, {
  required String id,
  required String name,
}) async {
  final api = ref.read(sequenceApiProvider);
  final messenger = ScaffoldMessenger.of(context);
  if (api == null) {
    messenger.showSnackBar(const SnackBar(
        content: Text('Connect to a daemon to delete sequences.'),
        backgroundColor: AraColors.accentError));
    return false;
  }

  // Ask the daemon NOW whether a run is active, so the confirm wording — and
  // whether we abort first — matches reality rather than a stale list row.
  // Unreachable daemon → treat as idle; the delete round-trip below owns
  // surfacing the transport error.
  var active = false;
  try {
    final live = await api.getRunState(id);
    active = live?.state?.isActive ?? false;
  } catch (_) {}
  if (!context.mounted) return false;

  final displayName = name.isEmpty ? 'this sequence' : '"$name"';
  final confirmed = await showDialog<bool>(
    context: context,
    builder: (ctx) => AlertDialog(
      title: Text(active ? 'Stop the run and delete?' : 'Delete sequence?'),
      content: Text(active
          ? '$displayName is currently running. This aborts the run, then '
              'permanently deletes the sequence from the daemon.'
          : 'This permanently deletes $displayName from the daemon.'),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(ctx).pop(false),
          child: const Text('Cancel'),
        ),
        TextButton(
          style: TextButton.styleFrom(foregroundColor: AraColors.accentError),
          onPressed: () => Navigator.of(ctx).pop(true),
          child: Text(active ? 'Stop & Delete' : 'Delete'),
        ),
      ],
    ),
  );
  if (confirmed != true || !context.mounted) return false;

  try {
    if (active && !await _abortAndSettle(api, id)) {
      messenger.showSnackBar(const SnackBar(
          content: Text("Couldn't stop the run — the sequence was not "
              'deleted. Try again once it has stopped.'),
          backgroundColor: AraColors.accentError));
      return false;
    }
    // false = the daemon didn't know the id (already gone) — same outcome the
    // user wanted, so both paths clean up and report success.
    await api.deleteSequence(id);
    if (!context.mounted) return true;
    if (ref.read(selectedSequenceIdProvider) == id) {
      ref.read(selectedSequenceIdProvider.notifier).select(null);
      ref.read(sequenceEditorProvider.notifier).clear();
    }
    unawaited(ref.read(sequenceListProvider.notifier).refresh());
    messenger.showSnackBar(SnackBar(content: Text('Deleted $displayName.')));
    return true;
  } catch (e, st) {
    debugPrint('[sequencer] delete error: $e\n$st');
    messenger.showSnackBar(const SnackBar(
        content: Text("Couldn't delete that sequence. Check the connection "
            'and try again.'),
        backgroundColor: AraColors.accentError));
    return false;
  }
}

/// Abort the active run and wait (bounded) for the daemon to report it over.
/// True once no active run remains; false when it's still going after the
/// timeout — deleting then would pull the file out from under a live executor.
Future<bool> _abortAndSettle(SequenceClient api, String id) async {
  try {
    await api.abort(id);
  } catch (_) {
    // The run may have ended between the confirm and this call — the state
    // poll below is the arbiter, not the abort round-trip.
  }
  for (var i = 0; i < 20; i++) {
    final state = await api.getRunState(id);
    if (!(state?.state?.isActive ?? false)) return true;
    await Future<void>.delayed(const Duration(milliseconds: 500));
  }
  return false;
}
