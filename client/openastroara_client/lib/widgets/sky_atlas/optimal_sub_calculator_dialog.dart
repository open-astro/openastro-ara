import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/planning/planning_prefs_state.dart';
import '../../state/planning/sky_model_calibration_state.dart';
import '../../state/settings/camera_electronics_state.dart';
import '../../state/settings/filter_set_state.dart';
import '../../state/settings/optics_settings_state.dart';
import '../../state/settings/site_settings_state.dart';
import '../../theme/ara_colors.dart';
import '../../util/optimal_sub.dart';
import '../../util/optimal_sub_calculator.dart';
import '../help_icon.dart';
import '../settings/editable_field.dart';
import '../settings/settings_row.dart';

/// #1140 / NEXTGEN §5 — the standalone Optimal Sub calculator, seeded from the
/// profile (optics, camera electronics, site sky, the first filter's passband,
/// the noise-tolerance knob and the fitted sky-model k for that filter's kind).
Future<void> showOptimalSubCalculator(BuildContext context, WidgetRef ref) {
  final optics = ref.read(opticsSettingsProvider);
  final cam = ref.read(cameraElectronicsProvider);
  final site = ref.read(siteSettingsProvider);
  final filters = ref.read(filterSetProvider).filters;
  final k =
      ref.read(skyModelCalibrationProvider).asData?.value?.appliedK ??
      const <String, double>{};
  final kind = filters.isNotEmpty ? filters.first.kind : FilterKind.l;
  final bandwidth = filters.isNotEmpty && filters.first.bandwidthNm > 0
      ? filters.first.bandwidthNm
      : kind.defaultBandwidthNm;
  final seed = CalculatorInputs(
    readNoiseE: cam.readNoiseE > 0 ? cam.readNoiseE : defaultReadNoiseE,
    fullWellE: cam.fullWellE > 0 ? cam.fullWellE : defaultFullWellE,
    electronsPerAdu: cam.electronsPerAdu > 0 ? cam.electronsPerAdu : 0,
    pixelSizeUm: optics.pixelSizeUm,
    apertureMm: optics.apertureMm,
    focalLengthMm: optics.focalLengthMm,
    reducerFactor: optics.reducerFactor > 0 ? optics.reducerFactor : 1.0,
    quantumEfficiency: cam.quantumEfficiencyPeak > 0
        ? cam.quantumEfficiencyPeak
        : defaultQuantumEfficiency,
    skyMagPerArcsec2: effectiveSkyMag(
      bortleClass: site.bortleClass,
      sqmMagPerArcsec2: site.sqmMagPerArcsec2,
    ),
    filterBandwidthNm: bandwidth,
    noiseTolerancePct: ref.read(planningPrefsProvider).noiseTolerancePct,
    skyCalibrationK: k[kind.wire] ?? 1.0,
  );
  return showDialog<void>(
    context: context,
    builder: (_) => OptimalSubCalculatorDialog(
      initial: seed,
      kByKind: k,
      initialKind: kind,
    ),
  );
}

class OptimalSubCalculatorDialog extends StatefulWidget {
  final CalculatorInputs initial;
  final Map<String, double> kByKind;
  final FilterKind initialKind;
  const OptimalSubCalculatorDialog({
    super.key,
    required this.initial,
    this.kByKind = const {},
    this.initialKind = FilterKind.l,
  });

  @override
  State<OptimalSubCalculatorDialog> createState() =>
      _OptimalSubCalculatorDialogState();
}

