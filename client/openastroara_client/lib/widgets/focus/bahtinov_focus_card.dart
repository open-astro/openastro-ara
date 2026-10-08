import 'dart:math' as math;
import 'dart:typed_data';

import 'package:fl_chart/fl_chart.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/bahtinov_focus.dart';
import '../../models/equipment_device_status.dart';
import '../../state/equipment/focuser_state.dart';
import '../../state/focus/autofocus_live_state.dart';
import '../../state/focus/bahtinov_focus_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../fit_pane.dart';
import 'focus_section.dart';
import 'guide_focus_card.dart' show GuideFocusHint, TurnAdvice, TurnHint;

/// Setup → Smart Focus → Main telescope with "Bahtinov mask" picked (#1299):
/// the user fits the mask, points at a bright star and turns the focuser while
/// the daemon measures the spikes on every frame. The card shows the star crop
/// with the three fitted lines, the central spike's signed offset from the X as
/// the big number, advice relative to the last move, and the trend; Finish asks
/// for the mask to come off before anything else uses the camera.
class BahtinovFocusCard extends ConsumerStatefulWidget {
  /// The Autofocus | Bahtinov mask picker, drawn in the title row.
  final Widget methodSelector;
  const BahtinovFocusCard({super.key, required this.methodSelector});

  @override
  ConsumerState<BahtinovFocusCard> createState() => _BahtinovFocusCardState();

  /// The card's headline and its colour. Pure — unit-tested.
  static (String, Color) headlineFor(BahtinovFocusLive live) {
    final status = live.status;
    final latest = status.latest;
    if (live.finished) {
      return status.hasMeasurement && _lastMeasured(status)?.withinZone == true
          ? ('In focus · mask off', AraColors.accentConnected)
          : ('Mask off', AraColors.textPrimary);
    }
    if (status.state == BahtinovFocusStates.error) {
      return ('Stopped by a camera error', AraColors.accentError);
    }
    if (status.active) {
      if (latest == null) return ('Waiting for the first frame', AraColors.textPrimary);
      if (!latest.detected) return (bahtinovHint(status).title, AraColors.accentWarning);
      final px = formatOffset(latest.offsetPx!.abs());
      if (latest.withinZone) return ('In focus · $px px', AraColors.accentConnected);
      final hint = bahtinovHint(status);
      return (
        '${hint.title} · $px px from focus',
        hint.advice == TurnAdvice.turnBack ? AraColors.accentWarning : AraColors.textPrimary,
      );
    }
    if (_lastMeasured(status) case final last?) {
      return ('Stopped · last ${formatOffset(last.offsetPx!.abs())} px from focus', AraColors.textPrimary);
    }
    return ('Fit the mask, then Start', AraColors.textPrimary);
  }
}

class _BahtinovFocusCardState extends ConsumerState<BahtinovFocusCard> {
  double _exposureSec = 1;
  static const _exposures = [0.5, 1.0, 2.0];

