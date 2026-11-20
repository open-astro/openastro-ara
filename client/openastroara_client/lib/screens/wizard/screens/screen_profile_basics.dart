import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lat_lng_to_timezone/lat_lng_to_timezone.dart' as tz_map;

import '../../../l10n/l10n.dart';
import '../../../models/profile_draft.dart';
import '../../../util/optimal_sub.dart';
import '../../../state/settings/site_settings_state.dart';
import '../../../state/wizard_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../util/gps_site_fill.dart';
import '../wizard_form_kit.dart';
import '../../../widgets/adaptive_controls.dart';

/// §37.1 Screen 1 — Profile name + location.
class ScreenProfileBasics extends ConsumerStatefulWidget {
  const ScreenProfileBasics({super.key});

  @override
  ConsumerState<ScreenProfileBasics> createState() =>
      _ScreenProfileBasicsState();
}

class _ScreenProfileBasicsState extends ConsumerState<ScreenProfileBasics> {
  late final ProfileDraft _draft;

  /// Bumped when a GPS fix fills the location fields: the text fields seed
  /// their controllers from initialValue on mount, so re-keying them is how a
  /// programmatic fill becomes visible.
  int _gpsFill = 0;
  bool _gpsBusy = false;
  String? _gpsStatus;

  @override
  void initState() {
    super.initState();
    _draft = ref.read(wizardControllerProvider).draft;
    _sqmMode = _sky().isSqm;
  }

  /// Fill the observing site from GPS (§31.3) — a USB GPS dongle on this
  /// computer preferred, then the position the rig already holds, then the
  /// client machine's own location. Shared logic in [fillSiteFromGps], so
  /// profile creation behaves like the Safety → Site panel. Works with no
  /// mount connected at all.
  Future<void> _fillFromGps() async {
    if (_gpsBusy) return;
    // A lookup can take up to ~20 s — snapshot the site so a fetched fix can't
    // silently overwrite lat/long/elevation/timezone the user typed meanwhile.
    final beforeLat = _draft.latitudeDeg;
    final beforeLng = _draft.longitudeDeg;
    final beforeAlt = _draft.altitudeMeters;
    final beforeTz = _draft.timezone;
    final l = context.l10n;
    setState(() {
      _gpsBusy = true;
      _gpsStatus = null;
    });
    try {
      final result = await fillSiteFromGps(ref);
      if (!mounted) return;
      if (result.success) {
        if (beforeLat != _draft.latitudeDeg ||
            beforeLng != _draft.longitudeDeg ||
            beforeAlt != _draft.altitudeMeters ||
            beforeTz != _draft.timezone) {
          setState(() => _gpsStatus = l.wizardBasicsGpsEdited);
          return;
        }
        setState(() {
          // 2-decimal (~1 km) site precision: enough for ephemerides/safety
          // limits without publishing the observer's exact backyard position.
          _draft.latitudeDeg = _round2(result.lat);
          _draft.longitudeDeg = _round2(result.lng);
          if (result.alt != null) _draft.altitudeMeters = result.alt;
          // GPS transmits UTC + position, never a timezone — derive the IANA
          // zone from the coordinates (worldwide polygon lookup, offline). The
          // NAME stays valid through DST-policy changes (the OS tz database
          // carries the rules); only boundary redraws would need a package bump.
          _draft.timezone = tz_map.latLngToTimezoneString(result.lat, result.lng);
          _tzUserEdited = false; // a fresh fix re-arms coordinate derivation
          _gpsFill++; // remount the fields so the fill is visible
          _gpsStatus = l.wizardBasicsGpsFilled(result.sourceLabel);
        });
      } else {
        setState(() => _gpsStatus = result.message);
      }
    } finally {
      if (mounted) setState(() => _gpsBusy = false);
    }
  }

  static double _round2(double v) => (v * 100).roundToDouble() / 100;

  /// Bumped when a coordinate edit re-derives the timezone, so ONLY the tz
  /// field remounts with the new value (remounting via _gpsFill would reset
  /// the lat/lng field mid-typing).
  int _tzFill = 0;

  String? _bortleError;
  String? _sqmError;

  /// The mode in use: taken from the draft once on mount, then only the
  /// selector changes it. An invalid entry nulls the draft, which must not
  /// flip the mode to the saved settings' one.
  late bool _sqmMode;

  /// The sky in effect: what the draft holds, else the saved site settings.
  SkyState _sky() {
    final saved = ref.read(siteSettingsProvider).value ?? const SiteSettings();
    return SkyState(
      bortleClass: _draft.site.bortleClass ?? saved.bortleClass,
      sqmMagPerArcsec2: _draft.site.sqmMagPerArcsec2 ?? saved.sqmMagPerArcsec2,
    );
  }

