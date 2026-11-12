import 'dart:async';

import 'dart:math' as math;

import 'package:flutter/cupertino.dart'
    show CupertinoDatePicker, CupertinoDatePickerMode;
import 'package:flutter/gestures.dart' show PointerDeviceKind, kPrimaryButton;
import 'package:flutter/material.dart';
import 'package:flutter/semantics.dart' show CustomSemanticsAction;
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/imaging_run_body.dart';
import '../../models/sequence/sequence_summary.dart';
import '../../state/equipment/camera_state.dart';
import '../../state/planning/sky_model_calibration_state.dart';
import '../../state/sequencer/create_imaging_run.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../state/settings/filter_set_state.dart';
import '../../state/setup/setup_readiness.dart';
import '../../state/imaging/exposure_state.dart';
import '../../state/simple_messages_state.dart';
import '../../state/simple_mode_state.dart';
import '../../state/simple_run_state.dart';
export '../../state/simple_run_state.dart';
import '../../state/settings/imaging_defaults_state.dart';
import '../../state/settings/settings_nav.dart';
import '../../state/settings/site_settings_state.dart';
import '../../services/dso_catalog_service.dart'
    show DsoSuggestion, PlanningDso, designationKey, searchSuggestions;
import '../../state/sky_atlas/dso_catalog_state.dart'
    show bundledCatalogProvider;
import '../../services/tonight_sky_api.dart';
import '../../state/sky_atlas/planetarium_selection_state.dart';
import '../../state/sky_atlas/tonight_sky_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../l10n/l10n.dart';
import '../../util/tonight_sky_local.dart'
    show altitudeDegAt, darkStart, nightEnd;
import '../../widgets/sequencer/target_plan_dialog.dart'
    show suggestedSubSeconds;
import '../../widgets/emergency_stop_button.dart' show kEmergencyStopHold;
import '../../widgets/sequencer/run_completion_sheet.dart'
    show runFramesPerFilter;
import 'simple_preflight.dart'
    show SimplePreflightStep, simplePreflightProvider, simplePreflightNext;
import 'simple_calibration.dart' show SimpleNightCalibration;
import 'simple_device_windows.dart' show SimpleGainSlider;
import 'simple_preview_panel.dart'
    show
        SimplePresetTile,
        SimpleWheelKeys,
        exposurePresets,
        formatExposure,
        simpleSayWhy,
        kSimpleText;
import 'simple_shell.dart'
    show
        SimplePanel,
        SimpleSegments,
        simpleFrost,
        kSimpleGoStyle,
        kSimpleInset,
        kSimpleRailWidth,
        simpleRailScale;
import '../../widgets/adaptive_controls.dart';

/// The plan's one-line hint: the suggested sub length of every row that
/// differs from it, rows sharing a length together ("L 120 s · GB 180 s",
/// "Ha/OIII 300 s"); null when every row already matches. Pure —
/// unit-tested.
String? simpleRunSuggestion(
  List<FilterPlanStep> steps,
  List<double> suggested,
) {
  final byLength = <double, List<String>>{};
  for (var i = 0; i < steps.length; i++) {
    if (suggested[i] == steps[i].exposureSeconds) continue;
    (byLength[suggested[i]] ??= []).add(steps[i].filterName);
  }
  if (byLength.isEmpty) return null;
  String len(double s) =>
      formatExposure(Duration(milliseconds: (s * 1000).round()));
  return byLength.entries
      .map((e) {
        final names = e.value.where((n) => n.isNotEmpty).toList();
        final joined = names.join(names.every((n) => n.length == 1) ? '' : '/');
        return joined.isEmpty ? len(e.key) : '$joined ${len(e.key)}';
      })
      .join(' · ');
}

/// "6 h 20 min", "45 min". Pure — unit-tested.
String simpleRunDuration(double seconds) {
  final min = (seconds / 60).round();
  return min >= 60 ? '${min ~/ 60} h ${min % 60} min' : '$min min';
}

/// The rail chip in Plan: the plan's total imaging time, the created plan's
/// once there is one, else the sheet's rows; hours over minutes, as P.A.'s
/// chip takes two lines in 60 px. Pure — unit-tested.
String simpleRunChip(SimpleRun run, List<FilterPlanStep> rows) =>
    simpleRunDuration(
      (run.steps.isEmpty ? rows : run.steps).fold(
        0.0,
        (t, s) => t + s.exposureSeconds * s.frameCount,
      ),
    ).replaceFirst(' h ', ' h\n');

String _hhmm(DateTime t) =>
    '${t.hour.toString().padLeft(2, '0')}:${t.minute.toString().padLeft(2, '0')}';

/// Where the selection stands, in words: "62° up · max 74°", "Below your
/// horizon · rises 22:40", "Never rises here". Pure — unit-tested.
String simpleTargetStatus(PlanetariumSelection s, AppLocalizations l) {
  if (s.neverRises) return l.planNeverRises;
  if (s.belowHorizon) {
    final rise = s.rise;
    return rise == null
        ? l.planBelowHorizon
        : l.planBelowHorizonRises(_hhmm(rise));
  }
  return l.planAltitudeUpMax((s.altDeg ?? 0).round(), s.maxAltDeg.round());
}

/// A suggestion's second line: its type, then its altitude now or when it
/// rises. Pure — unit-tested.
String simpleSuggestionLine(AppLocalizations l, TonightSkyObject o) {
  final rise = o.windowStartUtc?.toLocal();
  final where = o.altitudeDeg > 0
      ? l.planAltitudeUp(o.altitudeDeg.round())
      : rise == null
      ? l.planBelowTheHorizon
      : l.planRisesAt(_hhmm(rise));
  return [if (o.type.isNotEmpty) o.type, where].join(' · ');
}

/// A suggestion as the planetarium's selection, so the plan takes it as if
/// tapped in the sky. Pure — unit-tested.
// shortcut: the ranker's position is J2000 and stands in for JNOW too; only
// the plan (J2000) reads it, the planetarium isn't pointed at it.
PlanetariumSelection simpleSelectionOf(TonightSkyObject o) =>
    PlanetariumSelection(
      name: o.name,
      kind: o.type,
      raHours: o.raDeg / 15,
      decDeg: o.decDeg,
      raJ2000Hours: o.raDeg / 15,
      decJ2000Deg: o.decDeg,
      altDeg: o.altitudeDeg,
      maxAltDeg: o.maxAltitudeDeg,
      rise: o.windowStartUtc?.toLocal(),
      designations: [o.id, ...o.aliases],
    );

/// A position's altitude above the site at [utc], in degrees. Pure —
/// unit-tested.
// shortcut: takes the J2000 position as today's, under half a degree off;
// enough for above or below the horizon, not for pointing.
double simpleAltitudeDeg(
  double raDeg,
  double decDeg, {
  required double latitudeDeg,
  required double longitudeDeg,
  required DateTime utc,
}) => altitudeDegAt(
  raDeg,
  decDeg,
  latitudeDeg: latitudeDeg,
  longitudeDeg: longitudeDeg,
  utc: utc,
);

/// A picked catalog object as the selection, at the site and time now.
PlanetariumSelection simplePickDso(WidgetRef ref, PlanningDso d) {
  final site = ref.read(siteSettingsProvider);
  return simpleSelectionOfDso(
    d,
    ref.read(tonightSkyProvider).value ?? const [],
    latitudeDeg: site.latitudeDeg,
    // 0, 0 is the unset site (as tonight_sky_state reads it): no altitude from a made-up position.
    longitudeDeg: site.latitudeDeg == 0 && site.longitudeDeg == 0 ? null : site.longitudeDeg,
    utc: ref.read(clockProvider)().toUtc(),
  );
}

/// A catalog object (a Sky Album card) as the selection: the ranker's row
/// when it lists the object under any designation, so altitude and rise come
/// with it; else the catalog position, with the altitude computed when
/// [longitudeDeg] and [utc] are given and the transit altitude from
/// [latitudeDeg]. Pure — unit-tested.
PlanetariumSelection simpleSelectionOfDso(
  PlanningDso d,
  List<TonightSkyObject> ranked, {
  required double latitudeDeg,
  double? longitudeDeg,
  DateTime? utc,
}) {
  final keys = {for (final n in [d.id, ...d.aliases]) designationKey(n)};
  final row = ranked
      .where(
        (o) => [o.id, ...o.aliases].any((n) => keys.contains(designationKey(n))),
      )
      .firstOrNull;
  if (row != null) return simpleSelectionOf(row);
  return PlanetariumSelection(
    name: d.name,
    kind: d.type,
    raHours: d.raDeg / 15,
    decDeg: d.decDeg,
    raJ2000Hours: d.raDeg / 15,
    decJ2000Deg: d.decDeg,
    altDeg: longitudeDeg == null || utc == null
        ? null
        : simpleAltitudeDeg(
            d.raDeg,
            d.decDeg,
            latitudeDeg: latitudeDeg,
            longitudeDeg: longitudeDeg,
            utc: utc,
          ),
    maxAltDeg: 90 - (latitudeDeg - d.decDeg).abs(),
    designations: [d.id, ...d.aliases],
  );
}

