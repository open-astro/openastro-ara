import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/profile_draft.dart';
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

  @override
  void initState() {
    super.initState();
    _draft = ref.read(wizardControllerProvider).draft;
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
                  'gear, and make sure they\'re reachable on the same local '
                  'network as the server — the wizard discovers equipment '
                  'live in the next steps.',
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
          hint: 'e.g. Backyard Texas',
          onChanged: (v) => _draft.profileName = v.trim().isEmpty ? null : v.trim(),
        ),
        WizardTextField(
          label: 'Site name',
          initialValue: _draft.siteName,
          hint: 'e.g. Bortle 4 site',
          onChanged: (v) => _draft.siteName = v.trim().isEmpty ? null : v.trim(),
        ),
        const WizardSectionHeader('Location (optional)'),
        WizardTextField(
          label: 'Latitude (°)',
          initialValue: _draft.latitudeDeg?.toString(),
          keyboardType: const TextInputType.numberWithOptions(
              signed: true, decimal: true),
          inputFormatters: WizardInput.signedDecimal,
          helperText: 'North positive. Enter manually for now — device-GPS '
              'auto-fill lands in a later build.',
          onChanged: (v) => _setDouble(v, (d) => _draft.latitudeDeg = d),
        ),
        WizardTextField(
          label: 'Longitude (°)',
          initialValue: _draft.longitudeDeg?.toString(),
          keyboardType: const TextInputType.numberWithOptions(
              signed: true, decimal: true),
          inputFormatters: WizardInput.signedDecimal,
          helperText: 'East positive.',
          onChanged: (v) => _setDouble(v, (d) => _draft.longitudeDeg = d),
        ),
        WizardTextField(
          label: 'Altitude (m)',
          initialValue: _draft.altitudeMeters?.toString(),
          keyboardType:
              const TextInputType.numberWithOptions(signed: true, decimal: true),
          inputFormatters: WizardInput.signedDecimal,
          onChanged: (v) => _setDouble(v, (d) => _draft.altitudeMeters = d),
        ),
        WizardTextField(
          label: 'Timezone',
          initialValue: _draft.timezone,
          hint: 'e.g. America/Chicago',
          onChanged: (v) => _draft.timezone = v.trim().isEmpty ? null : v.trim(),
        ),
      ],
    );
  }
}
