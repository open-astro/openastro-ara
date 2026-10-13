import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/equipment_device_status.dart';
import '../../state/equipment/camera_state.dart';
import '../../state/equipment/mount_state.dart';
import '../../state/focus/autofocus_live_state.dart';
import '../../state/polar_align/polar_align_state.dart';
import '../../state/preflight/preflight_state.dart';
import '../../state/profile_management_state.dart';
import '../../state/settings/phd2_settings_state.dart';
import '../../state/settings/settings_nav.dart';
import '../../state/setup/setup_readiness.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../widgets/fit_pane.dart';
import '../../widgets/focus/focusing_pane.dart';
import '../../widgets/guider/guider_setup_wizard.dart';
import '../../widgets/imaging/polar_align_panel.dart';
import '../../widgets/preflight/preflight_checklist_card.dart';
import '../calibration/calibration_screen.dart';

/// Setup tab — the dusk ritual as a two-pane surface (§25 flow redesign).
/// Left: the Tonight checklist (Connect equipment → Smart Focus → Polar align →
/// Calibration frames → Before you sleep), each row with a live readiness glyph. Right: the selected step's
/// instrument. Gates are checkmarks, not dams — nothing here blocks; the rail
/// order just reads as the night (Plan → Setup → Run → Live).
class SetupTab extends ConsumerStatefulWidget {
  const SetupTab({super.key});

  @override
  ConsumerState<SetupTab> createState() => _SetupTabState();
}

enum _SetupStep { connect, focus, polarAlign, calibration, preSleep }

class _SetupTabState extends ConsumerState<SetupTab> {
  _SetupStep _selected = _SetupStep.connect;

  @override
  Widget build(BuildContext context) {
    // §58.14 — the bedtime card's Adjust lands on the Before you sleep step.
    ref.listen(setupPreSleepRequestProvider, (prev, next) {
      if (next != prev) _select(_SetupStep.preSleep);
    });
    return Row(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        SizedBox(
          width: 280,
          child: Container(
            // Border only — the fill comes from the Material below, so the
            // ListTiles' selection/ink paint on it instead of being hidden.
            decoration: const BoxDecoration(
              border: Border(right: BorderSide(color: AraColors.border)),
            ),
            child: Material(
              color: AraColors.bgPanel,
              child: ListView(
                padding: const EdgeInsets.symmetric(vertical: 8),
                children: [
                  Padding(
                    padding: const EdgeInsets.fromLTRB(16, 8, 16, 12),
                    child: Text(
                      'Tonight',
                      style: Theme.of(context).textTheme.titleMedium,
                    ),
                  ),
                  _ChecklistRow(
                    step: _SetupStep.connect,
                    selected: _selected,
                    title: 'Connect equipment',
                    onTap: _select,
                  ),
                  _ChecklistRow(
                    step: _SetupStep.focus,
                    selected: _selected,
                    title: 'Smart Focus',
                    onTap: _select,
                  ),
                  _ChecklistRow(
                    step: _SetupStep.polarAlign,
                    selected: _selected,
                    title: 'Polar align',
                    onTap: _select,
                  ),
                  _ChecklistRow(
                    step: _SetupStep.calibration,
                    selected: _selected,
                    title: 'Calibration frames',
                    onTap: _select,
                  ),
                  _ChecklistRow(
                    step: _SetupStep.preSleep,
                    selected: _selected,
                    title: 'Before you sleep',
                    onTap: _select,
                  ),
                ],
              ),
            ),
          ),
        ),
        Expanded(
          child: switch (_selected) {
            _SetupStep.connect => const _ConnectPane(),
            _SetupStep.focus => const FocusingPane(),
            _SetupStep.polarAlign => const _PolarAlignPane(),
            _SetupStep.calibration => const _CalibrationPane(),
            _SetupStep.preSleep => const _PreSleepPane(),
          },
        ),
      ],
    );
  }

  void _select(_SetupStep step) => setState(() => _selected = step);
}

