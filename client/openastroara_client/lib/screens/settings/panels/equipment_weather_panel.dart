import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/equipment_device_status.dart';
import '../../../models/weather_status.dart';
import '../../../state/equipment/weather_state.dart';
import '../../../state/settings/equipment_connection_state.dart';
import '../../../state/settings/safety_policies_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../state/settings/site_settings_state.dart';
import '../../../util/tonight_sky_local.dart'
    show moonPhase, sqmDerived, sunMoonAltitudeDeg;
import '../../../widgets/equipment/equipment_connection_card.dart';
import '../../../widgets/equipment/equipment_time_format.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.4 Weather (ObservingConditions) panel. Shows the connected device's live
/// sensor readout via the shared connection card; the §35 threshold rows below
/// stay as references to the safety policy.
class EquipmentWeatherPanel extends ConsumerWidget {
  const EquipmentWeatherPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connection = ref.watch(equipmentConnectionProvider);
    final n = ref.read(equipmentConnectionProvider.notifier);
    final status = ref.watch(weatherProvider);
    final policies = ref.watch(safetyPoliciesProvider);
    final notifier = ref.read(weatherProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Connection'),
        EquipmentConnectionCard<WeatherStatus>(
          status: status,
          deviceType: EquipmentDeviceType.weather,
          deviceTypeLabel: 'weather station',
          emptyLabel: 'No weather station connected.',
          onConnect: notifier.connect,
          onDisconnect: notifier.disconnect,
          onReconnect: notifier.reconnect,
          onRetry: notifier.refresh,
          connectedBody: (context, s) => _WeatherBody(status: s),
        ),
        SettingsSwitchRow(
          label: 'Auto-connect on boot',
          helpKey: 'eq.auto_connect_on_boot',
          value: connection.autoConnect(EquipmentDeviceType.weather),
          onChanged: (v) => n.setAutoConnect(EquipmentDeviceType.weather, v),
        ),
        const SettingsSectionHeader('Thresholds'),
        // Live values from the §35 safety policies — the ONE place they are
        // configured. Shown read-only here so this panel can never disagree
        // with what actually parks the rig.
        SettingsRow(
          label: 'Weather triggers safety',
          value: policies.weatherTriggersEnabled ? 'On' : 'Off',
          hint: 'Edit in Settings → Safety → Policies',
        ),
        SettingsRow(
          label: 'Wind speed max (km/h)',
          value: policies.maxWindKmh.toString(),
          hint: 'Edit in Settings → Safety → Policies',
        ),
        SettingsRow(
          label: 'Humidity max (%)',
          value: policies.maxHumidityPct.toString(),
          hint: 'Edit in Settings → Safety → Policies',
        ),
        SettingsRow(
          label: 'Dew-point margin (°C)',
          value: policies.minDewDeltaC.toString(),
          hint: 'Edit in Settings → Safety → Policies',
        ),
      ],
    );
  }
}

/// Live sensor readout for the connected weather station. Every sensor is
/// optional — only the ones the device implements (non-null) are shown.
class _WeatherBody extends ConsumerWidget {
  final WeatherStatus status;
  const _WeatherBody({required this.status});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    // While connecting the readings are the daemon default (transient); an `error`
    // sub-state is a failed read, not "no sensors" — surface it distinctly.
    if (status.isConnecting) return const Text('Reading…');
    if (status.connectionState == EquipmentConnectionState.error) {
      return const Row(children: [
        Icon(Icons.error_outline, color: AraColors.accentError, size: 20),
        SizedBox(width: 8),
        Expanded(child: Text('Sensor read failed — check the device.')),
      ]);
    }

