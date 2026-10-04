import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/guider_status.dart';
import '../../state/guider/guide_graph_settings.dart';
import '../../state/guider/guide_step_state.dart';
import '../../state/guider/guider_state.dart';
import '../../state/guider/live_guiding_state.dart';
import '../../state/settings/phd2_settings_state.dart';
import '../../theme/ara_colors.dart';
import '../../util/guide_graph_stats.dart';
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

/// §63.18 live guiding — the strip along the BOTTOM of the Live tab, built to
/// behave like PHD2's graph window (the guider IS PHD2): a scrolling RA / Dec
/// error graph with the correction pulses as bars, PHD2's markers (dither
/// line, shaded settle window, star-lost marks), its window controls (frames
/// in view, y range, arcsec / px, corrections on/off) and its stats block
/// (RMS RA / Dec / Total, peak per axis, RA oscillation index) — all computed
/// over the frames in view, exactly as PHD2 does. The header stays as a
/// one-line status when the graph is collapsed. Quick-adjust tuning lives in
/// [GuidingTuneDialog] (the Tune button).
///
/// Points come from the daemon's per-frame `guider.step` events, markers from
/// `guider.event` ([guideStepsProvider] / [guideMarkersProvider]); the header
/// state comes from the guider status, kept fresh by the 2 s poll
/// [liveGuidingRmsProvider] runs while the strip is expanded.
class GuidingStrip extends ConsumerWidget {
  const GuidingStrip({super.key});

  static const _emDash = '—';

  /// Height of the open graph area (graph + its control row).
  static const double graphHeight = 164;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final status = ref.watch(guiderStatusProvider).asData?.value;
    final expanded = ref.watch(guidingStripExpandedProvider);
    // Keeps the guider-status poller alive (autoDispose) only while the graph
    // is open — collapsing the strip stops the 2 s polling.
    if (expanded) ref.watch(liveGuidingRmsProvider);
    final settings = ref.watch(guideGraphSettingsProvider);
    final steps = ref.watch(guideStepsProvider);
    final markers =
        expanded ? ref.watch(guideMarkersProvider) : const <GuideMarker>[];
    final phd2 = ref.watch(phd2SettingsProvider);
    // Client-side arcsec/px from the §63.5 guide train — the fallback when
    // the daemon has not reported the guider's own pixel scale yet.
    final fallbackScale =
        guiderArcsecPerPixel(phd2.guideFocalLength, phd2.guidePixelSize);
    final model = GuideGraphModel(
      steps: steps,
      markers: markers,
      settings: settings,
      fallbackScale: fallbackScale,
    );
    final stats = model.stats;

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
                    'RMS ${fmt(stats.rmsTotal, model.unitSuffix)}',
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
                      padding: const EdgeInsets.fromLTRB(12, 0, 8, 6),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          Expanded(
                            child: _GraphArea(model: model, status: status),
                          ),
                          const SizedBox(height: 4),
                          _ControlsRow(settings: settings, model: model),
                        ],
                      ),
                    ),
                  ),
                  const VerticalDivider(width: 1, color: AraColors.border),
                  SizedBox(
                    width: 200,
                    child: _StatsColumn(model: model),
                  ),
                ],
              ),
            ),
        ],
      ),
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

  /// `0.52″` / `0.14 px` / em-dash.
  static String fmt(double? v, String unitSuffix) =>
      v == null ? _emDash : '${v.toStringAsFixed(2)}$unitSuffix';
}

/// Everything the graph, its controls and its stats derive from the raw
/// history + settings, resolved once per build: which steps are in view,
/// which unit they are plotted in, the y half-range, and PHD2's stats over
/// that window. Pure Dart — unit-tested without widgets.
class GuideGraphModel {
  GuideGraphModel({
    required this.steps,
    required this.markers,
    required this.settings,
    required this.fallbackScale,
  })  : visible = steps.length > settings.xRange
            ? steps.sublist(steps.length - settings.xRange)
            : steps {
    scale = _reportedScale(steps) ?? fallbackScale;
    inArcsec = switch (settings.unit) {
      GuideGraphUnit.arcsec => true,
      GuideGraphUnit.px => false,
      GuideGraphUnit.auto => scale != null,
    };
    stats = GuideGraphStats.compute(visible, pick);
    yHalfRange = settings.yHalfRange ?? _autoHalfRange();
  }