class _OptimalSubCalculatorDialogState
    extends State<OptimalSubCalculatorDialog> {
  late CalculatorInputs _in = widget.initial;
  late FilterKind _kind = widget.initialKind;

  void _set(CalculatorInputs next) => setState(() => _in = next);

  // `read` is a getter, not a value: EditableTextRow asks for the canonical
  // text right after parsing, and the "done" action also drops focus, which
  // commits a second time — a stale captured value would reset the field and
  // then re-parse the old number (the dialog's first cut did exactly that).
  Widget _num(
    String label,
    double Function() read,
    void Function(double) apply, {
    String? hint,
    int digits = 2,
  }) {
    String fmt() =>
        read().toStringAsFixed(digits).replaceFirst(RegExp(r'\.?0+$'), '');
    return EditableNumberRow(
      label: label,
      currentValue: fmt(),
      getCanonical: fmt,
      hint: hint,
      parse: (s) {
        final v = double.tryParse(s.trim());
        if (v != null) apply(v);
      },
    );
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final dim = theme.textTheme.bodySmall?.copyWith(
      color: AraColors.textSecondary,
    );
    final r = calculate(_in);
    return AlertDialog(
      backgroundColor: AraColors.bgPanel,
      title: Row(
        children: [
          const Expanded(child: Text('Optimal Sub calculator')),
          const HelpIcon(helpKey: 'plan.optimal_sub_calculator'),
        ],
      ),
      content: SizedBox(
        width: 560,
        child: SingleChildScrollView(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: [
              _Results(result: r, inputs: _in),
              const SizedBox(height: 12),
              const SettingsSectionHeader('Filter and sky'),
              Row(
                children: [
                  const SizedBox(width: 4),
                  Text('Filter kind', style: theme.textTheme.bodyMedium),
                  const SizedBox(width: 12),
                  Expanded(
                    child: DropdownButton<FilterKind>(
                      value: _kind,
                      isDense: true,
                      isExpanded: true,
                      items: [
                        for (final k in FilterKind.values)
                          DropdownMenuItem(value: k, child: Text(k.label)),
                      ],
                      onChanged: (k) {
                        if (k == null) return;
                        setState(() {
                          _kind = k;
                          _in = _in.copyWith(
                            filterBandwidthNm: k.defaultBandwidthNm,
                            skyCalibrationK: widget.kByKind[k.wire] ?? 1.0,
                          );
                        });
                      },
                    ),
                  ),
                ],
              ),
              _num(
                'Filter passband (nm)',
                () => _in.filterBandwidthNm,
                (v) => _set(_in.copyWith(filterBandwidthNm: v)),
                digits: 1,
              ),
              _num(
                'Sky brightness (mag/arcsec²)',
                () => _in.skyMagPerArcsec2,
                (v) => _set(_in.copyWith(skyMagPerArcsec2: v)),
                hint: 'SQM reading; Bortle 4 ≈ 20.5, Bortle 6 ≈ 19.5',
                digits: 2,
              ),
              _num(
                'Sky model calibration k',
                () => _in.skyCalibrationK,
                (v) => _set(_in.copyWith(skyCalibrationK: v)),
                hint: '1 = spec-sheet model; the fitted figure for this kind when one passed',
                digits: 3,
              ),
              _num(
                'Noise tolerance (%)',
                () => _in.noiseTolerancePct,
                (v) => _set(_in.copyWith(noiseTolerancePct: v)),
                digits: 1,
              ),
              const SettingsSectionHeader('Optics'),
              _num(
                'Aperture (mm)',
                () => _in.apertureMm,
                (v) => _set(_in.copyWith(apertureMm: v)),
                digits: 1,
              ),
              _num(
                'Focal length (mm)',
                () => _in.focalLengthMm,
                (v) => _set(_in.copyWith(focalLengthMm: v)),
                digits: 1,
              ),
              _num(
                'Reducer factor',
                () => _in.reducerFactor,
                (v) => _set(_in.copyWith(reducerFactor: v)),
                digits: 3,
              ),
              _num(
                'Pixel size (µm)',
                () => _in.pixelSizeUm,
                (v) => _set(_in.copyWith(pixelSizeUm: v)),
                digits: 2,
              ),
              const SettingsSectionHeader('Camera'),
              _num(
                'Read noise (e⁻)',
                () => _in.readNoiseE,
                (v) => _set(_in.copyWith(readNoiseE: v)),
                digits: 2,
              ),
              _num(
                'Full well (e⁻)',
                () => _in.fullWellE,
                (v) => _set(_in.copyWith(fullWellE: v)),
                digits: 0,
              ),
              _num(
                'e⁻/ADU (0 = unknown)',
                () => _in.electronsPerAdu,
                (v) => _set(_in.copyWith(electronsPerAdu: v)),
                digits: 3,
              ),
              _num(
                'Quantum efficiency (0–1)',
                () => _in.quantumEfficiency,
                (v) => _set(_in.copyWith(quantumEfficiency: v)),
                digits: 2,
              ),
              const SettingsSectionHeader('Target (optional, for the hours)'),
              _num(
                'Surface brightness (mag/arcsec²)',
                () => _in.surfaceBrightnessMagArcsec2 ?? 0,
                (v) => _set(
                  v > 0
                      ? _in.copyWith(surfaceBrightnessMagArcsec2: v)
                      : _in.copyWith(clearSurfaceBrightness: true),
                ),
                hint: '0 = none. M42 core ≈ 17, a typical nebula ≈ 22, the IFN ≈ 27',
                digits: 1,
              ),
              _num(
                'Peak altitude (°)',
                () => _in.peakAltitudeDeg,
                (v) => _set(_in.copyWith(peakAltitudeDeg: v)),
                digits: 0,
              ),
              const SizedBox(height: 8),
              Text(
                'Seeded from your profile; edits here change nothing in it. '
                'Glover: t = (noise tolerance) · R² / P — the sub past which read noise stops mattering.',
                style: dim,
              ),
            ],
          ),
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Close'),
        ),
      ],
    );
  }
}