/// A paused plan's colour (DESIGN.md status rule): paused by Ara (a star
/// lost, safety, a flip) is on its way back, blue; paused waiting for the
/// user needs a look, amber.
Color simplePausedColour(WidgetRef ref) =>
    ref.watch(
      sequenceRunStateProvider.select(
        (s) => s.value?.state == SequenceRunState.pausedAwaitingUser,
      ),
    )
    ? AraColors.accentBusy
    : AraColors.accentInfo;

/// The row the run is on (the first not yet full), by the plan order.
int _current(List<FilterPlanStep> steps, List<int> done) {
  for (var i = 0; i < steps.length; i++) {
    if (done[i] < steps[i].frameCount) return i;
  }
  return steps.length - 1;
}

String _sub(FilterPlanStep s) {
  final len = formatExposure(
    Duration(milliseconds: (s.exposureSeconds * 1000).round()),
  );
  return s.filterName.isEmpty ? len : '${s.filterName} · $len';
}

/// The plan squares under the strip, for the queue's whole life, right to
/// left in queue order: each target's tag, frames done of its total, the
/// filter and sub length in hand, and the time (total before it starts, left
/// while imaging). The target in hand is green ready, blue imaging, amber
/// paused; the others green. Nothing without a plan. A tap on any square:
/// the sheet (where the queue is managed) or the progress panel.
class SimpleRunTracker extends ConsumerStatefulWidget {
  const SimpleRunTracker({super.key, required this.onTap});

  final VoidCallback onTap;

  @override
  ConsumerState<SimpleRunTracker> createState() => _SimpleRunTrackerState();
}

class _SimpleRunTrackerState extends ConsumerState<SimpleRunTracker> {
  @override
  Widget build(BuildContext context) {
    final run = ref.watch(simpleRunProvider);
    final info = ref.watch(sequenceRunStateProvider).value;
    final phase = simpleRunPhase(run, info);
    if (phase == SimpleRunPhase.none) return const SizedBox.shrink();
    final ours = run.sequenceId != null && info?.sequenceId == run.sequenceId;
    final captured = ours || !run.started ? (info?.framesCaptured ?? 0) : 0;
    final known =
        run.targets.isNotEmpty && (ours || phase == SimpleRunPhase.ready);
    final active = switch (phase) {
      SimpleRunPhase.ready || SimpleRunPhase.done => AraColors.accentConnected,
      SimpleRunPhase.paused => simplePausedColour(ref),
      _ => AraColors.selectionBg,
    };
    String? stateWord() => switch (phase) {
      // #1747: no frame in hand while the rig waits out the meridian.
      SimpleRunPhase.imaging when info?.meridianWaitLeft != null =>
        context.l10n.planWaitingForFlip,
      SimpleRunPhase.pausing => context.l10n.planPausing,
      SimpleRunPhase.paused => context.l10n.planPaused,
      SimpleRunPhase.done => context.l10n.planDone,
      _ => null,
    };
    if (!known) {
      // A run picked up from the daemon: what it reports.
      return _PlanSquare(
        key: const Key('simple-run-tracker'),
        name: info?.currentTargetName ?? '',
        frames: '$captured',
        last:
            stateWord() ??
            (info?.estimatedRemainingSeconds == null
                ? ''
                : simpleRunDuration(info!.estimatedRemainingSeconds!)),
        color: active,
        onTap: widget.onTap,
      );
    }
    final frames = phase == SimpleRunPhase.ready ? 0 : captured;
    final progress = simpleTargetProgress(run.targets, frames);
    final cur = simpleCurrentTarget(run.targets, frames);
    final now = DateTime.now();
    // #1560 — Simple Mode's queue parks the mount after its last target.
    final parks = phase == SimpleRunPhase.done
        ? null
        : context.l10n.planSquareParks;
    Widget square(int i, {String? foot}) {
      final t = run.targets[i];
      final done = progress[i];
      final got = done.fold(0, (a, b) => a + b);
      final total = simplePlanTotal(t.steps);
      final finished = phase == SimpleRunPhase.done || i < cur;
      final inHand = i == cur && !finished;
      final left = [
        for (var r = 0; r < t.steps.length; r++)
          (t.steps[r].frameCount - done[r]) * t.steps[r].exposureSeconds,
      ].fold(0.0, (a, b) => a + b);
      final last = finished
          ? context.l10n.planDone
          : t.waiting(now) && (!inHand || phase == SimpleRunPhase.ready)
          ? context.l10n.planRisesAt(_hhmm(t.rise!.toLocal()))
          : inHand
          ? stateWord() ??
                simpleRunDuration(
                  run.targets.length == 1
                      ? info?.estimatedRemainingSeconds ?? left
                      : left,
                )
          : simpleRunDuration(left);
      return _PlanSquare(
        name: t.tag ?? t.name,
        label: t.name,
        frames: '$got / $total',
        sub: t.steps.isEmpty ? null : _sub(t.steps[_current(t.steps, done)]),
        last: last,
        paused: inHand && phase == SimpleRunPhase.paused,
        color: inHand ? active : AraColors.accentConnected,
        fraction: inHand && phase != SimpleRunPhase.ready && total > 0
            ? got / total
            : null,
        foot: foot,
        onTap: widget.onTap,
      );
    }

    // As many squares as fit the room the shell gives; past that, the target
    // in hand and those after it, then one "+N" square for the rest
    // (finished targets drop out first).
    return LayoutBuilder(
      builder: (context, box) {
        final fit = math.max(
          1,
          ((box.maxWidth + _kSquareGap) /
                  (kSimpleRailWidth * simpleRailScale(context) + _kSquareGap))
              .floor(),
        );
        final all = run.targets.length;
        final shown = all <= fit
            ? [for (var i = 0; i < all; i++) i]
            : [for (var i = cur; i < math.min(all, cur + fit - 1); i++) i];
        final rest = [
          for (var i = shown.isEmpty ? 0 : shown.last + 1; i < all; i++) i,
        ];
        return Row(
          key: const Key('simple-run-tracker'),
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          spacing: _kSquareGap,
          children: [
            if (all > fit && rest.isNotEmpty)
              _PlanSquare(
                name: '+${rest.length}',
                label: context.l10n.planMoreInQueue(rest.length),
                frames: context.l10n.planQueued,
                last: simpleRunDuration(
                  rest.fold(
                    0.0,
                    (t, i) =>
                        t +
                        run.targets[i].steps.fold(
                          0.0,
                          (u, s) => u + s.exposureSeconds * s.frameCount,
                        ),
                  ),
                ),
                color: AraColors.accentConnected,
                foot: parks,
                onTap: widget.onTap,
              ),
            // The queue runs right to left: the first target sits in the corner.
            for (final i in shown.reversed)
              square(i, foot: rest.isEmpty && i == all - 1 ? parks : null),
          ],
        );
      },
    );
  }
}

const double _kSquareGap = 6;

/// A plan square's height (Plan's sheet starts below the squares).
const double kSimplePlanSquareHeight = 100;

/// One plan square: the target's tag, frames, filter · sub and time, a bar
/// filling while it images.
class _PlanSquare extends StatelessWidget {
  const _PlanSquare({
    super.key,
    required this.name,
    this.label,
    required this.frames,
    this.sub,
    required this.last,
    required this.color,
    this.paused = false,
    this.fraction,
    this.foot,
    required this.onTap,
  });

  final String name;

  /// The whole name, for screen readers ([name] may be the tag).
  final String? label;
  final String frames;
  final String? sub;
  final String last;
  final Color color;
  final bool paused;
  final double? fraction;

