import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lat_lng_to_timezone/lat_lng_to_timezone.dart' as tz_map;

import '../../../services/profile_api.dart';
import '../../../state/saved_server_state.dart';
import '../../../state/settings/panel_save_registry.dart';
import '../../../state/time_sync_state.dart';
import '../../../state/settings/custom_horizon_state.dart';
import '../../../state/settings/site_settings_state.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';
import '../../../widgets/settings/time_sync_section.dart';

/// §37.12 Site preferences — editable. Phase 12h.6e added the daemon
/// round-trip — values hydrate from the active server on mount and
/// persist back on Save.
class SafetySitePanel extends ConsumerStatefulWidget {
  const SafetySitePanel({super.key});

  @override
  ConsumerState<SafetySitePanel> createState() => _SafetySitePanelState();
}

class _SafetySitePanelState extends ConsumerState<SafetySitePanel>
    with PanelSaveRegistration {
  String? _lastError;
  bool _gpsBusy = false;
  String? _gpsStatus;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _hydrate());
  }

  Future<void> _hydrate() async {
    final api = _api();
    if (api == null) return;
    try {
      await ref.read(siteSettingsProvider.notifier).hydrateFromServer(api);
      await ref.read(customHorizonProvider.notifier).hydrateFromServer(api);
    } catch (e) {
      if (mounted) {
        setState(() => _lastError = 'Could not load saved values: $e');
      }
    }
  }

  @override
  Future<void> panelSave() => _save();

  Future<void> _save() async {
    setState(() => _lastError = null);
    final api = _api();
    final messenger = ScaffoldMessenger.of(context);
    if (api == null) {
      setState(
          () => _lastError = 'No active server — connect to a daemon first.');
      messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
      return;
    }
    // Two sequential PUTs — track which committed so a partial failure
    // reports honestly ("site saved; skyline failed") instead of implying
    // the whole Save was rolled back (review r2).
    var siteSaved = false;
    try {
      await ref.read(siteSettingsProvider.notifier).persistToServer(api);
      siteSaved = true;
      await ref.read(customHorizonProvider.notifier).persistToServer(api);
      if (!mounted) return;
      messenger.showSnackBar(
        const SnackBar(content: Text('Site preferences saved to daemon.')),
      );
    } catch (e) {
      if (!mounted) return;
      setState(
        () => _lastError = siteSaved
            ? 'Site preferences saved, but the horizon skyline failed: $e'
            : 'Save failed: $e',
      );
      messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
    }
  }

  /// Pull the server's last GPS fix (§31.3 time-sync state) into the site
  /// fields — same source as the wizard's "Fill from GPS" (a USB dongle on
  /// the server machine). Values still go through Save to persist.
  Future<void> _fillFromGps() async {
    final api = ref.read(timeSyncApiProvider);
    if (api == null) {
      setState(() => _gpsStatus =
          'Not connected to a server — GPS fixes come from the dongle on the '
          'server machine.');
      return;
    }
    setState(() {
      _gpsBusy = true;
      _gpsStatus = null;
    });
    try {
      final state = await api.getState();
      final loc = state.location;
      if (!mounted) return;
      if (loc == null) {
        setState(() => _gpsStatus =
            'No GPS fix yet. Plug a USB GPS dongle into the computer running '
            'Ara Server and give it a minute or two under open sky, then try '
            'again.');
        return;
      }
      final n = ref.read(siteSettingsProvider.notifier);
      // 2-decimal (~1 km) site precision — matches the wizard's GPS fill.
      n.setLatitudeDeg(_round2(loc.lat));
      n.setLongitudeDeg(_round2(loc.lng));
      final alt = loc.alt;
      if (alt != null) n.setElevationM(alt);
      // GPS transmits UTC + position, never a timezone — derive the IANA
      // zone from the coordinates (offline polygon lookup).
      n.setTimeZone(tz_map.latLngToTimezoneString(loc.lat, loc.lng));
      setState(() => _gpsStatus =
          'Filled from the server\'s GPS fix (source: ${state.source}). '
          'Press Save to persist.');
    } catch (_) {
      if (!mounted) return;
      setState(() =>
          _gpsStatus = 'Couldn\'t read the server\'s GPS state — try again.');
    } finally {
      if (mounted) setState(() => _gpsBusy = false);
    }
  }

  static double _round2(double v) => (v * 100).roundToDouble() / 100;

  ProfileApi? _api() {
    final server = ref.read(activeServerProvider);
    return server == null ? null : ProfileApi(server);
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(siteSettingsProvider);
    final n = ref.read(siteSettingsProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Location'),
        Padding(
          padding: const EdgeInsets.only(bottom: 8),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              OutlinedButton.icon(
                key: const ValueKey('site_fill_from_gps'),
                onPressed: _gpsBusy ? null : _fillFromGps,
                icon: _gpsBusy
                    ? const SizedBox(
                        width: 16,
                        height: 16,
                        child: CircularProgressIndicator(strokeWidth: 2))
                    : const Icon(Icons.gps_fixed, size: 18),
                label: const Text('Fill from GPS'),
              ),
              if (_gpsStatus != null)
                Padding(
                  padding: const EdgeInsets.only(top: 4),
                  child: Text(
                    _gpsStatus!,
                    style: Theme.of(context).textTheme.bodySmall,
                  ),
                ),
            ],
          ),
        ),
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
        const SettingsSectionHeader('Time sync'),
        const TimeSyncSection(),
        const SettingsSectionHeader('Horizon'),
        SettingsSwitchRow(
          label: 'Use custom horizon polygon (§36.8)',
          helpKey: 'safety.site.use_custom_horizon',
          value: s.useCustomHorizon,
          onChanged: n.setUseCustomHorizon,
        ),
        if (s.useCustomHorizon)
          const _CustomHorizonEditor()
        else
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
          label: 'SQM sky brightness (0 = use Bortle)',
          helpKey: 'safety.site.sqm_mag_per_arcsec2',
          currentValue: s.sqmMagPerArcsec2.toString(),
          getCanonical: () =>
              ref.read(siteSettingsProvider).sqmMagPerArcsec2.toString(),
          parse: (str) {
            final v = double.tryParse(str);
            if (v != null) n.setSqmMagPerArcsec2(v);
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
        EditableNumberRow(
          label: 'Soft warning altitude (°)',
          helpKey: 'safety.site.soft_warning_altitude_deg',
          currentValue: s.softWarningAltitudeDeg.toString(),
          getCanonical: () =>
              ref.read(siteSettingsProvider).softWarningAltitudeDeg.toString(),
          parse: (str) {
            final v = double.tryParse(str);
            if (v != null) n.setSoftWarningAltitudeDeg(v);
          },
        ),
        // "0 = no limit" lives in the row's help entry — the label must fit the
        // panel's fixed label column.
        EditableNumberRow(
          label: 'Max sequence runtime (min)',
          helpKey: 'safety.site.max_sequence_runtime_min',
          currentValue: s.maxSequenceRuntimeMin.toString(),
          getCanonical: () =>
              ref.read(siteSettingsProvider).maxSequenceRuntimeMin.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setMaxSequenceRuntimeMin(v);
          },
        ),
        const SizedBox(height: 24),
        if (_lastError != null) ...[
          Text(
            _lastError!,
            style: TextStyle(color: Theme.of(context).colorScheme.error),
          ),
          const SizedBox(height: 12),
        ],
        // Save lives in the settings-shell header (PanelSaveRegistration) —
        // fixed chrome, always visible, no scrolling to find it.
      ],
    );
  }
}