class _ChecklistRow extends ConsumerWidget {
  final _SetupStep step;
  final _SetupStep selected;
  final String title;
  final void Function(_SetupStep) onTap;
  const _ChecklistRow({
    required this.step,
    required this.selected,
    required this.title,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final (state, subtitle) = switch (step) {
      _SetupStep.connect => (
        ref.watch(setupConnectStateProvider),
        _connectSubtitle(ref),
      ),
      _SetupStep.focus => (
        ref.watch(setupFocusStateProvider),
        _focusSubtitle(ref),
      ),
      _SetupStep.polarAlign => (
        ref.watch(setupPolarAlignStateProvider),
        _polarAlignSubtitle(ref),
      ),
      // No completion signal for calibration frames yet — the library is
      // reusable across nights, so the row stays a neutral shortcut.
      _SetupStep.calibration => (
        SetupStepState.pending,
        'Optional — darks, flats & the dark library',
      ),
      _SetupStep.preSleep => (
        ref.watch(setupPreSleepStateProvider),
        _preSleepSubtitle(ref),
      ),
    };
    return ListTile(
      selected: step == selected,
      selectedTileColor: AraColors.bgInput,
      leading: _StepGlyph(state: state),
      title: Text(title),
      subtitle: Text(
        subtitle,
        style: Theme.of(
          context,
        ).textTheme.bodySmall?.copyWith(color: AraColors.textSecondary),
        maxLines: 2,
        overflow: TextOverflow.ellipsis,
      ),
      onTap: () => onTap(step),
    );
  }

  String _connectSubtitle(WidgetRef ref) {
    final mount = ref.watch(mountProvider).asData?.value;
    final camera = ref.watch(cameraStatusProvider).asData?.value;
    final mountOk =
        mount?.connectionState == EquipmentConnectionState.connected;
    final cameraOk =
        camera?.connectionState == EquipmentConnectionState.connected;
    if (mountOk && cameraOk) return 'Mount and camera connected';
    if (!mountOk && !cameraOk) return 'Mount and camera not connected';
    return mountOk ? 'Camera not connected' : 'Mount not connected';
  }

  String _focusSubtitle(WidgetRef ref) {
    final live = ref.watch(autofocusLiveProvider);
    final run = live.run;
    return switch (ref.watch(setupFocusStateProvider)) {
      SetupStepState.done when run.isComplete && run.finalHfr != null && run.finalHfr! > 0 =>
        'In focus — HFR ${run.finalHfr!.toStringAsFixed(2)} at ${run.finalPosition}',
      SetupStepState.done => 'In focus',
      SetupStepState.inProgress => 'Autofocus running…',
      SetupStepState.problem => 'Autofocus failed — see the panel',
      SetupStepState.pending => 'Main scope and guide camera',
    };
  }

  String _preSleepSubtitle(WidgetRef ref) {
    final checklist = ref.watch(preflightChecklistProvider);
    final acked =
        ref.watch(preflightPromptProvider).asData?.value.acknowledgedTonight ??
        false;
    final c = checklist.asData?.value;
    if (checklist.hasError) return 'Could not read the checklist';
    if (c == null) return 'The pre-flight check, once before bed';
    if (c.hasFailure) return 'Something needs attention — see the panel';
    if (acked) {
      return c.hasWarning
          ? 'Answered tonight, with warnings'
          : 'All good tonight';
    }
    return c.hasWarning
        ? 'Warnings to look at before bed'
        : 'Everything checks out';
  }

  String _polarAlignSubtitle(WidgetRef ref) {
    final live = ref.watch(polarAlignLiveProvider);
    final total = live.totalErrorArcmin;
    return switch (ref.watch(setupPolarAlignStateProvider)) {
      SetupStepState.done when total != null =>
        'Aligned — ${total.toStringAsFixed(1)}′ from the pole',
      SetupStepState.done => 'Aligned',
      SetupStepState.inProgress => 'Alignment in progress…',
      SetupStepState.problem =>
        live.errorMessage ?? 'Alignment failed — see the panel',
      SetupStepState.pending => 'Not aligned this session',
    };
  }
}

/// Checklist readiness glyph: green check when the gate is satisfied, amber
/// progress while underway, red on a problem, neutral dot otherwise.
class _StepGlyph extends StatelessWidget {
  final SetupStepState state;
  const _StepGlyph({required this.state});

  @override
  Widget build(BuildContext context) {
    return switch (state) {
      SetupStepState.done => const Icon(
        Icons.check_circle,
        size: 20,
        color: AraColors.accentConnected,
      ),
      SetupStepState.inProgress => const Icon(
        Icons.timelapse,
        size: 20,
        color: AraColors.accentBusy,
      ),
      SetupStepState.problem => const Icon(
        Icons.error_outline,
        size: 20,
        color: AraColors.accentError,
      ),
      SetupStepState.pending => const Icon(
        Icons.circle_outlined,
        size: 20,
        color: AraColors.textDisabled,
      ),
    };
  }
}

/// Connect step — per-device status rows with jumps to the matching Options
/// panel (the top equipment chips do the actual connect flow; this pane is the
/// checklist's "where do I look" surface).
class _ConnectPane extends ConsumerWidget {
  const _ConnectPane();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final mount = ref.watch(mountProvider).asData?.value;
    final camera = ref.watch(cameraStatusProvider).asData?.value;
    return _PaneScaffold(
      title: 'Connect equipment',
      subtitle:
          'A session needs at least the mount and the main camera. Use the '
          'equipment chips along the top bar to connect; each row below jumps '
          'to that device\'s settings.',
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _DeviceRow(
            label: 'Mount',
            state: mount?.connectionState,
            detail: mount?.name,
            onConfigure: () => openSettingsPanel(ref, 'eq.mount'),
          ),
          _DeviceRow(
            label: 'Camera',
            state: camera?.connectionState,
            detail: camera?.name,
            onConfigure: () => openSettingsPanel(ref, 'eq.camera'),
          ),
          const SizedBox(height: 16),
          const _GuiderSection(),
        ],
      ),
    );
  }
}