  /// A last line under [last] ("Parks" on the queue's last square).
  final String? foot;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final small = kSimpleText.copyWith(
      fontSize: 11.5,
      color: AraColors.textSecondary,
    );
    return Semantics(
      button: true,
      label: context.l10n.planSquareLabel(label ?? name),
      child: simpleFrost(
        Material(
        color: AraColors.overlayScrim,
        shape: RoundedRectangleBorder(
          side: BorderSide(color: color),
          borderRadius: BorderRadius.circular(AraRadius.simplePanel),
        ),
        clipBehavior: Clip.antiAlias,
        child: InkWell(
          onTap: onTap,
          // Grows with the text past 1.3x, as the rails do.
          child: SizedBox(
            width: kSimpleRailWidth * simpleRailScale(context),
            height: kSimplePlanSquareHeight * simpleRailScale(context),
            child: Stack(
              children: [
                // A long name or time shrinks to fit, clear of the border; so
                // does the whole column when its lines outgrow the square's
                // height (five lines past 1.1x text), all by the same amount.
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 6),
                  child: Center(
                    child: FittedBox(
                      fit: BoxFit.scaleDown,
                      child: SizedBox(
                        width: kSimpleRailWidth * simpleRailScale(context) - 12,
                        child: Column(
                          mainAxisSize: MainAxisSize.min,
                          spacing: 2,
                          children: [
                            for (final (text, style) in [
                              (
                                name,
                                kSimpleText.copyWith(
                                  fontWeight: FontWeight.w600,
                                ),
                              ),
                              (frames, kSimpleText),
                              if (sub != null) (sub!, small),
                              (
                                last,
                                paused
                                    ? small.copyWith(color: color)
                                    : small,
                              ),
                              // One short word: the square has 60 px for it.
                              if (foot != null) (foot!, small),
                            ])
                              FittedBox(
                                fit: BoxFit.scaleDown,
                                child: Text(text, style: style, maxLines: 1),
                              ),
                          ],
                        ),
                      ),
                    ),
                  ),
                ),
                if (fraction != null)
                  Align(
                    alignment: Alignment.bottomLeft,
                    child: FractionallySizedBox(
                      widthFactor: fraction,
                      child: Container(height: 3, color: color),
                    ),
                  ),
              ],
            ),
          ),
        ),
      ),
      ),
    );
  }
}

/// How long the shutter is held to stop a run (the Emergency Stop's hold).
const kSimpleRunStopHold = kEmergencyStopHold;

/// The rail's shutter in Plan. Plan ready: a play triangle in a green ring;
/// a tap starts it. Imaging: pause bars, with a blue ring filling as the
/// frame exposes; a tap pauses (after the frame in hand). Paused: a play
/// triangle in an amber ring; a tap resumes. While imaging or paused, a
/// 2 s hold stops the run (a red ring fills; letting go early cancels).
/// Screen readers get "Stop the run" as an action. Keyboards: Tab reaches
/// it, Enter / Space taps it, ⌘. / Ctrl+. pressed twice stops the run.
class SimpleRunShutter extends ConsumerStatefulWidget {
  const SimpleRunShutter({super.key});

  @override
  ConsumerState<SimpleRunShutter> createState() => _SimpleRunShutterState();
}