  @override
  Widget build(BuildContext context) {
    final live = ref.watch(bahtinovFocusProvider);
    final focuser = ref.watch(focuserProvider).asData?.value;
    final focuserConnected = focuser?.connectionState == EquipmentConnectionState.connected;
    final autofocusRunning = ref.watch(autofocusLiveProvider.select((s) => s.run.isRunning));
    final status = live.status;
    final active = status.active;
    final latest = status.latest;
    final hasData = live.frame != null || latest != null;
    final measured = status.hasMeasurement;
    final (headline, headlineColor) = BahtinovFocusCard.headlineFor(live);

    final exposure = active && status.exposureSec > 0 ? status.exposureSec : _exposureSec;
    final subhead = [
      if (focuserConnected && focuser != null) 'Focuser at ${focuser.position ?? '—'} · ${focuser.name}' else 'Manual focuser',
      'Bahtinov mask',
      '${_seconds(exposure)} frames',
    ].join(' · ');

    final busySpinner = const SizedBox(
        width: 14, height: 14, child: CircularProgressIndicator(strokeWidth: 2));
    final startStop = active
        ? FilledButton.tonalIcon(
            onPressed: live.busy ? null : () => ref.read(bahtinovFocusProvider.notifier).stop(),
            icon: live.busy ? busySpinner : const Icon(Icons.stop_rounded, size: 18),
            label: const Text('Stop'),
          )
        : FilledButton.icon(
            onPressed: live.busy || autofocusRunning
                ? null
                : () => ref.read(bahtinovFocusProvider.notifier).start(exposureSec: _exposureSec),
            icon: live.busy ? busySpinner : const Icon(Icons.play_arrow_rounded, size: 18),
            label: Text(hasData ? 'Start again' : 'Start'),
          );
    final canFinish = measured && !live.finished;
    final within = active && latest?.withinZone == true;
    final finish = !canFinish
        ? null
        : within
            ? FilledButton.icon(
                onPressed: live.busy ? null : () => _finish(context),
                style: FilledButton.styleFrom(
                  backgroundColor: AraColors.accentConnected,
                  foregroundColor: Colors.black,
                ),
                icon: const Icon(Icons.check_rounded, size: 18),
                label: const Text('Finish'),
              )
            : OutlinedButton.icon(
                onPressed: live.busy ? null : () => _finish(context),
                icon: const Icon(Icons.check_rounded, size: 18),
                label: const Text('Finish'),
              );

    return FocusSection(
      title: 'Main telescope',
      helpKey: 'setup.focusing.bahtinov',
      titleTrailing: widget.methodSelector,
      headline: headline,
      headlineColor: headlineColor,
      subhead: subhead,
      action: Wrap(
        spacing: 8,
        runSpacing: 8,
        children: [?finish, startStop],
      ),
      secondaryAction: SegmentedButton<double>(
        segments: [
          for (final e in _exposures) ButtonSegment(value: e, label: Text(_seconds(e))),
        ],
        selected: {_exposureSec},
        showSelectedIcon: false,
        style: const ButtonStyle(visualDensity: VisualDensity.compact),
        onSelectionChanged: active ? null : (s) => setState(() => _exposureSec = s.first),
      ),
      child: FitColumn(
        children: [
          if (live.error != null || status.error != null) ...[
            InlineNotice.error(live.error ?? status.error!),
            const SizedBox(height: 16),
          ],
          if (autofocusRunning && !active) ...[
            const InlineNotice.info('An autofocus run has the camera. Wait for it to finish, or cancel it on the Autofocus side.'),
            const SizedBox(height: 16),
          ],
          if (live.finished) ...[
            InlineNotice.info(_lastMeasured(status)?.withinZone == true
                ? 'Focused with the Bahtinov mask, and the mask is off. Lock the focuser.'
                : 'The mask is off. The readout never reached the focus zone — start again to check focus.'),
            const SizedBox(height: 16),
          ] else if (measured && !status.zoneFromOptics) ...[
            InlineNotice.info(
                'Set the focal length, aperture and pixel size in Options → Imaging → Optics to judge the critical focus zone. '
                'Until then, in focus means within ${formatOffset(status.zonePx)} px.'),
            const SizedBox(height: 16),
          ],
          if (!hasData)
            const FitFill(child: EmptyState(
              icon: Icons.flare,
              title: 'Fit the mask, then Start',
              message: 'Put the Bahtinov mask over the front of the telescope and point at a bright star. '
                  'Ara uses the brightest star in the frame and shows how far the middle spike sits from the centre of the X.',
            ))
          else ...[
            FitFill(child: FitBand(builder: (context, width, height) {
              // The trend keeps a fixed strip; the picture and the readout
              // take the rest of the height the window leaves.
              final short = AraBreakpoints.isShort(context);
              final trendHeight = short ? 100.0 : (width * 0.11).clamp(140.0, 200.0);
              final trend = SizedBox(height: trendHeight, child: BahtinovTrendChart(status: status));
              Widget frame(double h) => BahtinovFrameView(
                frame: live.frame,
                overlay: live.overlay,
                offsetPx: latest?.offsetPx,
                live: active,
                caption: latest == null
                    ? null
                    : [
                        'frame ${latest.seq}',
                        if (latest.peakAdu > 0) 'peak ${(latest.peakAdu / 1000).toStringAsFixed(1)}k',
                      ].join(' · '),
                height: h,
              );
              final hero = _Hero(status: status);
              if (width >= 760) {
                // The frame stays 4:3 and takes up to 5/8 of the width; the
                // readout gets the rest. Past that the band stops growing.
                final band = math.max(height - trendHeight - 16, short ? 160.0 : 200.0);
                final frameWidth = math.min(band * 4 / 3, (width - 16) * 5 / 8);
                final h = math.min(band, frameWidth * 0.75);
                return Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    SizedBox(
                      height: h,
                      child: Row(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          SizedBox(width: frameWidth, child: frame(h)),
                          const SizedBox(width: 16),
                          Expanded(child: hero),
                        ],
                      ),
                    ),
                    const SizedBox(height: 16),
                    trend,
                  ],
                );
              }
              return Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  frame((width * 0.75).clamp(220.0, 480.0)),
                  const SizedBox(height: 12),
                  hero,
                  const SizedBox(height: 16),
                  trend,
                ],
              );
            })),
          ],
        ],
      ),
    );
  }

  Future<void> _finish(BuildContext context) async {
    if (await confirmMaskRemoved(context) && mounted) {
      await ref.read(bahtinovFocusProvider.notifier).finish();
    }
  }
}

