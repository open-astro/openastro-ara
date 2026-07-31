import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/guider_status.dart';
import '../../state/guider/guider_state.dart';
import '../../state/guider/live_guiding_state.dart';
import '../../state/settings/phd2_settings_state.dart';
import '../../theme/ara_colors.dart';
import 'guiding_tune_dialog.dart';

/// §63.18 live guiding panel — collapsible Imaging-tab section showing the
/// guider's live state + rolling RMS. The quick-adjust tuning controls live in
/// [GuidingTuneDialog], opened from the header's Tune button — the main view
/// keeps live telemetry only (the macOS quick-settings idiom: adjustments
/// happen in a transient surface, not inline in the dashboard).
///
/// Follows the [DiagnosticPanel] collapse idiom; the live history comes from
/// [liveGuidingRmsProvider] (a poll-fed local ring buffer — see that file for
/// why it isn't the §50.7 stats series).
class GuidingPanel extends ConsumerStatefulWidget {
  const GuidingPanel({super.key});

  @override
  ConsumerState<GuidingPanel> createState() => _GuidingPanelState();
}

class _GuidingPanelState extends ConsumerState<GuidingPanel> {
  bool _expanded = false;

  static const _emDash = '—';

  @override
  Widget build(BuildContext context) {
    final statusAsync = ref.watch(guiderStatusProvider);
    final status = statusAsync.asData?.value;
    // Only keep the poller/buffer alive while the section is expanded — the
    // provider is autoDispose, so collapsing stops the status polling.
    final samples =
        _expanded ? ref.watch(liveGuidingRmsProvider) : const <RmsSample>[];

    return Container(
      decoration: const BoxDecoration(
        color: AraColors.bgPanel,
        border: Border(top: BorderSide(color: AraColors.border)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          InkWell(
            onTap: () => setState(() => _expanded = !_expanded),
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
              child: Row(
                children: [
                  Icon(_expanded ? Icons.expand_more : Icons.chevron_right,
                      size: 18, color: AraColors.textSecondary),
                  const SizedBox(width: 4),
                  const Icon(Icons.track_changes,
                      size: 16, color: AraColors.textSecondary),
                  const SizedBox(width: 6),
                  Text('Guiding',
                      style: Theme.of(context).textTheme.titleSmall),
                  const SizedBox(width: 12),
                  Text(
                    _stateLabel(status),
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(
                          color: _stateColor(status),
                        ),
                  ),
                  const Spacer(),
                  Text(
                    'RMS ${_fmtArcsec(_liveRms(status)?.$1)}',
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(
                          color: AraColors.textSecondary,
                        ),
                  ),
                  const SizedBox(width: 4),
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
          if (_expanded) ...[
            _rmsSection(context, status),
            _SparklineSection(samples: samples),
          ],
        ],
      ),
    );
  }

  /// RMS while actively guiding (guiding/dithering), else null → em-dashes.
  /// Returns (total, ra, dec) in arcsec.
  static (double?, double?, double?)? _liveRms(GuiderStatus? status) {
    if (status == null) return null;
    final actively = status.runtimeState == GuiderRuntimeState.guiding ||
        status.runtimeState == GuiderRuntimeState.dithering;
    if (!actively) return null;
    return (status.rmsTotal, status.rmsRa, status.rmsDec);
  }

  static String _stateLabel(GuiderStatus? status) {
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

  static String _fmtArcsec(double? v) =>
      v == null ? _emDash : '${v.toStringAsFixed(2)}″';

  static String _fmtPx(double? arcsec, double? scale) {
    if (arcsec == null || scale == null || scale <= 0) return _emDash;
    return '${(arcsec / scale).toStringAsFixed(2)} px';
  }

  Widget _rmsSection(BuildContext context, GuiderStatus? status) {
    final rms = _liveRms(status);
    final phd2 = ref.watch(phd2SettingsProvider);
    // px is derived from the §63.5 guide train (focal length + pixel size);
    // when unset, px reads as em-dash while arcsec stays live.
    final scale =
        guiderArcsecPerPixel(phd2.guideFocalLength, phd2.guidePixelSize);
    return Padding(
      padding: const EdgeInsets.fromLTRB(12, 8, 12, 4),
      child: Row(
        children: [
          _rmsCell(context, 'Total', rms?.$1, scale),
          _rmsCell(context, 'RA', rms?.$2, scale),
          _rmsCell(context, 'Dec', rms?.$3, scale),
        ],
      ),
    );
  }

  Widget _rmsCell(
      BuildContext context, String label, double? arcsec, double? scale) {
    return Expanded(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(label,
              style: Theme.of(context)
                  .textTheme
                  .labelSmall
                  ?.copyWith(color: AraColors.textSecondary)),
          Text(_fmtArcsec(arcsec),
              style: Theme.of(context).textTheme.bodyMedium),
          Text(_fmtPx(arcsec, scale),
              style: Theme.of(context)
                  .textTheme
                  .labelSmall
                  ?.copyWith(color: AraColors.textDisabled)),
        ],
      ),
    );
  }

}

/// Compact rolling sparkline of total RMS over the buffer window (~5 min).
class _SparklineSection extends StatelessWidget {
  const _SparklineSection({required this.samples});

  final List<RmsSample> samples;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(12, 2, 12, 8),
      child: SizedBox(
        height: 36,
        child: samples.length < 2
            ? Align(
                alignment: Alignment.centerLeft,
                child: Text(
                  'No guiding history yet',
                  style: Theme.of(context)
                      .textTheme
                      .labelSmall
                      ?.copyWith(color: AraColors.textDisabled),
                ),
              )
            : CustomPaint(
                size: Size.infinite,
                painter: RmsSparklinePainter(samples),
              ),
      ),
    );
  }
}

/// Painter for the §63.18 RMS sparkline: total RMS as a polyline, x mapped
/// over the sample time span, y auto-scaled 0..max (a fixed floor of 1″ keeps
/// tiny excellent-guiding traces from filling the whole height).
class RmsSparklinePainter extends CustomPainter {
  RmsSparklinePainter(this.samples);

  final List<RmsSample> samples;

  @override
  void paint(Canvas canvas, Size size) {
    if (samples.length < 2) return;
    final t0 = samples.first.time.millisecondsSinceEpoch;
    final t1 = samples.last.time.millisecondsSinceEpoch;
    final span = (t1 - t0).clamp(1, 1 << 62);
    var maxRms = 1.0;
    for (final s in samples) {
      if (s.total > maxRms) maxRms = s.total;
    }
    final path = Path();
    for (var i = 0; i < samples.length; i++) {
      final s = samples[i];
      final x =
          (s.time.millisecondsSinceEpoch - t0) / span * size.width;
      final y = size.height - (s.total / maxRms * size.height);
      if (i == 0) {
        path.moveTo(x, y);
      } else {
        path.lineTo(x, y);
      }
    }
    final paint = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = 1.5
      ..color = AraColors.accentInfo;
    canvas.drawPath(path, paint);
  }

  @override
  bool shouldRepaint(RmsSparklinePainter oldDelegate) =>
      oldDelegate.samples != samples;
}
