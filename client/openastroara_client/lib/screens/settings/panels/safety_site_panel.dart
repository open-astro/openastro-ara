import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lat_lng_to_timezone/lat_lng_to_timezone.dart' as tz_map;

import '../../../state/settings/panel_save_registry.dart';
import '../../../state/settings/custom_horizon_state.dart';
import '../../../state/settings/loaded_section.dart';
import '../../../state/settings/site_settings_state.dart';
import '../../../state/simple_mode_state.dart';
import '../../../theme/ara_metrics.dart';
import '../../../util/friendly_error.dart';
import '../../../l10n/l10n.dart';
import '../../../util/gps_site_fill.dart';
import '../../../util/optimal_sub.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/section_load_state.dart';
import '../../../widgets/settings/section_panel.dart';
import '../../../widgets/settings/settings_row.dart';
import '../../../widgets/settings/time_sync_section.dart';
import '../../../widgets/adaptive_controls.dart';

/// §37.12 Site preferences — editable. The fields show the rig's copy of the
/// `site` section (ADR 0015: loaded before it is saved; not loaded, they are
/// not shown) and go back on Save with the skyline.
class SafetySitePanel extends ConsumerStatefulWidget {
  const SafetySitePanel({super.key});

  @override
  ConsumerState<SafetySitePanel> createState() => _SafetySitePanelState();
}

