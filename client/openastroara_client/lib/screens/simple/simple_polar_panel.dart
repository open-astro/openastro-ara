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
import 'simple_preview_panel.dart' show kSimpleText;
import 'simple_shell.dart' show SimplePanel;

/// True while the server runs any phase of the routine.
bool simplePolarActive(PolarAlignLive live) =>
    live.phase == PolarAlignStates.seeding ||
    live.phase == PolarAlignStates.adjusting ||
    live.phase == PolarAlignStates.paused;

/// "14′" while the routine has an error to show, else "Ready": the rail
/// chip's caption in Polar align. Pure — unit-tested.
String simplePolarSummary(PolarAlignLive live) {
  final total = live.totalErrorArcmin;
  if (!simplePolarActive(live) || total == null) return 'Ready';
  return '${total.toStringAsFixed(total < 10 ? 1 : 0)}′';
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

/// Polar align's panel beside the right rail: the phase, the total error with
/// its zone, the bullseye, the altitude and azimuth turns, Start, then Stop
/// and Done (once inside the profile's tolerance).
class SimplePolarPanel extends ConsumerStatefulWidget {
  const SimplePolarPanel({super.key});

  @override
  ConsumerState<SimplePolarPanel> createState() => _SimplePolarPanelState();
}

class _SimplePolarPanelState extends ConsumerState<SimplePolarPanel> {
  bool _busy = false;
  String? _error;
  double _tolerance = const PolarAlignSettings().targetToleranceArcmin;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _hydrate());
  }

  /// The WS stream may have skipped past routine events fired before this
  /// panel opened: seed from REST, and read the profile's tolerance.
  Future<void> _hydrate() async {
    final api = ref.read(polarAlignApiProvider);
    if (api == null) return;
    try {
      final status = await api.getStatus();
      if (status != null && mounted) {
        ref.read(polarAlignLiveProvider.notifier).hydrateFromStatus(status);
      }
      final settings = await api.getSettings();
      if (mounted) setState(() => _tolerance = settings.targetToleranceArcmin);
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

  @override
  Widget build(BuildContext context) {
    final live = ref.watch(polarAlignLiveProvider);
    final connected = ref.watch(polarAlignApiProvider) != null;
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
            if (!active)
              FilledButton(
                key: const Key('simple-polar-start'),
                onPressed: connected && !_busy
                    ? () => _run('start polar alignment', (api) => api.start())
                    : null,
                child: const Text('Start'),
              )
            else
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
                          !_busy && total != null && total < _tolerance
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