    final rows = <Widget?>[
      _sensor('Temperature', status.temperatureC, '°C'),
      _sensor('Humidity', status.humidityPct, '%'),
      _sensor('Dew point', status.dewPointC, '°C'),
      _sensor('Pressure', status.pressureHpa, ' hPa'),
      _sensor('Cloud cover', status.cloudCoverPct, '%'),
      _sensor('Wind speed', status.windSpeedMs, ' m/s'),
      _sensor('Wind gust', status.windGustMs, ' m/s'),
      _sensor('Wind direction', status.windDirectionDeg, '°'),
      _sensor('Rain rate', status.rainRate, ' mm/h'),
      _sensor('Sky quality (SQM)', status.skyQualityMagArcsec2, ' mag/arcsec²'),
      _sensor('SQM sensor temp', status.skyTemperatureC, '°C'),
    ].whereType<Widget>().toList();

    // SQM-derived sky metrics — pure functions of the meter reading (same
    // formulas the WeeWX SQM extension publishes), so relaying SQM alone
    // carries the whole family.
    final sqm = status.skyQualityMagArcsec2;
    if (sqm != null && sqm.isFinite) {
      final derived = sqmDerived(sqm);
      rows.addAll([
        _computed('Limiting magnitude (NELM)', derived.nelm.toStringAsFixed(2)),
        _computed('Sky luminance',
            '${derived.luminanceCdM2.toStringAsExponential(2)} cd/m²'),
        _computed('Night sky units (NSU)', derived.nsu.toStringAsFixed(2)),
      ]);
    }

    // The sun/moon rows are computed, so "no sensors" is judged before them.
    final noSensors = rows.isEmpty;

    // Computed, not station sensors: the sun/moon geometry belongs with the
    // sky conditions the observer plans around, so it rides in the same list.
    final nowUtc = DateTime.now().toUtc();
    final site = ref.watch(siteSettingsProvider);
    // Same unset-site guard as tonight_sky_state._rankAt: the (0,0) default
    // would confidently report Null Island altitudes — skip the rows instead.
    final siteSet = site.latitudeDeg != 0 || site.longitudeDeg != 0;
    if (siteSet) {
      final alt =
          sunMoonAltitudeDeg(nowUtc, site.latitudeDeg, site.longitudeDeg);
      rows.addAll([
        _computed('Sun altitude', '${alt.sunAltDeg.toStringAsFixed(1)}°'),
        _computed('Moon altitude', '${alt.moonAltDeg.toStringAsFixed(1)}°'),
      ]);
    }
    final moon = moonPhase(nowUtc);
    rows.add(Padding(
      padding: const EdgeInsets.symmetric(vertical: 2),
      child: Row(
        children: [
          const Expanded(child: Text('Moon')),
          Text('${(moon.illuminatedFraction * 100).round()}% lit — '
              '${moon.phaseName}'),
        ],
      ),
    ));

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        if (noSensors)
          // Computed sky rows still follow, so say what IS shown rather than
          // a bare "no sensors" that contradicts the list under it.
          const Text('This weather station reports no sensors — showing '
              'computed sky data only.'),
        ...rows,
        if (status.capturedAt != null)
          Padding(
            padding: const EdgeInsets.only(top: 8),
            child: Text(
              'Updated: ${formatUtcMinute(status.capturedAt!)}',
              style: Theme.of(context)
                  .textTheme
                  .bodySmall
                  ?.copyWith(color: AraColors.textSecondary),
            ),
          ),
      ],
    );
  }

  // A computed (non-sensor) row: same layout as [_sensor], value pre-formatted.
  Widget _computed(String label, String value) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 2),
        child: Row(
          children: [
            Expanded(child: Text(label)),
            Text(value),
          ],
        ),
      );

  // Returns null for an unimplemented (null) sensor — or a non-finite one (a
  // driver emitting NaN/Infinity) — so it's omitted rather than rendered as "NaN".
  Widget? _sensor(String label, double? value, String unit) {
    if (value == null || !value.isFinite) return null;
    final shown = value == value.roundToDouble()
        ? value.toInt().toString()
        : value.toStringAsFixed(1);
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 2),
      child: Row(
        children: [
          Expanded(child: Text(label)),
          Text('$shown$unit'),
        ],
      ),
    );
  }
}
