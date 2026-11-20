import 'package:flutter/material.dart';

import '../../../util/friendly_error.dart';
import '../../../l10n/l10n.dart';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../services/profile_api.dart';
import '../../../state/imaging/exposure_state.dart' show FrameKind;
import '../../../state/profile_management_state.dart';
import '../../../state/settings/imaging_defaults_state.dart';
import '../../../services/planning_prefs_service.dart';
import '../../../state/planning/planning_prefs_state.dart';
import '../../../state/planning/sky_model_calibration_state.dart';
import '../../../state/settings/filter_set_state.dart';
import '../../../state/settings/panel_save_registry.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/section_load_state.dart';
import '../../../widgets/settings/section_panel.dart';
import '../../../widgets/settings/settings_row.dart';
import '../../../theme/ara_colors.dart';

/// §37.9 Imaging Defaults panel. Phase 12h.3b registered all 8 fields in
/// `settings/registry.dart` and wired `helpKey`s on the non-obvious controls;
/// Phase 12h.6b adds the daemon round-trip — values hydrate from the active
/// server on mount and persist back on Save.
class ImagingDefaultsPanel extends ConsumerStatefulWidget {
  const ImagingDefaultsPanel({super.key});

  @override
  ConsumerState<ImagingDefaultsPanel> createState() =>
      _ImagingDefaultsPanelState();
}