/// §36 skyline editor — the azimuth/altitude vertices behind "use custom
/// horizon". Kept deliberately simple (a row per vertex + Add): the daemon
/// interpolates between vertices and canonicalizes on Save, so a handful of
/// measured points (one per obstruction) is the intended workflow.
class _CustomHorizonEditor extends ConsumerWidget {
  const _CustomHorizonEditor();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final points = ref.watch(customHorizonProvider);
    final n = ref.read(customHorizonProvider.notifier);
    final theme = Theme.of(context);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Text(
            points.isEmpty
                ? 'No skyline entered yet — visibility falls back to the flat '
                      'default altitude until at least one vertex is added. Enter '
                      'the sky altitude of your obstructions per compass bearing; '
                      'the daemon interpolates between vertices (wrapping north).'
                : 'Skyline vertices (azimuth 0-360°, altitude -10..90°). Values '
                      'are interpolated between vertices and saved with the panel.',
            style: theme.textTheme.bodySmall,
          ),
        ),
        for (var i = 0; i < points.length; i++)
          Row(
            key: ValueKey('horizon_point_$i'),
            children: [
              Expanded(
                child: EditableNumberRow(
                  label: 'Azimuth (°)',
                  currentValue: points[i].azimuthDeg.toString(),
                  getCanonical: () =>
                      ref.read(customHorizonProvider)[i].azimuthDeg.toString(),
                  parse: (str) {
                    final v = double.tryParse(str);
                    if (v != null) n.updateAt(i, azimuthDeg: v);
                  },
                ),
              ),
              Expanded(
                child: EditableNumberRow(
                  label: 'Altitude (°)',
                  currentValue: points[i].altitudeDeg.toString(),
                  getCanonical: () =>
                      ref.read(customHorizonProvider)[i].altitudeDeg.toString(),
                  parse: (str) {
                    final v = double.tryParse(str);
                    if (v != null) n.updateAt(i, altitudeDeg: v);
                  },
                ),
              ),
              IconButton(
                key: ValueKey('remove_horizon_point_$i'),
                icon: const Icon(Icons.delete_outline, size: 18),
                tooltip: 'Remove vertex',
                onPressed: () => n.removeAt(i),
              ),
            ],
          ),
        Align(
          alignment: Alignment.centerLeft,
          child: TextButton.icon(
            key: const ValueKey('add_horizon_point'),
            onPressed: () => n.addPoint(0, 20),
            icon: const Icon(Icons.add, size: 16),
            label: const Text('Add vertex'),
          ),
        ),
      ],
    );
  }
}
