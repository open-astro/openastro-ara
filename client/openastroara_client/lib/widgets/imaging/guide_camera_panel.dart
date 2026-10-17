import 'dart:async';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/guider_live.dart';
import '../../state/guider/guider_live_state.dart';
import '../../theme/ara_colors.dart';
import '../focus/bahtinov_focus_card.dart' show BahtinovFrameView;
import 'guide_calibration_panel.dart';
import 'guider_advanced_dialog.dart';

/// #1364 (epic item 1) — the guide camera's own picture from Ara: what the
/// guider is looping on, the star it locked (cross + search box), the other
/// stars it found (rings; multi-star secondaries), the last guide frame's SNR
/// and mass. Tap a star to lock on it; Loop / Stop / Deselect are the guider's
/// own buttons. Opened from the Live tab's guiding strip.
Future<void> showGuideCameraDialog(BuildContext context) => showDialog<void>(
  context: context,
  builder: (_) => const Dialog(
    insetPadding: EdgeInsets.all(24),
    child: SizedBox(width: 920, height: 640, child: GuideCameraPanel()),
  ),
);

class GuideCameraPanel extends ConsumerWidget {
  const GuideCameraPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final state = ref.watch(guiderLiveProvider);
    final notifier = ref.read(guiderLiveProvider.notifier);
    final live = state.live;
    final text = Theme.of(context).textTheme;
    return Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              const Icon(
                Icons.track_changes,
                size: 18,
                color: AraColors.textSecondary,
              ),
              const SizedBox(width: 8),
              Text('Guide camera', style: text.titleMedium),
              const SizedBox(width: 12),
              Flexible(
                child: Text(
                  live.summary,
                  key: const Key('guide-camera-summary'),
                  overflow: TextOverflow.ellipsis,
                  style: text.bodySmall?.copyWith(color: _stateColor(live)),
                ),
              ),
              const Spacer(),
              if (live.multiStarEnabled != null)
                Tooltip(
                  message: live.multiStarEnabled!
                      ? 'The guider tracks several stars; the rings are the secondaries.'
                      : 'Single-star guiding.',
                  child: Chip(
                    visualDensity: VisualDensity.compact,
                    label: Text(
                      live.multiStarEnabled! ? 'Multi-star' : 'Single star',
                      style: const TextStyle(fontSize: 12),
                    ),
                  ),
                ),
              const SizedBox(width: 8),
              IconButton(
                key: const Key('guide-camera-settings'),
                tooltip: 'Guider settings…',
                icon: const Icon(Icons.tune, size: 18),
                onPressed: () => showGuiderAdvancedDialog(context),
              ),
              IconButton(
                tooltip: 'Close',
                icon: const Icon(Icons.close, size: 18),
                onPressed: () => Navigator.of(context).maybePop(),
              ),
            ],
          ),
          const SizedBox(height: 8),
          Expanded(
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Expanded(
                  child: GuideCameraView(
                    frame: state.frame,
                    live: live,
                    onTap: live.connected && !state.busy
                        ? (x, y) => unawaited(notifier.selectStar(x, y))
                        : null,
                  ),
                ),
                const SizedBox(width: 12),
                // #1364 item 2 — the calibration beside the picture: the step
                // plot, the stored result and Ara's verdict.
                SizedBox(
                  width: 260,
                  child: GuideCalibrationPanel(
                    view: state.calibration,
                    onClear:
                        live.connected &&
                            !state.busy &&
                            !state.calibration.isRunning
                        ? () => unawaited(notifier.clearCalibration())
                        : null,
                  ),
                ),
              ],
            ),
          ),
          const SizedBox(height: 8),
          Row(
            children: [
              FilledButton.tonalIcon(
                onPressed: !live.connected || state.busy || live.isBusy
                    ? null
                    : () => unawaited(notifier.loop()),
                icon: const Icon(Icons.loop, size: 16),
                label: const Text('Loop'),
              ),
              const SizedBox(width: 8),
              FilledButton.tonalIcon(
                onPressed: !live.connected || state.busy || !live.isBusy
                    ? null
                    : () => unawaited(notifier.stop()),
                icon: const Icon(Icons.stop, size: 16),
                label: const Text('Stop'),
              ),
              const SizedBox(width: 8),
              TextButton.icon(
                onPressed: !live.connected || state.busy || !live.hasLock
                    ? null
                    : () => unawaited(notifier.deselectStar()),
                icon: const Icon(Icons.highlight_off, size: 16),
                label: const Text('Deselect star'),
              ),
              const Spacer(),
              Flexible(
                child: Text(
                  state.error ??
                      (live.connected
                          ? (live.hasFrame
                                ? 'Tap a star to guide on it.'
                                : 'Press Loop to see the guide camera.')
                          : 'Connect the guider first.'),
                  key: const Key('guide-camera-hint'),
                  textAlign: TextAlign.right,
                  overflow: TextOverflow.ellipsis,
                  style: text.bodySmall?.copyWith(
                    color: state.error != null
                        ? AraColors.accentError
                        : AraColors.textSecondary,
                  ),
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }

  static Color _stateColor(GuiderLive live) {
    if (!live.connected) return AraColors.accentDisconnected;
    return switch (live.appState) {
      'Guiding' => AraColors.accentConnected,
      'LostLock' => AraColors.accentError,
      'Calibrating' || 'Looping' || 'Selected' => AraColors.accentBusy,
      _ => AraColors.textSecondary,
    };
  }
}

/// The picture with the guider's marks over it. The frame is drawn
/// BoxFit.contain; the marks are in guide-camera pixels and mapped through
/// the same letterbox arithmetic the Bahtinov view uses for taps.
class GuideCameraView extends StatelessWidget {
  final Uint8List? frame;
  final GuiderLive live;
  final void Function(double x, double y)? onTap;
  const GuideCameraView({
    super.key,
    required this.frame,
    required this.live,
    this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    final jpeg = frame;
    final small = Theme.of(context).textTheme.bodySmall;
    return ClipRRect(
      borderRadius: BorderRadius.circular(10),
      child: Container(
        color: const Color(0xFF07080B),
        child: LayoutBuilder(
          builder: (context, constraints) {
            final box = constraints.biggest;
            return Stack(
              children: [
                Positioned.fill(
                  child: jpeg == null
                      ? Center(
                          child: Text(
                            live.connected
                                ? 'No guide frame yet'
                                : 'Guider not connected',
                            style: small?.copyWith(
                              color: AraColors.textSecondary,
                            ),
                          ),
                        )
                      : Image.memory(
                          jpeg,
                          gaplessPlayback: true,
                          fit: BoxFit.contain,
                          filterQuality: FilterQuality.medium,
                        ),
                ),
                if (jpeg != null && live.hasFrameSize)
                  Positioned.fill(
                    child: CustomPaint(painter: GuideMarksPainter(live, box)),
                  ),
                if (jpeg != null && onTap != null && live.hasFrameSize)
                  Positioned.fill(
                    child: GestureDetector(
                      behavior: HitTestBehavior.opaque,
                      onTapUp: (d) {
                        final p = BahtinovFrameView.tapToFramePixel(
                          box: box,
                          tap: d.localPosition,
                          frameWidth: live.frameWidth!,
                          frameHeight: live.frameHeight!,
                        );
                        if (p != null) onTap!(p.x.toDouble(), p.y.toDouble());
                      },
                      child: const MouseRegion(
                        cursor: SystemMouseCursors.precise,
                      ),
                    ),
                  ),
              ],
            );
          },
        ),
      ),
    );
  }
}

/// Lock cross + search box in amber, secondary stars as thin blue rings.
class GuideMarksPainter extends CustomPainter {
  final GuiderLive live;
  final Size box;
  GuideMarksPainter(this.live, this.box);

  /// Frame pixel → canvas point for a BoxFit.contain picture. Pure — tested.
  static Offset toCanvas(double x, double y, int frameWidth, int frameHeight, Size box) {
    final scale = scaleFor(frameWidth, frameHeight, box);
    final drawnW = frameWidth * scale;
    final drawnH = frameHeight * scale;
    return Offset((box.width - drawnW) / 2 + x * scale, (box.height - drawnH) / 2 + y * scale);
  }

  static double scaleFor(int frameWidth, int frameHeight, Size box) {
    final sx = box.width / frameWidth;
    final sy = box.height / frameHeight;
    return sx < sy ? sx : sy;
  }

  @override
  void paint(Canvas canvas, Size size) {
    final w = live.frameWidth!;
    final h = live.frameHeight!;
    final scale = scaleFor(w, h, size);
    final ring = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = 1
      ..color = AraColors.accentInfo.withValues(alpha: 0.9);
    for (final s in live.stars) {
      if (live.hasLock &&
          (s.x - live.lockX!).abs() < 2 &&
          (s.y - live.lockY!).abs() < 2) {
        continue;
      }
      canvas.drawCircle(toCanvas(s.x, s.y, w, h, size), 7, ring);
    }
    if (live.hasLock) {
      final c = toCanvas(live.lockX!, live.lockY!, w, h, size);
      final amber = Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = 1.5
        ..color = AraColors.accentBusy;
      final half = (live.searchRegion ?? 15) * scale;
      canvas.drawRect(
        Rect.fromCenter(center: c, width: 2 * half, height: 2 * half),
        amber,
      );
      const arm = 10.0;
      canvas.drawLine(
        c + const Offset(-arm - 4, 0),
        c + const Offset(-4, 0),
        amber,
      );
      canvas.drawLine(
        c + const Offset(4, 0),
        c + const Offset(arm + 4, 0),
        amber,
      );
      canvas.drawLine(
        c + const Offset(0, -arm - 4),
        c + const Offset(0, -4),
        amber,
      );
      canvas.drawLine(
        c + const Offset(0, 4),
        c + const Offset(0, arm + 4),
        amber,
      );
    }
  }

  @override
  bool shouldRepaint(GuideMarksPainter old) =>
      old.live != live || old.box != box;
}
