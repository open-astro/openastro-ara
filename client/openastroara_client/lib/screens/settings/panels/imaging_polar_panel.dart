import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../l10n/l10n.dart';
import '../../../models/polar_align.dart';
import '../../../state/polar_align/polar_align_routine_state.dart';
import '../../../util/friendly_error.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// #1736 Imaging → Polar align: the routine's knobs (profile section
/// `polar_align`). Each edit saves at once through
/// [polarAlignSettingsProvider]; the server holds the turns in range.
class ImagingPolarPanel extends ConsumerWidget {
  const ImagingPolarPanel({super.key});

  static String _num(double v) =>
      v == v.roundToDouble() ? v.toStringAsFixed(0) : v.toStringAsFixed(2);

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s =
        ref.watch(polarAlignSettingsProvider).value ??
        const PolarAlignSettings();
    final messenger = ScaffoldMessenger.of(context);
    final l10n = context.l10n;
    Future<void> save(PolarAlignSettings next) async {
      try {
        await ref.read(polarAlignSettingsProvider.notifier).save(next);
      } catch (e) {
        messenger.showSnackBar(
          SnackBar(
            content: Text(friendlyError(l10n, e, action: l10n.polarActionSave)),
          ),
        );
      }
    }

    Widget number(
      String label,
      double current,
      double Function(PolarAlignSettings) read,
      PolarAlignSettings Function(double) apply, {
      String? hint,
    }) => EditableNumberRow(
      label: label,
      helpKey: 'img.polar.knobs',
      hint: hint,
      currentValue: _num(current),
      getCanonical: () =>
          _num(read(ref.read(polarAlignSettingsProvider).value ?? s)),
      parse: (text) {
        final v = double.tryParse(text);
        if (v != null && v > 0) save(apply(v));
      },
    );

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Polar align'),
        number(
          'Tolerance (arcmin)',
          s.targetToleranceArcmin,
          (x) => x.targetToleranceArcmin,
          (v) => s.copyWith(targetToleranceArcmin: v),
          hint: l10n.polarKnobToleranceHint,
        ),
        SettingsSwitchRow(
          label: l10n.polarKnobUseHome,
          helpKey: 'img.polar.knobs',
          value: s.useHome,
          onChanged: (v) => save(s.copyWith(useHome: v)),
          hint: s.useHome ? l10n.polarKnobUseHomeOn : l10n.polarKnobUseHomeOff,
        ),
        number(
          'Turn (°)',
          s.seedRotationDeg,
          (x) => x.seedRotationDeg,
          (v) => s.copyWith(seedRotationDeg: v),
          hint: l10n.polarKnobTurnHint,
        ),
        number(
          'Verify step (°)',
          s.checkStepDeg,
          (x) => x.checkStepDeg,
          (v) => s.copyWith(checkStepDeg: v),
          hint: l10n.polarKnobVerifyStepHint,
        ),
        number(
          'Rough step exposure (s)',
          s.coarseExposureSeconds,
          (x) => x.coarseExposureSeconds,
          (v) => s.copyWith(coarseExposureSeconds: v),
        ),
      ],
    );
  }
}
