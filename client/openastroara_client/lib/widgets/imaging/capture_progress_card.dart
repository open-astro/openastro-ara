import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/imaging/capture_progress_state.dart';
import '../../theme/ara_colors.dart';

/// Live capture progress for the Imaging tab: shown while a Take One is in
/// flight (exposing → downloading → done/failed). Replaces the old blind
/// snackbar with a visible phase, a progress bar fed by the camera's exposure
/// progress (with an elapsed-time fallback so it moves even on a slow poll),
/// the remaining time, a "ready in" estimate, and a Cancel button while the
/// capture can still be stopped.
class CaptureProgressCard extends ConsumerStatefulWidget {
  /// Re-triggers the capture with the current exposure params (Take One).
  final VoidCallback? onRetry;
  /// Aborts the in-flight exposure.
  final VoidCallback? onCancel;
  const CaptureProgressCard({super.key, this.onRetry, this.onCancel});

  @override
  ConsumerState<CaptureProgressCard> createState() =>
      _CaptureProgressCardState();
}

class _CaptureProgressCardState extends ConsumerState<CaptureProgressCard> {
  Timer? _tick;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _syncTick();
  }

  @override
  void dispose() {
    _tick?.cancel();
    super.dispose();
  }

  /// While a capture is active, rebuild ~4×/s so the elapsed-based progress
  /// bar, remaining time, and ready-in estimate count down live even though
  /// the daemon's equipment poll is slow (15 s).
  void _syncTick() {
    final active =
        ref.read(captureProgressProvider).isActive;
    if (active && _tick == null) {
      _tick = Timer.periodic(const Duration(milliseconds: 250), (_) {
        if (mounted) setState(() {});
      });
    } else if (!active) {
      _tick?.cancel();
      _tick = null;
    }
  }

  @override
  Widget build(BuildContext context) {
    final progress = ref.watch(captureProgressProvider);
    _syncTick();
    if (!progress.isActive) return const SizedBox.shrink();
    switch (progress.phase) {
      case CapturePhase.exposing:
        return _Card(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: [
              _Row(icon: Icons.photo_camera, title: _exposingTitle(progress)),
              const SizedBox(height: 6),
              ClipRRect(
                borderRadius: BorderRadius.circular(4),
                child: LinearProgressIndicator(
                  value: (progress.displayProgressPct ?? 0) / 100,
                  minHeight: 8,
                  backgroundColor: AraColors.bgPrimary,
                ),
              ),
              const SizedBox(height: 4),
              Text(
                _exposingSubtitle(progress),
                style: const TextStyle(
                    fontSize: 11, color: AraColors.textSecondary),
              ),
              if (widget.onCancel != null) _cancelButton(),
            ],
          ),
        );
      case CapturePhase.downloading:
        return _Card(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: [
              _Row(
                icon: Icons.download,
                title: 'Downloading frame…',
                subtitle: _downloadSubtitle(progress),
              ),
              if (widget.onCancel != null) _cancelButton(),
            ],
          ),
        );
      case CapturePhase.done:
        return _Card(
          child: _Row(
            icon: Icons.check_circle,
            iconColor: AraColors.accentConnected,
            title: 'Frame ready',
          ),
        );
      case CapturePhase.failed:
        return _Card(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              _Row(
                icon: Icons.error_outline,
                iconColor: AraColors.accentError,
                title: 'Capture failed',
                subtitle: progress.error,
              ),
              if (widget.onRetry != null) ...[
                const SizedBox(height: 8),
                SizedBox(
                  width: double.infinity,
                  child: OutlinedButton.icon(
                    onPressed: widget.onRetry,
                    icon: const Icon(Icons.refresh, size: 16),
                    label: const Text('Retry'),
                  ),
                ),
              ],
            ],
          ),
        );
      case CapturePhase.idle:
        return const SizedBox.shrink();
    }
  }

  Widget _cancelButton() {
    return Padding(
      padding: const EdgeInsets.only(top: 8),
      child: SizedBox(
        width: double.infinity,
        child: OutlinedButton.icon(
          onPressed: widget.onCancel,
          icon: const Icon(Icons.stop_circle_outlined, size: 16),
          label: const Text('Cancel'),
        ),
      ),
    );
  }

  String _exposingTitle(CaptureProgress p) {
    final secs = p.requestedExposure.inMilliseconds / 1000.0;
    final pct = (p.displayProgressPct ?? 0).round();
    return 'Exposing ${secs.toStringAsFixed(p.requestedExposure.inMilliseconds % 1000 == 0 ? 0 : 1)}s… $pct%';
  }

  String _exposingSubtitle(CaptureProgress p) {
    final remaining = p.exposureRemaining;
    if (remaining == null) return 'Starting…';
    final ready = p.timeToDisplay;
    final left = '~${(remaining.inMilliseconds / 1000.0).toStringAsFixed(1)} s left';
    if (ready == null) return left;
    return '$left · ready in ~${(ready.inMilliseconds / 1000.0).toStringAsFixed(1)} s';
  }

  String _downloadSubtitle(CaptureProgress p) {
    final ready = p.timeToDisplay;
    final elapsed = p.downloadElapsed;
    final elapsedText = elapsed == null
        ? 'Writing the FITS and registering…'
        : '${(elapsed.inMilliseconds / 1000.0).toStringAsFixed(1)} s so far';
    if (ready == null) return elapsedText;
    return '$elapsedText · ready in ~${(ready.inMilliseconds / 1000.0).toStringAsFixed(1)} s';
  }
}

class _Card extends StatelessWidget {
  final Widget child;
  const _Card({required this.child});

  @override
  Widget build(BuildContext context) {
    return Container(
      margin: const EdgeInsets.fromLTRB(12, 0, 12, 12),
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
      decoration: BoxDecoration(
        color: AraColors.bgPanel,
        borderRadius: BorderRadius.circular(8),
        border: Border.all(color: AraColors.border),
      ),
      child: child,
    );
  }
}

class _Row extends StatelessWidget {
  final IconData icon;
  final Color? iconColor;
  final String title;
  final String? subtitle;
  const _Row({
    required this.icon,
    required this.title,
    this.iconColor,
    this.subtitle,
  });

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        if (icon == Icons.download)
          const SizedBox(
            width: 16,
            height: 16,
            child: CircularProgressIndicator(strokeWidth: 2),
          )
        else
          Icon(icon, size: 18, color: iconColor ?? AraColors.accentBusy),
        const SizedBox(width: 8),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: [
              Text(title,
                  style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w600)),
              if (subtitle != null)
                Text(subtitle!,
                    style: const TextStyle(
                        fontSize: 11, color: AraColors.textSecondary)),
            ],
          ),
        ),
      ],
    );
  }
}
