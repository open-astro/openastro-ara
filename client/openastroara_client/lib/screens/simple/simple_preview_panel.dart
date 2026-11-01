import 'package:flutter/material.dart';
import 'package:flutter/scheduler.dart' show Ticker;
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/imaging/capture_progress_state.dart';
import '../../state/imaging/exposure_state.dart';
import '../../state/imaging/last_frame_state.dart';
import '../../state/imaging/live_view_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../widgets/imaging/exposure_controls_panel.dart';
import '../../widgets/imaging/histogram_strip.dart' show frameHistogramProvider;
import '../imaging_actions.dart';
import 'simple_shell.dart' show SimplePanel, SimpleSegment, kSimpleTileFill;

/// Simple Mode body text: 14 px minimum (brief), tabular for live numbers.
const kSimpleText = TextStyle(
  fontSize: 14,
  color: AraColors.textPrimary,
  fontFeatures: [FontFeature.tabularFigures()],
);

/// Room the full last-frame facts take at the bottom of the capture panel (a
/// caption and five rows, with the divider); shorter columns get two rows.
const double kSimpleFactsHeight = 166;

/// What the capture panel needs to show without scrolling: two 48 px fields,
/// the shutter, Loop / Single and the shot status line, with padding.
const double kSimpleCaptureHeight = 300;

/// Single takes a saved frame (the daemon registers every Take One); Loop
/// runs live view, whose frames are not saved.
enum PreviewShot { loop, single }

final previewShotProvider = NotifierProvider<_PreviewShot, PreviewShot>(
  _PreviewShot.new,
);

class _PreviewShot extends Notifier<PreviewShot> {
  @override
  PreviewShot build() => PreviewShot.single;
  void set(PreviewShot v) => state = v;
}

/// The right column in Preview: the last frame's facts on top, then
/// Exposure, Filter, the shutter and Loop / Single.
class SimplePreviewPanel extends StatelessWidget {
  const SimplePreviewPanel({super.key});

  @override
  Widget build(BuildContext context) => Consumer(
    builder: (context, ref, _) {
      // The facts wait for something to describe: a saved frame or a loop.
      final hasFrame =
          ref.watch(lastCapturedFrameIdProvider) != null ||
          ref.watch(liveViewControllerProvider);
      return LayoutBuilder(
        builder: (context, box) {
          // A short column (an iPad in landscape) gets the two-row facts.
          // Decided by the column's height, so it doesn't flip when a frame
          // lands.
          final compact =
              box.maxHeight < kSimpleCaptureHeight + kSimpleFactsHeight;
          // One panel the full height of the column: the controls at the top,
          // where they never move, and the last frame's facts at the bottom.
          return SimplePanel(
            padding: const EdgeInsets.all(AraSpace.s12),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                const Expanded(child: _PreviewControls()),
                if (hasFrame) ...[
                  const Divider(height: AraSpace.s16, color: AraColors.border),
                  Padding(
                    padding: const EdgeInsets.symmetric(
                      horizontal: AraSpace.s4,
                    ),
                    child: _LastFrameFacts(compact: compact),
                  ),
                ],
              ],
            ),
          );
        },
      );
    },
  );
}

class _LastFrameFacts extends ConsumerWidget {
  const _LastFrameFacts({required this.compact});

