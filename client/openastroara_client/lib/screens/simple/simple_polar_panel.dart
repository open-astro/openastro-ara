import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../models/polar_align.dart';
import '../../services/polar_align_api.dart';
import '../../state/polar_align/polar_align_state.dart';
import '../../state/settings/site_settings_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../util/friendly_error.dart';
import '../../widgets/imaging/polar_align_panel.dart';
import 'simple_autopilot.dart' show AutopilotStep, autopilotProvider;
import 'simple_preview_panel.dart'
    show
        SimplePresetTile,
        SimpleShutterButton,
        exposurePresets,
        formatExposure,
        kSimpleText;
import 'simple_shell.dart'
    show SimplePanel, SimpleSegments, kSimpleGoStyle, simpleStackText;
import '../../state/simple_messages_state.dart';

/// True while the server runs any phase of the routine.
bool simplePolarActive(PolarAlignLive live) =>
    live.phase == PolarAlignStates.seeding ||
    live.phase == PolarAlignStates.adjusting ||
    live.phase == PolarAlignStates.paused;

Duration _exposure(PolarAlignSettings s) =>
    Duration(milliseconds: (s.exposureSeconds * 1000).round());

bool _single(PolarAlignSettings s) => s.loopMode == PolarAlignLoopModes.single;

/// "45 s" over "Single": the rail chip's caption in Polar align, the
/// routine's setup on two lines (one doesn't fit the 60 px chip). Pure —
/// unit-tested.
String simplePolarSummary(PolarAlignSettings s, AppLocalizations l10n) {
  return '${formatExposure(_exposure(s))}\n'
      '${_single(s) ? l10n.polarSingle : l10n.polarLoop}';
}

/// The profile's polar-align settings (exposure, Loop/Single, tolerance). A
/// save applies to the routine's next frame; a failed save rolls back.
class SimplePolarSettings extends AsyncNotifier<PolarAlignSettings> {
  @override
  Future<PolarAlignSettings> build() async {
    final api = ref.watch(polarAlignApiProvider);
    if (api == null) return const PolarAlignSettings();
    try {
      return await api.getSettings();
    } catch (_) {
      return const PolarAlignSettings();
    }
  }

  Future<void> save(PolarAlignSettings next) async {
    final api = ref.read(polarAlignApiProvider);
    final previous = state.value ?? const PolarAlignSettings();
    state = AsyncData(next);
    if (api == null) return;
    try {
      state = AsyncData(await api.putSettings(next));
    } catch (_) {
      state = AsyncData(previous);
      rethrow;
    }
  }
}

final simplePolarSettingsProvider =
    AsyncNotifierProvider<SimplePolarSettings, PolarAlignSettings>(
      SimplePolarSettings.new,
    );

/// The rail's shutter in Polar align: starts the routine; while it runs, its
/// square stops it.
class SimplePolarShutter extends ConsumerWidget {
  const SimplePolarShutter({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final api = ref.watch(polarAlignApiProvider);
    final active = simplePolarActive(ref.watch(polarAlignLiveProvider));
    final notify = userNotifier(context);
    final l10n = context.l10n;
    Future<void> run(String action, Future<void> Function() op) async {
      try {
        await op();
      } catch (e) {
        notify(
          friendlyError(l10n, e, action: action),
          error: true,
          snackBarRed: false,
        );
      }
    }

    return SimpleShutterButton(
      start: true,
      running: active,
      label: active ? l10n.polarStop : l10n.polarStart,
      onPressed: api == null
          ? null
          : active
          ? () => run(l10n.polarActionStop, api.stop)
          : () => run(l10n.polarActionStart, api.start),
    );
  }
}

/// The phase in words. Pure — unit-tested.
String simplePolarPhase(PolarAlignLive live, AppLocalizations l10n) =>
    switch (live.phase) {
      PolarAlignStates.seeding => l10n.polarPhaseSeeding,
      PolarAlignStates.adjusting => l10n.polarPhaseAdjusting,
      PolarAlignStates.paused => l10n.polarPhasePaused,
      PolarAlignStates.failed => l10n.polarPhaseFailed,
      _ => l10n.polarPhaseReady,
    };

/// The polar-align camera's latest frame, refetched after each finished
/// frame; null until there is one. Keeps the previous picture while the next
/// loads (read `.value`).
final simplePolarFrameProvider = FutureProvider.autoDispose<Uint8List?>((
  ref,
) async {
  ref.watch(polarAlignLiveProvider.select((l) => l.lastFrame));
  final api = ref.watch(polarAlignApiProvider);
  return api?.getLiveFrame();
});

/// Polar align's panel beside the right rail: the routine's settings
/// (exposure, Loop/Single); while it runs, the phase, the total error with its
/// zone, the bullseye, the altitude and azimuth turns, Take frame (Single) and
/// Done (once inside the profile's tolerance). The rail's shutter starts and
/// stops it.
class SimplePolarPanel extends ConsumerStatefulWidget {
  const SimplePolarPanel({super.key});

