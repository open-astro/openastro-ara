import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../widgets/imaging/exposure_controls_panel.dart';
import '../../widgets/imaging/frame_viewer.dart';
import '../../widgets/imaging/histogram_strip.dart';
import '../../widgets/status_indicator.dart';
import '../../theme/ara_colors.dart';

/// Imaging tab per playbook §25.5.1. Phase 12c.1 wires the layout (frame
/// viewer + histogram + exposure controls panel + always-visible §51 health
/// indicator) and the local Riverpod state. Phase 12c.2 connects Take One +
/// Live View toggle to the daemon and renders real frame previews.
class ImagingTab extends ConsumerStatefulWidget {
  const ImagingTab({super.key});

  @override
  ConsumerState<ImagingTab> createState() => _ImagingTabState();
}

class _ImagingTabState extends ConsumerState<ImagingTab> {
  bool _liveViewOn = false;

  @override
  Widget build(BuildContext context) {
    return Column(
      children: [
        const _ImagingHeader(),
        Expanded(
          child: Row(
            children: [
              Expanded(
                child: Column(
                  children: [
                    const Expanded(child: FrameViewer()),
                    const HistogramStrip(),
                  ],
                ),
              ),
              ExposureControlsPanel(
                liveViewOn: _liveViewOn,
                onTakeOne: () {
                  // Phase 12c.2: invoke /api/v1/sequence/exposure with the
                  // current ExposureParams + the connected camera.
                },
                onLiveViewToggle: (v) => setState(() => _liveViewOn = v),
              ),
            ],
          ),
        ),
      ],
    );
  }
}

class _ImagingHeader extends StatelessWidget {
  const _ImagingHeader();

  @override
  Widget build(BuildContext context) {
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
          // "always-visible" requirement. Wired to actual diagnostics
          // state in Phase 12c.2 alongside the Diagnostic Panel.
          const StatusIndicator(
            level: StatusLevel.disconnected,
            label: 'Diagnostics: not connected',
          ),
        ],
      ),
    );
  }
}
