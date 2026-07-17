import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:lat_lng_to_timezone/lat_lng_to_timezone.dart' as tz_map;

import '../../../models/profile_draft.dart';
import '../../../state/time_sync_state.dart';
import '../../../state/wizard_state.dart';
import '../../../theme/ara_colors.dart';
import '../wizard_form_kit.dart';

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
  }

  /// Pull the server's last GPS fix (§31.3 time-sync state) into the location
  /// fields. The daemon's USB-GPS worker polls any dongle plugged into the
  /// server machine, so this works with no mount connected at all.
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
      setState(() {
        // 2-decimal (~1 km) site precision: enough for ephemerides/safety
        // limits without publishing the observer's exact backyard position.
        _draft.latitudeDeg = _round2(loc.lat);
        _draft.longitudeDeg = _round2(loc.lng);
        if (loc.alt != null) _draft.altitudeMeters = loc.alt;
        // GPS transmits UTC + position, never a timezone — derive the IANA
        // zone from the coordinates (worldwide polygon lookup, offline). The
        // NAME stays valid through DST-policy changes (the OS tz database
        // carries the rules); only boundary redraws would need a package bump.
        _draft.timezone = tz_map.latLngToTimezoneString(loc.lat, loc.lng);
        _gpsFill++; // remount the fields so the fill is visible
        _gpsStatus =
            'Filled from the server\'s GPS fix (source: ${state.source}).';
      });
    } catch (_) {
      if (!mounted) return;
      setState(() =>
          _gpsStatus = 'Couldn\'t read the server\'s GPS state — try again.');
    } finally {
      if (mounted) setState(() => _gpsBusy = false);
    }
  }

  static double _round2(double v) => (v * 100).roundToDouble() / 100;

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
      intro: 'Name this profile and (optionally) set your observing site. '
          'Everything here can be changed later in Settings.',
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
          child: const Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Icon(Icons.power_settings_new,
                  size: 20, color: AraColors.accentInfo),
              SizedBox(width: 10),
              Expanded(
                child: Text(
                  'Before you begin: power on your mount and the rest of your '
                  'gear, make sure they\'re reachable on the same local '
                  'network as the server, and that every device is already '
                  'configured in AlpacaBridge — the wizard discovers '
                  'equipment live in the next steps and can only find what '
                  'AlpacaBridge exposes.',
                  style: TextStyle(color: AraColors.textSecondary),
                ),
              ),
            ],
          ),
        ),
        WizardTextField(
          label: 'Profile name',
          required: true,
          initialValue: _draft.profileName,
          hint: 'e.g. Backyard New Mexico',
          onChanged: (v) => _draft.profileName = v.trim().isEmpty ? null : v.trim(),
        ),
        WizardTextField(
          label: 'Site name',
          initialValue: _draft.siteName,
          hint: 'e.g. New Mexico USA',
          onChanged: (v) => _draft.siteName = v.trim().isEmpty ? null : v.trim(),
        ),
        const WizardSectionHeader('Location (optional)'),
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
                        child: CircularProgressIndicator(strokeWidth: 2))
                    : const Icon(Icons.gps_fixed, size: 18),
                label: const Text('Fill from GPS'),
              ),
              const SizedBox(height: 4),
              Text(
                _gpsStatus ??
                    'Tip: plug a USB GPS dongle into the computer running '
                        'Ara Server — the server polls it automatically. No '
                        'mount GPS needed.',
                style:
                    const TextStyle(color: AraColors.textSecondary, fontSize: 12),
              ),
            ],
          ),
        ),
        WizardTextField(
          key: ValueKey('wiz-lat-$_gpsFill'),
          label: 'Latitude (°)',
          initialValue: _draft.latitudeDeg?.toString(),
          keyboardType: const TextInputType.numberWithOptions(
              signed: true, decimal: true),
          inputFormatters: WizardInput.signedDecimal,
          helperText: 'North positive.',
          onChanged: (v) => _setDouble(v, (d) => _draft.latitudeDeg = d),
        ),
        WizardTextField(
          key: ValueKey('wiz-lng-$_gpsFill'),
          label: 'Longitude (°)',
          initialValue: _draft.longitudeDeg?.toString(),
          keyboardType: const TextInputType.numberWithOptions(
              signed: true, decimal: true),
          inputFormatters: WizardInput.signedDecimal,
          helperText: 'East positive.',
          onChanged: (v) => _setDouble(v, (d) => _draft.longitudeDeg = d),
        ),
        WizardTextField(
          key: ValueKey('wiz-alt-$_gpsFill'),
          label: 'Altitude (m)',
          initialValue: _draft.altitudeMeters?.toString(),
          keyboardType:
              const TextInputType.numberWithOptions(signed: true, decimal: true),
          inputFormatters: WizardInput.signedDecimal,
          onChanged: (v) => _setDouble(v, (d) => _draft.altitudeMeters = d),
        ),
        WizardTextField(
          key: ValueKey('wiz-tz-$_gpsFill'),
          label: 'Timezone',
          initialValue: _draft.timezone,
          hint: 'e.g. America/Denver',
          onChanged: (v) => _draft.timezone = v.trim().isEmpty ? null : v.trim(),
        ),
      ],
    );
  }
}
