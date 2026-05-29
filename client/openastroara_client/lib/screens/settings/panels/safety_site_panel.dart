import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/site_settings_state.dart';
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
        SettingsSwitchRow(
          label: 'Use custom horizon polygon (§36.8)',
          helpKey: 'safety.site.use_custom_horizon',
          value: s.useCustomHorizon,
          onChanged: n.setUseCustomHorizon,
        ),
        if (!s.useCustomHorizon)
          EditableNumberRow(
            label: 'Default horizon altitude (°)',
            helpKey: 'safety.site.default_horizon_altitude_deg',
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
          helpKey: 'safety.site.bortle_class',
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
          helpKey: 'safety.site.typical_seeing_arcsec',
          currentValue: s.typicalSeeingArcsec.toString(),
          getCanonical: () =>
              ref.read(siteSettingsProvider).typicalSeeingArcsec.toString(),
          parse: (str) {
            final v = double.tryParse(str);
            if (v != null) n.setTypicalSeeingArcsec(v);
          },
        ),
        SettingsDropdownRow<TwilightDefinition>(
          label: 'Twilight definition',
          helpKey: 'safety.site.twilight_definition',
          value: s.twilightDefinition,
          items: const {
            TwilightDefinition.civil: 'Civil (−6°)',
            TwilightDefinition.nautical: 'Nautical (−12°)',
            TwilightDefinition.astronomical: 'Astronomical (−18°)',
          },
          onChanged: (v) {
            if (v != null) n.setTwilightDefinition(v);
          },
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
