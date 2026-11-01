import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/polar_align.dart';
import '../../services/polar_align_api.dart';
import '../../state/polar_align/polar_align_state.dart';
import '../../state/settings/site_settings_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../util/friendly_error.dart';
import '../../widgets/imaging/polar_align_panel.dart';
import 'simple_preview_panel.dart'
    show SimplePresetTile, SimpleShutterButton, exposurePresets, formatExposure, kSimpleText;
import 'simple_shell.dart' show SimplePanel, SimpleSegment, kSimpleTileFill;

/// True while the server runs any phase of the routine.
bool simplePolarActive(PolarAlignLive live) =>
    live.phase == PolarAlignStates.seeding ||
    live.phase == PolarAlignStates.adjusting ||
    live.phase == PolarAlignStates.paused;

Duration _exposure(PolarAlignSettings s) =>
    Duration(milliseconds: (s.exposureSeconds * 1000).round());

bool _single(PolarAlignSettings s) => s.loopMode == PolarAlignLoopModes.single;

/// "1 s · Loop": the rail chip's caption in Polar align, the routine's setup
/// (as Preview's chip reads "5 s · 100"). Pure — unit-tested.
String simplePolarSummary(PolarAlignSettings s) =>
    '${formatExposure(_exposure(s))} · ${_single(s) ? 'Single' : 'Loop'}';

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

