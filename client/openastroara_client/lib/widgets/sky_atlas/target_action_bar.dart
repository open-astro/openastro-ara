import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/equipment_device_status.dart';
import '../../models/sequence/imaging_run_body.dart';
import '../../state/equipment/mount_state.dart';
import '../../state/sequencer/create_imaging_run.dart';
import '../../state/sequencer/sequence_editor_state.dart';
import '../../state/sky_atlas/sky_atlas_state.dart';
import '../../theme/ara_colors.dart';

/// §36 Planning — actions for the currently-selected planetarium target. Appears
/// above the sky view once you pick an object (Tonight's Sky, search): it names
/// the target and offers **GoTo** (slew a connected mount to it) and **Add to
/// Sequence** (a complete imaging run built from the user's Imaging Defaults —
/// see [createImagingRun]). When this target already has a block in the open
/// sequence, "Add to Sequence" is joined by **Remove** (the undo — pulls just
/// this target's block back out; see [removeTargetFromSequence]). Hidden when
/// nothing is selected.
class TargetActionBar extends ConsumerStatefulWidget {
  const TargetActionBar({super.key});

  @override
  ConsumerState<TargetActionBar> createState() => _TargetActionBarState();
}

class _TargetActionBarState extends ConsumerState<TargetActionBar> {
  bool _busy = false;

  @override
  Widget build(BuildContext context) {
    final target = ref.watch(skyTargetProvider);
    if (target == null) return const SizedBox.shrink();

    final mount = ref.watch(mountProvider).asData?.value;
    final mountReady = mount != null &&
        mount.connectionState == EquipmentConnectionState.connected &&
        !mount.isBusy;

    // Is THIS target already a block in the sequence open in the Run tab? Watch
    // the editor body so the Remove button appears the moment "Add to Sequence"
    // grafts it in, and disappears again once it's removed. Only the open
    // (edited) sequence is inspected — an unopened sequence's membership isn't
    // known here without a fetch, and Remove acts on what's on screen anyway.
    final inOpenSequence = ref.watch(sequenceEditorProvider.select((s) =>
        s != null && indexOfTargetBlock(s.body, target.name) >= 0));

    return Material(
      color: AraColors.bgPanel,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
        child: Row(
          children: [
            const Icon(Icons.my_location, size: 18, color: AraColors.accentInfo),
            const SizedBox(width: 8),
            Flexible(
              child: Text.rich(
                TextSpan(children: [
                  TextSpan(
                      text: target.name,
                      style: Theme.of(context)
                          .textTheme
                          .titleSmall
                          ?.copyWith(fontWeight: FontWeight.w600)),
                  TextSpan(
                      text: '   ${_fmtRa(target.raDeg)}  ${_fmtDec(target.decDeg)}',
                      style: Theme.of(context).textTheme.bodySmall?.copyWith(
                          color: AraColors.textSecondary,
                          fontFeatures: const [FontFeature.tabularFigures()])),
                ]),
                overflow: TextOverflow.ellipsis,
              ),
            ),
            const SizedBox(width: 12),
            OutlinedButton.icon(
              icon: const Icon(Icons.send, size: 16),
              label: const Text('GoTo'),
              // Enabled only with a connected, idle mount — the tooltip explains why not.
              onPressed: (_busy || !mountReady) ? null : _goto,
            ),
            const SizedBox(width: 8),
            FilledButton.tonalIcon(
              icon: const Icon(Icons.playlist_add, size: 16),
              label: const Text('Add to Sequence'),
              onPressed: _busy ? null : _addToSequence,
            ),
            if (inOpenSequence) ...[
              const SizedBox(width: 8),
              OutlinedButton.icon(
                icon: const Icon(Icons.playlist_remove, size: 16),
                label: const Text('Remove'),
                onPressed: _busy ? null : _removeFromSequence,
              ),
            ],
          ],
        ),
      ),
    );
  }

  Future<void> _goto() async {
    final target = ref.read(skyTargetProvider);
    if (target == null) return;
    final messenger = ScaffoldMessenger.of(context);
    setState(() => _busy = true);
    try {
      // ASCOM slews on RA in hours; catalogue RA is in degrees.
      final ok = await ref
          .read(mountProvider.notifier)
          .slewTo(target.raDeg / 15.0, target.decDeg);
      if (mounted) {
        messenger.showSnackBar(SnackBar(
          content: Text(ok
              ? 'Slewing to ${target.name}…'
              : "Couldn't start the slew. Check the mount."),
        ));
      }
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _addToSequence() async {
    final target = ref.read(skyTargetProvider);
    if (target == null) return;
    final messenger = ScaffoldMessenger.of(context);
    setState(() => _busy = true);
    try {
      // A complete imaging run (cool/unpark/track/slew/AF + exposure loop from
      // the user's Imaging Defaults), selected + brought up in the Run tab —
      // or, with a sequence already open, this target appended to it.
      final result = await createImagingRun(
        ref,
        raDeg: target.raDeg,
        decDeg: target.decDeg,
        targetName: target.name,
      );
      if (mounted && result != null) {
        messenger.showSnackBar(SnackBar(
            content: Text(result.appended
                ? 'Added "${target.name}" to the open sequence.'
                : 'Created an imaging run for "${target.name}".')));
      }
    } catch (e) {
      if (mounted) {
        messenger.showSnackBar(const SnackBar(
            content: Text(
                "Couldn't create the run. Check the connection and try again.")));
      }
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _removeFromSequence() async {
    final target = ref.read(skyTargetProvider);
    if (target == null) return;
    final messenger = ScaffoldMessenger.of(context);
    setState(() => _busy = true);
    try {
      final outcome =
          await removeTargetFromSequence(ref, targetName: target.name);
      if (!mounted) return;
      final message = switch (outcome) {
        RemoveTargetOutcome.removed =>
          'Removed "${target.name}" from the open sequence.',
        // notFound after the button was shown means the open sequence changed
        // under us (re-selected/closed) — the target block is no longer there.
        RemoveTargetOutcome.notFound =>
          '"${target.name}" is no longer in the open sequence.',
        RemoveTargetOutcome.runningBlocked =>
          "Can't edit the sequence while it's running. Stop it first.",
        RemoveTargetOutcome.noServer =>
          'Connect to a server before editing the sequence.',
      };
      messenger.showSnackBar(SnackBar(content: Text(message)));
    } catch (e) {
      if (mounted) {
        messenger.showSnackBar(const SnackBar(
            content: Text(
                "Couldn't update the sequence. Check the connection and try again.")));
      }
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }
}

// Compact RA (hours/minutes) and Dec (signed degrees/arcmin) for the bar.
String _fmtRa(double raDeg) {
  final hoursTotal = raDeg / 15.0;
  final h = hoursTotal.floor();
  final m = ((hoursTotal - h) * 60).round();
  return '${h.toString().padLeft(2, '0')}h ${m.toString().padLeft(2, '0')}m';
}

String _fmtDec(double decDeg) {
  final sign = decDeg < 0 ? '−' : '+';
  final a = decDeg.abs();
  final d = a.floor();
  final m = ((a - d) * 60).round();
  return '$sign${d.toString().padLeft(2, '0')}° ${m.toString().padLeft(2, '0')}′';
}
