import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/imaging/exposure_state.dart' show FrameKind;
import '../../../state/settings/imaging_defaults_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/settings/editable_field.dart';

/// §37.11 Imaging Defaults panel. Phase 12h.2-imaging-b made the form
/// editable; 12h.2-display-sync swaps the panel's local _NumberField for
/// the shared `EditableNumberRow` so rejected input snaps back to the
/// canonical state (round-1 CR finding on PR #94).
class ImagingDefaultsPanel extends ConsumerWidget {
  const ImagingDefaultsPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final d = ref.watch(imagingDefaultsProvider);
    final n = ref.read(imagingDefaultsProvider.notifier);
    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        EditableNumberRow(
          label: 'Default exposure (s)',
          currentValue: d.defaultExposure.inSeconds.toString(),
          getCanonical: () =>
              ref.read(imagingDefaultsProvider).defaultExposure.inSeconds.toString(),
          parse: (s) {
            final i = int.tryParse(s);
            if (i == null) return;
            n.setExposure(Duration(seconds: i));
          },
        ),
        EditableNumberRow(
          label: 'Default gain',
          currentValue: d.defaultGain.toString(),
          getCanonical: () =>
              ref.read(imagingDefaultsProvider).defaultGain.toString(),
          parse: (s) {
            final i = int.tryParse(s);
            if (i != null) n.setGain(i);
          },
        ),
        EditableNumberRow(
          label: 'Default offset',
          currentValue: d.defaultOffset.toString(),
          getCanonical: () =>
              ref.read(imagingDefaultsProvider).defaultOffset.toString(),
          parse: (s) {
            final i = int.tryParse(s);
            if (i != null) n.setOffset(i);
          },
        ),
        EditableNumberRow(
          label: 'Default bin',
          currentValue: d.defaultBin.toString(),
          getCanonical: () =>
              ref.read(imagingDefaultsProvider).defaultBin.toString(),
          parse: (s) {
            final i = int.tryParse(s);
            if (i != null) n.setBin(i);
          },
        ),
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Row(
            children: [
              SizedBox(
                width: 280,
                child: Text('Default frame type',
                    style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                          color: AraColors.textSecondary,
                        )),
              ),
              Expanded(
                child: DropdownButtonFormField<FrameKind>(
                  value: d.defaultFrameKind,
                  isDense: true,
                  items: const [
                    DropdownMenuItem(value: FrameKind.light, child: Text('Light')),
                    DropdownMenuItem(value: FrameKind.dark, child: Text('Dark')),
                    DropdownMenuItem(value: FrameKind.bias, child: Text('Bias')),
                    DropdownMenuItem(value: FrameKind.flat, child: Text('Flat')),
                  ],
                  onChanged: (v) {
                    if (v != null) n.setFrameKind(v);
                  },
                ),
              ),
            ],
          ),
        ),
        EditableNumberRow(
          label: 'Cooling target temperature (°C)',
          currentValue: d.coolerTargetC.toString(),
          getCanonical: () =>
              ref.read(imagingDefaultsProvider).coolerTargetC.toString(),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) n.setCoolerTargetC(v);
          },
        ),
        EditableNumberRow(
          label: 'Cooler ramp rate (°C/min)',
          currentValue: d.coolerRampRatePerMin.toString(),
          getCanonical: () =>
              ref.read(imagingDefaultsProvider).coolerRampRatePerMin.toString(),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) n.setCoolerRampRate(v);
          },
        ),
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Row(
            children: [
              SizedBox(
                width: 280,
                child: Text('Warm-up cooler at session end',
                    style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                          color: AraColors.textSecondary,
                        )),
              ),
              Switch(
                value: d.warmupAtSessionEnd,
                onChanged: n.setWarmupAtSessionEnd,
              ),
            ],
          ),
        ),
        const SizedBox(height: 24),
        Row(
          mainAxisAlignment: MainAxisAlignment.end,
          children: [
            FilledButton.icon(
              onPressed: () {
                ScaffoldMessenger.of(context).showSnackBar(
                  const SnackBar(
                    content: Text(
                      'Imaging defaults saved (in memory). Daemon round-trip lands in 12h.2b.',
                    ),
                  ),
                );
              },
              icon: const Icon(Icons.save, size: 16),
              label: const Text('Save'),
            ),
          ],
        ),
      ],
    );
  }
}
