import 'package:flutter/material.dart';
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

/// Height of the last-frame facts slot at the top of the column (DESIGN.md
/// layout C: 164 px).
const double kSimpleFactsHeight = 164;

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
      // The facts panel waits for something to describe: a saved frame
      // or a running loop.
      final hasFrame =
          ref.watch(lastCapturedFrameIdProvider) != null ||
          ref.watch(liveViewControllerProvider);
      return Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          // The slot is always reserved so the controls never move when the
          // first frame lands; only the panel inside it waits for a frame.
          SizedBox(
            height: kSimpleFactsHeight,
            child: hasFrame
                ? const Align(
                    alignment: Alignment.topCenter,
                    child: SimplePanel(
                      padding: EdgeInsets.symmetric(
                        horizontal: AraSpace.s16,
                        vertical: AraSpace.s12,
                      ),
                      child: _LastFrameFacts(),
                    ),
                  )
                : null,
          ),
          const SizedBox(height: 16),
          const Expanded(
            child: SimplePanel(
              padding: EdgeInsets.all(AraSpace.s12),
              child: _PreviewControls(),
            ),
          ),
        ],
      );
    },
  );
}

class _LastFrameFacts extends ConsumerWidget {
  const _LastFrameFacts();

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
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(looping ? 'Live view' : 'Last frame', style: AraText.caption),
        const SizedBox(height: 4),
        row('HFR', analysed?.hfr?.toStringAsFixed(2) ?? '—'),
        row('Stars', analysed?.stars?.toString() ?? '—'),
        row('Exposure', '${seconds(params.exposure)} · ${params.filterSlot}'),
        row('Saved', looping ? 'No' : (id == null ? '—' : 'Yes')),
      ],
    );
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
              running: looping || capturing,
              onPressed: switch (shot) {
                PreviewShot.loop => () => toggleLiveView(
                  context,
                  ref,
                  !looping,
                ),
                // The square stops the shot in flight, like a loop.
                PreviewShot.single =>
                  capturing
                      ? () => cancelCapture(context, ref)
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
          const _CaptureStatus(),
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

/// The shot in flight, in one line under the switch: the full UI's progress
/// card (with its own Cancel and Retry) is too wide for the column, so the
/// shutter cancels and retakes instead.
class _CaptureStatus extends ConsumerWidget {
  const _CaptureStatus();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final p = ref.watch(captureProgressProvider);
    final (String? text, Color color) = switch (p.phase) {
      CapturePhase.exposing => (
        p.exposureRemaining == null
            ? 'Starting…'
            : '${(p.exposureRemaining!.inMilliseconds / 1000).toStringAsFixed(1)} s left',
        AraColors.textSecondary,
      ),
      CapturePhase.downloading => ('Downloading…', AraColors.textSecondary),
      CapturePhase.done => ('Frame ready', AraColors.accentConnected),
      CapturePhase.failed => (
        p.error ?? 'Capture failed',
        AraColors.accentError,
      ),
      CapturePhase.idle => (null, AraColors.textSecondary),
    };
    if (text == null) return const SizedBox.shrink();
    return Padding(
      padding: const EdgeInsets.only(top: AraSpace.s12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          if (p.phase == CapturePhase.exposing)
            ClipRRect(
              borderRadius: BorderRadius.circular(4),
              child: LinearProgressIndicator(
                value: (p.displayProgressPct ?? 0) / 100,
                minHeight: 6,
                backgroundColor: kSimpleTileFill,
              ),
            ),
          const SizedBox(height: AraSpace.s4),
          Text(
            text,
            style: AraText.caption.copyWith(color: color),
            maxLines: 3,
            overflow: TextOverflow.ellipsis,
          ),
        ],
      ),
    );
  }
}