  /// One sky brightness input (#1529): the draft always carries the pair, so
  /// switching mode leaves no hidden value (Bortle mode sqm 0, SQM mode the
  /// class derived from the reading).
  void _setSkyMode(bool sqm) => setState(() {
    final sky = _sky();
    _sqmMode = sqm;
    _bortleError = null;
    _sqmError = null;
    if (sqm) {
      _draft.site.sqmMagPerArcsec2 = sky.skyMag;
      _draft.site.bortleClass = bortleFromSkyMag(sky.skyMag);
    } else {
      _draft.site.bortleClass = sky.bortle;
      _draft.site.sqmMagPerArcsec2 = 0;
    }
  });

  Widget _skyInput() {
    final sky = _sky();
    final isSqm = _sqmMode;
    final l10n = context.l10n;
    final modeKey = isSqm ? 'sqm' : 'bortle';
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Padding(
          padding: const EdgeInsets.only(bottom: 8),
          child: SegmentedButton<bool>(
            key: const ValueKey('wiz-sky-mode'),
            showSelectedIcon: false,
            segments: [
              ButtonSegment(
                value: false,
                label: Text(l10n.wizardBasicsSkyBortle),
              ),
              ButtonSegment(value: true, label: Text(l10n.wizardBasicsSkySqm)),
            ],
            selected: {isSqm},
            onSelectionChanged: (sel) => _setSkyMode(sel.first),
          ),
        ),
        if (isSqm)
          WizardTextField(
            key: ValueKey('wiz-sqm-$modeKey'),
            label: l10n.wizardBasicsSqm,
            initialValue: sky.skyMag.toString(),
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            inputFormatters: WizardInput.unsignedDecimal,
            errorText: _sqmError,
            helperText: l10n.wizardBasicsSqmHelper(sky.bortle),
            onChanged: (v) => setState(() {
              final n = double.tryParse(v.trim());
              _sqmError = n == null || !isSqmReading(n)
                  ? l10n.wizardBasicsSqmError
                  : null;
              // Blank or out-of-range is never saved (null keeps the base).
              _draft.site.sqmMagPerArcsec2 = _sqmError == null ? n : null;
              _draft.site.bortleClass = _sqmError == null
                  ? bortleFromSkyMag(n!)
                  : null;
            }),
          )
        else
          WizardTextField(
            key: ValueKey('wiz-bortle-$modeKey'),
            label: l10n.wizardBasicsBortle,
            initialValue: sky.bortle.toString(),
            keyboardType: TextInputType.number,
            inputFormatters: WizardInput.unsignedInt,
            errorText: _bortleError,
            helperText: l10n.wizardBasicsBortleHelper,
            onChanged: (v) => setState(() {
              final n = int.tryParse(v.trim());
              _bortleError = n == null || !SiteSettingsNotifier.isValidBortle(n)
                  ? l10n.wizardBasicsBortleError
                  : null;
              _draft.site.bortleClass = _bortleError == null ? n : null;
              _draft.site.sqmMagPerArcsec2 = _bortleError == null ? 0 : null;
            }),
          ),
      ],
    );
  }

  /// True once the user types in the timezone field themselves — manual
  /// coordinate edits then stop overwriting their choice (a remote-hosted rig
  /// deliberately shown in home time). A GPS fill re-arms the derivation.
  bool _tzUserEdited = false;

  /// #2 of the timezone plan: the zone is knowable from typed coordinates —
  /// same offline lat/lng → IANA lookup the GPS fill uses.
  void _deriveTimezoneFromCoords() {
    if (_tzUserEdited) return;
    final lat = _draft.latitudeDeg;
    final lng = _draft.longitudeDeg;
    if (lat == null || lng == null || lat < -90 || lat > 90) return;
    final zone = tz_map.latLngToTimezoneString(lat, lng);
    if (zone == _draft.timezone) return;
    setState(() {
      _draft.timezone = zone;
      _tzFill++;
    });
  }

  // Parse a possibly-empty/partial numeric field to a nullable double without
  // clobbering the stored value on transient input like "-" or "".
  void _setDouble(String raw, void Function(double?) assign) {
    final trimmed = raw.trim();
    if (trimmed.isEmpty) {
      assign(null);
      return;
    }
    final v = double.tryParse(trimmed);
    if (v != null) assign(v);
  }

  @override
  Widget build(BuildContext context) {
    return WizardScreenScaffold(
      step: 1,
      intro: context.l10n.wizardBasicsIntro,
      children: [
        // Equipment discovery (steps 2–3) probes the rig over Alpaca — gear
        // that's off or on another network simply won't show up, which reads
        // as "the wizard is broken" without this heads-up.
        Container(
          padding: const EdgeInsets.all(12),
          margin: const EdgeInsets.only(bottom: 16),
          decoration: BoxDecoration(
            color: AraColors.bgPanel,
            borderRadius: BorderRadius.circular(8),
            border: Border.all(color: AraColors.border),
          ),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const Icon(Icons.power_settings_new,
                  size: 20, color: AraColors.accentInfo),
              const SizedBox(width: 10),
              Expanded(
                child: Text(
                  context.l10n.wizardBasicsBefore,
                  style: const TextStyle(color: AraColors.textSecondary),
                ),
              ),
            ],
          ),
        ),
        WizardTextField(
          label: context.l10n.wizardBasicsProfileName,
          required: true,
          initialValue: _draft.profileName,
          hint: context.l10n.wizardBasicsProfileNameHint,
          onChanged: (v) => _draft.profileName = v.trim().isEmpty ? null : v.trim(),
        ),
        WizardTextField(
          label: context.l10n.wizardBasicsSiteName,
          initialValue: _draft.siteName,
          hint: context.l10n.wizardBasicsSiteNameHint,
          onChanged: (v) => _draft.siteName = v.trim().isEmpty ? null : v.trim(),
        ),
        WizardSectionHeader(context.l10n.wizardBasicsLocation),
        Padding(
          padding: const EdgeInsets.only(bottom: 12),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              OutlinedButton.icon(
                onPressed: _gpsBusy ? null : () => _fillFromGps(),
                icon: _gpsBusy
                    ? const SizedBox(
                        width: 16, height: 16,
                        child: AdaptiveProgressIndicator(strokeWidth: 2))
                    : const Icon(Icons.gps_fixed, size: 18),
                label: Text(context.l10n.wizardBasicsFillGps),
              ),
              const SizedBox(height: 4),
              Text(
                _gpsStatus ??
                    context.l10n.wizardBasicsGpsTip,
                style:
                    const TextStyle(color: AraColors.textSecondary, fontSize: 12),
              ),
            ],
          ),
        ),
        WizardTextField(
          key: ValueKey('wiz-lat-$_gpsFill'),
          label: context.l10n.wizardBasicsLatitude,
          initialValue: _draft.latitudeDeg?.toString(),
          keyboardType: const TextInputType.numberWithOptions(
              signed: true, decimal: true),
          inputFormatters: WizardInput.signedDecimal,
          helperText: context.l10n.wizardBasicsNorthPositive,
          onChanged: (v) => _setDouble(v, (d) {
            _draft.latitudeDeg = d;
            _deriveTimezoneFromCoords();
          }),
        ),
        WizardTextField(
          key: ValueKey('wiz-lng-$_gpsFill'),
          label: context.l10n.wizardBasicsLongitude,
          initialValue: _draft.longitudeDeg?.toString(),
          keyboardType: const TextInputType.numberWithOptions(
              signed: true, decimal: true),
          inputFormatters: WizardInput.signedDecimal,
          helperText: context.l10n.wizardBasicsEastPositive,
          onChanged: (v) => _setDouble(v, (d) {
            _draft.longitudeDeg = d;
            _deriveTimezoneFromCoords();
          }),
        ),
        WizardTextField(
          key: ValueKey('wiz-alt-$_gpsFill'),
          label: context.l10n.wizardBasicsAltitude,
          initialValue: _draft.altitudeMeters?.toString(),
          keyboardType:
              const TextInputType.numberWithOptions(signed: true, decimal: true),
          inputFormatters: WizardInput.signedDecimal,
          onChanged: (v) => _setDouble(v, (d) => _draft.altitudeMeters = d),
        ),
        _skyInput(),
        WizardTextField(
          key: ValueKey('wiz-tz-$_gpsFill-$_tzFill'),
          label: context.l10n.wizardBasicsTimezone,
          initialValue: _draft.timezone,
          hint: context.l10n.wizardBasicsTimezoneHint,
          helperText: context.l10n.wizardBasicsTimezoneHelper,
          onChanged: (v) {
            final t = v.trim();
            // Clearing the field re-arms derivation; typing a zone pins it.
            _tzUserEdited = t.isNotEmpty;
            _draft.timezone = t.isEmpty ? null : t;
          },
        ),
      ],
    );
  }
}