/// Ask the user to take the mask off. True once they say it is off. Shown by
/// Finish and before switching the card back to Autofocus.
Future<bool> confirmMaskRemoved(BuildContext context) async {
  final ok = await showDialog<bool>(
    context: context,
    builder: (context) => AlertDialog(
      icon: const Icon(Icons.flare, color: AraColors.accentWarning),
      title: const Text('Remove the Bahtinov mask'),
      content: const Text(
          'Take the mask off the front of the telescope before you image. '
          'Left on, it puts spikes on every star and costs light.'),
      actions: [
        TextButton(onPressed: () => Navigator.of(context).pop(false), child: const Text('Not yet')),
        FilledButton(onPressed: () => Navigator.of(context).pop(true), child: const Text('Mask removed')),
      ],
    ),
  );
  return ok ?? false;
}

/// The Autofocus | Bahtinov mask picker in the main telescope card's title row.
/// Locked while either method runs; leaving Bahtinov with a session that never
/// finished asks for the mask to come off first.
class MainFocusMethodSelector extends ConsumerWidget {
  final MainFocusMethod method;
  const MainFocusMethodSelector({super.key, required this.method});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final autofocusRunning = ref.watch(autofocusLiveProvider.select((s) => s.run.isRunning));
    final bahtinov = ref.watch(bahtinovFocusProvider);
    final locked = autofocusRunning || bahtinov.status.active || bahtinov.busy;
    return Tooltip(
      message: locked ? 'Stop the running focus first' : '',
      child: SegmentedButton<MainFocusMethod>(
        segments: const [
          ButtonSegment(value: MainFocusMethod.autofocus, label: Text('Autofocus')),
          ButtonSegment(value: MainFocusMethod.bahtinov, label: Text('Bahtinov mask')),
        ],
        selected: {method},
        style: const ButtonStyle(visualDensity: VisualDensity.compact),
        onSelectionChanged: locked
            ? null
            : (s) async {
                final next = s.first;
                if (next == MainFocusMethod.autofocus &&
                    bahtinov.status.hasMeasurement &&
                    !bahtinov.finished) {
                  if (!await confirmMaskRemoved(context)) return;
                  await ref.read(bahtinovFocusProvider.notifier).finish();
                }
                ref.read(mainFocusMethodProvider.notifier).pick(next);
              },
      ),
    );
  }
}