  /// Two rows, no caption: HFR and stars together, then the exposure.
  final bool compact;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final looping = ref.watch(liveViewControllerProvider);
    final id = ref.watch(lastCapturedFrameIdProvider);
    final params = ref.watch(exposureControllerProvider);
    // Stars and HFR exist for saved frames only (the daemon analyses those).
    final analysed = (looping || id == null)
        ? null
        : ref.watch(frameHistogramProvider(id)).asData?.value;
    String seconds(Duration d) => d.inMilliseconds % 1000 == 0
        ? '${d.inSeconds} s'
        : '${d.inMilliseconds / 1000} s';
    Widget row(String label, String value) => Padding(
      padding: const EdgeInsets.symmetric(vertical: 3),
      child: Row(
        children: [
          Text(label, style: AraText.caption),
          const SizedBox(width: AraSpace.s8),
          Expanded(
            child: Text(
              value,
              style: kSimpleText,
              textAlign: TextAlign.end,
              overflow: TextOverflow.ellipsis,
            ),
          ),
        ],
      ),
    );
    final hfr = analysed?.hfr?.toStringAsFixed(2) ?? '—';
    final stars = analysed?.stars?.toString() ?? '—';
    final exposure = '${seconds(params.exposure)} · ${params.filterSlot}';
    // The frame's own header values, not the controls' (they can differ when
    // the shot came from a sequence).
    final gainOffset = analysed?.gain == null
        ? '—'
        : '${analysed!.gain} · ${analysed.offset ?? '—'}';
    final clipped = analysed == null
        ? '—'
        : '${(analysed.highClipFraction * 100).toStringAsFixed(analysed.highClipFraction < 0.001 ? 0 : 2)} %';
    if (compact) {
      return Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          row('HFR $hfr', '★ $stars'),
          row(looping ? 'Live view' : 'Last frame', exposure),
        ],
      );
    }
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(looping ? 'Live view' : 'Last frame', style: AraText.caption),
        const SizedBox(height: 4),
        row('HFR', hfr),
        row('Stars', stars),
        row('Exposure', exposure),
        row('Gain · offset', gainOffset),
        row('Clipped', clipped),
      ],
    );
  }
}

/// True from the tap on the square until the daemon has answered the abort:
/// the abort is a round trip to the rig (about a second), and the shutter
/// and status should answer the tap, not the reply.
final _stoppingProvider = NotifierProvider<_Stopping, bool>(_Stopping.new);

class _Stopping extends Notifier<bool> {
  @override
  bool build() => false;
  void set(bool v) => state = v;
}

/// Cancels the shot in flight with immediate feedback. The capture state
/// itself still waits for the daemon (cancelCapture keeps tracking the shot
/// if the abort didn't go through, so its frame isn't orphaned).
Future<void> _stop(BuildContext context, WidgetRef ref) async {
  final stopping = ref.read(_stoppingProvider.notifier);
  stopping.set(true);
  try {
    await cancelCapture(context, ref);
  } finally {
    stopping.set(false);
  }
}

