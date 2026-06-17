import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/profile_api.dart';
import '../../state/saved_server_state.dart';
import '../../state/wizard_state.dart';
import '../../theme/ara_colors.dart';
import 'wizard_save.dart';
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
            onPressed: () => _saveAndExit(context, ref, controller),
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
                : () => _saveAndExit(context, ref, controller),
            isLast: state.step == ProfileWizard.totalSteps,
          ),
        ],
      ),
    );
  }

  // Persist the draft as a new profile (§37 Save / Save & Exit — both paths save
  // partial-or-complete state per §37.8), then exit the wizard. Shows a blocking
  // spinner during the round-trip and keeps the wizard open on failure so the
  // user doesn't lose their entries.
  Future<void> _saveAndExit(
      BuildContext context, WidgetRef ref, WizardController controller) async {
    final draft = controller.snapshot();
    final server = ref.read(activeServerProvider);
    if (server == null) {
      _showError(context, 'No active server — connect to a daemon before saving the profile.');
      return; // keep the wizard open; nothing to save against
    }
    final api = ProfileApi(server);

    showDialog<void>(
      context: context,
      barrierDismissible: false,
      builder: (_) => const Center(child: CircularProgressIndicator()),
    );

    String? error;
    try {
      await saveWizardProfile(api, draft);
    } on DioException catch (e) {
      error = 'Couldn\'t save the profile: ${e.message ?? 'network error'} '
          '(${e.response?.statusCode ?? 'no response'}).';
    } catch (e) {
      error = 'Couldn\'t save the profile: $e';
    }

    if (!context.mounted) return;
    Navigator.of(context).pop(); // close the spinner

    if (error != null) {
      // Still mounted here — the `if (!context.mounted) return` above guarantees it and
      // there's no await in between, so no extra guard is needed.
      _showError(context, error);
      return; // keep the wizard open so the user can retry
    }

    onComplete?.call(ProfileDraftSnapshot(draft));
    if (context.mounted) Navigator.of(context).pop(); // exit the wizard
  }

  void _showError(BuildContext context, String message) {
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text(message), backgroundColor: AraColors.accentError),
    );
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
