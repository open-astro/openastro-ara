import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/imaging/exposure_state.dart' show FrameKind;
import '../../../state/settings/imaging_defaults_state.dart';
import '../../../widgets/settings/editable_field.dart';

/// §37.9 Imaging Defaults panel. Phase 12h.3b registered all 8 fields in
/// `settings/registry.dart` and wired `helpKey`s on the non-obvious controls
/// (gain, offset, bin, ramp rate, warmup) — the ⌘K command palette now
/// finds them and the ⓘ icons explain them in-place.
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
          helpKey: 'imaging.defaults.gain',
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
          helpKey: 'imaging.defaults.offset',
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
          helpKey: 'imaging.defaults.bin',
          currentValue: d.defaultBin.toString(),
          getCanonical: () =>
              ref.read(imagingDefaultsProvider).defaultBin.toString(),
          parse: (s) {
            final i = int.tryParse(s);
            if (i != null) n.setBin(i);
          },
        ),
        SettingsDropdownRow<FrameKind>(
          label: 'Default frame type',
          value: d.defaultFrameKind,
          items: const {
            FrameKind.light: 'Light',
            FrameKind.dark: 'Dark',
            FrameKind.bias: 'Bias',
            FrameKind.flat: 'Flat',
          },
          onChanged: (v) {
            if (v != null) n.setFrameKind(v);
          },
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
          helpKey: 'imaging.defaults.cooler_ramp_c_per_min',
          currentValue: d.coolerRampRatePerMin.toString(),
          getCanonical: () =>
              ref.read(imagingDefaultsProvider).coolerRampRatePerMin.toString(),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) n.setCoolerRampRate(v);
          },
        ),
        SettingsSwitchRow(
          label: 'Warm-up cooler at session end',
          value: d.warmupAtSessionEnd,
          onChanged: n.setWarmupAtSessionEnd,
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