/// The rail's shutter in Polar align: starts the routine; while it runs, the
/// square stops it (Loop) or each tap takes the next frame (Single).
class SimplePolarShutter extends ConsumerWidget {
  const SimplePolarShutter({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final api = ref.watch(polarAlignApiProvider);
    final active = simplePolarActive(ref.watch(polarAlignLiveProvider));
    final single = _single(
      ref.watch(simplePolarSettingsProvider).value ??
          const PolarAlignSettings(),
    );
    final messenger = ScaffoldMessenger.maybeOf(context);
    Future<void> run(String action, Future<void> Function() op) async {
      try {
        await op();
      } catch (e) {
        messenger?.showSnackBar(
          SnackBar(content: Text(friendlyError(e, action: action))),
        );
      }
    }

    return SimpleShutterButton(
      running: active && !single,
      label: !active
          ? 'Start polar alignment'
          : single
          ? 'Take a frame'
          : 'Stop',
      onPressed: api == null
          ? null
          : !active
          ? () => run('start polar alignment', api.start)
          : single
          ? () => run('take a frame', api.requestCapture)
          : () => run('stop polar alignment', api.stop),
    );
  }
}

/// The phase in words. Pure — unit-tested.
String simplePolarPhase(PolarAlignLive live) => switch (live.phase) {
  PolarAlignStates.seeding => 'Finding the pole…',
  PolarAlignStates.adjusting => 'Adjusting',
  PolarAlignStates.paused => 'Paused',
  PolarAlignStates.failed => 'Polar alignment failed',
  _ => 'Ready',
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
/// zone, the bullseye, the altitude and azimuth turns, then Stop and Done
/// (once inside the profile's tolerance). The rail's shutter starts it.
class SimplePolarPanel extends ConsumerStatefulWidget {
  const SimplePolarPanel({super.key});

  @override
  ConsumerState<SimplePolarPanel> createState() => _SimplePolarPanelState();
}

class _SimplePolarPanelState extends ConsumerState<SimplePolarPanel> {
  bool _busy = false;
  String? _error;

  @override
  void initState() {
    super.initState();
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
  Future<void> _run(
    String action,
    Future<void> Function(PolarAlignClient api) op,
  ) async {
    final api = ref.read(polarAlignApiProvider);
    if (api == null) return;
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      await op(api);
    } catch (e) {
      if (mounted) setState(() => _error = friendlyError(e, action: action));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  void _save(PolarAlignSettings next) => _run(
    'save the polar-align settings',
    (_) => ref.read(simplePolarSettingsProvider.notifier).save(next),
  );

  @override
  Widget build(BuildContext context) {
    final live = ref.watch(polarAlignLiveProvider);
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

    Widget turn(String axis, double? value, (IconData, String) hint) => Row(
      spacing: AraSpace.s8,
      children: [
        Expanded(
          child: Text(
            axis,
            style: kSimpleText.copyWith(color: AraColors.textSecondary),
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
          ),
        ),
        Icon(hint.$1, size: 20, color: AraColors.textPrimary),
        Flexible(
          child: Text(
            value == null
                ? hint.$2
                : '${hint.$2} ${value.abs().toStringAsFixed(1)}′',
            style: kSimpleText,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
          ),
        ),
      ],
    );

    return SimplePanel(
      padding: const EdgeInsets.all(AraSpace.s16),
      child: SizedBox(
        width: 248,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          spacing: AraSpace.s8,
          children: [
            if (active || live.phase == PolarAlignStates.failed)
              Text(simplePolarPhase(live), style: kSimpleText),
            if (active && total != null) ...[
              Row(
                spacing: AraSpace.s8,
                children: [
                  Icon(Icons.circle, size: 12, color: color),
                  Text(
                    '${total.toStringAsFixed(1)}′',
                    style: kSimpleText.copyWith(
                      fontSize: 30,
                      fontWeight: FontWeight.w300,
                    ),
                  ),
                ],
              ),
              Center(
                child: SizedBox.square(
                  dimension: 140,
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
                ),
              ),
              turn(
                'Altitude',
                live.altErrorArcmin,
                polarAltHint(live.altErrorArcmin),
              ),
              turn(
                'Azimuth',
                live.azErrorArcmin,
                polarAzHint(live.azErrorArcmin, southern: southern),
              ),
            ],
            if (active && live.consecutiveSolveFailures > 0)
              Text(
                'No solve (${live.consecutiveSolveFailures} in a row). Check the sky.',
                style: kSimpleText.copyWith(color: AraColors.accentBusy),
              ),
            if (error != null)
              Text(
                error,
                style: kSimpleText.copyWith(color: AraColors.accentError),
                maxLines: 4,
                overflow: TextOverflow.ellipsis,
              ),
            SimplePresetTile<Duration>(
              key: const Key('simple-polar-exposure'),
              label: 'Exposure',
              value: _exposure(settings),
              options: exposurePresets(minSec: 0, maxSec: 60),
              format: formatExposure,
              onSelected: (d) => _save(
                settings.copyWith(exposureSeconds: d.inMilliseconds / 1000),
              ),
            ),
            Container(
              decoration: BoxDecoration(
                color: kSimpleTileFill,
                borderRadius: BorderRadius.circular(8),
              ),
              child: Row(
                children: [
                  for (final (mode, label) in const [
                    (PolarAlignLoopModes.loop, 'Loop'),
                    (PolarAlignLoopModes.single, 'Single'),
                  ])
                    Expanded(
                      child: SimpleSegment(
                        label: label,
                        selected: settings.loopMode == mode,
                        radius: 8,
                        onTap: () => _save(settings.copyWith(loopMode: mode)),
                      ),
                    ),
                ],
              ),
            ),
            if (active)
              Row(
                spacing: AraSpace.s8,
                children: [
                  Expanded(
                    child: FilledButton.tonal(
                      key: const Key('simple-polar-stop'),
                      onPressed: _busy
                          ? null
                          : () => _run(
                              'stop polar alignment',
                              (api) => api.stop(),
                            ),
                      child: const Text('Stop'),
                    ),
                  ),
                  Expanded(
                    child: FilledButton(
                      key: const Key('simple-polar-done'),
                      onPressed:
                          !_busy &&
                              total != null &&
                              total < settings.targetToleranceArcmin
                          ? () => _run(
                              'complete polar alignment',
                              (api) => api.complete(),
                            )
                          : null,
                      child: const Text('Done'),
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
