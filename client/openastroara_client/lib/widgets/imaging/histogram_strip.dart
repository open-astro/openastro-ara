import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/frame_histogram.dart';
import '../../services/frames_api.dart';
import '../../state/imaging/last_frame_state.dart';
import '../../state/imaging/stretch_state.dart';
import '../../state/saved_server_state.dart';
import '../../theme/ara_colors.dart';

/// §12c.2 — the RAW 16-bit histogram of the displayed frame, fetched from the
/// server (computed from the FITS pixels, cached beside the preview variants).
/// The screen stretch makes every frame look exposed; this is where clipping
/// and read-noise-floor burial actually show. Bar heights are sqrt-scaled —
/// an astro histogram is a sky-background spike plus a long faint tail, and a
/// linear plot renders the tail invisible.
final frameHistogramProvider =
    FutureProvider.autoDispose.family<FrameHistogram, String>((ref, id) async {
  final server = ref.read(activeServerProvider);
  if (server == null) {
    throw StateError('Not connected to a server.');
  }
  return FramesApi(server).histogram(id);
});

class HistogramStrip extends ConsumerWidget {
  const HistogramStrip({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final id = ref.watch(lastCapturedFrameIdProvider);
    final histogram =
        id == null ? null : ref.watch(frameHistogramProvider(id));
    return Container(
      decoration: const BoxDecoration(
        border: Border(top: BorderSide(color: AraColors.border)),
      ),
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
      child: histogram == null
          ? const SizedBox(height: 68, child: _EmptyBins())
          : histogram.when(
              data: (h) => _HistogramPlot(histogram: h),
              loading: () => const SizedBox(height: 68, child: _EmptyBins()),
              error: (_, _) => const SizedBox(height: 68, child: _EmptyBins()),
            ),
    );
  }
}

/// The pre-first-frame (or unavailable) state: a barely-there baseline so the
/// strip reads as "waiting for data", not as a broken chart.
class _EmptyBins extends StatelessWidget {
  const _EmptyBins();

  @override
  Widget build(BuildContext context) {
    return Align(
      alignment: Alignment.bottomLeft,
      child: Container(
        height: 2,
        color: AraColors.textDisabled.withValues(alpha: 0.15),
      ),
    );
  }
}

class _HistogramPlot extends ConsumerStatefulWidget {
  final FrameHistogram histogram;
  const _HistogramPlot({required this.histogram});

  @override
  ConsumerState<_HistogramPlot> createState() => _HistogramPlotState();
}

class _HistogramPlotState extends ConsumerState<_HistogramPlot> {
  /// Clipping worth flagging: 0.1% of the sensor is ~26k pixels on an
  /// ASI2600 — real stars saturate a few hundred; a blown sky is millions.
  static const _clipWarnFraction = 0.001;

  // Slider positions while dragging — the provider (and the ~1s server
  // re-render it triggers) is only updated on drag END, so scrubbing stays
  // fluid and each release renders exactly one variant.
  double? _dragBlack;
  double? _dragMid;
  double? _dragWhite;

  FrameHistogram get histogram => widget.histogram;

  /// Slider position → ADU value (both normalized 0..1), cube-law: astro
  /// data lives in the bottom few percent of the range, and a linear slider
  /// jumps over the whole signal in one pixel. p=0.25 → 1.6% — the sky
  /// background sits mid-track instead of against the left stop.
  static double posToValue(double p) => p * p * p;

  static double valueToPos(double v) =>
      math.pow(v.clamp(0.0, 1.0), 1 / 3).toDouble();

