import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/guider_status.dart';
import '../../state/guider/guide_step_state.dart';
import '../../state/guider/guider_state.dart';
import '../../state/guider/live_guiding_state.dart';
import '../../state/settings/phd2_settings_state.dart';
import '../../theme/ara_colors.dart';
import 'guiding_tune_dialog.dart';

/// Whether the Live tab's guiding strip shows its graph. Root-scoped so a
/// collapse survives a tab switch (the tab bodies are rebuilt on return).
class GuidingStripExpandedNotifier extends Notifier<bool> {
  @override
  bool build() => true;
  void toggle() => state = !state;
}

final guidingStripExpandedProvider =
    NotifierProvider<GuidingStripExpandedNotifier, bool>(
        GuidingStripExpandedNotifier.new);

/// §63.18 live guiding — the strip along the BOTTOM of the Live tab, laid out
/// the way PHD2 and the guider's own web page show guiding: a scrolling RA /
/// Dec error graph with the correction pulses as bars, and the RMS figures
/// beside it. The header stays as a one-line status when the graph is
/// collapsed. Quick-adjust tuning lives in [GuidingTuneDialog] (the Tune
/// button) — the strip is telemetry only.
///
/// Points come from the daemon's per-frame `guider.step` WS events
/// ([guideStepsProvider]); the RMS is the daemon's windowed figure from the
/// guider status, kept fresh by the 2 s poll [liveGuidingRmsProvider] runs
/// while the strip is expanded.
class GuidingStrip extends ConsumerWidget {
  const GuidingStrip({super.key});

  static const _emDash = '—';

  /// Height of the open graph area. Tall enough that a ±2″ trace is readable,
  /// short enough that the frame viewer keeps most of a laptop screen.
  static const double graphHeight = 136;

  /// How many of the most recent steps the graph spans — PHD2's default.
  static const int visibleSteps = 200;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final status = ref.watch(guiderStatusProvider).asData?.value;
    final expanded = ref.watch(guidingStripExpandedProvider);
    // Keeps the guider-status poller alive (autoDispose) only while the graph
    // is open — collapsing the strip stops the 2 s polling.
    if (expanded) ref.watch(liveGuidingRmsProvider);
    final steps = expanded ? ref.watch(guideStepsProvider) : const <GuideStep>[];
    final phd2 = ref.watch(phd2SettingsProvider);
    // Client-side arcsec/px from the §63.5 guide train — the fallback when
    // the daemon has not reported the guider's own pixel scale yet.
    final fallbackScale =
        guiderArcsecPerPixel(phd2.guideFocalLength, phd2.guidePixelSize);
    final scale = _reportedScale(steps) ?? fallbackScale;
    final rms = _liveRms(status, scale);

