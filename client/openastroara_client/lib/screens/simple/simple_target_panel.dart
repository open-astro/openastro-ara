import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/equipment_device_status.dart';
import '../../services/equipment_device_api.dart'
    show describeEquipmentError, isRunInProgressRefusal;
import '../../models/verify_position_result.dart';
import '../../services/center_api.dart';
import '../../services/plate_solve_api.dart' show PlateSolveException;
import '../../state/plate_solve/plate_solve_api_state.dart';
import '../../state/sky_atlas/sky_atlas_state.dart' show planetariumCommandProvider;
import '../../util/apparent_place.dart';
import '../../util/friendly_error.dart';
import '../../widgets/imaging/polar_align_panel.dart' show formatDecDms, formatRaHms;
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

/// Where the last Go to stands, for the panel and the shutter: centring
/// (the plate-solve job), centred, failed, or refused because a sequence is
/// running (the panel offers "Slew anyway"). Null: nothing in hand.
sealed class SimpleGoTo {
  const SimpleGoTo();
}

class SimpleCentering extends SimpleGoTo {
  const SimpleCentering(this.jobId, {this.done = 0, this.total = 0});
  final String jobId;
  final int done;
  final int total;
}

class SimpleCentered extends SimpleGoTo {
  const SimpleCentered();
}

class SimpleGoToFailed extends SimpleGoTo {
  const SimpleGoToFailed(this.message);
  final String message;
}

class SimpleGoToNeedsForce extends SimpleGoTo {
  const SimpleGoToNeedsForce();
}

/// Go to and centre: unpark (a parked mount ignores slews), slew (the daemon
/// refuses while a sequence runs, until forced), then the plate-solve
/// centring job on the J2000 position, polled every second.
class SimpleGoToNotifier extends Notifier<SimpleGoTo?> {
  Timer? _poll;

  @override
  SimpleGoTo? build() {
    ref.onDispose(() => _poll?.cancel());
    // A new selection starts clean.
    ref.watch(planetariumSelectionProvider);
    _poll?.cancel();
    return null;
  }

  Future<void> goTo({bool force = false}) async {
    final s = ref.read(planetariumSelectionProvider);
    if (s == null) return;
    final mount = ref.read(mountProvider.notifier);
    _poll?.cancel();
    state = null;
    try {
      if (ref.read(mountProvider).value?.parked ?? false) await mount.unpark();
      if (!await mount.slewTo(s.raHours, s.decDeg, force: force)) {
        state = const SimpleGoToFailed('The mount is busy. Try again.');
        return;
      }
      final api = ref.read(centerApiProvider);
      if (api == null) return;
      final job = await api.start(s.raJ2000Hours, s.decJ2000Deg);
      state = SimpleCentering(job.jobId, total: job.total);
      _poll = Timer.periodic(const Duration(seconds: 1), (_) => _check(api));
    } catch (e) {
      state = isRunInProgressRefusal(e)
          ? const SimpleGoToNeedsForce()
          : SimpleGoToFailed(
              "Couldn't go to ${s.name}: ${describeEquipmentError(e)}",
            );
    }
  }

  Future<void> _check(CenterClient api) async {
    final current = state;
    if (current is! SimpleCentering) return _poll?.cancel();
    try {
      final job = await api.job(current.jobId);
      if (state is! SimpleCentering) return;
      switch (job?.state) {
        case 'complete':
          _poll?.cancel();
          state = const SimpleCentered();
        case 'failed' || 'cancelled' || null:
          _poll?.cancel();
          state = SimpleGoToFailed(
            job == null
                ? 'Lost track of the centring (the server restarted).'
                : "Couldn't centre: ${job.errorMessage ?? job.state}",
          );
        default:
          state = SimpleCentering(
            current.jobId,
            done: job!.done,
            total: job.total,
          );
      }
    } catch (_) {
      // A missed poll is retried on the next tick.
    }
  }

  /// The red square: cancel the centring job, if any, and abort the slew.
  Future<void> stop() async {
    final current = state;
    _poll?.cancel();
    state = null;
    if (current is SimpleCentering) {
      await ref.read(centerApiProvider)?.cancel(current.jobId);
    }
    await ref.read(mountProvider.notifier).abortSlew();
  }
}

final simpleGoToProvider = NotifierProvider<SimpleGoToNotifier, SimpleGoTo?>(
  SimpleGoToNotifier.new,
);

/// Where Solve stands: solving, solved (with Sync offered, then synced), or
/// why it failed. Null: not asked.
sealed class SimpleSolve {
  const SimpleSolve();
}

class SimpleSolving extends SimpleSolve {
  const SimpleSolving();
}

class SimpleSolved extends SimpleSolve {
  const SimpleSolved(this.result, {this.synced = false});
  final VerifyPositionResult result;
  final bool synced;
}

class SimpleSolveFailed extends SimpleSolve {
  const SimpleSolveFailed(this.message);
  final String message;
}

/// Solve: a 2 s frame plate-solved by the server (§57.4 verify position),
/// the sky centred on the result; Sync then tells the mount where it really
/// points.
class SimpleSolveNotifier extends Notifier<SimpleSolve?> {
  @override
  SimpleSolve? build() => null;