/// The next 1–2–5 step at or above [v] (an axis limit that labels cleanly).
double _niceCeil(double v) {
  final magnitude = math.pow(10, (math.log(v) / math.ln10).floor()).toDouble();
  for (final step in const [1.0, 2.0, 5.0, 10.0]) {
    if (step * magnitude >= v - 1e-9) return step * magnitude;
  }
  return 10 * magnitude;
}

/// The offset with a true minus sign and two decimals ("−1.80").
String formatOffset(double px, {bool signed = false}) {
  final text = px.abs().toStringAsFixed(2);
  if (!signed || px.abs() < 0.005) return text;
  return px < 0 ? '−$text' : '+$text';
}

String _seconds(double s) => s == s.roundToDouble() ? '${s.round()} s' : '$s s';

BahtinovSample? _lastMeasured(BahtinovFocusStatus status) {
  if (status.latest case final l? when l.detected) return l;
  for (final s in status.recent.reversed) {
    if (s.detected) return s;
  }
  return null;
}

/// What to do with the focuser next. The offset's sign says which side of
/// focus the star is on, but which way that is on the focuser depends on how
/// the mask sits and how the image is flipped — so, as on the guide camera, the
/// advice is relative to the user's LAST move. The offset is linear in
/// defocus, so the size of the next move follows from the last one.
///
/// Pure — unit-tested. Compares a 3-frame median with the plateau before the
/// last move: the latest earlier 3-frame median that differs by more than the
/// noise allowance. Comparing with a fixed number of frames back instead
/// forgot the move after a couple of seconds of fast frames and said
/// "Holding" while the user was still deciding where to turn next.
GuideFocusHint bahtinovHint(BahtinovFocusStatus status, {int lookback = 3}) {
  final latest = status.latest;
  if (latest == null) {
    return const GuideFocusHint(TurnAdvice.wait, 'Waiting', 'The first frame is on its way.');
  }
  if (!latest.detected) {
    return switch (latest.problem) {
      BahtinovProblems.noStar => const GuideFocusHint(TurnAdvice.noStars, 'No star',
          'Point at a bright star, or lengthen the exposure.'),
      BahtinovProblems.nearEdge => const GuideFocusHint(TurnAdvice.noStars, 'Star at the edge',
          'Centre the star so its spikes fit in the frame.'),
      _ => const GuideFocusHint(TurnAdvice.noStars, 'No spikes',
          'Fit the mask over the front of the telescope. Far out of focus? Turn until the star shrinks and spikes appear.'),
    };
  }
  final zone = status.zonePx;
  if (latest.withinZone) {
    return GuideFocusHint(TurnAdvice.atBest, 'In focus',
        status.zoneUm != null
            ? 'The middle spike is centred, inside the ±${status.zoneUm!.toStringAsFixed(0)} µm focus zone. Lock the focuser, then Finish.'
            : 'The middle spike is centred. Lock the focuser, then Finish.');
  }
  final offsets = [
    for (final s in status.recent)
      if (s.detected && s.offsetPx != null) s.offsetPx!,
  ];
  const firstMove = GuideFocusHint(TurnAdvice.hold, 'Make a move', 'Turn the focuser a little either way, then watch the number.');
  if (offsets.length <= lookback) return firstMove;
  final now = _medianEndingAt(offsets, offsets.length - 1);
  final tolerance = math.max(zone / 2, 0.15);
  double? earlier;
  for (var end = offsets.length - 1 - lookback; end >= 2; end--) {
    final m = _medianEndingAt(offsets, end);
    // A plateau, not a frame taken while the focuser was still turning:
    // sizing the next move from a mid-move reading said "twice" for "a third".
    if ((m - now).abs() > tolerance && _spreadEndingAt(offsets, end) <= 2 * tolerance) {
      earlier = m;
      break;
    }
  }
  if (earlier == null) return firstMove;
  final moved = now - earlier;
  if (now.sign != earlier.sign && earlier.abs() > zone) {
    return GuideFocusHint(TurnAdvice.turnBack, 'Go back',
        'You passed focus. Turn back about ${_moveSize(now.abs() / moved.abs())} your last move.');
  }
  if (now.abs() < earlier.abs()) {
    return GuideFocusHint(TurnAdvice.keepGoing, 'Keep going',
        'Same way — about ${_moveSize(now.abs() / moved.abs())} your last move again.');
  }
  return const GuideFocusHint(TurnAdvice.turnBack, 'Go back', 'Wrong way — turn back past where you started.');
}