  final List<GuideStep> steps;
  final List<GuideMarker> markers;
  final GuideGraphSettings settings;
  final double? fallbackScale;
  final List<GuideStep> visible;
  late final double? scale;
  late final bool inArcsec;
  late final GuideGraphStats stats;
  late final double yHalfRange;

  String get unitSuffix => inArcsec ? '″' : ' px';
  String get unitLabel => inArcsec ? 'arc-sec' : 'px';

  /// (ra, dec) of a step in the graph's unit; null axis when unknown. Arcsec
  /// without any scale is honest about it: null, never a pixel in disguise.
  (double?, double?) pick(GuideStep s) => inArcsec
      ? (s.raArcsecWith(scale), s.decArcsecWith(scale))
      : (s.raPx, s.decPx);

  /// The guider's own pixel scale, from the newest step that carried one.
  static double? _reportedScale(List<GuideStep> steps) {
    for (var i = steps.length - 1; i >= 0; i--) {
      final s = steps[i].pixelScaleArcsec;
      if (s != null && s > 0) return s;
    }
    return null;
  }

  /// Auto y: the smallest rung of PHD2's ladder that holds every visible
  /// point, so steady guiding is not drawn flat and one bad frame does not
  /// squash the trace forever.
  double _autoHalfRange() {
    final peak = math.max(stats.peakRa ?? 0, stats.peakDec ?? 0);
    return GuideGraphSettings.yRanges.firstWhere((r) => r >= peak,
        orElse: () => GuideGraphSettings.yRanges.last);
  }
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

/// PHD2's graph-window controls, under the plot: frames in view, y range,
/// units, corrections, and Clear.
class _ControlsRow extends ConsumerWidget {
  const _ControlsRow({required this.settings, required this.model});
  final GuideGraphSettings settings;
  final GuideGraphModel model;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final n = ref.read(guideGraphSettingsProvider.notifier);
    final small = Theme.of(context)
        .textTheme
        .labelSmall
        ?.copyWith(color: AraColors.textSecondary);
    // Scrolls sideways rather than overflowing when the strip is narrow (a
    // phone in landscape, a split window) — PHD2's row simply clips.
    return SizedBox(
      height: 22,
      child: SingleChildScrollView(
        scrollDirection: Axis.horizontal,
        child: Row(
        children: [
          Text('x:', style: small),
          const SizedBox(width: 2),
          _Pick<int>(
            value: settings.xRange,
            items: [
              for (final x in GuideGraphSettings.xRanges) (x, '$x'),
            ],
            onChanged: n.setXRange,
          ),
          const SizedBox(width: 10),
          Text('y:', style: small),
          const SizedBox(width: 2),
          _Pick<double?>(
            value: settings.yHalfRange,
            items: [
              (null, 'Auto'),
              for (final y in GuideGraphSettings.yRanges)
                (y, '±${y == y.roundToDouble() ? y.toStringAsFixed(0) : y.toStringAsFixed(1)}'),
            ],
            onChanged: n.setYHalfRange,
          ),
          const SizedBox(width: 10),
          _Pick<GuideGraphUnit>(
            value: settings.unit,
            items: const [
              (GuideGraphUnit.auto, 'auto'),
              (GuideGraphUnit.arcsec, 'arc-sec'),
              (GuideGraphUnit.px, 'pixels'),
            ],
            onChanged: n.setUnit,
          ),
          const SizedBox(width: 10),
          InkWell(
            onTap: () => n.setShowCorrections(!settings.showCorrections),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(
                  settings.showCorrections
                      ? Icons.check_box
                      : Icons.check_box_outline_blank,
                  size: 14,
                  color: AraColors.textSecondary,
                ),
                const SizedBox(width: 3),
                Text('Corrections', style: small),
              ],
            ),
          ),
          const SizedBox(width: 16),
          Text(
            model.scale == null
                ? 'no scale'
                : '${model.scale!.toStringAsFixed(2)}″/px',
            style: small,
          ),
          const SizedBox(width: 12),
          InkWell(
            onTap: () {
              ref.read(guideStepsProvider.notifier).clear();
              ref.read(guideMarkersProvider.notifier).clear();
            },
            child: Text('Clear', style: small?.copyWith(color: AraColors.accentInfo)),
          ),
        ],
        ),
      ),
    );
  }
}

