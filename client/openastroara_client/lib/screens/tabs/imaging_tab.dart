import 'package:flutter/material.dart';
import '../../util/friendly_error.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/camera_exposure_api.dart';
import '../../services/frames_api.dart';
import '../../state/imaging/capture_progress_state.dart';
import '../../state/imaging/exposure_state.dart';
import '../../state/imaging/last_frame_state.dart';
import '../../state/imaging/live_view_frame_state.dart';
import '../../state/imaging/live_view_state.dart';
import '../../state/imaging/solve_state.dart';
import '../../state/saved_server_state.dart';
import '../../widgets/imaging/diagnostic_panel.dart';
import '../../widgets/equipment/cooler_controls.dart';
import '../../widgets/imaging/capture_progress_card.dart';
import '../../widgets/imaging/exposure_controls_panel.dart';
import '../../widgets/imaging/fault_panel.dart';
import '../../widgets/imaging/frame_viewer.dart';
import '../../widgets/imaging/guiding_panel.dart';
import '../../widgets/imaging/histogram_strip.dart';
import '../../widgets/imaging/solve_panel.dart';
import '../../widgets/status_indicator.dart';
import '../../theme/ara_colors.dart';

/// Imaging tab per playbook §25.5.1. Phase 12c.2: Live View state lifted
/// into `liveViewControllerProvider` (observable cross-component), §51
/// Health Indicator + Diagnostic Panel sourced from the diagnostics
/// provider (currently a stub; real WS event wiring lands in 12c.3).
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
                  onTakeOne: exposing ? null : () => _takeOne(context, ref),
                  onLiveViewToggle: (v) {
                    _toggleLiveView(context, ref, v);
                  },
                ),
                CaptureProgressCard(
                  onRetry: () => _takeOne(context, ref),
                  onCancel: () => _cancelCapture(context, ref),
                ),
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
                const SolvePanel(),
                const _RailGap(),
                const GuidingPanel(),
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

  /// §64 Live View toggle — drives the daemon's short-exposure loop. The
  /// boolean controller keeps the UI intent (toggle + any cross-component
  /// mirror); the frame notifier owns the start/poll/stop against the daemon,
  /// seeded with the current Imaging-tab exposure/gain/binning.
  Future<void> _toggleLiveView(
    BuildContext context,
    WidgetRef ref,
    bool on,
  ) async {
    final messenger = ScaffoldMessenger.of(context);
    final controller = ref.read(liveViewControllerProvider.notifier);
    controller.set(on); // optimistic — reflect the tap immediately
    final lv = ref.read(liveViewFrameProvider.notifier);
    if (on) {
      final p = ref.read(exposureControllerProvider);
      // inMicroseconds / 1e6 is lossless for sub-millisecond live exposures.
      await lv.start(
        exposureSec: p.exposure.inMicroseconds / 1e6,
        gain: p.gain,
        binX: p.bin,
        binY: p.bin,
      );
      // A start() failure flips active back to false — keep the toggle honest
      // (it can't sit stuck "on") AND surface why, since FrameViewer only shows
      // the live error while active.
      if (!context.mounted) return;
      final lvState = ref.read(liveViewFrameProvider);
      // Only an actual error means start failed — a null-error inactive state
      // here means the user toggled OFF while start() was in flight (stop() set
      // idle), which must not raise a spurious "couldn't start" snackbar.
      if (!lvState.active && lvState.error != null) {
        controller.set(false);
        messenger.showSnackBar(
          SnackBar(content: Text("Couldn't start Live View: ${lvState.error}")),
        );
      }
    } else {
      await lv.stop();
    }
  }

  /// §14e Take One — fire a single exposure on the connected camera. The
  /// daemon runs the capture in the background and registers the frame; we
  /// just surface accepted/failed to the user.
  Future<void> _takeOne(BuildContext context, WidgetRef ref) async {
    final messenger = ScaffoldMessenger.of(context);
    // Commit any half-typed control edit (the fields commit on focus loss)
    // before reading the exposure params this shot will use. The zero delay
    // lets the focus system deliver the change listeners.
    FocusManager.instance.primaryFocus?.unfocus();
    await Future<void>.delayed(Duration.zero);
    final server = ref.read(activeServerProvider);
    if (server == null) {
      messenger.showSnackBar(
        const SnackBar(content: Text('Not connected to a server.')),
      );
      return;
    }
    final params = ref.read(exposureControllerProvider);
    // Notifier handles, captured before any await so the finally-reset and the
    // result update don't go through WidgetRef after a possible unmount.
    final progress = ref.read(captureProgressProvider.notifier);
    final lastFrame = ref.read(lastCapturedFrameIdProvider.notifier);
    final solve = ref.read(solveResultProvider.notifier);
    progress.beginExposing(params.exposure);
    // Identity of this capture cycle — a Cancel (reset) or a newer Take One
    // bumps the generation, so this loop's late complete()/fail() no-op.
    final generation = progress.currentGeneration;
    // Phase 1 — the exposure POST. A failure here means the shot never
    // started; the user should re-shoot.
    final String frameId;
    try {
      frameId = await CameraExposureApi(server).takeOne(params);
    } catch (e) {
      progress.fail(
          friendlyError(e, action: 'take that exposure'),
          generation: generation);
      return;
    }
    // Phase 2 — the POST returned 202; the capture (expose → download → FITS)
    // runs in the background. Poll the catalog until the frame is registered.
    // A failure here means the exposure was accepted but we couldn't confirm
    // it landed — distinct remedy (retry the preview, don't re-shoot).
    final api = FramesApi(server);
    final deadline = DateTime.now().add(
      params.exposure + const Duration(seconds: 20),
    );
    var landed = false;
    try {
      while (DateTime.now().isBefore(deadline)) {
        // Keep polling even if the user navigated away — the capture runs on
        // the daemon regardless, and the notifier is root-scoped, so it must
        // still reach a terminal state (done/failed) to schedule its own
        // auto-clear. Only the UI side-effects below need the mounted guard.
        // A stale generation, though, means this cycle was cancelled or
        // superseded — its complete()/fail() would no-op anyway, so stop
        // polling instead of hitting the catalog until the full deadline
        // (rapid Cancel → Retry would otherwise stack several dead loops).
        if (!progress.isCurrent(generation)) return;
        if (await api.isRegistered(frameId)) {
          landed = true;
          break;
        }
        await Future<void>.delayed(const Duration(milliseconds: 500));
      }
    } catch (e) {
      progress.fail(friendlyError(e, action: 'confirm the frame arrived'),
          generation: generation);
      return;
    }
    if (landed) {
      progress.complete(frameId, generation: generation);
      // A stale cycle (cancelled or superseded while this loop polled) must
      // not repoint the viewer at its frame or clear the new cycle's solve.
      if (!context.mounted || !progress.isCurrent(generation)) return;
      lastFrame.set(frameId);
      // A new frame invalidates any previous solve result shown in the panel.
      solve.clear();
      // Force a re-fetch in case the same id was shown before.
      ref.invalidate(framePreviewProvider(frameId));
    } else {
      progress.fail('Capture timed out.', generation: generation);
    }
    // The notifier owns the terminal-state auto-clear (done ~1.8s, failed
    // ~6s) — nothing to do here.
  }

  /// Cancel the in-flight capture — abort the exposure on the daemon, then
  /// drop the capture state back to idle.
  Future<void> _cancelCapture(BuildContext context, WidgetRef ref) async {
    final messenger = ScaffoldMessenger.of(context);
    final progress = ref.read(captureProgressProvider.notifier);
    final server = ref.read(activeServerProvider);
    if (server == null) {
      progress.reset();
      return;
    }
    try {
      await CameraExposureApi(server).abort();
      progress.reset();
    } catch (e) {
      // The abort POST failed. A lost response is NOT a successful abort —
      // the exposure may still be running and its frame will land, so do NOT
      // fail the card or bump the generation here: that would orphan the
      // frame (the poll loop's complete() would no-op and lastFrame.set would
      // be skipped). Keep tracking the capture and tell the user the cancel
      // didn't go through; the card resolves with the truth (done or timeout).
      if (!progress.isCapturing) return; // already reset or resolved (a
      // double-tap race where the first abort won, or the capture finished
      // while the abort was in flight) — nothing to warn about.
      if (!context.mounted) return;
      messenger.showSnackBar(SnackBar(
        content: Text(friendlyError(e, action: 'abort the capture')),
        backgroundColor: AraColors.accentError,
      ));
    }
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