  Future<void> solve() async {
    final api = ref.read(plateSolveApiProvider);
    if (api == null || state is SimpleSolving) return;
    state = const SimpleSolving();
    try {
      final r = await api.verifyPosition();
      final ra = r.solvedRaHours, dec = r.solvedDecDegrees;
      if (!r.success || ra == null || dec == null) {
        state = SimpleSolveFailed(r.note ?? 'The frame did not solve.');
        return;
      }
      state = SimpleSolved(r);
      ref
          .read(planetariumCommandProvider.notifier)
          .send({'type': 'goto', 'ra': ra * 15, 'dec': dec});
    } on PlateSolveException catch (e) {
      state = SimpleSolveFailed(e.message);
    } catch (e) {
      state = SimpleSolveFailed(friendlyError(e, action: 'solve'));
    }
  }

  /// Sync the mount to the solve, in the mount's own system (the solve is
  /// J2000; most mounts work of date).
  Future<void> sync() async {
    final current = state;
    if (current is! SimpleSolved) return;
    final r = current.result;
    var ra = r.solvedRaHours! * 15, dec = r.solvedDecDegrees!;
    if (r.mountEpoch != 'J2000') {
      final now = j2000ToApparent(ra, dec, atUtc: DateTime.now().toUtc());
      ra = now.raDeg;
      dec = now.decDeg;
    }
    try {
      await ref
          .read(mountProvider.notifier)
          .slewTo(ra / 15, dec, sync: true);
      state = SimpleSolved(r, synced: true);
    } catch (e) {
      state = SimpleSolveFailed(
        "Couldn't sync: ${describeEquipmentError(e)}",
      );
    }
  }
}

final simpleSolveProvider = NotifierProvider<SimpleSolveNotifier, SimpleSolve?>(
  SimpleSolveNotifier.new,
);

/// The rail's shutter in Target: goes to the selected object and centres it;
/// while the mount slews or the centring runs, its red square stops both.
/// Off with nothing to go to (no selection, no mount, or an object below the
/// horizon).
class SimpleTargetShutter extends ConsumerWidget {
  const SimpleTargetShutter({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final mount = ref.watch(mountProvider).value;
    final sel = ref.watch(planetariumSelectionProvider);
    final goTo = ref.watch(simpleGoToProvider);
    final connected =
        mount?.connectionState == EquipmentConnectionState.connected;
    final running =
        (connected && mount!.isBusy) || goTo is SimpleCentering;
    final canGo = connected && sel != null && !sel.belowHorizon;
    return SimpleShutterButton(
      running: running,
      label: running ? 'Stop' : 'Go to ${sel?.name ?? 'the target'}',
      onPressed: running
          ? () => ref.read(simpleGoToProvider.notifier).stop()
          : canGo
          ? () => ref.read(simpleGoToProvider.notifier).goTo()
          : null,
    );
  }
}

/// Target's panel beside the right rail: a search, then the selected object
/// (name, type, altitude, or why it can't be reached), where the Go to and
/// centre stands, and "Slew anyway" when a running sequence refused it. The
/// rail's shutter does the Go to.
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
    final outcome = ref.watch(simpleGoToProvider);
    final solve = ref.watch(simpleSolveProvider);
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
            FilledButton.tonal(
              key: const Key('simple-target-solve'),
              onPressed: solve is SimpleSolving
                  ? null
                  : () => ref.read(simpleSolveProvider.notifier).solve(),
              child: Text(solve is SimpleSolving ? 'Solving…' : 'Solve'),
            ),
            switch (solve) {
              SimpleSolved(:final result, :final synced) => Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                spacing: AraSpace.s8,
                children: [
                  Text(
                    'Solved: RA ${formatRaHms(result.solvedRaHours! * 15)}, '
                    'Dec ${formatDecDms(result.solvedDecDegrees!)}',
                    style: kSimpleText,
                  ),
                  if (result.solvedToMountArcmin case final off?)
                    Text(
                      'The mount is off by ${off.round()}′',
                      style: kSimpleText.copyWith(
                        color: AraColors.textSecondary,
                      ),
                    ),
                  synced
                      ? Text(
                          'Synced',
                          style: kSimpleText.copyWith(
                            color: AraColors.accentConnected,
                          ),
                        )
                      : FilledButton.tonal(
                          key: const Key('simple-target-sync'),
                          onPressed: connected
                              ? () => ref
                                    .read(simpleSolveProvider.notifier)
                                    .sync()
                              : null,
                          child: const Text('Sync'),
                        ),
                ],
              ),
              SimpleSolveFailed(:final message) => Text(
                message,
                style: kSimpleText.copyWith(color: AraColors.accentError),
                maxLines: 4,
                overflow: TextOverflow.ellipsis,
              ),
              _ => const SizedBox.shrink(),
            },
            if (!connected)
              Text(
                'No mount connected',
                style: kSimpleText.copyWith(color: AraColors.textSecondary),
              ),
            switch (outcome) {
              SimpleCentering(:final done, :final total) => Text(
                total > 0
                    ? 'Centering · solve ${done + 1} of $total'
                    : 'Centering…',
                style: kSimpleText,
              ),
              SimpleCentered() => Text(
                'Centered on ${sel?.name ?? 'the target'}',
                style: kSimpleText.copyWith(color: AraColors.accentConnected),
              ),
              SimpleGoToFailed(:final message) => Text(
                message,
                style: kSimpleText.copyWith(color: AraColors.accentError),
                maxLines: 4,
                overflow: TextOverflow.ellipsis,
              ),
              SimpleGoToNeedsForce() => Column(
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
                        .read(simpleGoToProvider.notifier)
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