class _SafetySitePanelState extends ConsumerState<SafetySitePanel>
    with PanelSaveRegistration, SectionPanel {
  String? _lastError;
  bool _gpsBusy = false;
  String? _gpsStatus;
  // Bumped after a GPS fill so the Lat/Long/Elevation/Time-zone rows remount
  // with the fetched values — a provider-driven rebuild alone leaves the
  // seeded controllers stale until the panel is reopened.
  int _gpsFill = 0;

  // The skyline and the site load and save through the section module, in
  // that order: one Save, two PUTs.
  @override
  List<PanelSection> get sections => [
    customHorizonProvider,
    siteSettingsProvider,
  ];

  @override
  Future<void> panelSave() async {
    setState(() => _lastError = null);
    // A field still being edited commits on focus loss; drop focus first so
    // the typed value is what gets sent.
    FocusManager.instance.primaryFocus?.unfocus();
    // A site failure says the skyline was kept instead of implying the whole
    // Save was rolled back (review r2).
    final error = await saveSections(
      partlySaved:
          'The horizon skyline was saved, but the site preferences were not. ',
    );
    if (mounted) setState(() => _lastError = error);
  }

  /// Fill the observing site from GPS (§31.3). Preferred: a USB GPS dongle on
  /// this computer; then the rig's held position; then this machine's own
  /// location. Shared
  /// logic in [fillSiteFromGps]. Values still go through Save.
  Future<void> _fillFromGps() async {
    if (_gpsBusy) return;
    // A lookup can take up to ~20 s — snapshot the site so a fetched fix can't
    // silently overwrite lat/long/elevation/timezone the user typed meanwhile.
    final before = ref.read(siteSettingsProvider).value;
    if (before == null) return; // the fields are not shown: nothing to fill
    setState(() {
      _gpsBusy = true;
      _gpsStatus = null;
    });
    try {
      final result = await fillSiteFromGps(ref);
      // The device-location fallback can run for ~20s (permission prompt +
      // getCurrentPosition timeout); the panel may be long gone by now, and
      // _applyFix reads a provider off `ref`.
      if (!mounted) return;
      if (result.success) {
        if (!_siteUnchangedSince(before)) {
          if (mounted) {
            setState(() => _gpsStatus =
                'A location was found, but you edited the site fields while it '
                'was being looked up — not overwritten. Press Fill from GPS '
                'again to apply it.');
          }
          return;
        }
        _applyFix(
          result.lat,
          result.lng,
          result.alt,
          'Filled from ${result.sourceLabel}. Press Save to persist.',
        );
      } else {
        setState(() => _gpsStatus = result.message);
      }
    } finally {
      if (mounted) setState(() => _gpsBusy = false);
    }
  }

  /// True when the site's GPS-relevant fields still match [before] — i.e. the
  /// user didn't edit them while the location fetch was in flight.
  bool _siteUnchangedSince(SiteSettings before) {
    final s = ref.read(siteSettingsProvider).value;
    return s != null &&
        s.latitudeDeg == before.latitudeDeg &&
        s.longitudeDeg == before.longitudeDeg &&
        s.elevationM == before.elevationM &&
        s.timeZone == before.timeZone;
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
    final s = ref.read(siteSettingsProvider).value;
    if (s == null) return;
    if (s.latitudeDeg < -90 || s.latitudeDeg > 90) return;
    final zone = tz_map.latLngToTimezoneString(s.latitudeDeg, s.longitudeDeg);
    if (zone != s.timeZone) {
      ref.read(siteSettingsProvider.notifier).setTimeZone(zone);
    }
  }

  @override
  Widget build(BuildContext context) {
    reloadWhenReset();
    // Not loaded from the rig: the site's rows are not shown (no default), the
    // permanent pier and time sync, which are not this panel's Save, still are.
    final section = ref.watch(siteSettingsProvider);
    final s = section.canSave ? section.value : null;
    final n = ref.read(siteSettingsProvider.notifier);
    // A field's canonical text only; a reset mid-edit reads the plain
    // values, and no save can send them (the section is not loaded).
    SiteSettings now() =>
        ref.read(siteSettingsProvider).value ?? const SiteSettings();

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        // The skyline's banner shows here, also with the editor hidden.
        ?sectionConflict(),
        const SettingsSectionHeader('Location'),
        if (s == null)
          SectionNotLoaded(
            section: section,
            padding: const EdgeInsets.symmetric(vertical: 12),
            onRetry: loadSections,
          )
        else ...[
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
                        child: AdaptiveProgressIndicator(strokeWidth: 2),
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
          getCanonical: () => now().siteName,
          parse: n.setSiteName,
        ),
        EditableNumberRow(
          label: 'Latitude (°)',
          helpKey: 'safety.site.latitude',
          key: ValueKey('site-gps-lat-$_gpsFill'),
          currentValue: s.latitudeDeg.toString(),
          getCanonical: () =>
              now().latitudeDeg.toString(),
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
              now().longitudeDeg.toString(),
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
              now().elevationM.toString(),
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
          getCanonical: () => now().timeZone,
          parse: (v) {
            _tzUserEdited = true; // pin: lat/lng edits stop overwriting it
            n.setTimeZone(v);
          },
          hint:
              'IANA name (e.g. America/Los_Angeles) — auto-filled from '
              'the coordinates',
        ),
        ],
        // Same provider as Simple Mode's switch (#1565): it writes the profile's
        // site.permanent_pier at once, not through this panel's Save.
        SettingsSwitchRow(
          label: context.l10n.shellPermanentPier,
          helpKey: 'safety.site.permanent_pier',
          hint: context.l10n.shellPermanentPierHint,
          value: ref.watch(simplePermanentPierProvider).value ?? false,
          onChanged: (v) async {
            final messenger = ScaffoldMessenger.of(context);
            final l10n = context.l10n;
            try {
              await ref.read(simplePermanentPierProvider.notifier).set(v);
            } catch (e) {
              // A refused switch-off: the switch shows the rig's value again.
              messenger.showSnackBar(
                SnackBar(
                  content: Text(
                    e is SectionNotLoadedException ||
                            e is SectionConflictException
                        ? e.toString()
                        : friendlyError(l10n, e, action: l10n.actionSave),
                  ),
                ),
              );
            }
          },
        ),
        const SettingsSectionHeader('Time sync'),
        const TimeSyncSection(),
        if (s != null) ...[
        const SettingsSectionHeader('Horizon'),
        SettingsSwitchRow(
          label: 'Use my measured horizon',
          helpKey: 'safety.site.use_custom_horizon',
          value: s.useCustomHorizon,
          onChanged: n.setUseCustomHorizon,
        ),
        if (s.useCustomHorizon)
          _CustomHorizonEditor(onRetry: loadSections)
        else
          EditableNumberRow(
            label: 'Default horizon altitude (°)',
            helpKey: 'safety.site.default_horizon_altitude_deg',
            currentValue: s.defaultHorizonAltitudeDeg.toString(),
            getCanonical: () => now().defaultHorizonAltitudeDeg
                .toString(),
            parse: (str) {
              final v = double.tryParse(str);
              if (v != null) n.setDefaultHorizonAltitudeDeg(v);
            },
          ),
        const SettingsSectionHeader('Conditions defaults'),
        _SkyBrightnessRows(
          sky: SkyState(
            bortleClass: s.bortleClass,
            sqmMagPerArcsec2: s.sqmMagPerArcsec2,
          ),
          sqmText: s.sqmMagPerArcsec2.toString(),
          bortleText: s.bortleClass.toString(),
        ),
        EditableNumberRow(
          label: 'Typical seeing (″)',
          helpKey: 'safety.site.typical_seeing_arcsec',
          currentValue: s.typicalSeeingArcsec.toString(),
          getCanonical: () =>
              now().typicalSeeingArcsec.toString(),
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
              now().softWarningAltitudeDeg.toString(),
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
              now().maxSequenceRuntimeMin.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setMaxSequenceRuntimeMin(v);
          },
        ),
        ],
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
  const _CustomHorizonEditor({required this.onRetry});

  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final section = ref.watch(customHorizonProvider);
    if (!section.canSave) {
      return SectionNotLoaded(section: section, onRetry: onRetry);
    }
    final points = section.value!;
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
                  getCanonical: () => ref
                      .read(customHorizonProvider)
                      .value![i]
                      .azimuthDeg
                      .toString(),
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
                  getCanonical: () => ref
                      .read(customHorizonProvider)
                      .value![i]
                      .altitudeDeg
                      .toString(),
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

