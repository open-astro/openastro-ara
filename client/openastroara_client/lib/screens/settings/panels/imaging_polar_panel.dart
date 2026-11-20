import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../l10n/l10n.dart';
import '../../../models/polar_align.dart';
import '../../../state/polar_align/polar_align_routine_state.dart';
import '../../../state/polar_align/polar_align_state.dart'
    show polarAlignApiProvider;
import '../../../state/settings/loaded_section.dart';
import '../../../util/friendly_error.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/section_load_state.dart';
import '../../../widgets/settings/settings_row.dart';

/// #1736 Imaging → Polar align: the routine's knobs (profile section
/// `polar_align`). Each edit saves at once through
/// [polarAlignSettingsProvider]. Nothing shows, and nothing saves, until the
/// rig's copy is loaded (ADR 0015 rule 1); a value outside the registry's
/// range snaps back unsaved, and the label names the range.
class ImagingPolarPanel extends ConsumerWidget {
  const ImagingPolarPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final loaded = ref.watch(polarAlignSettingsProvider);
    final s = loaded.value;
    // No rig: the provider's defaults are not the rig's values.
    if (s == null || ref.watch(polarAlignApiProvider) == null) {
      return SectionNotLoaded(
        section: LoadedSection(lastFailure: loaded.error),
        onRetry: () => ref.invalidate(polarAlignSettingsProvider),
      );
    }
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
      required String helpKey,
      required PolarKnobRange range,
      String? hint,
    }) => EditableNumberRow(
      label: label,
      helpKey: helpKey,
      hint: hint,
      currentValue: polarKnobText(current),
      getCanonical: () =>
          polarKnobText(read(ref.read(polarAlignSettingsProvider).value ?? s)),
      parse: (text) {
        final v = double.tryParse(text);
        final now = ref.read(polarAlignSettingsProvider).value ?? s;
        if (v != null && v >= range.min && v <= range.max && v != read(now)) {
          save(apply(v));
        }
      },
    );

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        SettingsSectionHeader(l10n.polarKnobSection),
        number(
          l10n.polarKnobTolerance,
          s.targetToleranceArcmin,
          (x) => x.targetToleranceArcmin,
          (v) => s.copyWith(targetToleranceArcmin: v),
          helpKey: 'img.polar.target_tolerance_arcmin',
          range: (min: 0.1, max: 10),
          hint: l10n.polarKnobToleranceHint,
        ),
        // #1971 a profile setting: shown with or without a Polar Aligner connected.
        SettingsSwitchRow(
          label: l10n.polarKnobAlignerAuto,
          helpKey: 'img.polar.polar_aligner_auto',
          value: s.polarAlignerAuto,
          onChanged: (v) => save(s.copyWith(polarAlignerAuto: v)),
          hint: s.polarAlignerAuto ? l10n.polarKnobAlignerAutoOn : l10n.polarKnobAlignerAutoOff,
        ),
        // #2048 NINA's StopTrackingWhenDone: on by default.
        SettingsSwitchRow(
          label: l10n.polarKnobStopTracking,
          helpKey: 'img.polar.stop_tracking_when_done',
          value: s.stopTrackingWhenDone,
          onChanged: (v) => save(s.copyWith(stopTrackingWhenDone: v)),
          hint: s.stopTrackingWhenDone ? l10n.polarKnobStopTrackingOn : l10n.polarKnobStopTrackingOff,
        ),
        number(
          l10n.polarKnobTurn,
          s.seedRotationDeg,
          (x) => x.seedRotationDeg,
          (v) => s.copyWith(seedRotationDeg: v),
          helpKey: 'img.polar.seed_rotation_deg',
          range: polarSeedRotationRange,
          hint: l10n.polarKnobTurnHint,
        ),
        number(
          l10n.polarKnobVerifyStep,
          s.checkStepDeg,
          (x) => x.checkStepDeg,
          (v) => s.copyWith(checkStepDeg: v),
          helpKey: 'img.polar.check_step_deg',
          range: polarCheckStepRange,
          hint: l10n.polarKnobVerifyStepHint,
        ),
        number(
          l10n.polarKnobRoughExposure,
          s.coarseExposureSeconds,
          (x) => x.coarseExposureSeconds,
          (v) => s.copyWith(coarseExposureSeconds: v),
          helpKey: 'img.polar.coarse_exposure_seconds',
          range: polarCoarseExposureRange,
        ),
      ],
    );
  }
}