    return Container(
      decoration: const BoxDecoration(
        color: AraColors.bgPanel,
        border: Border(top: BorderSide(color: AraColors.border)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: [
          InkWell(
            onTap: () =>
                ref.read(guidingStripExpandedProvider.notifier).toggle(),
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 4),
              child: Row(
                children: [
                  Icon(expanded ? Icons.expand_more : Icons.chevron_right,
                      size: 18, color: AraColors.textSecondary),
                  const SizedBox(width: 4),
                  const Icon(Icons.track_changes,
                      size: 16, color: AraColors.textSecondary),
                  const SizedBox(width: 6),
                  Text('Guiding',
                      style: Theme.of(context).textTheme.titleSmall),
                  const SizedBox(width: 12),
                  Text(
                    stateLabel(status),
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(
                          color: _stateColor(status),
                        ),
                  ),
                  const SizedBox(width: 12),
                  Text(
                    'RMS ${_fmtRms(rms?.totalArcsec, rms?.totalPx)}',
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(
                          color: AraColors.textSecondary,
                        ),
                  ),
                  const Spacer(),
                  if (expanded) ...[
                    const _LegendDot(color: AraColors.accentInfo, label: 'RA'),
                    const SizedBox(width: 10),
                    const _LegendDot(color: AraColors.accentError, label: 'Dec'),
                    const SizedBox(width: 8),
                  ],
                  IconButton(
                    tooltip: 'Tune guiding…',
                    visualDensity: VisualDensity.compact,
                    icon: const Icon(Icons.tune,
                        size: 16, color: AraColors.textSecondary),
                    onPressed: () => showGuidingTuneDialog(context),
                  ),
                ],
              ),
            ),
          ),
          if (expanded)
            SizedBox(
              height: graphHeight,
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Expanded(
                    child: Padding(
                      padding: const EdgeInsets.fromLTRB(12, 0, 8, 8),
                      child: _GraphArea(
                        steps: steps,
                        status: status,
                        fallbackScale: fallbackScale,
                      ),
                    ),
                  ),
                  const VerticalDivider(width: 1, color: AraColors.border),
                  SizedBox(
                    width: 180,
                    child: _RmsColumn(rms: rms, scale: scale, steps: steps),
                  ),
                ],
              ),
            ),
        ],
      ),
    );
  }

  /// The guider's own pixel scale, from the newest step that carried one.
  static double? _reportedScale(List<GuideStep> steps) {
    for (var i = steps.length - 1; i >= 0; i--) {
      final s = steps[i].pixelScaleArcsec;
      if (s != null && s > 0) return s;
    }
    return null;
  }

  /// RMS while actively guiding (guiding/dithering), else null → em-dashes.
  /// Arcsec prefers the daemon's own conversion; without it the pixel RMS is
  /// scaled by [scale] when known.
  static _Rms? _liveRms(GuiderStatus? status, double? scale) {
    if (status == null) return null;
    final actively = status.runtimeState == GuiderRuntimeState.guiding ||
        status.runtimeState == GuiderRuntimeState.dithering;
    if (!actively) return null;
    double? arcsec(double? reported, double? px) =>
        reported ?? (px != null && scale != null ? px * scale : null);
    return _Rms(
      totalPx: status.rmsTotal,
      raPx: status.rmsRa,
      decPx: status.rmsDec,
      totalArcsec: arcsec(status.rmsTotalArcsec, status.rmsTotal),
      raArcsec: arcsec(status.rmsRaArcsec, status.rmsRa),
      decArcsec: arcsec(status.rmsDecArcsec, status.rmsDec),
    );
  }

  static String stateLabel(GuiderStatus? status) {
    if (status == null || !status.isConnected) return 'disconnected';
    switch (status.runtimeState) {
      case GuiderRuntimeState.stopped:
        return 'stopped';
      case GuiderRuntimeState.calibrating:
        return 'calibrating';
      case GuiderRuntimeState.guiding:
        return 'guiding';
      case GuiderRuntimeState.paused:
        return 'paused';
      case GuiderRuntimeState.starLost:
        return 'star lost';
      case GuiderRuntimeState.dithering:
        return 'dithering';
      case GuiderRuntimeState.unknown:
        return 'connected';
    }
  }

  static Color _stateColor(GuiderStatus? status) {
    if (status == null || !status.isConnected) {
      return AraColors.accentDisconnected;
    }
    switch (status.runtimeState) {
      case GuiderRuntimeState.guiding:
        return AraColors.accentConnected;
      case GuiderRuntimeState.starLost:
        return AraColors.accentError;
      case GuiderRuntimeState.calibrating:
      case GuiderRuntimeState.dithering:
        return AraColors.accentBusy;
      case GuiderRuntimeState.stopped:
      case GuiderRuntimeState.paused:
      case GuiderRuntimeState.unknown:
        return AraColors.textSecondary;
    }
  }

  static String fmtArcsec(double? v) =>
      v == null ? _emDash : '${v.toStringAsFixed(2)}″';

  static String fmtPx(double? v) =>
      v == null ? _emDash : '${v.toStringAsFixed(2)} px';

  /// Arcsec when known, else the pixel figure — never a bare em-dash while a
  /// number exists.
  static String _fmtRms(double? arcsec, double? px) =>
      arcsec != null ? fmtArcsec(arcsec) : fmtPx(px);
}

class _Rms {
  final double? totalPx, raPx, decPx, totalArcsec, raArcsec, decArcsec;
  const _Rms({
    this.totalPx,
    this.raPx,
    this.decPx,
    this.totalArcsec,
    this.raArcsec,
    this.decArcsec,
  });
}

class _LegendDot extends StatelessWidget {
  const _LegendDot({required this.color, required this.label});
  final Color color;
  final String label;

  @override
  Widget build(BuildContext context) {
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Container(
          width: 10,
          height: 3,
          decoration: BoxDecoration(
              color: color, borderRadius: BorderRadius.circular(2)),
        ),
        const SizedBox(width: 4),
        Text(label,
            style: Theme.of(context)
                .textTheme
                .labelSmall
                ?.copyWith(color: AraColors.textSecondary)),
      ],
    );
  }
}

/// The RMS figures beside the graph: Total / RA / Dec, arcsec with the pixel
/// figure under each, plus the scale the graph is drawn with.
class _RmsColumn extends StatelessWidget {
  const _RmsColumn({required this.rms, required this.scale, required this.steps});
  final _Rms? rms;
  final double? scale;
  final List<GuideStep> steps;

