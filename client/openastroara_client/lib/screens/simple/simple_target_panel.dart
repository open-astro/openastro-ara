import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/equipment_device_status.dart';
import '../../services/equipment_device_api.dart'
    show describeEquipmentError, isRunInProgressRefusal;
import '../../state/equipment/mount_state.dart';
import '../../state/sky_atlas/planetarium_selection_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import 'simple_preview_panel.dart' show SimpleShutterButton, kSimpleText;
import 'simple_shell.dart' show SimplePanel;

/// The selection's name, or "None": the rail chip's caption in Target.
/// Pure — unit-tested.
String simpleTargetSummary(PlanetariumSelection? s) => s?.name ?? 'None';

/// Where the selection stands, in words: "62° up · max 74°", "Below your
/// horizon · rises 22:40", "Never rises here". Pure — unit-tested.
String simpleTargetStatus(PlanetariumSelection s) {
  if (s.neverRises) return 'Never rises here';
  if (s.belowHorizon) {
    final rise = s.rise;
    return rise == null
        ? 'Below your horizon'
        : 'Below your horizon · rises '
              '${rise.hour.toString().padLeft(2, '0')}:'
              '${rise.minute.toString().padLeft(2, '0')}';
  }
  return '${(s.altDeg ?? 0).round()}° up · max ${s.maxAltDeg.round()}°';
}

/// The last Go to's outcome, for the panel: nothing, a failure to show, or a
/// refusal because a sequence is running (the panel offers "Slew anyway").
sealed class SimpleSlewOutcome {
  const SimpleSlewOutcome();
}

class SimpleSlewFailed extends SimpleSlewOutcome {
  const SimpleSlewFailed(this.message);
  final String message;
}

class SimpleSlewNeedsForce extends SimpleSlewOutcome {
  const SimpleSlewNeedsForce();
}

class SimpleSlewNotifier extends Notifier<SimpleSlewOutcome?> {
  @override
  SimpleSlewOutcome? build() {
    // A new selection starts clean.
    ref.watch(planetariumSelectionProvider);
    return null;
  }

  /// Unpark (a parked mount ignores slews), then slew to the selection.
  Future<void> goTo({bool force = false}) async {
    final s = ref.read(planetariumSelectionProvider);
    if (s == null) return;
    final mount = ref.read(mountProvider.notifier);
    state = null;
    try {
      if (ref.read(mountProvider).value?.parked ?? false) await mount.unpark();
      final sent = await mount.slewTo(s.raHours, s.decDeg, force: force);
      if (!sent) state = const SimpleSlewFailed('The mount is busy. Try again.');
    } catch (e) {
      state = isRunInProgressRefusal(e)
          ? const SimpleSlewNeedsForce()
          : SimpleSlewFailed("Couldn't go to ${s.name}: ${describeEquipmentError(e)}");
    }
  }
}

final simpleSlewProvider =
    NotifierProvider<SimpleSlewNotifier, SimpleSlewOutcome?>(
      SimpleSlewNotifier.new,
    );

/// The rail's shutter in Target: goes to the selected object; while the mount
/// slews, its red square aborts the slew. Off with nothing to go to (no
/// selection, no mount, or an object below the horizon).
class SimpleTargetShutter extends ConsumerWidget {
  const SimpleTargetShutter({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final mount = ref.watch(mountProvider).value;
    final sel = ref.watch(planetariumSelectionProvider);
    final connected =
        mount?.connectionState == EquipmentConnectionState.connected;
    final slewing = connected && mount!.isBusy;
    final canGo = connected && sel != null && !sel.belowHorizon;
    return SimpleShutterButton(
      running: slewing,
      label: slewing ? 'Stop the slew' : 'Go to ${sel?.name ?? 'the target'}',
      onPressed: slewing
          ? () => ref.read(mountProvider.notifier).abortSlew()
          : canGo
          ? () => ref.read(simpleSlewProvider.notifier).goTo()
          : null,
    );
  }
}

/// Target's panel beside the right rail: a search, then the selected object
/// (name, type, altitude, or why it can't be reached), the last Go to's
/// failure, and "Slew anyway" when a running sequence refused it. The rail's
/// shutter does the Go to.
class SimpleTargetPanel extends ConsumerStatefulWidget {
  const SimpleTargetPanel({super.key});

  @override
  ConsumerState<SimpleTargetPanel> createState() => _SimpleTargetPanelState();
}

class _SimpleTargetPanelState extends ConsumerState<SimpleTargetPanel> {
  final _query = TextEditingController();

  @override
  void dispose() {
    _query.dispose();
    super.dispose();
  }

  void _search() {
    final q = _query.text.trim();
    if (q.isNotEmpty) ref.read(planetariumSearchProvider.notifier).search(q);
  }

  @override
  Widget build(BuildContext context) {
    final sel = ref.watch(planetariumSelectionProvider);
    final outcome = ref.watch(simpleSlewProvider);
    final mount = ref.watch(mountProvider).value;
    final connected =
        mount?.connectionState == EquipmentConnectionState.connected;
    return SimplePanel(
      padding: const EdgeInsets.all(AraSpace.s16),
      child: SizedBox(
        width: 248,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          spacing: AraSpace.s8,
          children: [
            TextField(
              key: const Key('simple-target-search'),
              controller: _query,
              style: kSimpleText,
              textInputAction: TextInputAction.search,
              onSubmitted: (_) => _search(),
              decoration: InputDecoration(
                hintText: 'Search objects',
                prefixIcon: const Icon(Icons.search, size: 20),
                suffixIcon: IconButton(
                  tooltip: 'Search',
                  icon: const Icon(Icons.arrow_forward, size: 20),
                  onPressed: _search,
                ),
              ),
            ),
            if (sel == null)
              Text(
                'Tap an object in the sky, or search.',
                style: kSimpleText.copyWith(color: AraColors.textSecondary),
              )
            else ...[
              Text(
                sel.name,
                style: kSimpleText.copyWith(
                  fontSize: 16,
                  fontWeight: FontWeight.w600,
                ),
                maxLines: 2,
                overflow: TextOverflow.ellipsis,
              ),
              if (sel.kind.isNotEmpty)
                Text(
                  sel.kind,
                  style: kSimpleText.copyWith(color: AraColors.textSecondary),
                ),
              Text(
                simpleTargetStatus(sel),
                style: kSimpleText.copyWith(
                  color: sel.belowHorizon ? AraColors.accentBusy : null,
                ),
              ),
            ],
            if (!connected)
              Text(
                'No mount connected',
                style: kSimpleText.copyWith(color: AraColors.textSecondary),
              ),
            switch (outcome) {
              SimpleSlewFailed(:final message) => Text(
                message,
                style: kSimpleText.copyWith(color: AraColors.accentError),
                maxLines: 4,
                overflow: TextOverflow.ellipsis,
              ),
              SimpleSlewNeedsForce() => Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                spacing: AraSpace.s8,
                children: [
                  Text(
                    'A sequence is running. Slewing now stops its guiding.',
                    style: kSimpleText.copyWith(color: AraColors.accentBusy),
                  ),
                  FilledButton.tonal(
                    key: const Key('simple-target-force'),
                    onPressed: () => ref
                        .read(simpleSlewProvider.notifier)
                        .goTo(force: true),
                    child: const Text('Slew anyway'),
                  ),
                ],
              ),
              null => const SizedBox.shrink(),
            },
          ],
        ),
      ),
    );
  }
}