/// Guider entry in the connect pane. Suggest-don't-interrupt: when the guider
/// profile looks unconfigured (no guide camera selected AND no guide optics
/// entered), the wizard gets a first-run callout and the prominent button;
/// once configured it demotes to a plain re-run affordance next to the full
/// settings link. Nothing auto-opens — the nudge lives in the page, not a
/// modal (macOS Setup Assistant energy, matching the checklist's glyphs).
class _GuiderSection extends ConsumerStatefulWidget {
  const _GuiderSection();

  /// Unconfigured = nothing gear-specific has ever been chosen. The daemon
  /// host/port alone don't count — they carry defaults out of the box.
  static bool looksUnconfigured(Phd2Settings s) =>
      s.guiderCamera.isEmpty &&
      s.guideFocalLength == 0 &&
      s.guidePixelSize == 0;

  @override
  ConsumerState<_GuiderSection> createState() => _GuiderSectionState();
}

class _GuiderSectionState extends ConsumerState<_GuiderSection> {
  @override
  void initState() {
    super.initState();
    // The un/configured verdict reads the SHARED phd2 provider, which sits at
    // client defaults on a fresh launch until some surface hydrates it — the
    // Settings panel does, but this section can render first and would then
    // claim "not set up yet" about a fully configured guider. Hydrating here
    // is safe by design (shared providers carry no unapplied edits;
    // hydrateGuarded is generation-fenced). Listen rather than one-shot: the
    // active-server providers hydrate async, so the API can appear late.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      final api = ref.read(profileApiProvider);
      if (api != null) {
        ref.read(phd2SettingsProvider.notifier).hydrateFromServer(api).ignore();
      }
    });
  }

  @override
  Widget build(BuildContext context) {
    ref.listen(profileApiProvider, (prev, next) {
      if (prev == null && next != null) {
        ref.read(phd2SettingsProvider.notifier).hydrateFromServer(next).ignore();
      }
    });
    final phd2 = ref.watch(phd2SettingsProvider);
    final unconfigured = _GuiderSection.looksUnconfigured(phd2);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          unconfigured
              ? 'The guider hasn\'t been set up yet — polar alignment and '
                  'guiding both need it. The wizard walks through camera, '
                  'optics, mount and darks in a couple of minutes.'
              : 'Polar alignment also needs the guide camera (through the '
                  'guider).',
          style: Theme.of(
            context,
          ).textTheme.bodySmall?.copyWith(color: AraColors.textSecondary),
        ),
        const SizedBox(height: 4),
        Wrap(
          spacing: 8,
          runSpacing: 4,
          children: [
            if (unconfigured)
              FilledButton.icon(
                onPressed: () => showGuiderSetupWizard(context),
                icon: const Icon(Icons.auto_fix_high, size: 16),
                label: const Text('Set up the guider…'),
              )
            else
              TextButton.icon(
                onPressed: () => showGuiderSetupWizard(context),
                icon: const Icon(Icons.auto_fix_high, size: 16),
                label: const Text('Re-run guider wizard…'),
              ),
            TextButton.icon(
              onPressed: () => openSettingsPanel(ref, 'eq.guider'),
              icon: const Icon(Icons.tune, size: 16),
              label: const Text('All guider settings…'),
            ),
          ],
        ),
      ],
    );
  }
}

class _DeviceRow extends StatelessWidget {
  final String label;
  final EquipmentConnectionState? state;
  final String? detail;
  final VoidCallback onConfigure;
  const _DeviceRow({
    required this.label,
    required this.state,
    required this.detail,
    required this.onConfigure,
  });

  @override
  Widget build(BuildContext context) {
    final (color, text) = switch (state) {
      EquipmentConnectionState.connected => (
        AraColors.accentConnected,
        detail == null || detail!.isEmpty ? 'Connected' : detail!,
      ),
      EquipmentConnectionState.connecting => (
        AraColors.accentBusy,
        'Connecting…',
      ),
      EquipmentConnectionState.error => (AraColors.accentError, 'Error'),
      _ => (AraColors.textDisabled, 'Not connected'),
    };
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Row(
        children: [
          Icon(Icons.circle, size: 10, color: color),
          const SizedBox(width: 8),
          SizedBox(
            width: 72,
            child: Text(label, style: Theme.of(context).textTheme.bodyMedium),
          ),
          Expanded(
            child: Text(
              text,
              style: Theme.of(
                context,
              ).textTheme.bodySmall?.copyWith(color: AraColors.textSecondary),
              overflow: TextOverflow.ellipsis,
            ),
          ),
          TextButton(onPressed: onConfigure, child: const Text('Configure…')),
        ],
      ),
    );
  }
}

