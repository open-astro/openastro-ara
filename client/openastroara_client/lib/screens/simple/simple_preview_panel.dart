import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/imaging/capture_progress_state.dart';
import '../../state/imaging/exposure_state.dart';
import '../../state/imaging/last_frame_state.dart';
import '../../state/imaging/live_view_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../widgets/imaging/capture_progress_card.dart';
import '../../widgets/imaging/exposure_controls_panel.dart';
import '../../widgets/imaging/histogram_strip.dart' show frameHistogramProvider;
import '../imaging_actions.dart';
import 'simple_shell.dart' show SimplePanel;

/// Simple Mode body text: 14 px minimum (brief), tabular for live numbers.
const kSimpleText = TextStyle(
  fontSize: 14,
  color: AraColors.textPrimary,
  fontFeatures: [FontFeature.tabularFigures()],
);

/// Single takes a saved frame (the daemon registers every Take One); Loop
/// runs live view, whose frames are not saved.
enum PreviewShot { loop, single }

final previewShotProvider =
    NotifierProvider<_PreviewShot, PreviewShot>(_PreviewShot.new);

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
  Widget build(BuildContext context) => const Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          SimplePanel(
            padding: EdgeInsets.symmetric(
                horizontal: AraSpace.s16, vertical: AraSpace.s12),
            child: _LastFrameFacts(),
          ),
          SizedBox(height: 16),
          Expanded(
            child: SimplePanel(
              padding: EdgeInsets.all(AraSpace.s12),
              child: _PreviewControls(),
            ),
          ),
        ],
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
                PreviewShot.loop => () => toggleLiveView(context, ref, !looping),
                PreviewShot.single =>
                  capturing ? null : () => takeOne(context, ref),
              },
            ),
          ),
          const SizedBox(height: AraSpace.s16),
          SegmentedButton<PreviewShot>(
            showSelectedIcon: false,
            segments: const [
              ButtonSegment(value: PreviewShot.loop, label: Text('Loop')),
              ButtonSegment(value: PreviewShot.single, label: Text('Single')),
            ],
            selected: {shot},
            // Switching while a loop runs would strand it with no stop.
            onSelectionChanged: looping
                ? null
                : (s) => ref.read(previewShotProvider.notifier).set(s.first),
          ),
          CaptureProgressCard(
            onRetry: () => takeOne(context, ref),
            onCancel: () => cancelCapture(context, ref),
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
