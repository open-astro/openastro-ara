import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../services/profile_api.dart';
import '../../state/guider/guider_build_activity_state.dart';
import '../../state/guider/guider_calibration_state.dart';
import '../../state/guider/guider_equipment_state.dart';
import '../../state/profile_management_state.dart';
import '../../state/saved_server_state.dart';
import '../../state/wizard_state.dart';
import '../../theme/ara_colors.dart';
import 'wizard_equipment_apply.dart';
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

  // §76.2 S4 — when a darks build was kicked off at Finish, the wizard stays
  // open on a Done view with the §63.8 live progress instead of popping (the
  // user can still leave any time; the build runs daemon-side).
  bool _showDone = false;
  List<String> _finishNotes = const [];

  @override
  Widget build(BuildContext context) {
    if (_showDone) {
      return WizardDoneView(
        notes: _finishNotes,
        onFinish: () {
          final draft = ref.read(wizardControllerProvider).draft;
          Navigator.of(context).pop();
          widget.onComplete?.call(ProfileDraftSnapshot(draft));
        },
      );
    }
    final state = ref.watch(wizardControllerProvider);
    final controller = ref.read(wizardControllerProvider.notifier);
    // Gate Next / Save Profile on the current screen's inline validation (set by
    // the validated screens; reset to true by the controller on each step).
    final stepValid = ref.watch(wizardStepValidProvider);
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
                : () => _saveAndExit(controller, finalSave: true),
            isLast: state.step == ProfileWizard.totalSteps,
            canAdvance: stepValid,
          ),
        ],
      ),
    );
  }

  // Persist the draft as a new profile (§37 Save / Save & Exit — both paths save
  // partial-or-complete state per §37.8), then exit the wizard. Shows a blocking
  // spinner during the round-trip and keeps the wizard open on failure so the
  // user doesn't lose their entries.
  
  Future<void> _saveAndExit(WizardController controller,
      {bool finalSave = false}) async {
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
      _showError(messenger, 'Connect to your rig before saving this profile.');
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
    } on DioException catch (e) {
      error = 'Couldn\'t save the profile: ${e.message ?? 'network error'} '
          '(${e.response?.statusCode ?? 'no response'}).';
    } catch (e) {
      // Keep the raw detail in the logs; show the user a clean message (no
      // "Exception:" prefix / internal section text) — Save is retryable.
      debugPrint('[wizard] profile save failed: $e');
      error = 'Couldn\'t save the profile. Please try again.';
    }

    // Screen 17 — on a successful save, queue the user's chosen sky-data
    // downloads while the blocking spinner is STILL up, so the (fast, best-effort
    // 202) queue requests show a busy indicator instead of a momentarily-static
    // wizard. Kept OUTSIDE the save try/catch with its own swallow-all catch: any
    // error escaping the per-id catches inside must NOT turn a successful save
    // into a failure (downloads are fire-and-forget, visible in Settings → Data).
    // It runs here — before the wizard pops and while the widget is still mounted
    // — so the method's `!mounted` guard never skips queuing (firing it after the
    // wizard exit would race disposal and silently drop the downloads).
    if (error == null) {
      try {
      } catch (_) {
        // truly best-effort — per-id failures are already logged inside the method
      }
    }

    // Hand the assigned equipment to the daemon while the spinner is still up:
    // connect every assigned device (switch/weather/flat/safety/dome have no
    // per-device wizard screen, so this is their ONLY connect) and forget the
    // remembered device for any slot deliberately set to None (otherwise
    // auto-connect keeps erroring on hardware the user no longer has). Skipped
    // on save failure — the wizard stays open for a retry. Never throws.
    var equipmentNotes = const <String>[];
    if (error == null) {
      equipmentNotes = await applyWizardEquipment(server, draft.equipment);
    }

    // §76.2 S4 — guider provisioning + darks kickoff, both best-effort (a
    // failure degrades to an amber note, never fails the wizard). The push
    // re-sends the just-saved profile (incl. the exposure range + camera
    // pick) so the guider twin named after this profile carries the final
    // config even if the daemon's own profile-switch follow raced the
    // section PUTs.
    // Only a FINAL-step Save provisions the guider + shoots darks — a mid-
    // wizard "Save & Exit" persists a partial draft the user intends to
    // resume, and half-configured guiding must not push or expose frames.
    var darksStarted = false;
    final finishNotes = <String>[...equipmentNotes];
    if (error == null && finalSave) {
      try {
        await ref.read(guiderEquipmentProvider.notifier).pushProfile();
      } catch (e) {
        debugPrint('[wizard] guider profile push failed: $e');
        finishNotes.add('The guider profile push didn\'t complete — push it '
            'from the Setup tab\'s Guider panel when the guider is reachable.');
      }
      if (draft.guider.buildDarksOnFinish) {
        final calApi = ref.read(guiderCalibrationApiProvider);
        if (calApi == null) {
          finishNotes.add('Dark-library build skipped (no server connection) '
              '— build it later from the Guider panel.');
        } else {
          try {
            await calApi.buildDarkLibrary(
              frameCount: draft.guider.darkFrameCount,
              minExposureMs: draft.guider.darkMinExposureMs,
              maxExposureMs: draft.guider.darkMaxExposureMs,
            );
            darksStarted = true;
          } catch (e) {
            debugPrint('[wizard] darks kickoff failed: $e');
            finishNotes.add('The dark-library build couldn\'t start — cover '
                'the guide scope and build it from the Guider panel.');
          }
        }
      }
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

    // §76.2 S4 — darks running: swap to the Done view with live progress
    // instead of popping. Notes (equipment/guider degrades) show there.
    if (darksStarted && mounted) {
      ref.invalidate(profileManagementProvider);
      setState(() {
        _showDone = true;
        _finishNotes = finishNotes;
      });
      return;
    }

    // The new profile is now persisted + active on the daemon. Invalidate the
    // cached profile list so it shows up immediately — the wizard is launched
    // from entry points that pass no onComplete (the app-shell + Settings
    // launchers), so we can't rely on the caller to refresh. Without this the
    // save succeeds but the stale list makes it look like nothing was saved.
    if (mounted) ref.invalidate(profileManagementProvider);

    // Exit the wizard first, THEN notify — so if onComplete routes/pops, it can't
    // race our pop into popping an unintended route.
    if (nav.mounted) nav.pop(); // exit the wizard
    // Profile saved fine, but some follow-ups degraded (device connects,
    // guider push, darks) — tell the user which, with where to finish them.
    if (finishNotes.isNotEmpty) {
      messenger.showSnackBar(SnackBar(
        content: Text('Profile saved. ${finishNotes.join(' ')}'),
        duration: const Duration(seconds: 8),
      ));
    }
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

  /// False when the current screen has a blocking inline validation error or an
  /// unmet advance-gate (§68.2 bridge detection) — disables Next / Save Profile
  /// AND Skip: both advance the wizard, so a gate one of them ignored would be
  /// no gate at all (and on field-validation screens the invalid value is
  /// already in the draft — skipping wouldn't discard it). Back always works,
  /// so the user is never trapped.
  final bool canAdvance;

  const _BottomNavBar({
    required this.currentStep,
    required this.stageLabel,
    required this.onBack,
    required this.onSkip,
    required this.onNext,
    required this.isLast,
    required this.canAdvance,
  });

  @override
  Widget build(BuildContext context) {
    final nextButton = FilledButton.icon(
      onPressed: canAdvance ? onNext : null,
      icon: Icon(isLast ? Icons.check : Icons.chevron_right),
      label: Text(isLast ? 'Save Profile' : 'Next'),
    );
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
          // Gated like Next (see canAdvance) — a skippable gate is no gate.
          if (canAdvance)
            TextButton(
              onPressed: onSkip,
              child: const Text('Skip — use defaults'),
            )
          else
            Tooltip(
              message: 'Resolve the issue on this screen to continue.',
              child: const TextButton(
                onPressed: null,
                child: Text('Skip — use defaults'),
              ),
            ),
          const Spacer(),
          Text(stageLabel,
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                    color: AraColors.textSecondary,
                  )),
          const SizedBox(width: 16),
          // Only wrap in a Tooltip when disabled — an empty-message Tooltip on the
          // enabled button can flash a blank popup on hover on some platforms.
          if (canAdvance)
            nextButton
          else
            Tooltip(
              message: 'Fix the highlighted field to continue.',
              child: nextButton,
            ),
        ],
      ),
    );
  }
}