class _SimpleRunShutterState extends ConsumerState<SimpleRunShutter>
    with SingleTickerProviderStateMixin {
  late final AnimationController _hold =
      AnimationController(vsync: this, duration: kSimpleRunStopHold)
        ..addListener(() => setState(() {}))
        ..addStatusListener((s) {
          if (s == AnimationStatus.completed) {
            _held = true;
            _hold.value = 0;
            ref.read(simpleRunProvider.notifier).stop();
          }
        });

  /// The hold reached the end: the release is not a tap.
  bool _held = false;
  bool _down = false;

  /// The press came from a pointer, which [Listener] already handled; the
  /// [InkResponse] tap is for the keyboard (Enter / Space) only.
  bool _viaPointer = false;

  Timer? _riseTimer;

  @override
  void dispose() {
    _hold.dispose();
    _riseTimer?.cancel();
    super.dispose();
  }

  bool _pickable() {
    final sel = ref.read(planetariumSelectionProvider);
    return sel != null && !sel.belowHorizon;
  }

  bool _holdable(SimpleRunPhase p) =>
      p == SimpleRunPhase.imaging ||
      p == SimpleRunPhase.pausing ||
      p == SimpleRunPhase.paused;

  VoidCallback? _tap(SimpleRunPhase p) {
    final run = ref.read(simpleRunProvider.notifier);
    return switch (p) {
      SimpleRunPhase.none when _pickable() => () => run.createAndStart(),
      SimpleRunPhase.ready
          when !ref.read(simpleRunProvider).waiting(DateTime.now()) =>
        run.start,
      SimpleRunPhase.imaging => run.pause,
      SimpleRunPhase.paused => run.resume,
      _ => null,
    };
  }

  @override
  Widget build(BuildContext context) {
    final run = ref.watch(simpleRunProvider);
    final phase = simpleRunPhase(
      run,
      ref.watch(sequenceRunStateProvider).value,
    );
    final camera = ref.watch(cameraStatusProvider).value;
    final exposing = camera?.runtimeState == 'exposing';
    // #1747: a pause asked during the meridian wait lands after the flip.
    final flipWait = ref.watch(
      sequenceRunStateProvider.select((s) => s.value?.meridianWaitLeft != null),
    );
    final holdable = _holdable(phase);
    ref.watch(planetariumSelectionProvider);
    // A picked target with no plan yet starts like a ready plan; a plan
    // queued below the horizon waits for the rise.
    final waiting =
        phase == SimpleRunPhase.ready && run.waiting(DateTime.now());
    if (waiting) {
      _riseTimer ??= Timer(
        run.targets.first.rise!.difference(DateTime.now()),
        () {
          _riseTimer = null;
          if (mounted) setState(() {});
        },
      );
    }
    final idle = (phase == SimpleRunPhase.none && !_pickable()) || waiting;
    final tap = run.busy ? null : _tap(phase);
    final ring = switch (phase) {
      _ when idle => AraColors.textDisabled,
      // White like every mode's ring; the paused colour while paused.
      SimpleRunPhase.ready || SimpleRunPhase.none => AraColors.textPrimary,
      SimpleRunPhase.paused => simplePausedColour(ref),
      // Greyed out once the pause is asked: the frame in hand finishes first
      // (user, 2026-11-11).
      SimpleRunPhase.pausing => AraColors.textDisabled,
      _ => AraColors.textPrimary.withValues(alpha: 0.25),
    };
    final pauseBars =
        phase == SimpleRunPhase.imaging || phase == SimpleRunPhase.pausing;
    return Semantics(
      button: true,
      enabled: tap != null || holdable,
      label: switch (phase) {
        _ when waiting => context.l10n.planRisesAtCap(
          _hhmm(run.targets.first.rise!.toLocal()),
        ),
        _ when idle => context.l10n.planPickTargetFirst,
        SimpleRunPhase.ready ||
        SimpleRunPhase.none => context.l10n.planStartImaging,
        SimpleRunPhase.imaging when flipWait => context.l10n.planPauseAfterFlip,
        SimpleRunPhase.imaging => context.l10n.planPauseAfterFrame,
        SimpleRunPhase.pausing => context.l10n.planPausingLabel,
        SimpleRunPhase.paused => context.l10n.planResume,
        _ => context.l10n.planStartImaging,
      },
      onTap: tap,
      customSemanticsActions: holdable
          ? {
              CustomSemanticsAction(label: context.l10n.planStopTheRun): () =>
                  ref.read(simpleRunProvider.notifier).stop(),
            }
          : null,
      child: Listener(
        onPointerDown: (e) {
          _viaPointer = true;
          // Only a finger or the main mouse button, as the Emergency Stop.
          if (e.kind == PointerDeviceKind.mouse &&
              e.buttons != kPrimaryButton) {
            return;
          }
          _down = true;
          _held = false;
          if (holdable) _hold.forward(from: 0);
        },
        onPointerUp: (_) {
          scheduleMicrotask(() => _viaPointer = false);
          if (!_down) return;
          _down = false;
          // Up to 0.8 s is a tap (gloves, cold hands); longer, short of 2 s,
          // cancels the hold.
          final quick = _hold.value < 0.4;
          _hold.value = 0;
          if (!_held && (quick || !holdable)) {
            // Greyed out: a tap says why (user, 2026-11-04).
            tap == null && (idle || waiting || phase == SimpleRunPhase.pausing)
                ? simpleSayWhy(
                    context,
                    phase == SimpleRunPhase.pausing
                        ? context.l10n.planPausingAfterFrame
                        : waiting
                        ? context.l10n.planRisesAtCap(
                            _hhmm(run.targets.first.rise!.toLocal()),
                          )
                        : context.l10n.planPickTargetFirst,
                  )
                : tap?.call();
          }
        },
        onPointerCancel: (_) {
          scheduleMicrotask(() => _viaPointer = false);
          _down = false;
          _hold.value = 0;
        },
        child: InkResponse(
          radius: 36,
          onTap: () {
            if (!_viaPointer) tap?.call();
          },
          child: Stack(
            clipBehavior: Clip.none,
            alignment: Alignment.center,
            children: [
              Container(
                key: const Key('simple-shutter'),
                width: 60,
                height: 60,
                decoration: BoxDecoration(
                  shape: BoxShape.circle,
                  border: Border.all(color: ring, width: 3),
                ),
                alignment: Alignment.center,
                child: Icon(
                  pauseBars ? Icons.pause : Icons.play_arrow,
                  size: 40,
                  color: idle || phase == SimpleRunPhase.pausing ? AraColors.textDisabled : AraColors.textPrimary,
                ),
              ),
              // The frame exposing, or the hold to stop over it.
              if (_hold.value > 0 || (pauseBars && exposing))
                SizedBox(
                  width: 60,
                  height: 60,
                  child: TweenAnimationBuilder<double>(
                    tween: Tween(
                      end: _hold.value > 0
                          ? _hold.value
                          : (camera?.exposureProgressPct ?? 0) / 100,
                    ),
                    duration: _hold.value > 0
                        ? Duration.zero
                        : const Duration(milliseconds: 900),
                    builder: (_, v, _) => AdaptiveProgressIndicator(
                      value: v,
                      strokeWidth: 3,
                      valueColor: AlwaysStoppedAnimation(
                        _hold.value > 0
                            ? AraColors.accentError
                            : AraColors.selectionBg,
                      ),
                    ),
                  ),
                ),
              if (_hold.value > 0)
                Positioned(
                  right: 60 + kSimpleInset + 6,
                  child: SimplePanel(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 12,
                      vertical: 8,
                    ),
                    child: Text(
                      context.l10n.planHoldToStop,
                      style: kSimpleText,
                      maxLines: 1,
                      softWrap: false,
                    ),
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }
}

TextStyle get _dim => kSimpleText.copyWith(color: AraColors.textSecondary);


TextStyle get _head =>
    kSimpleText.copyWith(fontSize: 16, fontWeight: FontWeight.w600);

const _kFramePresets = [5, 10, 12, 15, 20, 30, 40, 60, 90, 120, 180, 240, 300];

/// Plan's sheet, centred over the image: the target (a search, or Pick in
/// Sky), a row per filter (sub length and frames), then Create plan (Save
/// plan once one exists); once the run ends, Night done. The shutter starts
/// the plan.
/// The sheet's list column (QUEUE, tonight's best) on a wide window.
const double _kListsWidth = 240;

/// The window width from which the lists sit beside the plan.
const double _kTwoColumnWidth = 960;

class SimpleRunSheet extends ConsumerStatefulWidget {
  const SimpleRunSheet({
    super.key,
    required this.onShowSky,
    required this.onPickInSky,
    required this.onCreated,
    this.editing,
  });

  /// The queued target being edited (its rows are in the plan rows); null
  /// when planning the next target.
  final int? editing;

  /// A search runs in the planetarium, so it shows behind the sheet.
  final VoidCallback onShowSky;

  /// Closes the sheet for the sky; Sky's card brings it back.
  final VoidCallback onPickInSky;
  final VoidCallback onCreated;

  @override
  ConsumerState<SimpleRunSheet> createState() => _SimpleRunSheetState();
}

class _SimpleRunSheetState extends ConsumerState<SimpleRunSheet> {
  final _query = TextEditingController();

  /// "M42 added · 3 in the queue", until the next pick.
  String? _added;

  /// When the plan starts: at dark by default (before dark), now when
  /// [_startNow], or at the [_pickedStart] the wheel set.
  bool _startNow = false;
  DateTime? _pickedStart;

  /// Bumped by an arrow key, so the time wheel re-reads [_pickedStart]
  /// (it reads its initial time once); a finger's turn leaves it alone.
  int _timeKeyed = 0;
  bool _picking = false;

  // #1560 — the dawn of the night a start at [start] runs into (local), and
  // whether it starts in the dark; a minute-by-minute sun scan, so worked out
  // once per minute and site (Start now is a new instant on every rebuild).
  (DateTime, SiteSettings, DateTime?, bool)? _night;
  (DateTime?, bool) _nightOf(DateTime start) {
    final site = ref.read(siteSettingsProvider);
    final minute = DateTime(
      start.year,
      start.month,
      start.day,
      start.hour,
      start.minute,
    );
    if (_night case (final m, final s, final dawn, final dark)
        when m == minute && identical(s, site)) {
      return (dawn, dark);
    }
    final darkAt = darkStart(site, minute.toUtc());
    final dawn = nightEnd(site, darkAt ?? minute.toUtc())?.toLocal();
    final dark = darkAt == null && dawn != null;
    _night = (minute, site, dawn, dark);
    return (dawn, dark);
  }

  /// When the plan stops at dawn, or null when it won't: a queue stops at
  /// dawn when it starts in the dark (settled at the start).
  DateTime? _stopsAtDawn(DateTime start) {
    final (dawn, dark) = _nightOf(start);
    return dark ? dawn : null;
  }

  @override
  void initState() {
    super.initState();
    // The rows start from the profile's filters and default exposure.
    final container = ProviderScope.containerOf(context, listen: false);
    hydratePlanningSettings(container);
    // The plan's gain starts from the camera's (an edit, from its target's);
    // after this frame, since a provider can't change while the tree builds.
    final editing = widget.editing;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      final targets = container.read(simpleRunProvider).targets;
      container
          .read(simpleRunGainProvider.notifier)
          .seed(
            (editing != null && editing < targets.length
                    ? targets[editing].gain
                    : null) ??
                container.read(exposureControllerProvider).gain,
          );
    });
  }

  @override
  void dispose() {
    _query.dispose();
    super.dispose();
  }

  void _search() {
    final q = _query.text.trim();
    if (q.isEmpty) return;
    widget.onShowSky();
    ref.read(planetariumSearchProvider.notifier).search(q);
    setState(() => _suggesting = false);
  }

  /// Whether the typed text shows its suggestions (closed by a pick or a
  /// search, opened again by typing).
  bool _suggesting = false;

  @override
  Widget build(BuildContext context) {
    final run = ref.watch(simpleRunProvider);
    final info = ref.watch(sequenceRunStateProvider).value;
    return SimplePanel(
      padding: const EdgeInsets.all(18),
      // A short window scrolls the sheet rather than cutting it.
      child: SingleChildScrollView(
        child: simpleRunPhase(run, info) == SimpleRunPhase.done
            ? SizedBox(
                width: 400,
                child: _NightDone(
                  run: run,
                  info: info!,
                ),
              )
            : _plan(run),
      ),
    );
  }

  Widget _plan(SimpleRun run) {
    // An edit whose target went (deleted, or the queue ended) plans anew.
    final editing =
        widget.editing != null && widget.editing! < run.targets.length
        ? widget.editing
        : null;
    ref.listen(planetariumSelectionProvider, (_, next) {
      if (next != null && _added != null) setState(() => _added = null);
    });
    final sel = ref.watch(planetariumSelectionProvider);
    final steps = ref.watch(simpleRunPlanProvider);
    final plan = ref.read(simpleRunPlanProvider.notifier);
    final filters = ref.watch(filterSetProvider).filters;
    final seconds = steps.fold(
      0.0,
      (t, s) => t + s.exposureSeconds * s.frameCount,
    );
    final k =
        ref.watch(skyModelCalibrationProvider).value?.appliedK ?? const {};
    final suggested = [
      for (final s in steps)
        sel == null
            ? s.exposureSeconds
            : suggestedSubSeconds(
                ref,
                // The unnamed row (no filter choice) is the set's one filter.
                filterName: s.filterName.isEmpty
                    ? filters.firstOrNull?.name ?? ''
                    : s.filterName,
                raDeg: sel.raJ2000Hours * 15,
                decDeg: sel.decJ2000Deg,
                defaultExposure:
                    ref
                        .watch(imagingDefaultsProvider)
                        .defaultExposure
                        .inMilliseconds /
                    1000,
                skyCalibrationK: k,
              ),
    ];
    final hint = simpleRunSuggestion(steps, suggested);
    Duration dur(double s) => Duration(milliseconds: (s * 1000).round());
    // The filter column fits the longest name ("L-eXtreme"); the tiles share
    // what's left.
    final nameStyle = kSimpleText.copyWith(fontWeight: FontWeight.w600);
    double widthOf(String name) {
      final painter = TextPainter(
        text: TextSpan(
          text: name,
          style: DefaultTextStyle.of(context).style.merge(nameStyle),
        ),
        textDirection: TextDirection.ltr,
        textScaler: MediaQuery.textScalerOf(context),
      )..layout();
      final w = painter.width.ceilToDouble();
      painter.dispose();
      return w;
    }

    final nameWidth = steps
        .map((s) => widthOf(s.filterName))
        .fold(32.0, math.max);
    // Below the horizon it can be queued, not started; while imaging the
    // sheet only adds (the shutter has the run).
    final imaging =
        ref.watch(sequenceRunStateProvider).value?.state?.isActive ?? false;
    final off = sel == null || sel.belowHorizon || run.busy || imaging;
    // Preflight's plan step only queues; its green button moves on (or, with
    // focus and polar alignment done, starts imaging).
    final inPreflight = ref.watch(simplePreflightProvider) == SimplePreflightStep.plan;
    final preflightNext = inPreflight
        ? simplePreflightNext(ref, after: SimplePreflightStep.plan)
        : null;
    final now = ref.watch(clockProvider)();
    final dark = ref.watch(simpleDarkStartProvider);
    final canSchedule = ref.watch(simpleScheduleAllowedProvider);
    final startAt = _startNow || !canSchedule ? null : _pickedStart ?? dark;
    final queueOff = sel == null || sel.neverRises || run.busy;
    final best = (ref.watch(tonightSkyProvider).value ?? const [])
        .where((o) => o.maxAltitudeDeg > 0)
        .take(4)
        .toList();
    // A wide window puts the lists in a column beside the plan, so the sheet
    // stays short enough to clear the status pill.
    final wide =
        best.isNotEmpty &&
        editing == null &&
        MediaQuery.sizeOf(context).width >= _kTwoColumnWidth;
    final lists = <Widget>[
      // Tonight's best, from the full UI's ranking: one tap picks a target.
      if (best.isNotEmpty) ...[
        Text(context.l10n.planTonightsBest, style: AraText.caption),
        Wrap(
          spacing: AraSpace.s8,
          runSpacing: AraSpace.s8,
          children: [
            for (final o in best)
              // 52 px at 1x, growing with the user's text size.
              Semantics(
                selected: sel?.name == o.name,
                child: SizedBox(
                  width: wide ? _kListsWidth : (400 - AraSpace.s8) / 2,
                  child: Material(
                    color: sel?.name == o.name
                        ? AraColors.selectionBg
                        : AraColors.tileFill,
                    borderRadius: BorderRadius.circular(AraRadius.simplePanel),
                    child: InkWell(
                      key: Key('simple-run-suggestion-${o.name}'),
                      borderRadius: BorderRadius.circular(AraRadius.simplePanel),
                      onTap: () => ref
                          .read(planetariumSelectionProvider.notifier)
                          .set(simpleSelectionOf(o)),
                      child: ConstrainedBox(
                        constraints: const BoxConstraints(minHeight: 52),
                        child: Padding(
                          padding: const EdgeInsets.symmetric(
                            horizontal: 12,
                            vertical: 6,
                          ),
                          child: Column(
                            mainAxisAlignment: MainAxisAlignment.center,
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Text(
                                o.name,
                                style: kSimpleText.copyWith(
                                  fontWeight: FontWeight.w600,
                                  // White on the blue reads at 4.6:1.
                                  color: sel?.name == o.name
                                      ? AraColors.selectionFg
                                      : null,
                                ),
                                maxLines: 1,
                                overflow: TextOverflow.ellipsis,
                              ),
                              Text(
                                simpleSuggestionLine(context.l10n, o),
                                style: _dim.copyWith(
                                  fontSize: AraFontSize.simpleCaption,
                                  color: sel?.name == o.name
                                      ? AraColors.selectionFg
                                      : null,
                                ),
                                maxLines: 1,
                                overflow: TextOverflow.ellipsis,
                              ),
                            ],
                          ),
                        ),
                      ),
                    ),
                  ),
                ),
              ),
          ],
        ),
      ],
    ];
    final main = Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s8,
      children: [
        // No title: the chip (or Preflight's step strip) already says Plan
        // (user, 2026-11-04); editing names the target.
        if (editing != null)
          Text(
            context.l10n.planEditTarget(run.targets[editing].name),
            style: _head,
          ),
        if (!wide && editing == null) ...lists,
        // Editing keeps the target: to change it, delete it and add another.
        if (editing == null) ...[
          TextField(
            key: const Key('simple-run-search'),
            controller: _query,
            style: kSimpleText,
            textInputAction: TextInputAction.search,
            onChanged: (_) => setState(() => _suggesting = true),
            onSubmitted: (_) => _search(),
            decoration: InputDecoration(
              hintText: context.l10n.planSearchObjects,
              prefixIcon: const Icon(Icons.search, size: 20),
              suffixIcon: IconButton(
                tooltip: context.l10n.planSearch,
                icon: const Icon(Icons.arrow_forward, size: 20),
                onPressed: _search,
              ),
            ),
          ),
          // Matching objects as you type; a tap sets the one picked as the target.
          if (_suggesting)
            for (final (i, hit) in searchSuggestions(
              ref.watch(bundledCatalogProvider).value ?? const [],
              _query.text,
              limit: 5,
            ).indexed)
              SimpleSuggestion(
                key: Key('simple-run-suggestion-$i'),
                hit: hit,
                onTap: () {
                  _query.text = hit.label;
                  // The sheet stays up, so the keyboard must not stay over it.
                  FocusManager.instance.primaryFocus?.unfocus();
                  // The target at once, and the Plan sheet stays up.
                  ref
                      .read(planetariumSelectionProvider.notifier)
                      .set(simplePickDso(ref, hit.dso));
                  setState(() => _suggesting = false);
                },
              ),
          Row(
            children: [
              Expanded(
                child: sel == null
                    ? Text(context.l10n.planSearchOrPick, style: _dim)
                    : Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            sel.name,
                            style: kSimpleText.copyWith(
                              fontWeight: FontWeight.w600,
                            ),
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                          ),
                          Text(
                            simpleTargetStatus(sel, context.l10n),
                            style: sel.belowHorizon
                                ? kSimpleText.copyWith(
                                    color: AraColors.accentBusy,
                                  )
                                : _dim,
                          ),
                        ],
                      ),
              ),
              TextButton(
                key: const Key('simple-run-pick-in-sky'),
                onPressed: widget.onPickInSky,
                child: Text(context.l10n.planPickInSky),
              ),
            ],
          ),
        ],
        const Divider(height: 1),
        // The filters as chips: a tap adds a row, another takes it out (the
        // last one stays: a run needs something to take).
        if (steps.first.filterName.isNotEmpty)
          Wrap(
            spacing: AraSpace.s8,
            runSpacing: AraSpace.s8,
            children: [
              for (final f in filters)
                FilterChip(
                  key: Key('simple-run-filter-${f.name}'),
                  // Picked: the segments' solid blue with white words, not a
                  // dim tint under dark ones (user, 2026-11-04).
                  label: Text(
                    f.name,
                    style: kSimpleText.copyWith(
                      color: steps.any((s) => s.filterName == f.name)
                          ? AraColors.selectionFg
                          : AraColors.textPrimary,
                    ),
                  ),
                  selected: steps.any((s) => s.filterName == f.name),
                  showCheckmark: false,
                  selectedColor: AraColors.selectionBg,
                  onSelected: (on) => on
                      ? plan.add(f.name)
                      : plan.remove(
                          steps.indexWhere((s) => s.filterName == f.name),
                        ),
                ),
            ],
          ),
        for (var i = 0; i < steps.length; i++)
          Row(
            // A tile's list folds out below it: its neighbours stay at the top.
            crossAxisAlignment: CrossAxisAlignment.start,
            spacing: AraSpace.s8,
            children: [
              SizedBox(
                width: nameWidth,
                child: Text(
                  steps[i].filterName.isEmpty ? '—' : steps[i].filterName,
                  style: nameStyle,
                  maxLines: 1,
                ),
              ),
              Expanded(
                child: SimplePresetTile<Duration>(
                  key: Key('simple-run-exposure-$i'),
                  label: context.l10n.planExposure,
                  value: dur(steps[i].exposureSeconds),
                  // The suggestion is listed, and marked, even off the presets.
                  options: {
                    ...exposurePresets(minSec: 1, maxSec: 0),
                    dur(suggested[i]),
                  }.toList()..sort(),
                  format: (d) => d == dur(suggested[i])
                      ? context.l10n.planExposureSuggested(formatExposure(d))
                      : formatExposure(d),
                  onSelected: (d) =>
                      plan.setExposure(i, d.inMilliseconds / 1000),
                ),
              ),
              Expanded(
                child: SimplePresetTile<int>(
                  key: Key('simple-run-frames-$i'),
                  label: context.l10n.planFrames,
                  value: steps[i].frameCount,
                  options: _kFramePresets,
                  format: (n) => '× $n',
                  onSelected: (n) => plan.setFrames(i, n),
                  wheel: true,
                ),
              ),
            ],
          ),
        // The plan's gain: starts from the camera's; changing it here leaves
        // the camera's alone, and a queued target keeps the one it was made
        // with.
        SimpleGainSlider(
          id: 'simple-run-gain',
          value:
              ref.watch(simpleRunGainProvider) ??
              ref.watch(exposureControllerProvider).gain,
          onSet: ref.read(simpleRunGainProvider.notifier).set,
        ),
        if (hint != null)
          Row(
            children: [
              Expanded(
                child: Text(context.l10n.planSuggested(hint), style: _dim),
              ),
              TextButton(
                key: const Key('simple-run-use-suggested'),
                onPressed: () {
                  for (var i = 0; i < steps.length; i++) {
                    plan.setExposure(i, suggested[i]);
                  }
                },
                child: Text(context.l10n.planUseAll),
              ),
            ],
          ),
        Row(
          children: [
            Expanded(
              child: Text(
                context.l10n.planFrameCount(simplePlanTotal(steps)),
                style: kSimpleText,
              ),
            ),
            Text(simpleRunDuration(seconds), style: _dim),
          ],
        ),
        // The full UI asks before Run; here it only warns (a permanent pier
        // or a daytime test runs fine unaligned).
        if (editing == null &&
            ref.watch(setupPolarAlignStateProvider) != SetupStepState.done &&
            !(ref.watch(simplePermanentPierProvider).value ?? false))
          Text(
            context.l10n.planNotPolarAligned,
            style: kSimpleText.copyWith(color: AraColors.accentBusy),
          ),
        if (_added != null)
          Text(
            _added!,
            style: kSimpleText.copyWith(color: AraColors.accentConnected),
          ),
        if (run.error != null || run.noStorage) _RunError(run),
        if (editing == null) const _TargetFlats(),
        if (inPreflight && editing == null)
          Text(context.l10n.preflightImagingStartsAfter, style: _dim)
        else if (editing == null && !imaging) ...[
          Wrap(
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              Text(
                startAt == null
                    ? context.l10n.planStartsNow
                    : startAt == dark
                    ? context.l10n.planStartsAtDark(_hhmm(startAt))
                    : context.l10n.planStartsAt(_hhmm(startAt)),
                style: _dim,
              ),
              if (startAt != null)
                TextButton(
                  key: const Key('simple-run-start-now'),
                  onPressed: () => setState(() {
                    _startNow = true;
                    _pickedStart = null;
                    _picking = false;
                  }),
                  child: Text(context.l10n.planStartNowInstead),
                )
              else if (dark != null)
                TextButton(
                  key: const Key('simple-run-wait-dark'),
                  onPressed: () => setState(() => _startNow = false),
                  child: Text(context.l10n.planWaitForDark(_hhmm(dark))),
                ),
              if (canSchedule && (startAt == null || startAt == dark))
                TextButton(
                  key: const Key('simple-run-pick-time'),
                  onPressed: () => setState(() {
                    _picking = true;
                    _startNow = false;
                    _pickedStart = simpleNextAt(startAt ?? dark ?? now, now);
                  }),
                  child: Text(context.l10n.planPickATime),
                ),
            ],
          ),
          if (_stopsAtDawn(startAt ?? now) case final dawn?)
            Text(context.l10n.planStopsAtDawn(_hhmm(dawn)), style: _dim),
          if (_picking && canSchedule)
            SizedBox(
              key: const Key('simple-run-time-picker'),
              height: 132,
              child: SimpleWheelKeys(
                debugLabel: 'simple-run-time-wheel',
                // Down is 5 minutes later, as the wheel's next row.
                onStep: (int d) => setState(() {
                  _pickedStart = simpleNextAt(
                    (_pickedStart ?? now).add(Duration(minutes: 5 * d)),
                    now,
                  );
                  _timeKeyed++;
                }),
                child: CupertinoDatePicker(
                  key: ValueKey(_timeKeyed),
                  mode: CupertinoDatePickerMode.time,
                  use24hFormat: MediaQuery.alwaysUse24HourFormatOf(context),
                  minuteInterval: 5,
                  initialDateTime: _pickedStart,
                  onDateTimeChanged: (t) =>
                      setState(() => _pickedStart = simpleNextAt(t, now)),
                ),
              ),
            ),
        ],
        if (editing != null)
          Row(
            spacing: AraSpace.s8,
            children: [
              Expanded(
                child: OutlinedButton(
                  onPressed: widget.onCreated,
                  child: Text(context.l10n.planCancel),
                ),
              ),
              Expanded(
                child: FilledButton(
                  key: const Key('simple-run-save'),
                  onPressed: run.busy
                      ? null
                      : () async {
                          final ok = await ref
                              .read(simpleRunProvider.notifier)
                              .edit(
                                editing,
                                steps,
                                gain: ref.read(simpleRunGainProvider),
                              );
                          if (ok) widget.onCreated();
                        },
                  child: Text(
                    run.busy ? context.l10n.planSaving : context.l10n.planSave,
                  ),
                ),
              ),
            ],
          )
        else
          Row(
            spacing: AraSpace.s8,
            children: [
              Expanded(
                // Solid, so it reads beside the green Create and start (user,
                // 2026-11-04): button-primary is the fill colour.
                child: FilledButton(
                  key: const Key('simple-run-queue'),
                  style: FilledButton.styleFrom(
                    backgroundColor: AraColors.buttonPrimary,
                    foregroundColor: AraColors.selectionFg,
                  ),
                  onPressed: queueOff
                      ? null
                      : () async {
                          final name = sel.name;
                          final l10n = context.l10n;
                          final ok = await ref
                              .read(simpleRunProvider.notifier)
                              .create();
                          if (!ok || !mounted) return;
                          // The sheet stays up for the next pick.
                          final n = ref.read(simpleRunProvider).targets.length;
                          ref
                              .read(planetariumSelectionProvider.notifier)
                              .set(null);
                          setState(
                            () => _added = l10n.planTargetAdded(name, n),
                          );
                        },
                  child: Text(context.l10n.planAddToQueue),
                ),
              ),
              Expanded(
                child: FilledButton(
                  key: const Key('simple-run-start'),
                  style: kSimpleGoStyle,
                  onPressed: inPreflight
                      ? (run.sequenceId == null || run.busy
                            ? null
                            : ref.read(simplePreflightProvider.notifier).next)
                      : off
                      ? null
                      : () async {
                          final ok = await ref
                              .read(simpleRunProvider.notifier)
                              .createAndStart(
                                startAt: imaging ? null : startAt,
                              );
                          if (ok) widget.onCreated();
                        },
                  child: Text(
                    inPreflight
                        ? switch (preflightNext) {
                            SimplePreflightStep.focus => context.l10n.preflightNextFocus,
                            SimplePreflightStep.polarAlign =>
                              context.l10n.preflightNextPolarAlign,
                            SimplePreflightStep.guide => context.l10n.preflightNextGuide,
                            _ => context.l10n.preflightStartImaging,
                          }
                        : run.busy
                        ? context.l10n.planStarting
                        : startAt != null && !imaging
                        ? context.l10n.planStartAt(_hhmm(startAt))
                        : run.sequenceId != null
                        ? context.l10n.planStartNow
                        : context.l10n.planCreateAndStart,
                  ),
                ),
              ),
            ],
          ),
      ],
    );
    if (!wide) return SizedBox(width: 400, child: main);
    return Row(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      spacing: AraSpace.s16,
      children: [
        SizedBox(
          width: _kListsWidth,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            spacing: AraSpace.s8,
            children: lists,
          ),
        ),
        SizedBox(width: 400, child: main),
      ],
    );
  }
}

