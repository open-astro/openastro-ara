import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/imaging/framing_state.dart';
import '../../theme/ara_colors.dart';

/// Framing Assistant per §25.5.2. Phase 12c.1 wires the three-panel layout
/// (target search top, sky-chart preview center, framing params right) with
/// placeholder content. Phase 12c.2 lights up the bundled catalog (Messier/
/// NGC/IC/by name or coords) + the sky-chart star-catalog scatter.
class FramingTab extends ConsumerWidget {
  const FramingTab({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    return Column(
      children: [
        const _FramingHeader(),
        const _TargetSearchBar(),
        Expanded(
          child: Row(
            children: [
              const Expanded(child: _SkyChartPreview()),
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
}

class _FramingHeader extends StatelessWidget {
  const _FramingHeader();

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
          const Icon(Icons.crop_free, size: 18),
          const SizedBox(width: 8),
          Text('Framing Assistant',
              style: Theme.of(context).textTheme.titleMedium),
        ],
      ),
    );
  }
}

class _TargetSearchBar extends ConsumerStatefulWidget {
  const _TargetSearchBar();

  @override
  ConsumerState<_TargetSearchBar> createState() => _TargetSearchBarState();
}

class _TargetSearchBarState extends ConsumerState<_TargetSearchBar> {
  final _ctrl = TextEditingController();

  @override
  void dispose() {
    _ctrl.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(12),
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
                labelText: 'Search: name (M42, NGC 7000), catalog ID, or RA/Dec',
                isDense: true,
              ),
              onSubmitted: (s) => ref
                  .read(targetSearchQueryProvider.notifier)
                  .set(s.trim()),
            ),
          ),
          const SizedBox(width: 8),
          FilledButton.icon(
            onPressed: () => ref
                .read(targetSearchQueryProvider.notifier)
                .set(_ctrl.text.trim()),
            icon: const Icon(Icons.search, size: 18),
            label: const Text('Search'),
          ),
        ],
      ),
    );
  }
}

class _SkyChartPreview extends ConsumerWidget {
  const _SkyChartPreview();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final query = ref.watch(targetSearchQueryProvider);
    return Container(
      color: AraColors.bgPanelAlt,
      child: Center(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const Icon(Icons.satellite_alt,
                size: 96, color: AraColors.textDisabled),
            const SizedBox(height: 12),
            Text(
              query.isEmpty
                  ? 'Type a target name to preview the sky chart'
                  : 'Bundled catalog lookup wires up in Phase 12c.2 — query: "$query"',
              style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                    color: AraColors.textSecondary,
                  ),
            ),
          ],
        ),
      ),
    );
  }
}

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
          Text('Mosaic',
              style: Theme.of(context).textTheme.titleSmall),
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
          FilledButton.icon(
            onPressed: () {
              // Phase 12c.2: append the current target + framing params to
              // the Sequencer tree as a new Target node.
            },
            icon: const Icon(Icons.add),
            label: const Text('Set as Target'),
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