/// §76.2 S4 — the post-Finish Done view: the profile is saved and the dark
/// library is building daemon-side; show the §63.8 live progress plus any
/// amber follow-up notes. Leaving is always safe — the build continues on
/// the server.
class WizardDoneView extends ConsumerWidget {
  const WizardDoneView({super.key, required this.notes, required this.onFinish});

  final List<String> notes;
  final VoidCallback onFinish;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final activity = ref
        .watch(guiderBuildActivityProvider)[CalibrationArtifact.darkLibrary];
    final phase = activity?.phase;
    final fraction = activity?.fraction;
    final String progressLine;
    if (phase == CalibrationBuildPhase.complete) {
      progressLine = 'Dark library complete.';
    } else if (phase == CalibrationBuildPhase.failed) {
      progressLine = 'Dark-library build failed'
          '${activity?.error != null ? ' (${activity!.error})' : ''} — '
          'rebuild it from the Guider panel.';
    } else if (activity?.exposureIndex != null) {
      progressLine = 'Building dark library — exposure '
          '${activity!.exposureIndex}/${activity.exposureCount} '
          '(frame ${activity.frame}/${activity.frameCount})…';
    } else {
      progressLine = 'Building dark library…';
    }
    return Scaffold(
      appBar: AppBar(title: const Text('Profile saved')),
      body: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 560),
          child: Padding(
            padding: const EdgeInsets.all(24),
            child: Column(
              mainAxisAlignment: MainAxisAlignment.center,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                const Icon(Icons.check_circle,
                    size: 44, color: AraColors.accentConnected),
                const SizedBox(height: 12),
                Text('You\'re all set',
                    style: Theme.of(context).textTheme.headlineSmall),
                const SizedBox(height: 8),
                Text(
                  'Your profile is saved and active. The dark library is '
                  'shooting in the background — it finishes on the server '
                  'even if you leave now.',
                  style: Theme.of(context)
                      .textTheme
                      .bodyMedium
                      ?.copyWith(color: AraColors.textSecondary),
                ),
                const SizedBox(height: 20),
                if (phase != CalibrationBuildPhase.failed) ...[
                  LinearProgressIndicator(
                    value: phase == CalibrationBuildPhase.complete
                        ? 1.0
                        : fraction,
                    backgroundColor: AraColors.bgPanel,
                    valueColor:
                        const AlwaysStoppedAnimation(AraColors.accentInfo),
                  ),
                  const SizedBox(height: 8),
                ],
                Text(progressLine,
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(
                          color: phase == CalibrationBuildPhase.failed
                              ? AraColors.accentBusy
                              : AraColors.textSecondary,
                        )),
                for (final note in notes) ...[
                  const SizedBox(height: 8),
                  Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      const Icon(Icons.warning_amber_rounded,
                          size: 16, color: AraColors.accentBusy),
                      const SizedBox(width: 6),
                      Expanded(
                        child: Text(note,
                            style: Theme.of(context)
                                .textTheme
                                .bodySmall
                                ?.copyWith(color: AraColors.textSecondary)),
                      ),
                    ],
                  ),
                ],
                const SizedBox(height: 24),
                Align(
                  alignment: Alignment.centerRight,
                  child: FilledButton.icon(
                    onPressed: onFinish,
                    icon: const Icon(Icons.check),
                    label: const Text('Finish'),
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