/// The sheet's QUEUE: each target with its tag, frames done of its total and
/// time left, and × for one that hasn't started.
class _QueueList extends ConsumerWidget {
  const _QueueList({required this.run, required this.onEdit});
  final SimpleRun run;

  /// A target that hasn't started, tapped: edit its rows.
  final ValueChanged<int> onEdit;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final info = ref.watch(sequenceRunStateProvider).value;
    final ours = run.started && info?.sequenceId == run.sequenceId;
    final captured = ours ? info?.framesCaptured ?? 0 : 0;
    final progress = simpleTargetProgress(run.targets, captured);
    final cur = simpleCurrentTarget(run.targets, captured);
    final notifier = ref.read(simpleRunProvider.notifier);
    // A target that hasn't started can be edited, removed and moved.
    bool open(int i) => !run.started || i > cur;
    // The Calibration block has no rows to edit and stays last.
    bool editable(int i) => open(i) && !run.targets[i].calibration;

    Future<void> remove(int i) async {
      final t = run.targets[i];
      final l10n = context.l10n;
      // Read before the await: this row may be gone when it returns.
      final container = ProviderScope.containerOf(context, listen: false);
      await notifier.remove(i);
      if (container.read(simpleRunProvider).sequenceId == null) return;
      container
          .read(simpleMessagesProvider.notifier)
          .show(
            SimpleMessageKind.result,
            l10n.planTargetRemoved(t.name),
            actions: [
              SimpleMessageAction(l10n.planUndo, () => notifier.restore(t, i)),
            ],
          );
    }

