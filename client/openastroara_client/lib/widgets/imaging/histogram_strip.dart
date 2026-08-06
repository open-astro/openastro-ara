import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/frame_histogram.dart';
import '../../services/frames_api.dart';
import '../../state/imaging/last_frame_state.dart';
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
      height: 72,
      decoration: const BoxDecoration(
        color: AraColors.bgPanel,
        border: Border(top: BorderSide(color: AraColors.border)),
      ),
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
      child: histogram == null
          ? const _EmptyBins()
          : histogram.when(
              data: (h) => _HistogramPlot(histogram: h),
              loading: () => const _EmptyBins(),
              error: (_, _) => const _EmptyBins(),
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

class _HistogramPlot extends StatelessWidget {
  final FrameHistogram histogram;
  const _HistogramPlot({required this.histogram});

  /// Clipping worth flagging: 0.1% of the sensor is ~26k pixels on an
  /// ASI2600 — real stars saturate a few hundred; a blown sky is millions.
  static const _clipWarnFraction = 0.001;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final lowClip = histogram.lowClipFraction >= _clipWarnFraction;
    final highClip = histogram.highClipFraction >= _clipWarnFraction;
    String pct(double f) => '${(f * 100).toStringAsFixed(1)}%';
    return Row(
      children: [
        Expanded(
          child: CustomPaint(
            size: const Size(double.infinity, double.infinity),
            painter: _BinsPainter(
              bins: histogram.bins,
              lowClip: lowClip,
              highClip: highClip,
            ),
          ),
        ),
        const SizedBox(width: 12),
        DefaultTextStyle(
          style: theme.textTheme.labelSmall!.copyWith(
              color: AraColors.textSecondary,
              fontFamily: 'monospace',
              fontSize: 10),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.end,
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              Text('mean ${histogram.meanAdu.round()}'),
              Text('min ${histogram.minAdu}  max ${histogram.maxAdu}'),
              if (lowClip)
                Text('▼ ${pct(histogram.lowClipFraction)} black-clipped',
                    style: const TextStyle(color: AraColors.accentBusy)),
              if (highClip)
                Text('▲ ${pct(histogram.highClipFraction)} saturated',
                    style: const TextStyle(color: AraColors.accentBusy)),
            ],
          ),
        ),
      ],
    );
  }
}

class _BinsPainter extends CustomPainter {
  final List<int> bins;
  final bool lowClip;
  final bool highClip;
  _BinsPainter({required this.bins, required this.lowClip, required this.highClip});

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
  }

  @override
  bool shouldRepaint(covariant _BinsPainter old) =>
      !identical(old.bins, bins) || old.lowClip != lowClip || old.highClip != highClip;
}
