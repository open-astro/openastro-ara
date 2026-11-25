import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../l10n/l10n.dart';
import '../../../models/polar_align.dart';
import '../../../state/polar_align/polar_align_routine_state.dart';
import '../../../state/settings/loaded_section.dart';
import '../../../state/settings/polar_align_settings_state.dart';
import '../../../util/friendly_error.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/section_panel.dart';
import '../../../widgets/settings/settings_row.dart';
import '../../../theme/ara_colors.dart';

/// #1736 Imaging → Polar align: the routine's knobs (profile section
/// `polar_align`). Each edit saves at once through
/// [polarAlignSettingsProvider]. Nothing shows, and nothing saves, until the
/// rig's copy is loaded (ADR 0015 rule 1); a value outside the registry's
/// range snaps back unsaved, and the label names the range.
class ImagingPolarPanel extends ConsumerStatefulWidget {
  const ImagingPolarPanel({super.key});

  @override
  ConsumerState<ImagingPolarPanel> createState() => _ImagingPolarPanelState();
}

class _ImagingPolarPanelState extends ConsumerState<ImagingPolarPanel>
    with SectionPanel {
  static const _auto = 'auto';
  static const _east = 'east';
  static const _west = 'west';

  static String _eastDirectionKey(bool? east) => switch (east) {
    null => _auto,
    true => _east,
    false => _west,
  };

  @override
  List<PanelSection> get sections => [polarAlignSettingsProvider];

  @override
  Widget build(BuildContext context) {
    final gate = sectionGate();
    if (gate != null) return gate;
    final s = ref.watch(polarAlignSettingsProvider).value!;
    final messenger = ScaffoldMessenger.of(context);
    final l10n = context.l10n;
    // On the rig's copy as it is when the edit lands (ADR 0015).
    Future<void> save(PolarAlignSettings Function(PolarAlignSettings) change) async {
      try {
        await ref.read(polarAlignSettingsProvider.notifier).saveKnob(change);
      } catch (e) {
        messenger.showSnackBar(
          SnackBar(
            content: Text(
              e is SectionNotLoadedException || e is SectionConflictException
                  ? e.toString()
                  : friendlyError(l10n, e, action: l10n.polarActionSave),
            ),
          ),
        );
      }
    }

    Widget number(
      String label,
      double current,
      double Function(PolarAlignSettings) read,
      PolarAlignSettings Function(PolarAlignSettings, double) apply, {
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
          save((x) => apply(x, v));
        }
      },
    );

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        ?sectionConflict(),
        SettingsSectionHeader(l10n.polarKnobSection),
        number(
          l10n.polarKnobTolerance,
          s.targetToleranceArcmin,
          (x) => x.targetToleranceArcmin,
          (x, v) => x.copyWith(targetToleranceArcmin: v),
          helpKey: 'img.polar.target_tolerance_arcmin',
          range: (min: 0.1, max: 10),
          hint: l10n.polarKnobToleranceHint,
        ),
        // #1971 a profile setting: shown with or without a Polar Aligner connected.
        SettingsSwitchRow(
          label: l10n.polarKnobAlignerAuto,
          helpKey: 'img.polar.polar_aligner_auto',
          value: s.polarAlignerAuto,
          onChanged: (v) => save((x) => x.copyWith(polarAlignerAuto: v)),
          hint: s.polarAlignerAuto ? l10n.polarKnobAlignerAutoOn : l10n.polarKnobAlignerAutoOff,
        ),
        // #2048 NINA's StopTrackingWhenDone: on by default.
        SettingsSwitchRow(
          label: l10n.polarKnobStopTracking,
          helpKey: 'img.polar.stop_tracking_when_done',
          value: s.stopTrackingWhenDone,
          onChanged: (v) => save((x) => x.copyWith(stopTrackingWhenDone: v)),
          hint: s.stopTrackingWhenDone ? l10n.polarKnobStopTrackingOn : l10n.polarKnobStopTrackingOff,
        ),
        number(
          l10n.polarKnobTurn,
          s.seedRotationDeg,
          (x) => x.seedRotationDeg,
          (x, v) => x.copyWith(seedRotationDeg: v),
          helpKey: 'img.polar.seed_rotation_deg',
          range: polarSeedRotationRange,
          hint: l10n.polarKnobTurnHint,
        ),
        number(
          l10n.polarKnobVerifyStep,
          s.checkStepDeg,
          (x) => x.checkStepDeg,
          (x, v) => x.copyWith(checkStepDeg: v),
          helpKey: 'img.polar.check_step_deg',
          range: polarCheckStepRange,
          hint: l10n.polarKnobVerifyStepHint,
        ),
        number(
          l10n.polarKnobMainExposure,
          s.coarseExposureSeconds,
          (x) => x.coarseExposureSeconds,
          (x, v) => x.copyWith(coarseExposureSeconds: v),
          helpKey: 'img.polar.coarse_exposure_seconds',
          range: polarCoarseExposureRange,
        ),
        // #2082 the measurement's camera.
        SettingsDropdownRow<String>(
          key: const Key('polar-knob-camera'),
          label: l10n.polarKnobCamera,
          helpKey: 'img.polar.camera',
          value: s.camera,
          items: {
            PolarAlignCameras.guide: l10n.polarKnobCameraGuide,
            PolarAlignCameras.main: l10n.polarKnobCameraMain,
          },
          onChanged: (v) {
            final now = ref.read(polarAlignSettingsProvider).value ?? s;
            if (v != null && v != now.camera) save((x) => x.copyWith(camera: v));
          },
        ),
        // #2082 frames averaged at each point: a whole number, 1 to 5.
        EditableNumberRow(
          key: const Key('polar-knob-frames-per-point'),
          label: l10n.polarKnobFramesPerPoint,
          helpKey: 'img.polar.frames_per_point',
          hint: l10n.polarKnobFramesPerPointHint,
          currentValue: '${s.framesPerPoint}',
          getCanonical: () =>
              '${(ref.read(polarAlignSettingsProvider).value ?? s).framesPerPoint}',
          parse: (text) {
            final v = int.tryParse(text.trim());
            final now = ref.read(polarAlignSettingsProvider).value ?? s;
            if (v != null &&
                v >= polarFramesPerPointRange.min &&
                v <= polarFramesPerPointRange.max &&
                v != now.framesPerPoint) {
              save((x) => x.copyWith(framesPerPoint: v));
            }
          },
        ),
        // #2081 the way a run's turns go (NINA's East direction).
        SettingsDropdownRow<String>(
          key: const Key('polar-knob-east-direction'),
          label: l10n.polarKnobEastDirection,
          helpKey: 'img.polar.east_direction',
          value: _eastDirectionKey(s.eastDirection),
          items: {
            _auto: l10n.polarKnobEastDirectionAuto,
            _east: l10n.polarKnobEastDirectionEast,
            _west: l10n.polarKnobEastDirectionWest,
          },
          onChanged: (v) {
            final now = ref.read(polarAlignSettingsProvider).value ?? s;
            if (v == null || v == _eastDirectionKey(now.eastDirection)) return;
            final east = switch (v) {
              _east => true,
              _west => false,
              _ => null,
            };
            save((x) => x.withEastDirection(east));
          },
        ),
        Padding(
          padding: const EdgeInsets.only(bottom: 8),
          child: Text(
            l10n.polarKnobEastDirectionHint,
            style: Theme.of(context).textTheme.labelSmall?.copyWith(
              color: AraColors.textDisabled,
            ),
          ),
        ),
      ],
    );
  }
}
