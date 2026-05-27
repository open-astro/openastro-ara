import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/wizard_state.dart';
import '../../theme/ara_colors.dart';
import 'wizard_screens.dart';

/// §37 wizard host. Renders progress bar + current screen + nav bar.
/// Launched from "Add a Profile" / "Run Wizard Again" or first-run when no
/// profile exists. Calls `onComplete` with the final ProfileDraft when the
/// user hits Save Profile on Screen 18 (or Save & Exit at any point).
class WizardShell extends ConsumerWidget {
  final void Function(ProfileDraftSnapshot snapshot)? onComplete;
  const WizardShell({super.key, this.onComplete});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final state = ref.watch(wizardControllerProvider);
    final controller = ref.read(wizardControllerProvider.notifier);
    final info = ProfileWizard.steps[state.step]!;
    final body = wizardScreenBuilders[state.step]?.call(context) ??
        Center(child: Text('Missing screen builder for step ${state.step}'));

    return Scaffold(
      appBar: AppBar(
        title: Text('Set up profile · Step ${state.step} of ${ProfileWizard.totalSteps}'),
        actions: [
          TextButton.icon(
            onPressed: () => _saveAndExit(context, controller),
            icon: const Icon(Icons.logout, size: 18),
            label: const Text('Save & Exit'),
          ),
          const SizedBox(width: 8),
        ],
      ),
      body: Column(
        children: [
          LinearProgressIndicator(
            value: state.step / ProfileWizard.totalSteps,
            backgroundColor: AraColors.bgPanel,
            valueColor: const AlwaysStoppedAnimation(AraColors.accentInfo),
          ),
          Expanded(child: body),
          _BottomNavBar(
            currentStep: state.step,
            stageLabel: info.stageLabel,
            onBack: state.step > 1 ? controller.back : null,
            onSkip: controller.skipCurrent,
            onNext: state.step < ProfileWizard.totalSteps
                ? controller.next
                : () => _saveAndExit(context, controller),
            isLast: state.step == ProfileWizard.totalSteps,
          ),
        ],
      ),
    );
  }

  void _saveAndExit(BuildContext context, WizardController controller) {
    final draft = controller.snapshot();
    final cb = onComplete;
    if (cb != null) {
      cb(ProfileDraftSnapshot(draft));
    } else {
      // No persistence callback wired yet (current AppShell launcher path) —
      // still dismiss the wizard route so Save & Exit + Save Profile don't
      // look broken. Profile persistence to ~/.config/openastroara/profiles/
      // lands with the first per-screen form follow-up PR (§30.4).
      Navigator.of(context).pop();
    }
  }
}

/// Opaque snapshot of a ProfileDraft. The caller can read the draft fields
/// for serialization but shouldn't mutate it after Save & Exit — the
/// underlying controller will keep state and a subsequent "Run Wizard Again"
/// call gets a fresh draft anyway via the auto-dispose provider.
class ProfileDraftSnapshot {
  final dynamic draft;
  const ProfileDraftSnapshot(this.draft);
}

class _BottomNavBar extends StatelessWidget {
  final int currentStep;
  final String stageLabel;
  final VoidCallback? onBack;
  final VoidCallback onSkip;
  final VoidCallback onNext;
  final bool isLast;

  const _BottomNavBar({
    required this.currentStep,
    required this.stageLabel,
    required this.onBack,
    required this.onSkip,
    required this.onNext,
    required this.isLast,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      decoration: const BoxDecoration(
        color: AraColors.bgPanel,
        border: Border(top: BorderSide(color: AraColors.border)),
      ),
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
      child: Row(
        children: [
          TextButton.icon(
            onPressed: onBack,
            icon: const Icon(Icons.chevron_left),
            label: const Text('Back'),
          ),
          const SizedBox(width: 8),
          TextButton(
            onPressed: onSkip,
            child: const Text('Skip — use defaults'),
          ),
          const Spacer(),
          Text(stageLabel,
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                    color: AraColors.textSecondary,
                  )),
          const SizedBox(width: 16),
          FilledButton.icon(
            onPressed: onNext,
            icon: Icon(isLast ? Icons.check : Icons.chevron_right),
            label: Text(isLast ? 'Save Profile' : 'Next'),
          ),
        ],
      ),
    );
  }
}