    Widget row(int i) {
      final t = run.targets[i];
      final got = progress[i].fold(0, (a, b) => a + b);
      final left = [
        for (var r = 0; r < t.steps.length; r++)
          (t.steps[r].frameCount - progress[i][r]) * t.steps[r].exposureSeconds,
      ].fold(0.0, (a, b) => a + b);
      final content = Row(
        spacing: AraSpace.s8,
        children: [
          // A visible handle: dragging it moves the target in the queue.
          SizedBox(
            width: 28,
            height: 44,
            child: editable(i)
                ? ReorderableDragStartListener(
                    index: i,
                    child: Semantics(
                      label: context.l10n.planMoveTarget(t.name),
                      child: const Icon(
                        Icons.drag_indicator,
                        key: null,
                        size: 20,
                        color: AraColors.textSecondary,
                      ),
                    ),
                  )
                : null,
          ),
          // Two lines, to fit the narrow list column; a target that hasn't
          // started opens its rows for editing.
          Expanded(
            child: InkWell(
              key: Key('simple-run-edit-$i'),
              borderRadius: BorderRadius.circular(AraRadius.container),
              onTap: editable(i) ? () => onEdit(i) : null,
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    t.tag ?? t.name,
                    style: kSimpleText.copyWith(fontWeight: FontWeight.w600),
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  ),
                  Text(
                    '$got / ${simplePlanTotal(t.steps)} · ${simpleRunDuration(left)}',
                    style: _dim.copyWith(fontSize: AraFontSize.simpleCaption),
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  ),
                ],
              ),
            ),
          ),
          SizedBox(
            width: 44,
            height: 44,
            child: open(i)
                ? IconButton(
                    key: Key('simple-run-remove-$i'),
                    tooltip: context.l10n.planRemoveFromQueue(t.name),
                    icon: const Icon(Icons.close, size: 18),
                    onPressed: run.busy ? null : () => remove(i),
                  )
                : null,
          ),
        ],
      );
      // A swipe left removes it too (the × stays for those who don't swipe).
      return Material(
        key: ValueKey('simple-run-row-${t.name}'),
        type: MaterialType.transparency,
        child: !open(i) || run.busy
            ? content
            : Dismissible(
                key: ValueKey('simple-run-swipe-${t.name}'),
                direction: DismissDirection.endToStart,
                background: Container(
                  alignment: Alignment.centerRight,
                  padding: const EdgeInsets.only(right: AraSpace.s16),
                  decoration: BoxDecoration(
                    color: AraColors.emergencyStop,
                    borderRadius: BorderRadius.circular(AraRadius.container),
                  ),
                  child: Text(
                    context.l10n.planRemove,
                    style: const TextStyle(
                      color: AraColors.onFill,
                      fontWeight: FontWeight.w600,
                    ),
                  ),
                ),
                // The row goes when the rig confirms, not before.
                confirmDismiss: (_) async {
                  await remove(i);
                  return false;
                },
                child: content,
              ),
      );
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(
          context.l10n.planQueueHeader(
            run.targets.length,
            simpleRunDuration(
              run.steps.fold(
                0.0,
                (t, s) => t + s.exposureSeconds * s.frameCount,
              ),
            ),
          ),
          style: AraText.caption,
        ),
        ReorderableListView(
          shrinkWrap: true,
          physics: const NeverScrollableScrollPhysics(),
          buildDefaultDragHandles: false,
          onReorderItem: (from, to) {
            if (!open(from) || !open(to)) return; // started ones stay put
            notifier.reorder(from, to);
          },
          children: [
            for (var i = 0; i < run.targets.length; i++)
              KeyedSubtree(
                key: ValueKey('simple-run-item-${run.targets[i].name}'),
                child: KeyedSubtree(
                  key: Key('simple-run-drag-$i'),
                  child: row(i),
                ),
              ),
          ],
        ),
      ],
    );
  }
}

