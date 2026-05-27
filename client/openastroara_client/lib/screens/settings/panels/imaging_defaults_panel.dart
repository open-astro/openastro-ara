import 'package:flutter/material.dart';

import '../../../theme/ara_colors.dart';

/// Imaging defaults panel — values seeded from the wizard's `ImagingDefaults`
/// for the active profile, editable here per §61.4 (every setting must be
/// findable via the registry). Phase 12h.1 ships read-only stubs; 12h.2
/// wires the editable form + persistence to the profile JSON.
class ImagingDefaultsPanel extends StatelessWidget {
  const ImagingDefaultsPanel({super.key});

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.all(24),
      children: const [
        _SettingsField(label: 'Default exposure (s)', value: '5'),
        _SettingsField(label: 'Default gain', value: '100'),
        _SettingsField(label: 'Default offset', value: '50'),
        _SettingsField(label: 'Default bin', value: '1×1'),
        _SettingsField(label: 'Default frame type', value: 'Light'),
        _SettingsField(
          label: 'Cooling target temperature (°C)',
          value: '−10',
        ),
        _SettingsField(label: 'Cooler ramp rate (°C/min)', value: '1.0'),
        _SettingsField(label: 'Cooler warmup at session end', value: 'Off'),
      ],
    );
  }
}

class _SettingsField extends StatelessWidget {
  final String label;
  final String value;
  const _SettingsField({required this.label, required this.value});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Row(
        children: [
          SizedBox(
            width: 280,
            child: Text(label,
                style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                      color: AraColors.textSecondary,
                    )),
          ),
          // Phase 12h.1 ships read-only stubs — using Text instead of a
          // disabled TextField avoids allocating a TextEditingController
          // every rebuild. 12h.2 swaps these for real editable form fields
          // (each one in its own StatefulWidget with properly-disposed
          // controllers).
          Expanded(
            child: Text(value, style: Theme.of(context).textTheme.bodyMedium),
          ),
        ],
      ),
    );
  }
}