  void _commit() {
    final current = ref.read(stretchOverrideProvider);
    ref.read(stretchOverrideProvider.notifier).set(StretchOverride(
          black: posToValue(
              _dragBlack ?? valueToPos(current?.black ?? 0)),
          mid: _dragMid ?? current?.mid ?? 0.5,
          white: posToValue(
              _dragWhite ?? valueToPos(current?.white ?? 1)),
        ));
    setState(() {
      _dragBlack = null;
      _dragMid = null;
      _dragWhite = null;
    });
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final lowClip = histogram.lowClipFraction >= _clipWarnFraction;
    final highClip = histogram.highClipFraction >= _clipWarnFraction;
    String pct(double f) => '${(f * 100).toStringAsFixed(1)}%';
    final override = ref.watch(stretchOverrideProvider);
    // Slider space (cube-root of value space) for the thumbs; value space
    // for the cursor lines on the plot.
    final black = _dragBlack ?? valueToPos(override?.black ?? 0);
    final mid = _dragMid ?? override?.mid ?? 0.5;
    final white = _dragWhite ?? valueToPos(override?.white ?? 1);
    final blackValue = posToValue(black);
    final whiteValue = posToValue(white);
    // Quiet, edge-to-edge markers that align with the plot axis above —
    // the default Material treatment (thick blue, thumb-inset track) reads
    // as a form control, not as histogram cursors.
    final sliderTheme = SliderTheme.of(context).copyWith(
      trackHeight: 1,
      activeTrackColor: AraColors.textDisabled,
      inactiveTrackColor: AraColors.border,
      thumbColor: AraColors.textSecondary,
      overlayColor: AraColors.textSecondary.withValues(alpha: 0.08),
      rangeThumbShape: const RoundRangeSliderThumbShape(enabledThumbRadius: 4),
      thumbShape: const RoundSliderThumbShape(enabledThumbRadius: 4),
      overlayShape: const RoundSliderOverlayShape(overlayRadius: 8),
      trackShape: const _EdgeToEdgeTrackShape(),
      rangeTrackShape: const _EdgeToEdgeRangeTrackShape(),
    );
    // Rail-width layout: bars get the full width; under them the
    // PixInsight-style stretch controls — black/white as a range on the same
    // axis as the plot, midtone below, Auto snaps back to the default STF.
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      mainAxisSize: MainAxisSize.min,
      children: [
        SizedBox(
          height: 44,
          child: CustomPaint(
            size: const Size(double.infinity, double.infinity),
            painter: _BinsPainter(
              bins: histogram.bins,
              lowClip: lowClip,
              highClip: highClip,
              // Cursor lines only while a manual stretch is in play.
              blackCursor: override != null || _dragBlack != null ? blackValue : null,
              midCursor: override != null || _dragMid != null
                  ? blackValue + mid * (whiteValue - blackValue)
                  : null,
              whiteCursor: override != null || _dragWhite != null ? whiteValue : null,
            ),
          ),
        ),
        SizedBox(
          height: 20,
          child: SliderTheme(
            data: sliderTheme,
            child: RangeSlider(
              values: RangeValues(black, white.clamp(black + 0.001, 1.0)),
              onChanged: (v) => setState(() {
                _dragBlack = v.start;
                _dragWhite = v.end;
              }),
              onChangeEnd: (_) => _commit(),
            ),
          ),
        ),
        Row(
          children: [
            Text('mid',
                style: theme.textTheme.labelSmall?.copyWith(
                    color: AraColors.textSecondary, fontSize: 10)),
            Expanded(
              child: SizedBox(
                height: 20,
                child: SliderTheme(
                  data: sliderTheme,
                  child: Slider(
                    value: mid,
                    onChanged: (v) => setState(() => _dragMid = v),
                    onChangeEnd: (_) => _commit(),
                  ),
                ),
              ),
            ),
            if (override != null || _dragBlack != null)
              TextButton(
                onPressed: () {
                  setState(() {
                    _dragBlack = null;
                    _dragMid = null;
                    _dragWhite = null;
                  });
                  ref.read(stretchOverrideProvider.notifier).resetToAuto();
                },
                style: TextButton.styleFrom(
                    padding: const EdgeInsets.symmetric(horizontal: 8),
                    minimumSize: const Size(0, 28)),
                child: const Text('Auto', style: TextStyle(fontSize: 11)),
              ),
          ],
        ),
        DefaultTextStyle(
          style: theme.textTheme.labelSmall!.copyWith(
              color: AraColors.textSecondary,
              fontFamily: 'monospace',
              fontSize: 10),
          child: Row(
            mainAxisAlignment: MainAxisAlignment.spaceBetween,
            children: [
              Text('mean ${histogram.meanAdu.round()} · '
                  '${histogram.minAdu}–${histogram.maxAdu}'),
              if (lowClip)
                Text('▼ ${pct(histogram.lowClipFraction)}',
                    style: const TextStyle(color: AraColors.accentBusy)),
              if (highClip)
                Text('▲ ${pct(histogram.highClipFraction)}',
                    style: const TextStyle(color: AraColors.accentBusy)),
            ],
          ),
        ),
      ],
    );
  }
}

