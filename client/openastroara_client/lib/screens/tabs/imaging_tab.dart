import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../services/camera_exposure_api.dart';
import '../../services/frames_api.dart';
import '../../state/imaging/exposure_state.dart';
import '../../state/imaging/last_frame_state.dart';
import '../../state/imaging/live_view_state.dart';
import '../../state/saved_server_state.dart';
import '../../widgets/imaging/diagnostic_panel.dart';
import '../../widgets/imaging/exposure_controls_panel.dart';
import '../../widgets/imaging/frame_viewer.dart';
import '../../widgets/imaging/histogram_strip.dart';
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
    final exposing = ref.watch(captureInProgressProvider);
    return Column(
      children: [
        const _ImagingHeader(),
        Expanded(
          child: Row(
            children: [
              Expanded(
                child: Column(
                  children: const [
                    Expanded(child: FrameViewer()),
                    HistogramStrip(),
                    DiagnosticPanel(),
                  ],
                ),
              ),
              ExposureControlsPanel(
                liveViewOn: liveViewOn,
                onTakeOne: exposing ? null : () => _takeOne(context, ref),
                onLiveViewToggle: ref
                    .read(liveViewControllerProvider.notifier)
                    .set,
              ),
            ],
          ),
        ),
      ],
    );
  }

  /// §14e Take One — fire a single exposure on the connected camera. The
  /// daemon runs the capture in the background and registers the frame; we
  /// just surface accepted/failed to the user.
  Future<void> _takeOne(BuildContext context, WidgetRef ref) async {
    final messenger = ScaffoldMessenger.of(context);
    final servers = ref.read(savedServersProvider).maybeWhen(
          data: (list) => list,
          orElse: () => const <AraServer>[],
        );
    if (servers.isEmpty) {
      messenger.showSnackBar(
        const SnackBar(content: Text('Not connected to a server.')),
      );
      return;
    }
    final params = ref.read(exposureControllerProvider);
    final server = servers.last;
    // Notifier handles, captured before any await so the finally-reset and the
    // result update don't go through WidgetRef after a possible unmount.
    final progress = ref.read(captureInProgressProvider.notifier);
    final lastFrame = ref.read(lastCapturedFrameIdProvider.notifier);
    progress.set(true);
    messenger.showSnackBar(
      SnackBar(content: Text(
          'Exposing ${params.exposure.inMilliseconds / 1000.0}s…')),
    );
    try {
      // Phase 1 — the exposure POST. A failure here means the shot never
      // started; the user should re-shoot.
      final String frameId;
      try {
        frameId = await CameraExposureApi(server).takeOne(params);
      } catch (e) {
        if (context.mounted) {
          messenger.hideCurrentSnackBar();
          messenger.showSnackBar(
            SnackBar(content: Text('Exposure failed: $e')),
          );
        }
        return;
      }
      // Phase 2 — the POST returned 202; the capture (expose → download → FITS)
      // runs in the background. Poll the catalog until the frame is registered.
      // A failure here means the exposure was accepted but we couldn't confirm
      // it landed — distinct remedy (retry the preview, don't re-shoot).
      final api = FramesApi(server);
      final deadline = DateTime.now()
          .add(params.exposure + const Duration(seconds: 20));
      var landed = false;
      try {
        while (DateTime.now().isBefore(deadline)) {
          // Bail if the user navigated away mid-capture — stop polling and
          // don't touch a detached scaffold.
          if (!context.mounted) return;
          if (await api.isRegistered(frameId)) {
            landed = true;
            break;
          }
          await Future<void>.delayed(const Duration(milliseconds: 500));
        }
      } catch (e) {
        if (context.mounted) {
          messenger.hideCurrentSnackBar();
          messenger.showSnackBar(
            SnackBar(content: Text(
                'Exposure accepted but confirming the frame failed: $e')),
          );
        }
        return;
      }
      // The widget can unmount during the final delay, after the loop exits.
      if (!context.mounted) return;
      // Replace the "Exposing…" snackbar rather than queueing behind it.
      messenger.hideCurrentSnackBar();
      if (landed) {
        lastFrame.set(frameId);
        // Force a re-fetch in case the same id was shown before.
        ref.invalidate(framePreviewProvider(frameId));
        messenger.showSnackBar(
          const SnackBar(content: Text('Frame captured.')),
        );
      } else {
        messenger.showSnackBar(
          const SnackBar(content: Text('Capture timed out.')),
        );
      }
    } finally {
      // Direct call on the captured notifier — safe even if the widget
      // unmounted (the notifier lives in the ProviderContainer).
      progress.set(false);
    }
  }
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
          // diagnosticsStateProvider (stub today; real WS event stream
          // in 12c.3).
          Consumer(builder: (context, ref, _) {
            final diag = ref.watch(diagnosticsStateProvider);
            return StatusIndicator(
              level: diag.level,
              label: diag.label,
              // Tap wires up in Phase 12c.3 (opens §51 expanded view).
              // null here so the affordance isn't misleading until then.
              onTap: null,
            );
          }),
        ],
      ),
    );
  }
}