  @override
  ConsumerState<SimplePolarPanel> createState() => _SimplePolarPanelState();
}

class _SimplePolarPanelState extends ConsumerState<SimplePolarPanel> {
  bool _busy = false;
  String? _error;
  final _bolt = BoltNudgeTracker();

  @override
  void initState() {
    super.initState();
    _bolt.track(
      ref.read(polarAlignLiveProvider),
    ); // the first frame is the baseline
    WidgetsBinding.instance.addPostFrameCallback((_) => _hydrate());
  }

  /// The WS stream may have skipped past routine events fired before this
  /// panel opened: seed from REST.
  Future<void> _hydrate() async {
    final api = ref.read(polarAlignApiProvider);
    if (api == null) return;
    try {
      final status = await api.getStatus();
      if (status != null && mounted) {
        ref.read(polarAlignLiveProvider.notifier).hydrateFromStatus(status);
      }
    } catch (_) {
      // Best effort: the panel still follows the WS stream.
    }
  }

  // [action] completes "Couldn't <action>".
  /// True when the rig took it.
  Future<bool> _run(
    String action,
    Future<void> Function(PolarAlignClient api) op,
  ) async {
    final api = ref.read(polarAlignApiProvider);
    if (api == null) return false;
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      await op(api);
      return true;
    } catch (e) {
      if (mounted) {
        setState(() => _error = friendlyError(context.l10n, e, action: action));
      }
      return false;
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  void _save(PolarAlignSettings next) => _run(
    context.l10n.polarActionSave,
    (_) => ref.read(simplePolarSettingsProvider.notifier).save(next),
  );

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final autopilot = ref.watch(autopilotProvider) == AutopilotStep.polarAlign;
    final live = ref.watch(polarAlignLiveProvider);
    ref.listen(
      polarAlignLiveProvider,
      (_, next) => setState(() => _bolt.track(next)),
    );
    final settings =
        ref.watch(simplePolarSettingsProvider).value ??
        const PolarAlignSettings();
    final southern = ref.watch(siteSettingsProvider).latitudeDeg < 0;
    final active = simplePolarActive(live);
    final total = live.totalErrorArcmin;
    final color = zoneColor(total);
    final error =
        _error ??
        (live.phase == PolarAlignStates.failed
            ? live.errorMessage ?? live.errorReason
            : null);

    // A turn as a tile: the axis, a big arrow and the move, read at arm's
    // length while the hands are on the bolts.
    Widget turn(
      String id,
      String axis,
      double? value,
      (IconData, String) hint,
    ) => Container(
      key: Key('simple-polar-turn-$id'),
      padding: const EdgeInsets.all(AraSpace.s8),
      decoration: BoxDecoration(
        color: AraColors.tileFill,
        borderRadius: BorderRadius.circular(10),
      ),
      child: Row(
        spacing: AraSpace.s8,
        children: [
          Icon(hint.$1, size: 44, color: AraColors.textPrimary),
          Expanded(
            child: Column(
              mainAxisAlignment: MainAxisAlignment.center,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(axis, style: AraText.caption, maxLines: 1),
                Text(
                  value == null
                      ? hint.$2
                      : '${hint.$2} ${value.abs().toStringAsFixed(1)}′',
                  style: kSimpleText,
                  maxLines: 2,
                ),
              ],
            ),
          ),
        ],
      ),
    );
    // At large text the tiles go under the bullseye at their own height, and
    // the settings stack: a caption and a two-line move no longer fit 86 px.
    final stack = simpleStackText(context);
    final bullseye = total == null
        ? const SizedBox.shrink()
        : SizedBox.square(
            key: const Key('simple-polar-bullseye'),
            dimension: 180,
            child: CustomPaint(
              painter: BullseyePainter(
                dotFraction: bullseyeDotFraction(
                  live.azErrorArcmin,
                  live.altErrorArcmin,
                  bullseyeRangeArcmin(total),
                  southern: southern,
                ),
                zoneColor: color,
                reticle: nearPoleReticle(total),
              ),
            ),
          );
    final turns = [
      turn(
        'alt',
        l10n.polarAltitude,
        live.altErrorArcmin,
        polarAltHint(l10n, live.altErrorArcmin),
      ),
      turn(
        'az',
        l10n.polarAzimuth,
        live.azErrorArcmin,
        polarAzHint(l10n, live.azErrorArcmin, southern: southern),
      ),
    ];

