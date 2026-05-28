import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/site_settings_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.12 Site preferences — editable.
class SafetySitePanel extends ConsumerWidget {
  const SafetySitePanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(siteSettingsProvider);
    final n = ref.read(siteSettingsProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Location'),
        EditableTextRow(
          label: 'Site name',
          currentValue: s.siteName,
          getCanonical: () => ref.read(siteSettingsProvider).siteName,
          parse: n.setSiteName,
        ),
        EditableNumberRow(
          label: 'Latitude (°)',
          currentValue: s.latitudeDeg.toString(),
          getCanonical: () =>
              ref.read(siteSettingsProvider).latitudeDeg.toString(),
          parse: (str) {
            final v = double.tryParse(str);
            if (v != null) n.setLatitudeDeg(v);
          },
        ),
        EditableNumberRow(
          label: 'Longitude (°)',
          currentValue: s.longitudeDeg.toString(),
          getCanonical: () =>
              ref.read(siteSettingsProvider).longitudeDeg.toString(),
          parse: (str) {
            final v = double.tryParse(str);
            if (v != null) n.setLongitudeDeg(v);
          },
        ),
        EditableNumberRow(
          label: 'Elevation (m)',
          currentValue: s.elevationM.toString(),
          getCanonical: () =>
              ref.read(siteSettingsProvider).elevationM.toString(),
          parse: (str) {
            final v = double.tryParse(str);
            if (v != null) n.setElevationM(v);
          },
        ),
        EditableTextRow(
          label: 'Time zone',
          currentValue: s.timeZone,
          getCanonical: () => ref.read(siteSettingsProvider).timeZone,
          parse: n.setTimeZone,
          hint: 'IANA name (e.g. America/Los_Angeles)',
        ),
        const SettingsSectionHeader('Horizon'),
        _SwitchRow(
          label: 'Use custom horizon polygon (§36.8)',
          value: s.useCustomHorizon,
          onChanged: n.setUseCustomHorizon,
        ),
        if (!s.useCustomHorizon)
          EditableNumberRow(
            label: 'Default horizon altitude (°)',
            currentValue: s.defaultHorizonAltitudeDeg.toString(),
            getCanonical: () => ref
                .read(siteSettingsProvider)
                .defaultHorizonAltitudeDeg
                .toString(),
            parse: (str) {
              final v = double.tryParse(str);
              if (v != null) n.setDefaultHorizonAltitudeDeg(v);
            },
          ),
        const SettingsSectionHeader('Conditions defaults'),
        EditableNumberRow(
          label: 'Bortle class (1..9)',
          currentValue: s.bortleClass.toString(),
          getCanonical: () =>
              ref.read(siteSettingsProvider).bortleClass.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setBortleClass(v);
          },
        ),
        EditableNumberRow(
          label: 'Typical seeing (″)',
          currentValue: s.typicalSeeingArcsec.toString(),
          getCanonical: () =>
              ref.read(siteSettingsProvider).typicalSeeingArcsec.toString(),
          parse: (str) {
            final v = double.tryParse(str);
            if (v != null) n.setTypicalSeeingArcsec(v);
          },
        ),
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Row(
            children: [
              SizedBox(
                width: 280,
                child: Text('Twilight definition',
                    style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                          color: AraColors.textSecondary,
                        )),
              ),
              Expanded(
                child: DropdownButtonFormField<TwilightDefinition>(
                  initialValue: s.twilightDefinition,
                  isDense: true,
                  items: const [
                    DropdownMenuItem(
                      value: TwilightDefinition.civil,
                      child: Text('Civil (−6°)'),
                    ),
                    DropdownMenuItem(
                      value: TwilightDefinition.nautical,
                      child: Text('Nautical (−12°)'),
                    ),
                    DropdownMenuItem(
                      value: TwilightDefinition.astronomical,
                      child: Text('Astronomical (−18°)'),
                    ),
                  ],
                  onChanged: (v) {
                    if (v != null) n.setTwilightDefinition(v);
                  },
                ),
              ),
            ],
          ),
        ),
        const SizedBox(height: 24),
        Row(mainAxisAlignment: MainAxisAlignment.end, children: [
          FilledButton.icon(
            onPressed: () {
              ScaffoldMessenger.of(context).showSnackBar(
                const SnackBar(
                  content: Text(
                    'Site settings saved (in memory). Daemon round-trip lands in 12h.2b.',
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
  const _SwitchRow({
    required this.label,
    required this.value,
    required this.onChanged,
  });

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Row(children: [
        SizedBox(
          width: 280,
          child: Text(label,
              style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                    color: AraColors.textSecondary,
                  )),
        ),
        Switch(value: value, onChanged: onChanged),
      ]),
    );
  }
}