/// A move as a multiple of the last one, in words.
String _moveSize(double r) {
  if (r < 0.29) return 'a quarter of';
  if (r < 0.42) return 'a third of';
  if (r < 0.65) return 'half';
  if (r < 0.85) return 'three quarters of';
  if (r < 1.25) return 'the same as';
  if (r < 1.75) return '1½×';
  if (r < 2.5) return 'twice';
  return 'several times';
}

double _spreadEndingAt(List<double> values, int end) {
  final window = values.sublist(end - 2, end + 1);
  return window.reduce(math.max) - window.reduce(math.min);
}

double _medianEndingAt(List<double> values, int end) {
  final start = end - 2 < 0 ? 0 : end - 2;
  final window = [for (var i = start; i <= end; i++) values[i]]..sort();
  final n = window.length;
  return n.isOdd ? window[n ~/ 2] : (window[n ~/ 2 - 1] + window[n ~/ 2]) / 2;
}

const double _heroFont = 88;
// A short window keeps the readout's caption in view under the figure.
const double _heroFontShort = 64;
const double _statFont = 24;
const double _textFont = 14;
const _tabular = [FontFeature.tabularFigures()];

/// The signed offset as the big number, the zone under it, the advice capsule,
/// then the session facts.
class _Hero extends StatelessWidget {
  final BahtinovFocusStatus status;
  const _Hero({required this.status});

