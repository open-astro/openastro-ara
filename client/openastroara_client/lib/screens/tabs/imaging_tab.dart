import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/imaging/live_view_state.dart';
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
                onTakeOne: () {
                  // Phase 12c.3: invoke /api/v1/sequence/exposure with the
                  // current ExposureParams + the connected camera.
                },
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