  @override
  Widget build(BuildContext context) {
    final small = Theme.of(context)
        .textTheme
        .labelSmall
        ?.copyWith(color: AraColors.textSecondary);
    return Padding(
      padding: const EdgeInsets.fromLTRB(12, 2, 12, 6),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisAlignment: MainAxisAlignment.spaceBetween,
        children: [
          _cell(context, 'Total', rms?.totalArcsec, rms?.totalPx),
          _cell(context, 'RA', rms?.raArcsec, rms?.raPx),
          _cell(context, 'Dec', rms?.decArcsec, rms?.decPx),
          // Short on purpose: the column is 180 px wide.
          Text(
            scale == null
                ? 'No scale · graph in px'
                : '${scale!.toStringAsFixed(2)}″/px · ${steps.length} steps',
            style: small,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
          ),
        ],
      ),
    );
  }

  Widget _cell(BuildContext context, String label, double? arcsec, double? px) {
    final theme = Theme.of(context).textTheme;
    return Row(
      children: [
        SizedBox(
          width: 36,
          child: Text(label,
              maxLines: 1,
              style: theme.labelSmall?.copyWith(color: AraColors.textSecondary)),
        ),
        Expanded(
          child: Text(GuidingStrip.fmtArcsec(arcsec),
              maxLines: 1, style: theme.bodySmall, textAlign: TextAlign.right),
        ),
        SizedBox(
          width: 64,
          child: Text(GuidingStrip.fmtPx(px),
              maxLines: 1,
              style: theme.labelSmall?.copyWith(color: AraColors.textDisabled),
              textAlign: TextAlign.right),
        ),
      ],
    );
  }
}

class _GraphArea extends StatelessWidget {
  const _GraphArea({
    required this.steps,
    required this.status,
    required this.fallbackScale,
  });
  final List<GuideStep> steps;
  final GuiderStatus? status;
  final double? fallbackScale;

  @override
  Widget build(BuildContext context) {
    final String? empty;
    if (status == null || !status!.isConnected) {
      empty = 'Guider not connected';
    } else if (steps.isEmpty) {
      empty = status!.runtimeState == GuiderRuntimeState.guiding ||
              status!.runtimeState == GuiderRuntimeState.dithering
          ? 'Waiting for guide frames…'
          : 'Not guiding';
    } else {
      empty = null;
    }
    return ClipRRect(
      borderRadius: BorderRadius.circular(6),
      child: Container(
        color: AraColors.bgPanelAlt,
        child: empty != null
            ? Center(
                child: Text(empty,
                    style: Theme.of(context)
                        .textTheme
                        .labelSmall
                        ?.copyWith(color: AraColors.textDisabled)),
              )
            : CustomPaint(
                size: Size.infinite,
                painter: GuideGraphPainter(
                  steps,
                  fallbackScale: fallbackScale,
                  visibleSteps: GuidingStrip.visibleSteps,
                ),
              ),
      ),
    );
  }
}

/// PHD2-style guide graph: time runs left → right with the newest step at the
/// right edge and a fixed [visibleSteps] window, so the trace scrolls as
/// frames arrive. RA error in blue, Dec in red, correction pulses as faint
/// bars from the zero line (RA / Dec in the same hues). The y-axis is in
/// arcsec when a scale is known (the guider's own, else [fallbackScale]) and
/// in guide-camera pixels otherwise; its range snaps to the smallest rung of
/// [yRungs] that holds every visible point, so steady guiding is not drawn
/// as a flat line and one bad frame does not squash the trace forever.
class GuideGraphPainter extends CustomPainter {
  GuideGraphPainter(this.steps,
      {this.fallbackScale, this.visibleSteps = GuidingStrip.visibleSteps});

  final List<GuideStep> steps;
  final double? fallbackScale;
  final int visibleSteps;

  /// Half-range rungs, PHD2's ladder, in whichever unit the graph draws.
  static const List<double> yRungs = [0.5, 1, 2, 4, 8, 16];

  /// The (unit, half-range) the painter draws with — exposed for tests.
  (String unit, double halfRange) scaleFor(List<GuideStep> visible) {
    final inArcsec = visible.any((s) => s.raArcsecWith(fallbackScale) != null ||
        s.decArcsecWith(fallbackScale) != null);
    var peak = 0.0;
    for (final s in visible) {
      final ra = inArcsec ? s.raArcsecWith(fallbackScale) : s.raPx;
      final dec = inArcsec ? s.decArcsecWith(fallbackScale) : s.decPx;
      if (ra != null) peak = math.max(peak, ra.abs());
      if (dec != null) peak = math.max(peak, dec.abs());
    }
    final half = yRungs.firstWhere((r) => r >= peak, orElse: () => yRungs.last);
    return (inArcsec ? '″' : 'px', half);
  }

