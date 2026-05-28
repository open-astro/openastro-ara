import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/plate_solve_settings_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.10 Plate Solving panel — editable.
class ImagingPlateSolvePanel extends ConsumerWidget {
  const ImagingPlateSolvePanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(plateSolveSettingsProvider);
    final n = ref.read(plateSolveSettingsProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Solver'),
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Row(children: [
            SizedBox(
              width: 280,
              child: Text('Engine',
                  style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                        color: AraColors.textSecondary,
                      )),
            ),
            Expanded(
              child: DropdownButtonFormField<PlateSolveEngine>(
                initialValue: s.engine,
                isDense: true,
                items: const [
                  DropdownMenuItem(
                    value: PlateSolveEngine.astap,
                    child: Text('ASTAP'),
                  ),
                  DropdownMenuItem(
                    value: PlateSolveEngine.astrometryNet,
                    child: Text('astrometry.net'),
                  ),
                  DropdownMenuItem(
                    value: PlateSolveEngine.platesolve2,
                    child: Text('PlateSolve 2'),
                  ),
                ],
                onChanged: (v) {
                  if (v != null) n.setEngine(v);
                },
              ),
            ),
          ]),
        ),
        EditableTextRow(
          label: 'Path / endpoint',
          currentValue: s.pathOrEndpoint,
          getCanonical: () =>
              ref.read(plateSolveSettingsProvider).pathOrEndpoint,
          parse: n.setPathOrEndpoint,
        ),
        EditableTextRow(
          label: 'Index download path',
          currentValue: s.indexDownloadPath,
          getCanonical: () =>
              ref.read(plateSolveSettingsProvider).indexDownloadPath,
          parse: n.setIndexDownloadPath,
        ),
        const SettingsSectionHeader('Solving parameters'),
        EditableNumberRow(
          label: 'Search radius (°)',
          currentValue: s.searchRadiusDeg.toString(),
          getCanonical: () =>
              ref.read(plateSolveSettingsProvider).searchRadiusDeg.toString(),
          parse: (str) {
            final v = double.tryParse(str);
            if (v != null) n.setSearchRadiusDeg(v);
          },
        ),
        EditableNumberRow(
          label: 'Downsample factor (1..8)',
          currentValue: s.downsampleFactor.toString(),
          getCanonical: () => ref
              .read(plateSolveSettingsProvider)
              .downsampleFactor
              .toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setDownsampleFactor(v);
          },
        ),
        EditableNumberRow(
          label: 'Timeout (s)',
          currentValue: s.timeoutSeconds.toString(),
          getCanonical: () =>
              ref.read(plateSolveSettingsProvider).timeoutSeconds.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setTimeoutSeconds(v);
          },
        ),
        _SwitchRow(
          label: 'Use blind solve as fallback',
          value: s.useBlindFallback,
          onChanged: n.setUseBlindFallback,
          hint: 'if hint-based solve times out',
        ),
        const SettingsSectionHeader('Slew + sync'),
        _SwitchRow(
          label: 'Center after slew',
          value: s.centerAfterSlew,
          onChanged: n.setCenterAfterSlew,
        ),
        _SwitchRow(
          label: 'Sync to coordinates',
          value: s.syncToCoordinates,
          onChanged: n.setSyncToCoordinates,
        ),
        EditableNumberRow(
          label: 'Max iterations',
          currentValue: s.maxIterations.toString(),
          getCanonical: () =>
              ref.read(plateSolveSettingsProvider).maxIterations.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setMaxIterations(v);
          },
        ),
        EditableNumberRow(
          label: 'Convergence tolerance (″)',
          currentValue: s.convergenceToleranceArcsec.toString(),
          getCanonical: () => ref
              .read(plateSolveSettingsProvider)
              .convergenceToleranceArcsec
              .toString(),
          parse: (str) {
            final v = double.tryParse(str);
            if (v != null) n.setConvergenceToleranceArcsec(v);
          },
        ),
        const SizedBox(height: 24),
        Row(mainAxisAlignment: MainAxisAlignment.end, children: [
          FilledButton.icon(
            onPressed: () {
              ScaffoldMessenger.of(context).showSnackBar(
                const SnackBar(
                  content: Text(
                    'Plate Solve settings saved (in memory). Daemon round-trip lands in 12h.2b.',
                  ),
                ),
              );
            },
            icon: const Icon(Icons.save, size: 16),
            label: const Text('Save'),
          ),
        ]),
      ],
    );
  }
}

class _SwitchRow extends StatelessWidget {
  final String label;
  final bool value;
  final ValueChanged<bool> onChanged;
  final String? hint;
  const _SwitchRow({
    required this.label,
    required this.value,
    required this.onChanged,
    this.hint,
  });

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Row(children: [
        SizedBox(
          width: 280,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(label,
                  style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                        color: AraColors.textSecondary,
                      )),
              if (hint != null)
                Text(hint!,
                    style: Theme.of(context).textTheme.labelSmall?.copyWith(
                          color: AraColors.textDisabled,
                        )),
            ],
          ),
        ),
        Switch(value: value, onChanged: onChanged),
      ]),
    );
  }
}
