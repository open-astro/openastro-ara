import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lat_lng_to_timezone/lat_lng_to_timezone.dart' as tz_map;

import '../../../services/profile_api.dart';
import '../../../state/saved_server_state.dart';
import '../../../state/settings/panel_save_registry.dart';
import '../../../state/settings/custom_horizon_state.dart';
import '../../../state/settings/site_settings_state.dart';
import '../../../util/friendly_error.dart';
import '../../../util/gps_site_fill.dart';
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
  // Bumped after a GPS fill so the Lat/Long/Elevation/Time-zone rows remount
  // with the fetched values — a provider-driven rebuild alone leaves the
  // seeded controllers stale until the panel is reopened.
  int _gpsFill = 0;

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
        setState(
          () =>
              _lastError = friendlyError(e, action: 'load your saved settings'),
        );
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
        () => _lastError = 'Not connected — connect to your rig to save this.',
      );
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
      messenger.showSnackBar(const SnackBar(content: Text('Saved.')));
    } catch (e) {
      if (!mounted) return;
      setState(
        () => _lastError = siteSaved
            ? 'Site preferences saved, but the horizon skyline was not. '
                '${friendlyError(e, action: 'save the horizon skyline')}'
            : friendlyError(e, action: 'save that'),
      );
      messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
    }
  }

  /// Fill the observing site from GPS (§31.3). Preferred: a USB GPS dongle on
  /// the server machine; fallback: this Mac's own location (internet + fresh
  /// fix). Shared logic in [fillSiteFromGps]. Values still go through Save.
  Future<void> _fillFromGps() async {
    if (_gpsBusy) return;
    setState(() {
      _gpsBusy = true;
      _gpsStatus = null;
    });
    try {
      final result = await fillSiteFromGps(ref);
      if (result.success) {
        _applyFix(
          result.lat,
          result.lng,
          result.alt,
          'Filled from ${result.sourceLabel}. Press Save to persist.',
        );
      } else if (mounted) {
        setState(() => _gpsStatus = result.message);
      }
    } finally {
      if (mounted) setState(() => _gpsBusy = false);
    }
  }

  /// Writes a fix into the site fields (2-dp ~1 km precision) and derives the
  /// IANA timezone from the coordinates, then reports [message].
  void _applyFix(double lat, double lng, double? alt, String message) {
    final n = ref.read(siteSettingsProvider.notifier);
    n.setLatitudeDeg(_round2(lat));
    n.setLongitudeDeg(_round2(lng));
    if (alt != null) n.setElevationM(alt);
    // GPS transmits UTC + position, never a timezone — derive the IANA zone
    // from the coordinates (offline polygon lookup).
    n.setTimeZone(tz_map.latLngToTimezoneString(lat, lng));
    _tzUserEdited = false; // a fresh fix re-arms coordinate derivation
    if (mounted) {
      setState(() {
        _gpsStatus = message;
        _gpsFill++; // remount the location rows so the fetched values show.
      });
    }
  }

  static double _round2(double v) => (v * 100).roundToDouble() / 100;

  /// True once the user edits the time-zone row themselves this session —
  /// coordinate edits then stop overwriting their choice (a remote rig
  /// deliberately viewed in another zone). A GPS fill re-arms derivation.
  bool _tzUserEdited = false;

  /// Same rule as the wizard's profile-basics screen: the zone is knowable
  /// from the coordinates (offline lat/lng → IANA lookup), so a committed
  /// lat/lng edit refreshes it. Values still go through Save to persist.
  void _deriveTimezoneFromCoords() {
    if (_tzUserEdited) return;
    final s = ref.read(siteSettingsProvider);
    if (s.latitudeDeg < -90 || s.latitudeDeg > 90) return;
    final zone = tz_map.latLngToTimezoneString(s.latitudeDeg, s.longitudeDeg);
    if (zone != s.timeZone) {
      ref.read(siteSettingsProvider.notifier).setTimeZone(zone);
    }
  }

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
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
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
          helpKey: 'safety.site.site_name',
          currentValue: s.siteName,
          getCanonical: () => ref.read(siteSettingsProvider).siteName,
          parse: n.setSiteName,
        ),
        EditableNumberRow(
          label: 'Latitude (°)',
          helpKey: 'safety.site.latitude',
          key: ValueKey('site-gps-lat-$_gpsFill'),
          currentValue: s.latitudeDeg.toString(),
          getCanonical: () =>
              ref.read(siteSettingsProvider).latitudeDeg.toString(),
          parse: (str) {
            final v = double.tryParse(str);
            if (v != null) {
              n.setLatitudeDeg(v);
              _deriveTimezoneFromCoords();
            }
          },
        ),
        EditableNumberRow(
          label: 'Longitude (°)',
          helpKey: 'safety.site.longitude',
          key: ValueKey('site-gps-lng-$_gpsFill'),
          currentValue: s.longitudeDeg.toString(),
          getCanonical: () =>
              ref.read(siteSettingsProvider).longitudeDeg.toString(),
          parse: (str) {
            final v = double.tryParse(str);
            if (v != null) {
              n.setLongitudeDeg(v);
              _deriveTimezoneFromCoords();
            }
          },
        ),
        EditableNumberRow(
          label: 'Elevation (m)',
          helpKey: 'safety.site.elevation',
          key: ValueKey('site-gps-alt-$_gpsFill'),
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
          helpKey: 'safety.site.time_zone',
          key: ValueKey('site-gps-tz-$_gpsFill'),
          currentValue: s.timeZone,
          getCanonical: () => ref.read(siteSettingsProvider).timeZone,
          parse: (v) {
            _tzUserEdited = true; // pin: lat/lng edits stop overwriting it
            n.setTimeZone(v);
          },
          hint:
              'IANA name (e.g. America/Los_Angeles) — auto-filled from '
              'the coordinates',
        ),
        const SettingsSectionHeader('Time sync'),
        const TimeSyncSection(),
        const SettingsSectionHeader('Horizon'),
        SettingsSwitchRow(
          label: 'Use my measured horizon',
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
                      'Ara draws a smooth line between the points you enter.'
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
                  helpKey: 'safety.site.horizon_azimuth',
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
                  helpKey: 'safety.site.horizon_altitude',
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
