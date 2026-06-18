import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../services/profile_api.dart';
import '../../state/saved_server_state.dart';
import '../../state/sky_atlas/data_manager_state.dart';
import '../../models/profile_draft.dart';
import '../../state/wizard_state.dart';
import '../../theme/ara_colors.dart';
import 'wizard_save.dart';
import 'wizard_screens.dart';

/// §37 wizard host. Renders progress bar + current screen + nav bar.
/// Launched from "Add a Profile" / "Run Wizard Again" or first-run when no
/// profile exists. Calls `onComplete` with the final ProfileDraft when the
/// user hits Save Profile on Screen 18 (or Save & Exit at any point).
class WizardShell extends ConsumerStatefulWidget {
  final void Function(ProfileDraftSnapshot snapshot)? onComplete;

  /// Builds the [ProfileApi] used to persist the profile. Defaults to the real
  /// `ProfileApi.new`; widget tests inject a fake so the Save flow (spinner,
  /// double-tap guard, navigation) can be exercised without a live daemon.
  final ProfileApi Function(AraServer server)? createApi;

  const WizardShell({super.key, this.onComplete, this.createApi});

  @override
  ConsumerState<WizardShell> createState() => _WizardShellState();
}

class _WizardShellState extends ConsumerState<WizardShell> {
  // Guards the brief async window between the Save tap and the blocking spinner
  // mounting: without it a rapid double-tap launches two concurrent saves that
  // race-write all four sections. Stays true until the save settles.
  bool _isSaving = false;

  @override
  Widget build(BuildContext context) {
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
            onPressed: () => _saveAndExit(controller),
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
                : () => _saveAndExit(controller),
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
  /// Queue the sky-data packages the user ticked on screen 17. Best-effort: each
  /// download is an independent 202-accepted request, and a failure here is
  /// non-fatal (the profile is already saved) — so per-id errors are logged, not
  /// thrown, and never block the wizard from finishing.
  Future<void> _queueSkyDataDownloads(ProfileDraft draft) async {
    if (draft.skyDataDownloadIds.isEmpty) return;
    if (!mounted) return; // widget disposed between save and queue — ref is dead
    final dm = ref.read(dataManagerApiProvider);
    if (dm == null) return; // no active server — nothing to queue against
    // Fire the 202-accepted queue requests concurrently; each is independent and
    // its failure is non-fatal (logged, not thrown), so one bad id never blocks
    // the others or the wizard from finishing.
    await Future.wait(draft.skyDataDownloadIds.map((id) async {
      try {
        await dm.download(id);
      } catch (e) {
        debugPrint('[wizard] sky-data download queue failed for $id: $e');
      }
    }));
  }

  Future<void> _saveAndExit(WizardController controller) async {
    if (_isSaving) return; // double-tap guard until the spinner blocks input
    // liveDraft() returns the live draft object: saveWizardProfile stamps
    // draft.savedProfileId on the first attempt so a retry re-uses the same
    // profile instead of orphaning a new one.
    final draft = controller.liveDraft();
    // Capture the Navigator + Messenger BEFORE the async gap so a pop/snackbar is
    // safe even if the widget unmounts mid-save (otherwise an early
    // `!context.mounted` return would strand the non-dismissible spinner forever).
    final nav = Navigator.of(context);
    final messenger = ScaffoldMessenger.of(context);

    final server = ref.read(activeServerProvider);
    if (server == null) {
      _showError(messenger, 'No active server — connect to a daemon before saving the profile.');
      return; // keep the wizard open; nothing to save against
    }
    final api = (widget.createApi ?? ProfileApi.new)(server);

    setState(() => _isSaving = true);
    showDialog<void>(
      context: context,
      barrierDismissible: false,
      // Push the spinner onto the SAME navigator we captured in `nav`
      // (Navigator.of(context), the nearest one the wizard route lives on).
      // showDialog defaults to useRootNavigator:true, which would put the
      // spinner on the root navigator while nav.pop() targets the nearest —
      // leaving the (canPop:false, barrierDismissible:false) spinner stuck if
      // the wizard ever runs inside a nested navigator.
      useRootNavigator: false,
      // PopScope(canPop: false) also blocks the Android system-back button from
      // dismissing the spinner; otherwise a back press mid-save would pop the
      // spinner early and the nav.pop() calls below would pop the wizard (and the
      // route under it) instead of the spinner.
      builder: (_) => const PopScope(
        canPop: false,
        child: Center(child: CircularProgressIndicator()),
      ),
    );

    String? error;
    try {
      await saveWizardProfile(api, draft);
      // Screen 17 — queue the user's chosen sky-data downloads now that the
      // profile exists. Best-effort + after the profile is saved: a failed
      // queue request must NOT turn a successful save into an error (downloads
      // are 202/fire-and-forget and visible in Settings → Data).
      await _queueSkyDataDownloads(draft);
    } on DioException catch (e) {
      error = 'Couldn\'t save the profile: ${e.message ?? 'network error'} '
          '(${e.response?.statusCode ?? 'no response'}).';
    } catch (e) {
      // Keep the raw detail in the logs; show the user a clean message (no
      // "Exception:" prefix / internal section text) — Save is retryable.
      debugPrint('[wizard] profile save failed: $e');
      error = 'Couldn\'t save the profile. Please try again.';
    }

    if (nav.mounted) nav.pop(); // close the spinner — independent of widget mount state
    // Clear the guard inside setState when still mounted (Flutter contract for
    // state mutations); fall back to a bare assignment if the widget is gone.
    if (mounted) {
      setState(() => _isSaving = false);
    } else {
      _isSaving = false;
    }

    if (error != null) {
      _showError(messenger, error);
      return; // keep the wizard open so the user can retry
    }

    // Exit the wizard first, THEN notify — so if onComplete routes/pops, it can't
    // race our pop into popping an unintended route.
    if (nav.mounted) nav.pop(); // exit the wizard
    widget.onComplete?.call(ProfileDraftSnapshot(draft));
  }

  void _showError(ScaffoldMessengerState messenger, String message) {
    messenger.showSnackBar(
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