class _PreviewControls extends ConsumerWidget {
  const _PreviewControls();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final params = ref.watch(exposureControllerProvider);
    final ctrl = ref.read(exposureControllerProvider.notifier);
    final shot = ref.watch(previewShotProvider);
    final looping = ref.watch(liveViewControllerProvider);
    final capturing = ref.watch(captureProgressProvider).isCapturing;
    final stopping = ref.watch(_stoppingProvider);
    return SingleChildScrollView(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          DurationSecondsField(
            label: 'Exposure (s)',
            value: params.exposure,
            onChanged: ctrl.setExposure,
          ),
          const SizedBox(height: AraSpace.s8),
          FilterDropdown(
            value: params.filterSlot,
            onChanged: ctrl.setFilterSlot,
          ),
          const SizedBox(height: AraSpace.s16),
          Center(
            child: _Shutter(
              running: (looping || capturing) && !stopping,
              onPressed: stopping
                  ? null
                  : switch (shot) {
                      PreviewShot.loop => () => toggleLiveView(
                        context,
                        ref,
                        !looping,
                      ),
                      // The square stops the shot in flight, like a loop.
                      PreviewShot.single =>
                        capturing
                            ? () => _stop(context, ref)
                            : () => takeOne(context, ref),
                    },
            ),
          ),
          const SizedBox(height: AraSpace.s16),
          // Switching while a loop runs would strand it with no stop.
          Container(
            decoration: BoxDecoration(
              color: kSimpleTileFill,
              borderRadius: BorderRadius.circular(8),
            ),
            child: Row(
              children: [
                for (final (v, label) in const [
                  (PreviewShot.loop, 'Loop'),
                  (PreviewShot.single, 'Single'),
                ])
                  Expanded(
                    child: SimpleSegment(
                      label: label,
                      selected: shot == v,
                      radius: 8,
                      onTap: looping
                          ? null
                          : () => ref.read(previewShotProvider.notifier).set(v),
                    ),
                  ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

/// The round capture button: a white disc to shoot, a square while a loop or
/// exposure runs.
class _Shutter extends StatelessWidget {
  const _Shutter({required this.running, required this.onPressed});

  final bool running;
  final VoidCallback? onPressed;

  @override
  Widget build(BuildContext context) {
    final enabled = onPressed != null;
    final fg = enabled ? AraColors.textPrimary : AraColors.textDisabled;
    return Semantics(
      button: true,
      label: running ? 'Stop' : 'Take a frame',
      child: InkResponse(
        key: const Key('simple-shutter'),
        onTap: onPressed,
        radius: 40,
        child: Container(
          width: 68,
          height: 68,
          decoration: BoxDecoration(
            shape: BoxShape.circle,
            border: Border.all(color: fg, width: 3),
          ),
          alignment: Alignment.center,
          child: Container(
            width: running ? 24 : 46,
            height: running ? 24 : 46,
            decoration: BoxDecoration(
              color: fg,
              shape: running ? BoxShape.rectangle : BoxShape.circle,
              borderRadius: running ? BorderRadius.circular(4) : null,
            ),
          ),
        ),
      ),
    );
  }
}

/// What the camera is doing, as a pill at the top-left of the image (like a
/// camera's REC light): looping, taking a frame with its countdown and bar,
/// stopping, frame ready, or why it failed. Nothing while idle. It sits
/// outside the panels, so it stays in the immersive view.
class SimpleStatusPill extends ConsumerStatefulWidget {
  const SimpleStatusPill({super.key});

  @override
  ConsumerState<SimpleStatusPill> createState() => _SimpleStatusPillState();
}

class _SimpleStatusPillState extends ConsumerState<SimpleStatusPill>
    with SingleTickerProviderStateMixin {
  late final Ticker _ticker = createTicker((_) => setState(() {}));

  @override
  void dispose() {
    _ticker.dispose();
    super.dispose();
  }

  /// The bar and remaining time are elapsed-based and the daemon's progress
  /// poll is slow, so redraw every frame while a shot is active — a smooth
  /// bar instead of one that freezes, or steps 4×/s.
  void _syncTick(bool active) {
    if (active && !_ticker.isActive) {
      _ticker.start();
    } else if (!active && _ticker.isActive) {
      _ticker.stop();
    }
  }

  @override
  Widget build(BuildContext context) {
    final p = ref.watch(captureProgressProvider);
    final stopping = ref.watch(_stoppingProvider);
    final looping = ref.watch(liveViewControllerProvider);
    _syncTick(p.isActive);
    final exposing = !stopping && p.phase == CapturePhase.exposing;
    final (String? label, String? detail, Color dot) = stopping
        ? ('Stopping…', null, AraColors.textSecondary)
        : switch (p.phase) {
            CapturePhase.exposing => (
              'Taking a frame',
              p.exposureRemaining == null
                  ? 'Starting…'
                  // Whole seconds, rounded up: "1 s left" until it's done.
                  : '${(p.exposureRemaining!.inMilliseconds / 1000).ceil()} s left',
              AraColors.accentConnected,
            ),
            CapturePhase.downloading => (
              'Downloading…',
              null,
              AraColors.accentConnected,
            ),
            CapturePhase.done => (
              'Frame ready',
              null,
              AraColors.accentConnected,
            ),
            CapturePhase.failed => (
              'Capture failed',
              p.error,
              AraColors.accentError,
            ),
            CapturePhase.idle =>
              looping
                  ? ('Looping', null, AraColors.accentConnected)
                  : (null, null, AraColors.textSecondary),
          };
    if (label == null) return const SizedBox.shrink();
    return ConstrainedBox(
      constraints: const BoxConstraints(maxWidth: 460),
      child: SimplePanel(
        padding: const EdgeInsets.symmetric(horizontal: AraSpace.s16),
        child: SizedBox(
          height: 48,
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(Icons.circle, size: 10, color: dot),
              const SizedBox(width: AraSpace.s8),
              Text(label, style: kSimpleText),
              if (detail != null) ...[
                const SizedBox(width: AraSpace.s8),
                Flexible(
                  child: Text(
                    detail,
                    style: kSimpleText.copyWith(color: AraColors.textSecondary),
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
              ],
              if (exposing) ...[
                const SizedBox(width: AraSpace.s12),
                SizedBox(
                  width: 120,
                  child: ClipRRect(
                    borderRadius: BorderRadius.circular(2),
                    child: LinearProgressIndicator(
                      value: (p.displayProgressPct ?? 0) / 100,
                      minHeight: 4,
                      backgroundColor: kSimpleTileFill,
                    ),
                  ),
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}