/// One sky brightness input (#1529): a Bortle / SQM selector and the one
/// value field of the mode in use. Switching writes the other mode's value
/// from the sky now in effect, and the notifier clears what the mode drops.
class _SkyBrightnessRows extends ConsumerWidget {
  const _SkyBrightnessRows({
    required this.sky,
    required this.sqmText,
    required this.bortleText,
  });

  final SkyState sky;
  final String sqmText;
  final String bortleText;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final n = ref.read(siteSettingsProvider.notifier);
    final l10n = context.l10n;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: SegmentedButton<bool>(
            key: const Key('site-sky-mode'),
            showSelectedIcon: false,
            segments: [
              ButtonSegment(
                value: false,
                label: Text(l10n.wizardBasicsSkyBortle),
              ),
              ButtonSegment(value: true, label: Text(l10n.wizardBasicsSkySqm)),
            ],
            selected: {sky.isSqm},
            onSelectionChanged: (sel) => sel.first
                ? n.setSkySqm(sky.skyMag)
                : n.setSkyBortle(sky.bortle),
          ),
        ),
        if (sky.isSqm) ...[
          EditableNumberRow(
            key: const ValueKey('site-sqm'),
            label: 'SQM sky brightness (16 to 22.5)',
            helpKey: 'safety.site.sqm_mag_per_arcsec2',
            currentValue: sqmText,
            getCanonical: () =>
                ref.read(siteSettingsProvider).value?.sqmMagPerArcsec2.toString() ?? '',
            parse: (str) {
              final v = double.tryParse(str);
              if (v != null) n.setSkySqm(v);
            },
          ),
          // Its own line, not the field's hint: a hint hides once the field
          // holds a value. Follows the saved reading, so it is never stale.
          Padding(
            padding: const EdgeInsets.only(bottom: AraSpace.s8),
            child: Text(
              l10n.sitePanelDerivedBortle(sky.bortle),
              key: const Key('site-sqm-derived-bortle'),
              style: AraText.caption,
            ),
          ),
        ] else
          EditableNumberRow(
            key: const ValueKey('site-bortle'),
            label: 'Bortle class (1..9)',
            helpKey: 'safety.site.bortle_class',
            currentValue: bortleText,
            getCanonical: () =>
                ref.read(siteSettingsProvider).value?.bortleClass.toString() ?? '',
            parse: (str) {
              final v = int.tryParse(str);
              if (v != null) n.setSkyBortle(v);
            },
          ),
      ],
    );
  }
}