/// Why the last call failed; a missing storage disk offers the full UI's
/// Storage page, where the drive is set up.
class _RunError extends ConsumerWidget {
  const _RunError(this.run);
  final SimpleRun run;

  @override
  Widget build(BuildContext context, WidgetRef ref) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      // A pre-flight refusal; a rig failure went out as a pill, and only the
      // storage hint stays here.
      if (run.error case final error?)
        Text(
          error,
          style: kSimpleText.copyWith(color: AraColors.errorText),
          maxLines: 4,
          overflow: TextOverflow.ellipsis,
        ),
      if (run.noStorage)
        TextButton(
          onPressed: () {
            // The shell opens Settings over Simple Mode.
            openSettingsPanel(ref, 'session.storage');
          },
          child: Text(context.l10n.planOpenStorage),
        ),
    ],
  );
}

class _NightDone extends ConsumerWidget {
  const _NightDone({
    required this.run,
    required this.info,
  });

  final SimpleRun run;
  final SequenceRunStateInfo info;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    // One target: the library's count when it answers (its newest session),
    // else the plan-order estimate, which a queue always uses.
    final multi = run.targets.length > 1;
    final filed = multi ? null : runFramesPerFilter(ref, info);
    final est = simpleTargetProgress(run.targets, info.framesCaptured);
    int done(int t, int r) {
      final s = run.targets[t].steps[r];
      if (filed == null) return est[t][r];
      return s.filterName.isEmpty
          ? filed.values.fold(0, (a, b) => a + b)
          : filed[s.filterName] ?? 0;
    }

    final start = info.startedUtc?.toLocal(),
        end = info.completedUtc?.toLocal();
    final stoppedAt = info.state == SequenceRunState.completed
        ? null
        : info.currentInstructionDescription;
    // #1560 — a queue with a dawn stop completed short of its frames, within a
    // sub of that night's dawn: the dawn stop ended it. The daemon starts no
    // frame that would end after dawn, so such a run ends up to a sub early (2
    // min more for the client's sun model and the park). A run waiting for
    // dark started in daylight, so its night begins at dark.
    final site = ref.watch(siteSettingsProvider);
    final dawn = start == null || run.dawnStop == null
        ? null
        : nightEnd(
            site,
            darkStart(site, start.toUtc()) ?? start.toUtc(),
          );
    final longestSub = run.steps.fold(
      0.0,
      (s, p) => math.max(s, p.exposureSeconds),
    );
    final short =
        info.framesCaptured < run.steps.fold(0, (n, s) => n + s.frameCount);
    final atDawn =
        info.state == SequenceRunState.completed &&
        short &&
        dawn != null &&
        end != null &&
        !end.isBefore(
          dawn.subtract(
            Duration(seconds: longestSub.round()) + const Duration(minutes: 2),
          ),
        );
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s8,
      children: [
        Text(context.l10n.planNightDone, style: _head),
        Text(
          [
            if (!multi) run.targetName ?? info.currentTargetName ?? '',
            if (start != null && end != null) ...[
              '${_hhmm(start)} – ${_hhmm(end)}',
              simpleRunDuration(end.difference(start).inSeconds.toDouble()),
            ],
            if (atDawn) context.l10n.planStoppedAtDawn,
            if (info.state == SequenceRunState.stopped)
              context.l10n.planStopped,
            if (info.state == SequenceRunState.failed) context.l10n.planFailed,
          ].where((s) => s.isNotEmpty).join(' · '),
          style: _dim,
        ),
        if (stoppedAt != null && stoppedAt.isNotEmpty)
          Text(
            context.l10n.planStoppedAt(stoppedAt),
            style: kSimpleText.copyWith(color: AraColors.errorText),
          ),
        for (var t = 0; t < run.targets.length; t++) ...[
          if (multi)
            Text(
              run.targets[t].name,
              style: kSimpleText.copyWith(fontWeight: FontWeight.w600),
            ),
          for (var r = 0; r < run.targets[t].steps.length; r++)
            Row(
              children: [
                Expanded(
                  child: Text(
                    run.targets[t].steps[r].filterName.isEmpty
                        ? context.l10n.planFrames
                        : run.targets[t].steps[r].filterName,
                    style: _dim,
                  ),
                ),
                Text(
                  '${done(t, r)} × ${formatExposure(Duration(milliseconds: (run.targets[t].steps[r].exposureSeconds * 1000).round()))}',
                  style: kSimpleText,
                ),
              ],
            ),
        ],
        if (info.startedUtc case final from?)
          if (info.completedUtc case final to?)
            SimpleNightCalibration(startUtc: from, endUtc: to),
        SizedBox(
          width: double.infinity,
          child: FilledButton(
            key: const Key('simple-run-new-plan'),
            onPressed: ref.read(simpleRunProvider.notifier).newPlan,
            child: Text(context.l10n.planNewPlan),
          ),
        ),
      ],
    );
  }
}