class _Results extends StatelessWidget {
  final CalculatorResult result;
  final CalculatorInputs inputs;
  const _Results({required this.result, required this.inputs});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    if (result.error case final e?) {
      return Text(
        e,
        style: theme.textTheme.bodyMedium?.copyWith(
          color: AraColors.accentError,
        ),
      );
    }
    final sub = result.sub!;
    final bound = switch (sub.limitingBound) {
      OptimalSubBound.readNoiseFloor => 'read-noise floor',
      OptimalSubBound.saturationCeiling => 'saturation ceiling',
      OptimalSubBound.starFloor => 'star-detectability floor',
    };
    final budget = result.budget;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          sub.viable
              ? 'Suggested exposure: ${formatSubSeconds(sub.recommendedSec)} per sub'
              : 'No viable window: longer subs just saturate (${formatSubSeconds(sub.ceilingSec)} ceiling)',
          style: theme.textTheme.titleMedium,
        ),
        const SizedBox(height: 6),
        SettingsRow(
          label: 'Glover floor',
          value: formatSubSeconds(sub.floorSec),
        ),
        SettingsRow(
          label: 'Saturation ceiling',
          value: formatSubSeconds(sub.ceilingSec),
        ),
        SettingsRow(label: 'Limited by', value: bound),
        SettingsRow(
          label: 'Sky flux',
          value:
              '${_flux(sub.skyFluxEPerSecPerPx)} e⁻/s/px'
              '${inputs.skyCalibrationK == 1.0 ? '' : ' (k ${inputs.skyCalibrationK.toStringAsFixed(2)} applied)'}',
        ),
        if (result.pixelScaleArcsec case final s?)
          SettingsRow(
            label: 'Pixel scale',
            value: '${s.toStringAsFixed(2)}"/px',
          ),
        if (budget != null) ...[
          const SizedBox(height: 6),
          Text(
            'Integration budget: ${budget.display}',
            style: theme.textTheme.bodyMedium,
          ),
          for (final l in budget.auditLines)
            SettingsRow(label: l.$1, value: l.$2),
        ],
      ],
    );
  }

  static String _flux(double v) =>
      v >= 10 ? v.toStringAsFixed(0) : v.toStringAsFixed(2);
}