    return SimplePanel(
      padding: const EdgeInsets.all(AraSpace.s16),
      child: SizedBox(
        width: 420,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          spacing: AraSpace.s8,
          children: [
            // The phase and the total error on one line.
            if (active || live.phase == PolarAlignStates.failed)
              Row(
                spacing: AraSpace.s8,
                children: [
                  Expanded(
                    child: Text(
                      simplePolarPhase(live, l10n),
                      style: kSimpleText,
                    ),
                  ),
                  if (active && total != null) ...[
                    Icon(Icons.circle, size: 12, color: color),
                    Text(
                      '${total.toStringAsFixed(1)}′',
                      style: kSimpleText.copyWith(
                        fontSize: 30,
                        fontWeight: FontWeight.w300,
                      ),
                    ),
                  ],
                ],
              ),
            if (active && total != null) ...[
              Flex(
                direction: stack ? Axis.vertical : Axis.horizontal,
                crossAxisAlignment: stack
                    ? CrossAxisAlignment.stretch
                    : CrossAxisAlignment.center,
                spacing: stack ? AraSpace.s8 : AraSpace.s12,
                children: [
                  // Its own size, centred, when it stands over the tiles.
                  if (stack) Center(child: bullseye) else bullseye,
                  if (stack)
                    ...turns
                  else
                    Expanded(
                      child: SizedBox(
                        height: 180,
                        child: Column(
                          spacing: AraSpace.s8,
                          children: [for (final t in turns) Expanded(child: t)],
                        ),
                      ),
                    ),
                ],
              ),
            ],
            // The full panel's nudge, cut to two lines for the 248 px panel.
            if (active && _bolt.nudge != null)
              Text(
                l10n.polarBoltWorse(
                  _bolt.nudge! ? l10n.polarAltitude : l10n.polarAzimuth,
                ),
                style: kSimpleText.copyWith(color: AraColors.accentBusy),
              ),
            if (active && live.consecutiveSolveFailures > 0)
              Text(
                l10n.polarNoSolve(live.consecutiveSolveFailures),
                style: kSimpleText.copyWith(color: AraColors.accentBusy),
              ),
            if (error != null)
              Text(
                error,
                style: kSimpleText.copyWith(color: AraColors.errorText),
                maxLines: 4,
                overflow: TextOverflow.ellipsis,
              ),
            // The settings for the next frame: a row each like the Focus
            // panel's choices (user, 2026-11-04: one row read oddly); side by
            // side while it runs, so the bullseye panel still fits an iPad.
            Flex(
              direction: active && !stack ? Axis.horizontal : Axis.vertical,
              crossAxisAlignment: active && !stack
                  ? CrossAxisAlignment.center
                  : CrossAxisAlignment.stretch,
              spacing: AraSpace.s8,
              children: [
                for (final w in <Widget>[
                  SimpleSegments<String>(
                    options: [
                      (PolarAlignLoopModes.loop, l10n.polarLoop, 'loop'),
                      (PolarAlignLoopModes.single, l10n.polarSingle, 'single'),
                    ],
                    selected: settings.loopMode,
                    onPick: (mode) => _save(settings.copyWith(loopMode: mode)),
                  ),
                  SimplePresetTile<Duration>(
                    key: const Key('simple-polar-exposure'),
                    label: l10n.polarExposure,
                    value: _exposure(settings),
                    options: exposurePresets(minSec: 0, maxSec: 60),
                    format: formatExposure,
                    onSelected: (d) => _save(
                      settings.copyWith(
                        exposureSeconds: d.inMilliseconds / 1000,
                      ),
                    ),
                  ),
                ])
                  active && !stack ? Expanded(child: w) : w,
              ],
            ),
            if (active)
              Row(
                spacing: AraSpace.s8,
                children: [
                  // Single waits for a tap before each frame; Loop shoots on
                  // its own.
                  if (_single(settings))
                    Expanded(
                      child: FilledButton.tonal(
                        key: const Key('simple-polar-capture'),
                        onPressed: _busy
                            ? null
                            : () => _run(
                                l10n.polarActionCapture,
                                (api) => api.requestCapture(),
                              ),
                        child: Text(l10n.polarTakeFrame),
                      ),
                    ),
                  Expanded(
                    child: FilledButton(
                      key: const Key('simple-polar-done'),
                      style: kSimpleGoStyle,
                      onPressed:
                          !_busy &&
                              total != null &&
                              total < settings.targetToleranceArcmin
                          ? () async {
                              // Read before the await: the panel may have
                              // closed by the time the rig answers.
                              final pilot = ref.read(
                                autopilotProvider.notifier,
                              );
                              final done = await _run(
                                l10n.polarActionComplete,
                                (api) => api.complete(),
                              );
                              // In Guided Run, Done moves on (the routine
                              // is complete: nothing to stop).
                              if (autopilot && done) pilot.polarDone();
                            }
                          : null,
                      child: Text(
                        autopilot
                            ? l10n.guidedDoneStartImaging
                            : l10n.polarDone,
                      ),
                    ),
                  ),
                ],
              ),
          ],
        ),
      ),
    );
  }
}

/// The polar-align camera's latest frame filling the image area while the
/// routine runs.
class SimplePolarFrame extends ConsumerWidget {
  const SimplePolarFrame({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final jpeg = ref.watch(simplePolarFrameProvider).value;
    return ColoredBox(
      color: Colors.black,
      child: jpeg == null
          ? const SizedBox.expand()
          : Center(child: Image.memory(jpeg, gaplessPlayback: true)),
    );
  }
}
