import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/imaging/framing_state.dart';
import '../../state/sky_atlas/sky_atlas_state.dart';
import '../../theme/ara_colors.dart';
import '../../widgets/sky_atlas/aladin_view.dart';
import '../../widgets/sky_atlas/data_manager_modal.dart';
import '../../widgets/sky_atlas/sky_data_missing_banner.dart';

/// Planning tab (§25.5 + §36) — the merged Sky Atlas + Framing Assistant
/// surface decided 2026-06-15 (PORT_DECISIONS §36/§25.5). One embedded Aladin
/// Lite atlas ([AladinView], a `webview_cef` Chromium texture) where the user
/// **finds** a target (Explore / Tonight's Sky + universal search) and **frames
/// it** for capture (Frame toggle → framing panel), without a tab switch.
///
/// This slice does the structural merge + mode model. The profile-derived FOV
/// overlay + "Build Sequence" output land in the next §36 slice — until then
/// Frame mode shows the rotation/mosaic controls (relocated from the old
/// Framing tab) and the sequence action stays disabled.
class PlanningTab extends ConsumerWidget {
  const PlanningTab({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final mode = ref.watch(skyAtlasModeProvider);
    final frameMode = ref.watch(frameModeEnabledProvider);

    return Column(
      children: [
        SkyDataMissingBanner(
          onOpenDataManager: () => _openDataManager(context),
        ),
        _PlanningHeader(
          mode: mode,
          onModeChanged: ref.read(skyAtlasModeProvider.notifier).set,
          frameMode: frameMode,
          onFrameModeChanged: ref.read(frameModeEnabledProvider.notifier).set,
          onOpenDataManager: () => _openDataManager(context),
        ),
        const _SearchBar(),
        Expanded(
          child: Row(
            children: [
              const Expanded(child: AladinView()),
              // Frame mode slides the framing controls in beside the atlas.
              if (frameMode)
                _FramingParamsPanel(
                  framing: ref.watch(framingControllerProvider),
                  onRotationChanged: ref
                      .read(framingControllerProvider.notifier)
                      .setRotationDeg,
                  onColsChanged: ref
                      .read(framingControllerProvider.notifier)
                      .setMosaicCols,
                  onRowsChanged: ref
                      .read(framingControllerProvider.notifier)
                      .setMosaicRows,
                  onOverlapChanged: ref
                      .read(framingControllerProvider.notifier)
                      .setOverlapPct,
                ),
            ],
          ),
        ),
      ],
    );
  }

  void _openDataManager(BuildContext context) {
    showDialog<void>(
      context: context,
      builder: (_) => const DataManagerModal(),
    );
  }
}

class _PlanningHeader extends StatelessWidget {
  final SkyAtlasMode mode;
  final ValueChanged<SkyAtlasMode> onModeChanged;
  final bool frameMode;
  final ValueChanged<bool> onFrameModeChanged;
  final VoidCallback onOpenDataManager;

  const _PlanningHeader({
    required this.mode,
    required this.onModeChanged,
    required this.frameMode,
    required this.onFrameModeChanged,
    required this.onOpenDataManager,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      height: 48,
      padding: const EdgeInsets.symmetric(horizontal: 12),
      decoration: const BoxDecoration(
        color: AraColors.bgPanel,
        border: Border(bottom: BorderSide(color: AraColors.border)),
      ),
      child: Row(
        children: [
          // Left cluster scrolls horizontally if the window gets narrow (same
          // pattern as the top equipment bar) so the header never overflows;
          // Data Manager stays pinned at the right edge.
          Expanded(
            child: SingleChildScrollView(
              scrollDirection: Axis.horizontal,
              child: Row(
                children: [
                  const Icon(Icons.public, size: 18),
                  const SizedBox(width: 8),
                  Text('Planning',
                      style: Theme.of(context).textTheme.titleMedium),
                  const SizedBox(width: 16),
                  // View mode: Explore (equatorial browse) vs Tonight's Sky.
                  SegmentedButton<SkyAtlasMode>(
                    segments: const [
                      ButtonSegment(
                        value: SkyAtlasMode.catalogView,
                        label: Text('Explore'),
                        icon: Icon(Icons.travel_explore, size: 16),
                      ),
                      ButtonSegment(
                        value: SkyAtlasMode.tonightsSky,
                        label: Text('Tonight\'s Sky'),
                        icon: Icon(Icons.nights_stay_outlined, size: 16),
                      ),
                    ],
                    selected: {mode},
                    onSelectionChanged: (set) => onModeChanged(set.first),
                  ),
                  const SizedBox(width: 12),
                  // Frame toggle — pulls the framing controls in over the atlas.
                  FilterChip(
                    selected: frameMode,
                    onSelected: onFrameModeChanged,
                    avatar: const Icon(Icons.crop_free, size: 16),
                    label: const Text('Frame'),
                  ),
                  const SizedBox(width: 12),
                ],
              ),
            ),
          ),
          TextButton.icon(
            onPressed: onOpenDataManager,
            icon: const Icon(Icons.download_for_offline_outlined, size: 16),
            label: const Text('Data Manager'),
          ),
        ],
      ),
    );
  }
}

class _SearchBar extends ConsumerStatefulWidget {
  const _SearchBar();