/// The target's flats (#1757, round 2): none, right after its last frame (at
/// its rotator angle), or with the queue's end-of-night flats. Shot from a
/// panel, or a screen the run asks for first.
class _TargetFlats extends ConsumerWidget {
  const _TargetFlats();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = context.l10n;
    final filters = [
      for (final s in ref.watch(simpleRunPlanProvider))
        if (s.filterName.isNotEmpty) s.filterName,
    ];
    if (filters.isEmpty) return const SizedBox.shrink();
    return Column(
      key: const Key('simple-run-flats'),
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s8,
      children: [
        Row(
          children: [
            Text(l10n.planFlats, style: kSimpleText),
            const SizedBox(width: AraSpace.s8),
            Expanded(
              child: Text(
                l10n.planFlatsFor(filters.join(', ')),
                style: _dim,
                overflow: TextOverflow.ellipsis,
              ),
            ),
          ],
        ),
        SimpleSegments<SimpleTargetFlats>(
          keyPrefix: 'simple-run-flats',
          group: l10n.planFlats,
          options: [
            (SimpleTargetFlats.none, l10n.planFlatsNone, 'none'),
            (SimpleTargetFlats.afterTarget, l10n.planFlatsAfterTarget, 'after'),
            (SimpleTargetFlats.endOfNight, l10n.planFlatsEndOfNight, 'end'),
          ],
          selected: ref.watch(simpleRunFlatsProvider),
          onPick: ref.read(simpleRunFlatsProvider.notifier).set,
        ),
        if (ref.watch(simpleRunFlatsProvider) != SimpleTargetFlats.none)
          Text(l10n.planFlatsHint, style: _dim),
      ],
    );
  }
}

/// Plan's queue panel beside the rail (a plan square tapped, or the chip
/// while imaging): while imaging the target in hand, its frames, when the
/// queue ends and a bar per filter; then the queue, where a tap edits a target
/// that hasn't started and × deletes it; then Add target. The shutter pauses
/// and, held, stops.
class SimpleRunPanel extends ConsumerWidget {
  const SimpleRunPanel({super.key, required this.onAdd, required this.onEdit});

  /// Add target: the plan sheet, to queue another.
  final VoidCallback onAdd;

  /// A queued target tapped: the plan sheet on its rows.
  final ValueChanged<int> onEdit;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final run = ref.watch(simpleRunProvider);
    final info = ref.watch(sequenceRunStateProvider).value;
    final ours = info?.sequenceId == run.sequenceId && run.targets.isNotEmpty;
    final allCaptured = info?.framesCaptured ?? 0;
    final cur = ours ? simpleCurrentTarget(run.targets, allCaptured) : 0;
    final target = ours ? run.targets[cur] : null;
    final steps = target?.steps ?? const <FilterPlanStep>[];
    final done = ours
        ? simpleTargetProgress(run.targets, allCaptured)[cur]
        : const <int>[];
    final captured = ours ? done.fold(0, (a, b) => a + b) : allCaptured;
    final remaining = info?.estimatedRemainingSeconds;
    final paused = simpleRunPaused(info?.state);
    final mountStopped = ref.watch(simpleMountStoppedProvider);
    final imaging = info?.state?.isActive ?? false;
    return SimplePanel(
      padding: const EdgeInsets.all(AraSpace.s16),
      child: SizedBox(
        width: 248,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          spacing: AraSpace.s8,
          children: [
            if (imaging) ...[
              Text(
                context.l10n.planImagingTarget(
                  target?.tag ?? target?.name ?? info?.currentTargetName ?? '',
                ),
                style: AraText.caption,
              ),
              Text(
                steps.isEmpty
                    ? context.l10n.planFrameCount(captured)
                    : '$captured / ${simplePlanTotal(steps)}',
                style: const TextStyle(
                  fontSize: AraFontSize.simpleReadingLarge,
                  fontWeight: FontWeight.w300,
                  color: AraColors.textPrimary,
                  fontFeatures: [FontFeature.tabularFigures()],
                ),
              ),
              if (paused && mountStopped)
                Text(
                  context.l10n.planMountStopped,
                  style: kSimpleText.copyWith(color: AraColors.accentBusy),
                ),
              if (remaining != null)
                Text(
                  context.l10n.planEndsLeft(
                    _hhmm(
                      DateTime.now().add(Duration(seconds: remaining.round())),
                    ),
                    simpleRunDuration(remaining),
                  ),
                  style: _dim,
                ),
              // One filter's progress is the headline already.
              if (steps.length > 1)
                Table(
                  columnWidths: const {
                    // As wide as the longest filter name.
                    0: IntrinsicColumnWidth(),
                    1: FlexColumnWidth(),
                    2: FixedColumnWidth(64),
                  },
                  defaultVerticalAlignment: TableCellVerticalAlignment.middle,
                  children: [
                    for (var i = 0; i < steps.length; i++)
                      TableRow(
                        children: [
                          Padding(
                            padding: const EdgeInsets.only(
                              right: AraSpace.s8,
                              top: 2,
                              bottom: 2,
                            ),
                            child: Text(
                              steps[i].filterName.isEmpty
                                  ? '—'
                                  : steps[i].filterName,
                              style: kSimpleText.copyWith(
                                fontWeight: FontWeight.w600,
                              ),
                              maxLines: 1,
                            ),
                          ),
                          LinearProgressIndicator(
                            value: done[i] / steps[i].frameCount,
                            minHeight: 6,
                            borderRadius: BorderRadius.circular(3),
                            color: done[i] == steps[i].frameCount
                                ? AraColors.accentConnected
                                : AraColors.selectionBg,
                            backgroundColor: AraColors.border.withValues(
                              alpha: 0.8,
                            ),
                          ),
                          Text(
                            '${done[i]} / ${steps[i].frameCount}',
                            style: _dim,
                            textAlign: TextAlign.right,
                          ),
                        ],
                      ),
                  ],
                ),
            ],
            // One target imaging is the headline; before that it's the way to edit it.
            if (run.targets.length > 1 || (run.targets.isNotEmpty && !imaging))
              _QueueList(run: run, onEdit: onEdit),
            if (run.error != null || run.noStorage) _RunError(run),
            if (imaging && paused) ...[
              Text(
                context.l10n.planHintResume,
                style: _dim.copyWith(fontSize: AraFontSize.simpleCaption),
              ),
              TextButton(
                onPressed: () => ref
                    .read(simpleRunProvider.notifier)
                    .resume(recenter: false),
                child: Text(context.l10n.planResumeWithoutRecenter),
              ),
              TextButton(
                onPressed: () =>
                    ref.read(simpleRunProvider.notifier).resume(refocus: true),
                child: Text(context.l10n.planRecenterAndRefocus),
              ),
            ] else if (imaging)
              Text(
                context.l10n.planHintPause,
                style: _dim.copyWith(fontSize: AraFontSize.simpleCaption),
              ),
            // Solid like the sheet's Add to queue (user, 2026-11-04).
            FilledButton(
              key: const Key('simple-run-add'),
              style: FilledButton.styleFrom(
                backgroundColor: AraColors.buttonPrimary,
                foregroundColor: AraColors.selectionFg,
              ),
              onPressed: onAdd,
              child: Text(context.l10n.planAddTarget),
            ),
          ],
        ),
      ),
    );
  }
}

/// One search suggestion: the designation that matched, the common name and
/// what it is, 44 pt tall.
/// A catalog suggestion as a row: the designation, then its common name and
/// magnitude. Plan's search and Simple Mode's search list them.
class SimpleSuggestion extends StatelessWidget {
  const SimpleSuggestion({super.key, required this.hit, required this.onTap});

  final DsoSuggestion hit;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final o = hit.dso;
    final detail = [
      if (o.name.isNotEmpty && o.name != hit.label) o.name,
      if (o.magnitude != null)
        context.l10n.planMagnitude(o.magnitude!.toStringAsFixed(1)),
    ].join(' · ');
    return InkWell(
      borderRadius: BorderRadius.circular(AraRadius.container),
      onTap: onTap,
      child: ConstrainedBox(
        constraints: const BoxConstraints(minHeight: 44),
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: AraSpace.s12),
          child: Row(
            spacing: AraSpace.s12,
            children: [
              const Icon(
                Icons.search,
                size: 18,
                color: AraColors.textSecondary,
              ),
              Text(
                hit.label,
                style: kSimpleText.copyWith(fontWeight: FontWeight.w600),
              ),
              if (detail.isNotEmpty)
                Expanded(
                  child: Text(
                    detail,
                    style: kSimpleText.copyWith(color: AraColors.textSecondary),
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }
}