class _ImagingDefaultsPanelState extends ConsumerState<ImagingDefaultsPanel>
    with PanelSaveRegistration, SectionPanel {
  String? _lastError;
  bool _refitting = false;

  @override
  List<PanelSection> get sections => [imagingDefaultsProvider];

  @override
  Future<void> panelSave() async {
    setState(() => _lastError = null);
    // A field still being edited commits on focus loss; drop focus first so
    // the typed value is what gets sent.
    FocusManager.instance.primaryFocus?.unfocus();
    final error = await saveSections();
    if (mounted) setState(() => _lastError = error);
  }

  ProfileApi? _api() => ref.read(profileApiProvider);

  @override
  Widget build(BuildContext context) {
    // The planning and sky-model rows below are not the profile section: they
    // show whether or not it loaded.
    final gate = sectionGate();
    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        if (gate != null)
          SectionNotLoaded(
            section: ref.watch(imagingDefaultsProvider),
            padding: const EdgeInsets.symmetric(vertical: 12),
            onRetry: loadSections,
          )
        else
          ..._sectionRows(),
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
        // #1140 — the Glover noise-tolerance knob (client-local: planning math
        // lives in the client; this is not a profile section).
        const SettingsSectionHeader('Planning'),
        EditableNumberRow(
          label: 'Optimal-sub noise tolerance (%)',
          helpKey: 'img.defaults.noise_tolerance_pct',
          currentValue: _fmtPct(
            ref.watch(planningPrefsProvider).noiseTolerancePct,
          ),
          getCanonical: () =>
              _fmtPct(ref.read(planningPrefsProvider).noiseTolerancePct),
          parse: (s) {
            final v = double.tryParse(s.replaceAll('%', '').trim());
            if (v == null) return;
            ref.read(planningPrefsProvider.notifier).setNoiseTolerancePct(v);
          },
        ),
        Padding(
          padding: const EdgeInsets.only(top: 4, left: 4),
          child: Text(
            'Lower = longer subs for the same sky (fewer files, more lost per ruined frame). '
            '${PlanningPrefs.defaultNoiseTolerancePct.toStringAsFixed(0)} % is the classic figure; '
            '${PlanningPrefs.minNoiseTolerancePct.toStringAsFixed(0)}–${PlanningPrefs.maxNoiseTolerancePct.toStringAsFixed(0)} %. '
            'Applies to Tonight\'s Sky, the plan dialog and the sequence editor\'s advisor.',
            style: Theme.of(context).textTheme.labelSmall
                ?.copyWith(color: AraColors.textSecondary),
          ),
        ),
        // #1176 — optional Tonight's Sky magnitude floor (off by default).
        SettingsSwitchRow(
          label: 'Tonight\'s Sky magnitude floor',
          value: ref.watch(planningPrefsProvider).magnitudeFloor != null,
          onChanged: (on) => ref
              .read(planningPrefsProvider.notifier)
              .setMagnitudeFloor(
                on ? PlanningPrefs.defaultMagnitudeFloor : null,
              ),
        ),
        if (ref.watch(planningPrefsProvider).magnitudeFloor != null)
          EditableNumberRow(
            label: 'Tonight\'s Sky magnitude floor (mag)',
            currentValue: ref
                .watch(planningPrefsProvider)
                .magnitudeFloor!
                .toStringAsFixed(1),
            getCanonical: () => ref
                .read(planningPrefsProvider)
                .magnitudeFloor!
                .toStringAsFixed(1),
            parse: (s) {
              final v = double.tryParse(s.trim());
              if (v == null) return;
              ref.read(planningPrefsProvider.notifier).setMagnitudeFloor(v);
            },
          ),
        Padding(
          padding: const EdgeInsets.only(top: 4, left: 4),
          child: Text(
            'Hides Tonight\'s Sky targets fainter than this magnitude '
            '(${PlanningPrefs.minMagnitudeFloor.toStringAsFixed(0)}–${PlanningPrefs.maxMagnitudeFloor.toStringAsFixed(0)}). '
            'Targets with no catalogued magnitude stay listed. Off by default.',
            style: Theme.of(context).textTheme.labelSmall
                ?.copyWith(color: AraColors.textSecondary),
          ),
        ),
        // #1622 — mosaic size the Tonight's Sky framing rank assumes.
        Padding(
          padding: const EdgeInsets.only(top: 12, left: 4, bottom: 4),
          child: Text(
            context.l10n.imagingMosaicHelp(
              PlanningPrefs.minMosaicTiles,
              PlanningPrefs.maxMosaicTiles,
            ),
            style: Theme.of(context).textTheme.labelSmall
                ?.copyWith(color: AraColors.textSecondary),
          ),
        ),
        for (final across in [true, false])
          EditableNumberRow(
            label: across
                ? context.l10n.imagingMosaicTilesAcross
                : context.l10n.imagingMosaicTilesDown,
            currentValue:
                '${across ? ref.watch(planningPrefsProvider).mosaicTilesX : ref.watch(planningPrefsProvider).mosaicTilesY}',
            getCanonical: () =>
                '${across ? ref.read(planningPrefsProvider).mosaicTilesX : ref.read(planningPrefsProvider).mosaicTilesY}',
            parse: (s) {
              final v = int.tryParse(s.trim());
              if (v == null) return;
              final p = ref.read(planningPrefsProvider);
              ref
                  .read(planningPrefsProvider.notifier)
                  .setMosaicTiles(
                    across ? v : p.mosaicTilesX,
                    across ? p.mosaicTilesY : v,
                  );
            },
          ),
        // #1140 P2 — the sky-model calibration store: k per filter kind, fitted
        // by the daemon from the library's frame backgrounds.
        const SizedBox(height: 16),
        const SettingsSectionHeader('Sky model calibration'),
        ..._skyModelRows(),
        const SizedBox(height: 8),
        Align(
          alignment: Alignment.centerLeft,
          child: OutlinedButton.icon(
            onPressed: _api() != null && !_refitting
                ? _recalibrateSkyModel
                : null,
            icon: const Icon(Icons.refresh, size: 18),
            label: const Text('Recalibrate from recent frames'),
          ),
        ),
      ],
    );
  }

  /// The profile section's rows, on the rig's copy (see [sectionGate]).
  List<Widget> _sectionRows() {
    final d = ref.watch(imagingDefaultsProvider).value!;
    final n = ref.read(imagingDefaultsProvider.notifier);
    return [
      ?sectionConflict(),
      EditableNumberRow(
        label: 'Default exposure (s)',
        helpKey: 'imaging.defaults.exposure',
        currentValue: d.defaultExposure.inSeconds.toString(),
        getCanonical: () => ref
            .read(imagingDefaultsProvider)
            .value!
            .defaultExposure
            .inSeconds
            .toString(),
        parse: (s) {
          final i = int.tryParse(s);
          if (i == null) return;
          n.setExposure(Duration(seconds: i));
        },
      ),
      EditableNumberRow(
        label: 'Default gain',
        helpKey: 'imaging.defaults.gain',
        currentValue: d.defaultGain.toString(),
        getCanonical: () =>
            ref.read(imagingDefaultsProvider).value?.defaultGain.toString() ?? '',
        parse: (s) {
          final i = int.tryParse(s);
          if (i != null) n.setGain(i);
        },
      ),
      EditableNumberRow(
        label: 'Default offset',
        helpKey: 'imaging.defaults.offset',
        currentValue: d.defaultOffset.toString(),
        getCanonical: () =>
            ref.read(imagingDefaultsProvider).value?.defaultOffset.toString() ?? '',
        parse: (s) {
          final i = int.tryParse(s);
          if (i != null) n.setOffset(i);
        },
      ),
      EditableNumberRow(
        label: 'Default bin',
        helpKey: 'imaging.defaults.bin',
        currentValue: d.defaultBin.toString(),
        getCanonical: () =>
            ref.read(imagingDefaultsProvider).value?.defaultBin.toString() ?? '',
        parse: (s) {
          final i = int.tryParse(s);
          if (i != null) n.setBin(i);
        },
      ),
      SettingsDropdownRow<FrameKind>(
        label: 'Default frame type',
        helpKey: 'imaging.defaults.frame_type',
        value: d.defaultFrameKind,
        items: const {
          FrameKind.light: 'Light',
          FrameKind.dark: 'Dark',
          FrameKind.bias: 'Bias',
          FrameKind.flat: 'Flat',
        },
        onChanged: (v) {
          if (v != null) n.setFrameKind(v);
        },
      ),
      EditableNumberRow(
        label: 'Cooling target temperature (°C)',
        helpKey: 'imaging.defaults.cooling_target',
        currentValue: d.coolerTargetC.toString(),
        getCanonical: () =>
            ref.read(imagingDefaultsProvider).value?.coolerTargetC.toString() ?? '',
        parse: (s) {
          final v = double.tryParse(s);
          if (v != null) n.setCoolerTargetC(v);
        },
      ),
      EditableNumberRow(
        label: 'Cooler ramp rate (°C/min)',
        helpKey: 'imaging.defaults.cooler_ramp_c_per_min',
        currentValue: d.coolerRampRatePerMin.toString(),
        getCanonical: () =>
            ref.read(imagingDefaultsProvider).value?.coolerRampRatePerMin.toString() ?? '',
        parse: (s) {
          final v = double.tryParse(s);
          if (v != null) n.setCoolerRampRate(v);
        },
      ),
      SettingsSwitchRow(
        label: 'Warm-up cooler at session end',
        helpKey: 'imaging.defaults.warmup_at_session_end',
        value: d.warmupAtSessionEnd,
        onChanged: n.setWarmupAtSessionEnd,
      ),
    ];
  }

  List<Widget> _skyModelRows() {
    final record = ref.watch(skyModelCalibrationProvider);
    final value = record.asData?.value;
    if (_api() == null) {
      return const [
        SettingsRow(
          label: 'Calibration',
          value: 'Connect to the rig to read or refit the calibration.',
          helpKey: 'img.defaults.sky_model_calibration',
        ),
      ];
    }
    if (record.isLoading) {
      return const [
        SettingsRow(
          label: 'Calibration',
          value: 'Reading…',
          helpKey: 'img.defaults.sky_model_calibration',
        ),
      ];
    }
    if (value == null) {
      return const [
        SettingsRow(
          label: 'Calibration',
          value:
              'Not fitted yet — the planner uses the spec-sheet sky model. Recalibrate reads your '
              'library\'s recent light frames (their sky background against exposure, per filter) and '
              'fits one factor per filter kind.',
          helpKey: 'img.defaults.sky_model_calibration',
        ),
      ];
    }
    final when = value.fittedUtc.toLocal();
    String two(int n) => n.toString().padLeft(2, '0');
    return [
      SettingsRow(
        label: 'Fitted',
        value:
            '${when.year}-${two(when.month)}-${two(when.day)} ${two(when.hour)}:${two(when.minute)} · '
            '${value.framesConsidered} frames considered',
        helpKey: 'img.defaults.sky_model_calibration',
      ),
      if (value.filters.isEmpty)
        const SettingsRow(
          label: 'Filters',
          value: 'No light frame with a measured background in the last 90 days — image a night, then recalibrate.',
        ),
      for (final f in value.filters)
        SettingsRow(
          label:
              '${FilterKind.fromWire(f.kind).label}${f.bin > 1 ? ' · bin ${f.bin}' : ''}',
          value: f.standing,
        ),
    ];
  }

  Future<void> _recalibrateSkyModel() async {
    final messenger = ScaffoldMessenger.of(context);
    final l10n = context.l10n;
    setState(() => _refitting = true);
    try {
      final record = await ref
          .read(skyModelCalibrationProvider.notifier)
          .recalibrate();
      final applied = record?.appliedK.length ?? 0;
      messenger.showSnackBar(
        SnackBar(
          content: Text(
            applied == 0
                ? 'Refitted: no filter passed the gate yet — see the notes per filter.'
                : 'Refitted: $applied filter kind${applied == 1 ? '' : 's'} calibrated.',
          ),
        ),
      );
    } catch (e) {
      messenger.showSnackBar(
        SnackBar(
          content: Text(friendlyError(l10n, e, action: l10n.actionRefitSkyModel)),
        ),
      );
    } finally {
      if (mounted) setState(() => _refitting = false);
    }
  }
}

String _fmtPct(double v) =>
    v == v.roundToDouble() ? v.toStringAsFixed(0) : v.toStringAsFixed(1);