/// A compact dropdown — PHD2's small combo boxes — without the Material
/// 48 px minimum height.
class _Pick<T> extends StatelessWidget {
  const _Pick({required this.value, required this.items, required this.onChanged});
  final T value;
  final List<(T, String)> items;
  final ValueChanged<T> onChanged;

  @override
  Widget build(BuildContext context) {
    final style = Theme.of(context).textTheme.labelSmall;
    return PopupMenuButton<T>(
      tooltip: '',
      padding: EdgeInsets.zero,
      onSelected: onChanged,
      itemBuilder: (_) => [
        for (final (v, label) in items)
          PopupMenuItem<T>(value: v, height: 28, child: Text(label, style: style)),
      ],
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
        decoration: BoxDecoration(
          border: Border.all(color: AraColors.border),
          borderRadius: BorderRadius.circular(4),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(items.firstWhere((e) => e.$1 == value, orElse: () => items.first).$2,
                style: style),
            const Icon(Icons.arrow_drop_down, size: 14, color: AraColors.textSecondary),
          ],
        ),
      ),
    );
  }
}

/// PHD2's stats block beside the graph: RMS per axis + total, peak per
/// axis, RA oscillation index — all over the frames in view.
class _StatsColumn extends StatelessWidget {
  const _StatsColumn({required this.model});
  final GuideGraphModel model;

  @override
  Widget build(BuildContext context) {
    final s = model.stats;
    final u = model.unitSuffix;
    final theme = Theme.of(context).textTheme;
    final label = theme.labelSmall?.copyWith(color: AraColors.textSecondary);
    final value = theme.bodySmall;
    Widget row(String name, String v) => Row(
          children: [
            SizedBox(width: 52, child: Text(name, maxLines: 1, style: label)),
            Expanded(
              child: Text(v, maxLines: 1, style: value, textAlign: TextAlign.right),
            ),
          ],
        );
    return Padding(
      padding: const EdgeInsets.fromLTRB(12, 2, 12, 6),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisAlignment: MainAxisAlignment.spaceBetween,
        children: [
          row('RMS RA', GuidingStrip.fmt(s.rmsRa, u)),
          row('RMS Dec', GuidingStrip.fmt(s.rmsDec, u)),
          row('RMS Tot', GuidingStrip.fmt(s.rmsTotal, u)),
          row('Peak RA', GuidingStrip.fmt(s.peakRa, u)),
          row('Peak Dec', GuidingStrip.fmt(s.peakDec, u)),
          row('RA Osc', s.raOscIndex == null ? '—' : s.raOscIndex!.toStringAsFixed(2)),
          Text(
            '${s.samples} of ${model.steps.length} frames · ${model.unitLabel}',
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: label,
          ),
        ],
      ),
    );
  }
}

class _GraphArea extends StatelessWidget {
  const _GraphArea({required this.model, required this.status});
  final GuideGraphModel model;
  final GuiderStatus? status;

  @override
  Widget build(BuildContext context) {
    final String? empty;
    if (status == null || !status!.isConnected) {
      empty = 'Guider not connected';
    } else if (model.visible.isEmpty) {
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
                painter: GuideGraphPainter(model),
              ),
      ),
    );
  }
}

/// PHD2's graph: time runs left → right with the newest frame at the right
/// edge and a fixed window of `xRange` frames, so the trace scrolls as frames
/// arrive. RA error in blue, Dec in red, correction pulses as faint bars from
/// the zero line (same hues). Markers like PHD2: a dashed vertical "Dither"
/// line at each dither, the settle window shaded until settle done, a red ×
/// on the zero line where the star was lost.
class GuideGraphPainter extends CustomPainter {
  GuideGraphPainter(this.model);

  final GuideGraphModel model;

