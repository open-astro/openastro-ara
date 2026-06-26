import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/equipment_device_status.dart';
import '../../models/sequence/slew_target_body.dart';
import '../../state/equipment/mount_state.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../state/sky_atlas/sky_atlas_state.dart';
import '../../theme/ara_colors.dart';

/// §36 Planning — actions for the currently-selected planetarium target. Appears
/// above the sky view once you pick an object (Tonight's Sky, search): it names
/// the target and offers **GoTo** (slew a connected mount to it) and **Add to
/// Sequence** (a one-slew sequence). Hidden when nothing is selected.
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
    final api = ref.read(sequenceApiProvider);
    final messenger = ScaffoldMessenger.of(context);
    if (api == null) return;
    setState(() => _busy = true);
    try {
      await api.create(
        target.name,
        buildSlewTargetBody(
          raDeg: target.raDeg,
          decDeg: target.decDeg,
          targetName: target.name,
        ),
      );
      if (mounted) {
        messenger.showSnackBar(
            SnackBar(content: Text('Added "${target.name}" to a new sequence.')));
      }
    } catch (e) {
      if (mounted) {
        messenger.showSnackBar(const SnackBar(
            content: Text(
                "Couldn't add to a sequence. Check the connection and try again.")));
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