/// Track that spans the full widget width (no thumb-radius inset), so the
/// slider's 0 and 1 line up with the histogram's first and last bin.
class _EdgeToEdgeTrackShape extends RoundedRectSliderTrackShape {
  const _EdgeToEdgeTrackShape();

  @override
  Rect getPreferredRect({
    required RenderBox parentBox,
    Offset offset = Offset.zero,
    required SliderThemeData sliderTheme,
    bool isEnabled = false,
    bool isDiscrete = false,
  }) {
    final height = sliderTheme.trackHeight ?? 2;
    return Rect.fromLTWH(offset.dx,
        offset.dy + (parentBox.size.height - height) / 2,
        parentBox.size.width, height);
  }
}

class _EdgeToEdgeRangeTrackShape extends RoundedRectRangeSliderTrackShape {
  const _EdgeToEdgeRangeTrackShape();

  @override
  Rect getPreferredRect({
    required RenderBox parentBox,
    Offset offset = Offset.zero,
    required SliderThemeData sliderTheme,
    bool isEnabled = false,
    bool isDiscrete = false,
  }) {
    final height = sliderTheme.trackHeight ?? 2;
    return Rect.fromLTWH(offset.dx,
        offset.dy + (parentBox.size.height - height) / 2,
        parentBox.size.width, height);
  }
}

class _BinsPainter extends CustomPainter {
  final List<int> bins;
  final bool lowClip;
  final bool highClip;

  /// Manual-stretch cursor positions in value space (0..1), or null when the
  /// stretch is on auto.
  final double? blackCursor;
  final double? midCursor;
  final double? whiteCursor;

  _BinsPainter({
    required this.bins,
    required this.lowClip,
    required this.highClip,
    this.blackCursor,
    this.midCursor,
    this.whiteCursor,
  });

  @override
  void paint(Canvas canvas, Size size) {
    if (bins.isEmpty) return;
    final peak = bins.reduce(math.max);
    if (peak <= 0) return;
    final barWidth = size.width / bins.length;
    final fill = Paint()..color = AraColors.textSecondary;
    final clip = Paint()..color = AraColors.accentBusy;
    final peakSqrt = math.sqrt(peak.toDouble());
    for (var i = 0; i < bins.length; i++) {
      if (bins[i] == 0) continue;
      final h = size.height * math.sqrt(bins[i].toDouble()) / peakSqrt;
      final isClipBar = (i == 0 && lowClip) || (i == bins.length - 1 && highClip);
      canvas.drawRect(
        Rect.fromLTWH(i * barWidth, size.height - h,
            math.max(barWidth - 0.5, 0.5), h),
        isClipBar ? clip : fill,
      );
    }
    // Stretch cursors: black and white bracket the kept range, mid dashed
    // between them — the feedback that says what the sliders actually did.
    void line(double? v, Color color, {bool dashed = false}) {
      if (v == null) return;
      final x = (v.clamp(0.0, 1.0)) * size.width;
      final paint = Paint()
        ..color = color
        ..strokeWidth = 1;
      if (!dashed) {
        canvas.drawLine(Offset(x, 0), Offset(x, size.height), paint);
        return;
      }
      for (var y = 0.0; y < size.height; y += 6) {
        canvas.drawLine(Offset(x, y), Offset(x, math.min(y + 3, size.height)), paint);
      }
    }

    line(blackCursor, AraColors.textDisabled);
    line(midCursor, AraColors.textDisabled, dashed: true);
    line(whiteCursor, Colors.white70);
  }

  @override
  bool shouldRepaint(covariant _BinsPainter old) =>
      !identical(old.bins, bins) ||
      old.lowClip != lowClip ||
      old.highClip != highClip ||
      old.blackCursor != blackCursor ||
      old.midCursor != midCursor ||
      old.whiteCursor != whiteCursor;
}
