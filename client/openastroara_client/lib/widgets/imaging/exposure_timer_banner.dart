import 'dart:async';

import 'package:clock/clock.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/imaging/exposure_activity_state.dart';
import '../../theme/ara_colors.dart';

/// The Live tab's exposure timer, in the right rail under Take One while the
/// camera is exposing — for every exposure the daemon runs (a sequence's
/// lights, a Smart Focus probe, a plate-solve capture), not only the ones
/// this client started; the tab hides it while its own Take One card is up. Counts elapsed / total with a bar, then switches
/// to "Downloading" once the shutter should have closed, and leaves the
/// screen when the daemon reports the pixels landed. Hidden when idle.
class ExposureTimerBanner extends ConsumerStatefulWidget {
  const ExposureTimerBanner({super.key});

  @override
  ConsumerState<ExposureTimerBanner> createState() =>
      _ExposureTimerBannerState();
}

class _ExposureTimerBannerState extends ConsumerState<ExposureTimerBanner> {
  Timer? _tick;

  @override
  void dispose() {
    _tick?.cancel();
    super.dispose();
  }

  /// Rebuild ~4×/s while an exposure is live so the clock counts smoothly;
  /// no timer at all while idle.
  void _syncTick(bool active) {
    if (active && _tick == null) {
      _tick = Timer.periodic(const Duration(milliseconds: 250), (_) {
        if (mounted) setState(() {});
      });
    } else if (!active && _tick != null) {
      _tick!.cancel();
      _tick = null;
    }
  }

  @override
  Widget build(BuildContext context) {
    final activity = ref.watch(exposureActivityProvider);
    _syncTick(activity != null);
    if (activity == null) return const SizedBox.shrink();
    final now = clock.now();
    final downloading = activity.isDownloading(now);
    final subject = activity.filterName == null
        ? activity.kindLabel
        : '${activity.kindLabel} · ${activity.filterName}';
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
      decoration: BoxDecoration(
        color: AraColors.bgPanel.withValues(alpha: 0.92),
        borderRadius: BorderRadius.circular(8),
        border: Border.all(color: AraColors.border),
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              if (downloading)
                const SizedBox(
                  width: 14,
                  height: 14,
                  child: CircularProgressIndicator(strokeWidth: 2),
                )
              else
                const Icon(Icons.camera, size: 16, color: AraColors.accentBusy),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  downloading
                      ? 'Downloading · $subject'
                      : 'Exposing · $subject',
                  style: const TextStyle(
                    fontSize: 13,
                    fontWeight: FontWeight.w600,
                  ),
                  overflow: TextOverflow.ellipsis,
                ),
              ),
              const SizedBox(width: 12),
              Text(
                formatExposureClock(activity.elapsed(now), activity.exposure),
                style: const TextStyle(
                  fontSize: 15,
                  fontWeight: FontWeight.w600,
                  fontFeatures: [FontFeature.tabularFigures()],
                ),
              ),
            ],
          ),
          // The question a user actually has mid-sub: how long is left.
          // Reads from the same clock as the bar; "Reading out" once the
          // shutter should have closed.
          Padding(
            padding: const EdgeInsets.only(top: 2),
            child: Text(
              downloading
                  ? 'Reading out the sensor…'
                  : '${formatRemaining(activity.remaining(now))} left',
              style: const TextStyle(
                fontSize: 12,
                color: AraColors.textSecondary,
                fontFeatures: [FontFeature.tabularFigures()],
              ),
            ),
          ),
          const SizedBox(height: 6),
          ClipRRect(
            borderRadius: BorderRadius.circular(3),
            child: LinearProgressIndicator(
              // Indeterminate while the daemon reads the sensor out — the
              // download has no known length.
              value: downloading ? null : activity.progress(now),
              minHeight: 5,
              backgroundColor: AraColors.bgPrimary,
            ),
          ),
        ],
      ),
    );
  }
}

/// `elapsed / total` for the banner. Exposures of a minute or more read as
/// `m:ss / m:ss`; shorter ones as seconds with a decimal (`4.2 / 10 s`) so a
/// 3 s focus probe still visibly moves. Elapsed is clamped to the total — the
/// download phase keeps showing the full exposure, never `12 / 10 s`.
String formatExposureClock(Duration elapsed, Duration total) {
  final e = elapsed > total ? total : elapsed;
  if (total.inSeconds >= 60) {
    return '${_mmss(e)} / ${_mmss(total)}';
  }
  final totalSec = total.inMilliseconds / 1000.0;
  final totalText = totalSec == totalSec.roundToDouble()
      ? totalSec.toStringAsFixed(0)
      : totalSec.toStringAsFixed(1);
  return '${(e.inMilliseconds / 1000.0).toStringAsFixed(1)} / $totalText s';
}

/// Time left in the exposure: `m:ss` from a minute up, else seconds with a
/// decimal — the countdown a user watches during a sub.
String formatRemaining(Duration remaining) {
  if (remaining.inSeconds >= 60) return _mmss(remaining);
  return '${(remaining.inMilliseconds / 1000.0).toStringAsFixed(1)} s';
}

String _mmss(Duration d) {
  final m = d.inMinutes;
  final s = d.inSeconds % 60;
  return '$m:${s.toString().padLeft(2, '0')}';
}
