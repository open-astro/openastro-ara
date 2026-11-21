import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/imaging/capture_progress_state.dart';
import '../../state/imaging/live_view_state.dart';
import '../../widgets/imaging/diagnostic_panel.dart';
import '../../widgets/equipment/cooler_controls.dart';
import '../../widgets/imaging/capture_progress_card.dart';
import '../../widgets/imaging/exposure_controls_panel.dart';
import '../../widgets/imaging/fault_panel.dart';
import '../../widgets/imaging/frame_viewer.dart';
import '../../widgets/imaging/exposure_timer_banner.dart';
import '../../widgets/imaging/guiding_strip.dart';
import '../../widgets/imaging/histogram_strip.dart';
import '../../widgets/imaging/solve_panel.dart';
import '../../widgets/status_indicator.dart';
import '../imaging_actions.dart';
import '../../theme/ara_colors.dart';

/// Imaging tab per playbook §25.5.1. Phase 12c.2: Live View state lifted
/// into `liveViewControllerProvider` (observable cross-component), §51
/// Health Indicator + Diagnostic Panel sourced from the diagnostics
/// provider (currently a stub; real WS event wiring lands in 12c.3).
///
/// Layout: the frame viewer and its right rail fill the window, with the
/// §63.18 guiding strip running the full width along the bottom — the PHD2 /
/// web-guider arrangement, so the guide graph is read at a glance under the
/// image instead of buried in the rail. The exposure timer sits in the rail
/// under Take One whenever the daemon's camera is exposing for anyone else
/// (a sequence, Smart Focus, a plate solve); Take One keeps its own card.
class ImagingTab extends ConsumerWidget {
  const ImagingTab({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final liveViewOn = ref.watch(liveViewControllerProvider);
    // Gate the main Take One button on an ACTIVE capture only (exposing /
    // downloading). The terminal display windows (done/failed) must not keep
    // it disabled — Retry covers the failed card, and the user may want to
    // tweak settings and re-shoot immediately after a result.
    final exposing = ref.watch(captureProgressProvider).isCapturing;
    return LayoutBuilder(
      builder: (context, c) => Column(
        children: [
          Expanded(child: _viewerAndRail(context, ref, liveViewOn, exposing)),
          // §63.18 — the guide graph strip, full width under viewer + rail;
          // it grows with the window so a tall monitor gets a readable graph.
          GuidingStrip(graphHeight: GuidingStrip.graphHeightFor(c.maxHeight)),
        ],
      ),
    );
  }

  Widget _viewerAndRail(
      BuildContext context, WidgetRef ref, bool liveViewOn, bool exposing) {
    return Row(
      // Stretch, not the default center: the rail Container shrink-wraps its
      // content and would otherwise float vertically centered in the row.
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        // The frame owns the whole canvas — every sensor aspect fits with
        // the least possible letterboxing when the viewer is as tall as the
        // window allows. The header tops only this column so the rail can
        // run flush to the top edge.
        const Expanded(
          child: Column(
            children: [
              _ImagingHeader(),
              Expanded(child: FrameViewer()),
            ],
          ),
        ),
        // Right rail: capture controls first, then solve + health —
        // the panels that used to squeeze the viewer from below. One
        // scroll view so an expanded panel never overflows the window.
        // The Container (not the Row's default centering) owns the
        // full-height background so short content pins to the top.
        Container(
          width: 320,
          decoration: const BoxDecoration(
            color: AraColors.bgPanel,
            border: Border(left: BorderSide(color: AraColors.border)),
          ),
          child: SingleChildScrollView(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                ExposureControlsPanel(
                  liveViewOn: liveViewOn,
                  onTakeOne: exposing ? null : () => takeOne(context, ref),
                  onLiveViewToggle: (v) {
                    toggleLiveView(context, ref, v);
                  },
                ),
                // In-flight capture status right under Take One (hidden when
                // idle).
                CaptureProgressCard(
                  onRetry: () => takeOne(context, ref),
                  onCancel: () => cancelCapture(context, ref),
                ),
                // The timer for every OTHER exposure the daemon runs — a
                // sequence sub, a Smart Focus probe, a plate-solve capture —
                // in the same slot, so "how long is left" is always read here.
                // A Take One already has its card above with the countdown,
                // so the banner steps aside while one is capturing.
                if (!exposing)
                  const Padding(
                    padding: EdgeInsets.fromLTRB(12, 0, 12, 12),
                    child: ExposureTimerBanner(),
                  ),
                // Solve sits directly under Take One with the panel's top
                // border as the separator — plate-solve the frame you just
                // took without scrolling.
                const SolvePanel(),
                // Separation before the cooling target section.
                const _RailGap(),
                // §25.5.5 — cooler on/off + target presets (−10/−5/0/+5 °C) +
                // custom target + fan, shared with Settings → Camera. Hidden
                // entirely when no camera with a cooler is connected.
                // Rail sections carry their own horizontal 12 padding (the
                // rail Container itself is unpadded).
                const Padding(
                  padding: EdgeInsets.symmetric(horizontal: 12),
                  child: CoolerControls(compact: true),
                ),
                const _RailGap(),
                const HistogramStrip(),
                const _RailGap(),
                const DiagnosticPanel(),
                const _RailGap(),
                const FaultPanel(),
              ],
            ),
          ),
        ),
      ],
    );
  }
}

/// Consistent vertical breathing room between the right-rail sections
/// (HIG spacing grid — sections never sit flush against each other).
class _RailGap extends StatelessWidget {
  const _RailGap();

  @override
  Widget build(BuildContext context) => const SizedBox(height: 12);
}

class _ImagingHeader extends ConsumerWidget {
  const _ImagingHeader();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    return Container(
      height: 40,
      padding: const EdgeInsets.symmetric(horizontal: 12),
      decoration: const BoxDecoration(
        color: AraColors.bgPanel,
        border: Border(bottom: BorderSide(color: AraColors.border)),
      ),
      child: Row(
        children: [
          const Icon(Icons.camera_alt, size: 18),
          const SizedBox(width: 8),
          Text('Imaging', style: Theme.of(context).textTheme.titleMedium),
          const Spacer(),
          // §51 Health Indicator — always visible per the playbook's
          // "always-visible" requirement. Sourced from
          // diagnosticsStateProvider, rolled up from the live `diagnostics.*`
          // WS event stream (WS slice 5).
          Consumer(
            builder: (context, ref, _) {
              final diag = ref.watch(diagnosticsStateProvider);
              return StatusIndicator(
                level: diag.level,
                label: diag.label,
                // §51 — the health chip is the summary; tapping opens the same
                // diagnostics panel that lives in the right-hand column, so the
                // "why is it amber?" answer is one tap away from any scroll
                // position.
                onTap: () => showModalBottomSheet<void>(
                  context: context,
                  showDragHandle: true,
                  builder: (_) => const SingleChildScrollView(
                    padding: EdgeInsets.only(bottom: 24),
                    child: DiagnosticPanel(),
                  ),
                ),
              );
            },
          ),
        ],
      ),
    );
  }
}