  @override
  void paint(Canvas canvas, Size size) {
    final visible = steps.length > visibleSteps
        ? steps.sublist(steps.length - visibleSteps)
        : steps;
    final (unit, half) = scaleFor(visible);
    const leftGutter = 34.0;
    final plot = Rect.fromLTWH(leftGutter, 4, size.width - leftGutter - 4,
        size.height - 8);
    final midY = plot.center.dy;
    double yOf(double v) =>
        (midY - (v / half) * (plot.height / 2)).clamp(plot.top, plot.bottom);

    // Grid: zero line, ±half, ±half/2.
    final grid = Paint()
      ..color = AraColors.border
      ..strokeWidth = 1;
    final zero = Paint()
      ..color = AraColors.textDisabled
      ..strokeWidth = 1;
    for (final f in [-1.0, -0.5, 0.5, 1.0]) {
      final y = yOf(f * half);
      canvas.drawLine(Offset(plot.left, y), Offset(plot.right, y), grid);
    }
    canvas.drawLine(Offset(plot.left, midY), Offset(plot.right, midY), zero);
    _label(canvas, '+${_fmt(half)}$unit', Offset(0, plot.top - 2));
    _label(canvas, '0', Offset(0, midY - 6));
    _label(canvas, '−${_fmt(half)}$unit', Offset(0, plot.bottom - 10));

    if (visible.isEmpty) return;
    final slot = plot.width / math.max(1, visibleSteps - 1);
    double xOf(int i) => plot.right - (visible.length - 1 - i) * slot;

    // Pulses first so the traces draw over them. Scaled to the largest pulse
    // in view (≥ 100 ms so a run of tiny nudges stays tiny).
    var maxPulse = 100.0;
    for (final s in visible) {
      maxPulse = math.max(maxPulse, math.max(s.raPulseMs.abs(), s.decPulseMs.abs()));
    }
    final raPulse = Paint()
      ..color = AraColors.accentInfo.withValues(alpha: 0.35)
      ..strokeWidth = math.max(1, slot * 0.6);
    final decPulse = Paint()
      ..color = AraColors.accentError.withValues(alpha: 0.35)
      ..strokeWidth = math.max(1, slot * 0.6);
    for (var i = 0; i < visible.length; i++) {
      final s = visible[i];
      final x = xOf(i);
      if (s.raPulseMs != 0) {
        final h = s.raPulseMs / maxPulse * (plot.height / 2) * 0.8;
        canvas.drawLine(Offset(x, midY), Offset(x, midY - h), raPulse);
      }
      if (s.decPulseMs != 0) {
        final h = s.decPulseMs / maxPulse * (plot.height / 2) * 0.8;
        canvas.drawLine(Offset(x, midY), Offset(x, midY - h), decPulse);
      }
    }

    // Error traces. A null sample (lost star) breaks the line.
    _trace(canvas, visible, xOf, yOf, AraColors.accentInfo,
        (s) => unit == '″' ? s.raArcsecWith(fallbackScale) : s.raPx);
    _trace(canvas, visible, xOf, yOf, AraColors.accentError,
        (s) => unit == '″' ? s.decArcsecWith(fallbackScale) : s.decPx);
  }

  void _trace(Canvas canvas, List<GuideStep> visible, double Function(int) xOf,
      double Function(double) yOf, Color color, double? Function(GuideStep) pick) {
    final paint = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = 1.5
      ..color = color;
    final path = Path();
    var open = false;
    for (var i = 0; i < visible.length; i++) {
      final v = pick(visible[i]);
      if (v == null) {
        open = false;
        continue;
      }
      final p = Offset(xOf(i), yOf(v));
      if (!open) {
        path.moveTo(p.dx, p.dy);
        open = true;
      } else {
        path.lineTo(p.dx, p.dy);
      }
    }
    canvas.drawPath(path, paint);
  }

  static String _fmt(double v) =>
      v == v.roundToDouble() ? v.toStringAsFixed(0) : v.toStringAsFixed(1);

  void _label(Canvas canvas, String text, Offset at) {
    final tp = TextPainter(
      text: TextSpan(
        text: text,
        style: const TextStyle(fontSize: 9, color: AraColors.textSecondary),
      ),
      textDirection: TextDirection.ltr,
    )..layout(maxWidth: 32);
    tp.paint(canvas, at);
  }

  @override
  bool shouldRepaint(GuideGraphPainter old) =>
      old.steps != steps ||
      old.fallbackScale != fallbackScale ||
      old.visibleSteps != visibleSteps;
}