/// Polar-align step — the §45 bullseye instrument, moved here from the Live
/// tab (Live is telemetry-only; alignment is a setup ritual).
class _PolarAlignPane extends StatelessWidget {
  const _PolarAlignPane();

  @override
  Widget build(BuildContext context) {
    // Fills the window; scrolls only when it is shorter than the readouts.
    return FitPane(
      padding: EdgeInsets.all(AraBreakpoints.isShort(context) ? 8 : 12),
      child: const PolarAlignPanel(),
    );
  }
}

/// Calibration step — shortcut into the full-screen §39.10 calibration
/// screen (sessions, matching flats, the dark library).
class _CalibrationPane extends StatelessWidget {
  const _CalibrationPane();

  @override
  Widget build(BuildContext context) {
    return _PaneScaffold(
      title: 'Calibration frames',
      subtitle:
          'Darks, flats and the dark library live in the calibration screen. '
          'A good library is reusable across nights — capture when your '
          'optical train or camera settings change.',
      child: Align(
        alignment: Alignment.centerLeft,
        child: FilledButton.icon(
          onPressed: () => Navigator.of(context).push(
            MaterialPageRoute<void>(builder: (_) => const CalibrationScreen()),
          ),
          icon: const Icon(Icons.flare_outlined, size: 16),
          label: const Text('Open Calibration'),
        ),
      ),
    );
  }
}

class _PaneScaffold extends StatelessWidget {
  final String title;
  final String subtitle;
  final Widget child;
  const _PaneScaffold({
    required this.title,
    required this.subtitle,
    required this.child,
  });

  @override
  Widget build(BuildContext context) {
    return SingleChildScrollView(
      padding: const EdgeInsets.all(24),
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 640),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(title, style: Theme.of(context).textTheme.titleLarge),
            const SizedBox(height: 8),
            Text(
              subtitle,
              style: Theme.of(
                context,
              ).textTheme.bodyMedium?.copyWith(color: AraColors.textSecondary),
            ),
            const SizedBox(height: 20),
            child,
          ],
        ),
      ),
    );
  }
}

/// §58.14 — Before you sleep: the daemon's pre-flight checklist on demand, with
/// a Refresh (every read is a fresh self-test) and the once-per-night answer.
class _PreSleepPane extends ConsumerWidget {
  const _PreSleepPane();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final checklist = ref.watch(preflightChecklistProvider);
    final acked =
        ref.watch(preflightPromptProvider).asData?.value.acknowledgedTonight ??
        false;
    final dim = Theme.of(context).textTheme.bodySmall
        ?.copyWith(color: AraColors.textSecondary);
    return _PaneScaffold(
      title: 'Before you sleep',
      subtitle:
          'A one-shot self-test of the rig as it stands: equipment, storage, the '
          'plate solver, the cooler, the mount, the running sequence\'s meridian '
          'crossings tonight, the unattended hours and the device that will sound '
          'the alarm. After dark, with a sequence running, Ara shows it once; it '
          'stays here with a refresh.',
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          switch (checklist) {
            AsyncData(:final value) when value != null =>
              PreflightChecklistCard(checklist: value),
            AsyncData() => Text(
              'No checklist: not connected, or Ara predates the pre-sleep check.',
              style: dim,
            ),
            AsyncError(:final error) => Text(
              'Could not read the checklist: $error',
              style: dim?.copyWith(color: AraColors.accentError),
            ),
            _ => const Padding(
              padding: EdgeInsets.all(8),
              child: SizedBox(
                width: 20,
                height: 20,
                child: CircularProgressIndicator(strokeWidth: 2),
              ),
            ),
          },
          const SizedBox(height: 12),
          Wrap(
            spacing: 8,
            runSpacing: 8,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              OutlinedButton.icon(
                onPressed: checklist.isLoading
                    ? null
                    : () => ref
                          .read(preflightChecklistProvider.notifier)
                          .refresh(),
                icon: const Icon(Icons.refresh, size: 18),
                label: const Text('Refresh'),
              ),
              if (checklist.asData?.value != null)
                FilledButton(
                  onPressed: acked
                      ? null
                      : () => ref
                            .read(preflightPromptProvider.notifier)
                            .acknowledgeTonight(),
                  child: Text(
                    acked ? 'Answered for tonight' : 'All good for tonight',
                  ),
                ),
            ],
          ),
        ],
      ),
    );
  }
}