  @override
  Widget build(BuildContext context) {
    final latest = status.latest;
    final offset = latest?.detected == true ? latest!.offsetPx : null;
    final within = latest?.withinZone == true;
    final zone = formatOffset(status.zonePx);
    final defocus = latest?.defocusUm;
    final sub = offset == null
        ? 'px · in focus ≤ $zone'
        : [
            'px',
            if (defocus != null) '≈ ${defocus.abs().toStringAsFixed(0)} µm from focus',
            'in focus ≤ $zone',
          ].join(' · ');
    final best = status.bestOffsetPx;
    return Container(
      padding: const EdgeInsets.fromLTRB(20, 16, 20, 16),
      decoration: BoxDecoration(
        color: AraColors.bgPanelAlt,
        borderRadius: BorderRadius.circular(10),
      ),
      child: LayoutBuilder(
        builder: (context, constraints) => SingleChildScrollView(
          child: ConstrainedBox(
            constraints: BoxConstraints(minHeight: constraints.hasBoundedHeight ? constraints.maxHeight : 0),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                const Text('SPIKE OFFSET', textAlign: TextAlign.center,
                    style: TextStyle(fontSize: _textFont, color: AraColors.textSecondary, letterSpacing: 1.1)),
                const SizedBox(height: 2),
                FittedBox(
                  fit: BoxFit.scaleDown,
                  child: Text(
                    offset == null ? '—' : formatOffset(offset, signed: true),
                    style: TextStyle(
                      fontSize: AraBreakpoints.isShort(context) ? _heroFontShort : _heroFont,
                      fontWeight: FontWeight.w300,
                      height: 1.05,
                      color: within ? AraColors.accentConnected : AraColors.textPrimary,
                      fontFeatures: _tabular,
                    ),
                  ),
                ),
                const SizedBox(height: 4),
                Text(sub, textAlign: TextAlign.center,
                    style: const TextStyle(fontSize: 16, color: AraColors.textSecondary, fontFeatures: _tabular)),
                if (status.active) ...[
                  const SizedBox(height: 14),
                  TurnHint(hint: bahtinovHint(status)),
                ],
                const SizedBox(height: 18),
                const Divider(height: 1, color: Color(0x1FFFFFFF)),
                const SizedBox(height: 14),
                Row(
                  children: [
                    Expanded(child: _Fact(label: 'Best', value: best == null ? '—' : formatOffset(best, signed: true))),
                    Expanded(child: _Fact(
                        label: 'Zone',
                        value: status.zoneUm != null ? '±${status.zoneUm!.toStringAsFixed(0)}µm' : '≤${zone}px')),
                    Expanded(child: _Fact(
                        label: 'Peak',
                        value: latest == null || latest.peakAdu <= 0 ? '—' : '${(latest.peakAdu / 1000).toStringAsFixed(1)}k')),
                    Expanded(child: _Fact(label: 'Frames', value: '${status.seq}')),
                  ],
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _Fact extends StatelessWidget {
  final String label;
  final String value;
  const _Fact({required this.label, required this.value});

  @override
  Widget build(BuildContext context) {
    return Column(
      children: [
        Text(label, style: const TextStyle(fontSize: _textFont, color: AraColors.textSecondary)),
        const SizedBox(height: 2),
        FittedBox(
          fit: BoxFit.scaleDown,
          child: Text(value,
              style: const TextStyle(fontSize: _statFont, fontWeight: FontWeight.w600, fontFeatures: _tabular)),
        ),
      ],
    );
  }
}

/// The star crop with the fitted spikes drawn over it: the X dashed amber, the
/// central spike solid blue, the crossing ringed, and a bracket from the
/// crossing to the central spike labelled with the offset. With no overlay
/// (nothing measured) the daemon sends the whole frame and nothing is drawn.
class BahtinovFrameView extends StatelessWidget {
  final Uint8List? frame;
  final BahtinovOverlay? overlay;
  final double? offsetPx;
  final bool live;
  final String? caption;
  final double height;

  const BahtinovFrameView({
    super.key,
    required this.frame,
    this.overlay,
    this.offsetPx,
    this.live = false,
    this.caption,
    this.height = 300,
  });

  @override
  Widget build(BuildContext context) {
    final jpeg = frame;
    final small = Theme.of(context).textTheme.bodySmall;
    return ClipRRect(
      borderRadius: BorderRadius.circular(10),
      child: Container(
        height: height,
        width: double.infinity,
        color: const Color(0xFF07080B),
        child: Stack(
          children: [
            Positioned.fill(
              child: jpeg == null
                  ? Center(
                      child: Text('Waiting for the first frame…',
                          style: small?.copyWith(color: AraColors.textSecondary)),
                    )
                  : Image.memory(jpeg, gaplessPlayback: true, fit: BoxFit.contain, filterQuality: FilterQuality.medium),
            ),
            if (jpeg != null && overlay != null)
              Positioned.fill(
                child: CustomPaint(painter: _SpikePainter(overlay!, offsetPx)),
              ),
            if (jpeg != null)
              Positioned(
                top: 12,
                left: 12,
                child: Container(
                  padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                  decoration: BoxDecoration(
                    color: (live ? AraColors.accentBusy : AraColors.textSecondary).withValues(alpha: 0.18),
                    borderRadius: BorderRadius.circular(6),
                  ),
                  child: Text(live ? 'LIVE' : 'LAST',
                      style: TextStyle(
                        color: live ? AraColors.accentBusy : AraColors.textSecondary,
                        fontSize: 11,
                        fontWeight: FontWeight.w700,
                        letterSpacing: 1,
                      )),
                ),
              ),
            if (overlay != null)
              Positioned(
                top: 12,
                right: 12,
                child: Container(
                  padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
                  decoration: BoxDecoration(
                    color: Colors.black.withValues(alpha: 0.55),
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: const Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      _LegendSwatch(color: AraColors.accentBusy, dashed: true),
                      SizedBox(width: 6),
                      Text('X spikes', style: TextStyle(fontSize: 12, color: Color(0xFFC8C8C8))),
                      SizedBox(width: 12),
                      _LegendSwatch(color: AraColors.accentInfo),
                      SizedBox(width: 6),
                      Text('Centre spike', style: TextStyle(fontSize: 12, color: Color(0xFFC8C8C8))),
                    ],
                  ),
                ),
              ),
            if (caption != null && jpeg != null)
              Positioned(
                bottom: 12,
                left: 12,
                child: Container(
                  padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                  decoration: BoxDecoration(
                    color: Colors.black.withValues(alpha: 0.55),
                    borderRadius: BorderRadius.circular(6),
                  ),
                  child: Text(caption!, style: const TextStyle(fontSize: 12, color: Color(0xFFC8C8C8))),
                ),
              ),
          ],
        ),
      ),
    );
  }
}

class _LegendSwatch extends StatelessWidget {
  final Color color;
  final bool dashed;
  const _LegendSwatch({required this.color, this.dashed = false});

  @override
  Widget build(BuildContext context) => SizedBox(
        width: 16,
        height: 2,
        child: dashed
            ? Row(children: [
                Expanded(child: ColoredBox(color: color)),
                const SizedBox(width: 2),
                Expanded(child: ColoredBox(color: color)),
                const SizedBox(width: 2),
                Expanded(child: ColoredBox(color: color)),
              ])
            : ColoredBox(color: color),
      );
}

class _SpikePainter extends CustomPainter {
  final BahtinovOverlay overlay;
  final double? offsetPx;
  _SpikePainter(this.overlay, this.offsetPx);

  @override
  void paint(Canvas canvas, Size size) {
    // The crop is square and drawn with BoxFit.contain: centred, side = the
    // shorter of the box's sides.
    final side = math.min(size.width, size.height);
    final scale = side / overlay.cropSize;
    final origin = Offset((size.width - side) / 2, (size.height - side) / 2);
    Offset at(double x, double y) => origin + Offset(x * scale, y * scale);

    final outerPaint = Paint()
      ..color = AraColors.accentBusy.withValues(alpha: 0.85)
      ..strokeWidth = 1.5
      ..style = PaintingStyle.stroke;
    final centralPaint = Paint()
      ..color = AraColors.accentInfo.withValues(alpha: 0.85)
      ..strokeWidth = 1.5
      ..style = PaintingStyle.stroke;
    BahtinovLine? central;
    for (final line in overlay.lines) {
      final a = at(line.x1, line.y1);
      final b = at(line.x2, line.y2);
      if (line.isCentral) {
        central = line;
        canvas.drawLine(a, b, centralPaint);
      } else {
        _dashed(canvas, a, b, outerPaint);
      }
    }
    final cross = at(overlay.intersectionX, overlay.intersectionY);
    canvas.drawCircle(cross, 5, outerPaint);

    // The offset: from the crossing to its foot on the central line.
    if (central != null) {
      final p1 = Offset(central.x1, central.y1);
      final d = Offset(central.x2, central.y2) - p1;
      final len2 = d.dx * d.dx + d.dy * d.dy;
      if (len2 > 0) {
        final i = Offset(overlay.intersectionX, overlay.intersectionY);
        final t = ((i - p1).dx * d.dx + (i - p1).dy * d.dy) / len2;
        final foot = at(p1.dx + d.dx * t, p1.dy + d.dy * t);
        final bracket = Paint()
          ..color = AraColors.textPrimary
          ..strokeWidth = 1.2;
        canvas.drawLine(cross, foot, bracket);
        if (offsetPx != null) {
          final tp = TextPainter(
            text: TextSpan(
              text: '${formatOffset(offsetPx!)} px',
              style: const TextStyle(color: AraColors.textPrimary, fontSize: 13, fontWeight: FontWeight.w600),
            ),
            textDirection: TextDirection.ltr,
          )..layout();
          final mid = Offset((cross.dx + foot.dx) / 2, (cross.dy + foot.dy) / 2);
          tp.paint(canvas, mid + const Offset(10, 6));
        }
      }
    }
  }

  void _dashed(Canvas canvas, Offset a, Offset b, Paint paint) {
    const dash = 6.0, gap = 5.0;
    final total = (b - a).distance;
    if (total <= 0) return;
    final dir = (b - a) / total;
    for (var s = 0.0; s < total; s += dash + gap) {
      canvas.drawLine(a + dir * s, a + dir * math.min(s + dash, total), paint);
    }
  }

  @override
  bool shouldRepaint(_SpikePainter old) => old.overlay != overlay || old.offsetPx != offsetPx;
}

/// The offset per frame (newest right) around zero, with the focus zone as a
/// green band. Frames with no measurement leave a gap.
class BahtinovTrendChart extends StatelessWidget {
  final BahtinovFocusStatus status;
  const BahtinovTrendChart({super.key, required this.status});

  @override
  Widget build(BuildContext context) {
    final samples = status.recent;
    final spots = <FlSpot>[
      for (var i = 0; i < samples.length; i++)
        if (samples[i].detected && samples[i].offsetPx != null) FlSpot(i.toDouble(), samples[i].offsetPx!),
    ];
    if (spots.length < 2) {
      return Container(
        decoration: BoxDecoration(color: AraColors.bgPanelAlt, borderRadius: BorderRadius.circular(10)),
        alignment: Alignment.center,
        child: Text(
          'The offset trend appears after the first measured frames.',
          style: Theme.of(context).textTheme.bodySmall?.copyWith(color: AraColors.textSecondary),
        ),
      );
    }
    final zone = status.zonePx;
    final peak = spots.fold<double>(0, (m, s) => math.max(m, s.y.abs()));
    final limit = _niceCeil(math.max(math.max(peak * 1.15, zone * 3), 1.0));
    return Container(
      decoration: BoxDecoration(color: AraColors.bgPanelAlt, borderRadius: BorderRadius.circular(10)),
      padding: const EdgeInsets.fromLTRB(8, 12, 16, 8),
      child: LineChart(
        LineChartData(
          minX: 0,
          maxX: (samples.length - 1).clamp(1, 1 << 20).toDouble(),
          minY: -limit,
          maxY: limit,
          borderData: FlBorderData(show: false),
          gridData: const FlGridData(show: false),
          rangeAnnotations: RangeAnnotations(horizontalRangeAnnotations: [
            HorizontalRangeAnnotation(
              y1: -zone,
              y2: zone,
              color: AraColors.accentConnected.withValues(alpha: 0.22),
            ),
          ]),
          extraLinesData: ExtraLinesData(horizontalLines: [
            HorizontalLine(y: 0, color: AraColors.textDisabled, strokeWidth: 1),
          ]),
          titlesData: FlTitlesData(
            leftTitles: AxisTitles(
              axisNameWidget: const Text('offset (px)', style: TextStyle(fontSize: 11)),
              sideTitles: SideTitles(
                showTitles: true,
                reservedSize: 40,
                interval: limit,
                getTitlesWidget: (value, meta) => SideTitleWidget(
                  meta: meta,
                  child: Text(
                    value == 0 ? '0' : formatOffset(value, signed: true).replaceFirst(RegExp(r'\.?0+$'), ''),
                    style: const TextStyle(fontSize: 11, color: AraColors.textSecondary),
                  ),
                ),
              ),
            ),
            bottomTitles: const AxisTitles(
              axisNameWidget: Text('frames · green band = critical focus zone', style: TextStyle(fontSize: 11)),
            ),
            topTitles: const AxisTitles(),
            rightTitles: const AxisTitles(),
          ),
          lineTouchData: const LineTouchData(enabled: false),
          lineBarsData: [
            LineChartBarData(
              spots: spots,
              color: const Color(0xFF90CAF9),
              barWidth: 2,
              dotData: FlDotData(
                show: true,
                getDotPainter: (spot, _, _, index) => FlDotCirclePainter(
                  radius: index == spots.length - 1 ? 5 : 3,
                  color: index == spots.length - 1 ? AraColors.accentBusy : const Color(0xFF90CAF9),
                  strokeWidth: 0,
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