  @override
  void paint(Canvas canvas, Size size) {
    final visible = model.visible;
    final half = model.yHalfRange;
    final unit = model.inArcsec ? '″' : 'px';
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
    final slot = plot.width / math.max(1, model.settings.xRange - 1);
    double xOf(int i) => plot.right - (visible.length - 1 - i) * slot;

    // Markers go under everything else.
    _markers(canvas, plot, visible, xOf, slot);

    // Pulses next, so the traces draw over them. Scaled to the largest pulse
    // in view (≥ 100 ms so a run of tiny nudges stays tiny).
    if (model.settings.showCorrections) {
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
    }

    // Error traces. A null sample (lost star) breaks the line.
    _trace(canvas, visible, xOf, yOf, AraColors.accentInfo, (s) => model.pick(s).$1);
    _trace(canvas, visible, xOf, yOf, AraColors.accentError, (s) => model.pick(s).$2);
  }

  /// x of a marker: between the last frame at or before it and the next one;
  /// null when it predates the window.
  double? _markerX(GuideMarker m, List<GuideStep> visible,
      double Function(int) xOf, double slot) {
    if (visible.isEmpty || m.at.isBefore(visible.first.at)) return null;
    var i = visible.length - 1;
    while (i > 0 && visible[i].at.isAfter(m.at)) {
      i--;
    }
    return xOf(i) + slot / 2;
  }

  void _markers(Canvas canvas, Rect plot, List<GuideStep> visible,
      double Function(int) xOf, double slot) {
    final dither = Paint()
      ..color = AraColors.accentWarning
      ..strokeWidth = 1;
    final settle = Paint()
      ..color = AraColors.accentWarning.withValues(alpha: 0.12);
    final lost = Paint()
      ..color = AraColors.accentError
      ..strokeWidth = 1.5;
    double? settleStart;
    for (final m in model.markers) {
      final x = _markerX(m, visible, xOf, slot);
      switch (m.kind) {
        case GuideMarkerKind.dithered:
        case GuideMarkerKind.settling:
          // The settle window opens at the dither (or at a settling without a
          // dither, e.g. after a star re-acquire) and runs to settle done.
          settleStart ??= x ?? plot.left;
          if (m.kind == GuideMarkerKind.dithered && x != null) {
            _dashedVertical(canvas, x, plot, dither);
            _label(canvas, 'Dither', Offset(x + 2, plot.top), color: AraColors.accentWarning);
          }
        case GuideMarkerKind.settleDone:
          if (settleStart != null) {
            final end = x ?? plot.right;
            canvas.drawRect(
                Rect.fromLTRB(settleStart, plot.top, end, plot.bottom), settle);
            settleStart = null;
          }
        case GuideMarkerKind.starLost:
          if (x != null) {
            final y = plot.center.dy;
            canvas.drawLine(Offset(x - 4, y - 4), Offset(x + 4, y + 4), lost);
            canvas.drawLine(Offset(x - 4, y + 4), Offset(x + 4, y - 4), lost);
          }
        case GuideMarkerKind.guidingStarted:
        case GuideMarkerKind.resumed:
          if (x != null) {
            _dashedVertical(canvas, x, plot,
                Paint()..color = AraColors.accentConnected..strokeWidth = 1);
          }
        case GuideMarkerKind.calibrationStarted:
        case GuideMarkerKind.calibrationComplete:
        case GuideMarkerKind.calibrationFailed:
        case GuideMarkerKind.guidingStopped:
        case GuideMarkerKind.paused:
        case GuideMarkerKind.lockPositionLost:
          break;
      }
    }
    // A settle still in progress shades to the right edge.
    if (settleStart != null) {
      canvas.drawRect(
          Rect.fromLTRB(settleStart, plot.top, plot.right, plot.bottom), settle);
    }
  }

  void _dashedVertical(Canvas canvas, double x, Rect plot, Paint paint) {
    for (var y = plot.top; y < plot.bottom; y += 6) {
      canvas.drawLine(Offset(x, y), Offset(x, math.min(y + 3, plot.bottom)), paint);
    }
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

  void _label(Canvas canvas, String text, Offset at, {Color? color}) {
    final tp = TextPainter(
      text: TextSpan(
        text: text,
        style: TextStyle(fontSize: 9, color: color ?? AraColors.textSecondary),
      ),
      textDirection: TextDirection.ltr,
    )..layout(maxWidth: 40);
    tp.paint(canvas, at);
  }

  @override
  bool shouldRepaint(GuideGraphPainter old) =>
      old.model.steps != model.steps ||
      old.model.markers != model.markers ||
      old.model.settings != model.settings ||
      old.model.fallbackScale != model.fallbackScale;
}
