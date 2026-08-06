import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/frame_histogram.dart';
import '../../services/frames_api.dart';
import '../../state/imaging/last_frame_state.dart';
import '../../state/saved_server_state.dart';
import '../../theme/ara_colors.dart';

/// §12c.2 — the RAW 16-bit histogram + NINA-style Statistics for the
/// displayed frame, fetched from the server (computed from the FITS pixels,
/// cached beside the preview variants). The screen stretch makes every frame
/// look exposed; these numbers are where exposure judgment actually lives.
/// Bar heights are sqrt-scaled — an astro histogram is a sky-background
/// spike plus a long faint tail, invisible on a linear axis.
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
          ? const SizedBox(height: 20, child: _EmptyBins())
          : histogram.when(
              data: (h) => _StatisticsPanel(histogram: h),
              loading: () => const SizedBox(height: 20, child: _EmptyBins()),
              error: (_, _) => const SizedBox(height: 20, child: _EmptyBins()),
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

/// The histogram plot with the Statistics grid beneath it — the numbers a
/// pixel-peeper wants without opening the FITS, laid out two columns wide.
class _StatisticsPanel extends StatelessWidget {
  final FrameHistogram histogram;
  const _StatisticsPanel({required this.histogram});

  /// Clipping worth flagging: 0.1% of the sensor is ~26k pixels on an
  /// ASI2600 — real stars saturate a few hundred; a blown sky is millions.
  static const _clipWarnFraction = 0.001;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final h = histogram;
    final lowClip = h.lowClipFraction >= _clipWarnFraction;
    final highClip = h.highClipFraction >= _clipWarnFraction;
    String fmt(double v) => v.toStringAsFixed(2);
    final labelStyle = theme.textTheme.labelSmall
        ?.copyWith(color: AraColors.textSecondary, fontSize: 11);
    final valueStyle = theme.textTheme.labelSmall?.copyWith(
        fontFamily: 'monospace', fontSize: 11);
    final warnStyle = valueStyle?.copyWith(color: AraColors.accentBusy);

    Widget stat(String label, String value, {bool warn = false}) => Row(
          children: [
            SizedBox(width: 64, child: Text(label, style: labelStyle)),
            Expanded(
                child: Text(value,
                    style: warn ? warnStyle : valueStyle,
                    overflow: TextOverflow.ellipsis)),
          ],
        );

    Widget pair(Widget left, Widget right) => Padding(
          padding: const EdgeInsets.only(bottom: 3),
          child: Row(children: [
            Expanded(child: left),
            const SizedBox(width: 8),
            Expanded(child: right),
          ]),
        );

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      mainAxisSize: MainAxisSize.min,
      children: [
        pair(stat('Width', '${h.width}'), stat('Height', '${h.height}')),
        pair(stat('Mean', fmt(h.meanAdu)), stat('SD', fmt(h.stdDev))),
        pair(stat('Median', fmt(h.median)), stat('MAD', fmt(h.mad))),
        pair(
          stat('Min', '${h.minAdu} (${h.minCount}x)', warn: lowClip),
          stat('Max', '${h.maxAdu} (${h.maxCount}x)', warn: highClip),
        ),
        pair(
          stat('#Stars', h.stars?.toString() ?? '—'),
          stat('HFR', h.hfr == null ? '—' : fmt(h.hfr!)),
        ),
        pair(
          stat('Bit depth', '${h.bitDepth}'),
          stat('Gain', h.gain?.toString() ?? '—'),
        ),
        stat('Offset', h.offset?.toString() ?? '—'),
      ],
    );
  }
}
