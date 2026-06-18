import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/profile_draft.dart';

/// State for the §37 setup wizard — current step (1-18) + the working
/// ProfileDraft + skipped flag bookkeeping. Per-screen forms in Phase 12b
/// follow-ups will read/mutate ProfileDraft through `ref.read`.
class WizardController extends Notifier<WizardState> {
  @override
  WizardState build() => WizardState(step: 1, draft: ProfileDraft());

  void next() {
    if (state.step < ProfileWizard.totalSteps) {
      state = state.copyWith(step: state.step + 1);
    }
  }

  void back() {
    if (state.step > 1) {
      state = state.copyWith(step: state.step - 1);
    }
  }

  void skipCurrent() {
    state.draft.skippedScreens.add(state.step);
    if (state.step < ProfileWizard.totalSteps) {
      next();
    } else {
      // On the last step, `next()` is a no-op, so explicitly emit a new
      // state to notify listeners that `skippedScreens` mutated and the
      // skipped-banner needs to render.
      state = state.copyWith();
    }
  }

  void jumpTo(int step) {
    // `clamp` returns `num`; copyWith expects `int`. Explicit toInt() so
    // static analysis is happy.
    final clamped = step.clamp(1, ProfileWizard.totalSteps).toInt();
    state = state.copyWith(step: clamped);
  }

  /// Save & Exit per §37 — the caller persists the partial draft to the
  /// profile store. The state stays put; the wizard host pops back to the
  /// previous screen.
  ///
  /// Returns the **live** draft object (not a copy): `saveWizardProfile` stamps
  /// `savedProfileId` onto it so a retry after a partial save re-uses the same
  /// profile. The name makes that contract explicit at the call site.
  ProfileDraft liveDraft() => state.draft;
}

class WizardState {
  final int step;
  final ProfileDraft draft;

  const WizardState({required this.step, required this.draft});

  WizardState copyWith({int? step, ProfileDraft? draft}) =>
      WizardState(step: step ?? this.step, draft: draft ?? this.draft);
}

final wizardControllerProvider =
    NotifierProvider.autoDispose<WizardController, WizardState>(
        WizardController.new);

/// Static wizard catalog so the shell can look up screen metadata + the
/// stage progress label without per-screen widgets being instantiated.
class ProfileWizard {
  static const int totalSteps = 18;

  /// 1-based step → (stage number, stage label, screen title)
  static const Map<int, WizardStepInfo> steps = <int, WizardStepInfo>{
    1: WizardStepInfo(stage: 1, stageLabel: 'Profile basics', title: 'Profile name + location'),
    2: WizardStepInfo(stage: 2, stageLabel: 'Equipment discovery', title: 'Connect to AlpacaBridge'),
    3: WizardStepInfo(stage: 2, stageLabel: 'Equipment discovery', title: 'Discover + assign equipment'),
    4: WizardStepInfo(stage: 3, stageLabel: 'Per-device setup', title: 'Telescope'),
    5: WizardStepInfo(stage: 3, stageLabel: 'Per-device setup', title: 'Camera'),
    6: WizardStepInfo(stage: 3, stageLabel: 'Per-device setup', title: 'Filter Wheel'),
    7: WizardStepInfo(stage: 3, stageLabel: 'Per-device setup', title: 'Focuser'),
    8: WizardStepInfo(stage: 3, stageLabel: 'Per-device setup', title: 'Mount'),
    9: WizardStepInfo(stage: 3, stageLabel: 'Per-device setup', title: 'Rotator'),
    10: WizardStepInfo(stage: 3, stageLabel: 'Per-device setup', title: 'Guider (PHD2)'),
    11: WizardStepInfo(stage: 4, stageLabel: 'Imaging tools', title: 'Plate solving (ASTAP)'),
    12: WizardStepInfo(stage: 4, stageLabel: 'Imaging tools', title: 'Autofocus'),
    13: WizardStepInfo(stage: 4, stageLabel: 'Imaging tools', title: 'File saving + naming'),
    14: WizardStepInfo(stage: 4, stageLabel: 'Imaging tools', title: 'Imaging defaults'),
    15: WizardStepInfo(stage: 5, stageLabel: 'Safety + site', title: 'Safety policies'),
    16: WizardStepInfo(stage: 5, stageLabel: 'Safety + site', title: 'Site preferences'),
    17: WizardStepInfo(stage: 6, stageLabel: 'Sky data downloads', title: 'Sky data downloads'),
    18: WizardStepInfo(stage: 7, stageLabel: 'Done', title: 'Review + Save'),
  };
}

class WizardStepInfo {
  final int stage;
  final String stageLabel;
  final String title;
  const WizardStepInfo({
    required this.stage,
    required this.stageLabel,
    required this.title,
  });
}