  @override
  ConsumerState<_SearchBar> createState() => _SearchBarState();
}

class _SearchBarState extends ConsumerState<_SearchBar> {
  final _ctrl = TextEditingController();

  @override
  void dispose() {
    _ctrl.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
      decoration: const BoxDecoration(
        color: AraColors.bgPanel,
        border: Border(bottom: BorderSide(color: AraColors.border)),
      ),
      child: Row(
        children: [
          Expanded(
            child: TextField(
              controller: _ctrl,
              decoration: const InputDecoration(
                labelText: 'Universal search — name (M42), catalog ID, '
                    'RA/Dec, comet, asteroid',
                isDense: true,
                prefixIcon: Icon(Icons.search, size: 18),
              ),
              onSubmitted: (s) =>
                  ref.read(skyAtlasSearchProvider.notifier).set(s.trim()),
            ),
          ),
        ],
      ),
    );
  }
}

/// Framing controls, relocated from the old Framing tab. Rotation + mosaic
/// grid today; the profile-derived FOV box drawn on the atlas + the enabled
/// "Build Sequence" action arrive with the FOV slice (PORT_DECISIONS §36/§25.5).
class _FramingParamsPanel extends StatelessWidget {
  final FramingParams framing;
  final ValueChanged<double> onRotationChanged;
  final ValueChanged<int> onColsChanged;
  final ValueChanged<int> onRowsChanged;
  final ValueChanged<double> onOverlapChanged;

  const _FramingParamsPanel({
    required this.framing,
    required this.onRotationChanged,
    required this.onColsChanged,
    required this.onRowsChanged,
    required this.onOverlapChanged,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      width: 280,
      padding: const EdgeInsets.all(12),
      decoration: const BoxDecoration(
        color: AraColors.bgPanel,
        border: Border(left: BorderSide(color: AraColors.border)),
      ),
      child: ListView(
        children: [
          Text('Framing', style: Theme.of(context).textTheme.titleSmall),
          const SizedBox(height: 4),
          Text(
            'FOV preview uses your profile\'s camera + scope — wires up next.',
            style: Theme.of(context).textTheme.bodySmall?.copyWith(
                  color: AraColors.textSecondary,
                ),
          ),
          const SizedBox(height: 8),
          Text('Rotation: ${framing.rotationDeg.toStringAsFixed(1)}°',
              style: Theme.of(context).textTheme.bodyMedium),
          Slider(
            value: framing.rotationDeg,
            min: -180,
            max: 180,
            divisions: 360,
            onChanged: onRotationChanged,
          ),
          const SizedBox(height: 8),
          Text('Mosaic', style: Theme.of(context).textTheme.titleSmall),
          Row(children: [
            Expanded(
              child: _StepperField(
                label: 'Cols',
                value: framing.mosaicCols,
                min: 1,
                max: 10,
                onChanged: onColsChanged,
              ),
            ),
            const SizedBox(width: 8),
            Expanded(
              child: _StepperField(
                label: 'Rows',
                value: framing.mosaicRows,
                min: 1,
                max: 10,
                onChanged: onRowsChanged,
              ),
            ),
          ]),
          const SizedBox(height: 8),
          Text('Overlap: ${framing.mosaicOverlapPct.toStringAsFixed(0)}%',
              style: Theme.of(context).textTheme.bodyMedium),
          Slider(
            value: framing.mosaicOverlapPct,
            min: 0,
            max: 50,
            divisions: 50,
            onChanged: onOverlapChanged,
          ),
          const SizedBox(height: 16),
          // Disabled until the FOV slice wires the "append to Sequencer tree"
          // path — an enabled-but-no-op button would falsely suggest it works.
          FilledButton.icon(
            onPressed: null,
            icon: const Icon(Icons.add),
            label: const Text('Add to Sequence'),
          ),
        ],
      ),
    );
  }
}

class _StepperField extends StatelessWidget {
  final String label;
  final int value;
  final int min;
  final int max;
  final ValueChanged<int> onChanged;
  const _StepperField({
    required this.label,
    required this.value,
    required this.min,
    required this.max,
    required this.onChanged,
  });

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(label, style: Theme.of(context).textTheme.bodySmall),
        Row(children: [
          IconButton(
            iconSize: 18,
            onPressed: value > min ? () => onChanged(value - 1) : null,
            icon: const Icon(Icons.remove),
          ),
          Text('$value'),
          IconButton(
            iconSize: 18,
            onPressed: value < max ? () => onChanged(value + 1) : null,
            icon: const Icon(Icons.add),
          ),
        ]),
      ],
    );
  }
}
