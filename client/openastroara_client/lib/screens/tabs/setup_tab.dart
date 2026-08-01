import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/equipment_device_status.dart';
import '../../state/equipment/camera_state.dart';
import '../../state/equipment/mount_state.dart';
import '../../state/polar_align/polar_align_state.dart';
import '../../state/settings/phd2_settings_state.dart';
import '../../state/settings/settings_nav.dart';
import '../../state/setup/setup_readiness.dart';
import '../../theme/ara_colors.dart';
import '../../widgets/guider/guider_setup_wizard.dart';
import '../../widgets/imaging/polar_align_panel.dart';
import '../calibration/calibration_screen.dart';

/// Setup tab — the dusk ritual as a two-pane surface (§25 flow redesign).
/// Left: the Tonight checklist (Connect equipment → Polar align → Calibration
/// frames), each row with a live readiness glyph. Right: the selected step's
/// instrument. Gates are checkmarks, not dams — nothing here blocks; the rail
/// order just reads as the night (Plan → Setup → Run → Live).
class SetupTab extends StatefulWidget {
  const SetupTab({super.key});

  @override
  State<SetupTab> createState() => _SetupTabState();
}

enum _SetupStep { connect, polarAlign, calibration }

class _SetupTabState extends State<SetupTab> {
  _SetupStep _selected = _SetupStep.connect;

  @override
  Widget build(BuildContext context) {
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
                ],
              ),
            ),
          ),
        ),
        Expanded(
          child: switch (_selected) {
            _SetupStep.connect => const _ConnectPane(),
            _SetupStep.polarAlign => const _PolarAlignPane(),
            _SetupStep.calibration => const _CalibrationPane(),
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
class _GuiderSection extends ConsumerWidget {
  const _GuiderSection();

  /// Unconfigured = nothing gear-specific has ever been chosen. The daemon
  /// host/port alone don't count — they carry defaults out of the box.
  static bool looksUnconfigured(Phd2Settings s) =>
      s.guiderCamera.isEmpty &&
      s.guideFocalLength == 0 &&
      s.guidePixelSize == 0;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final phd2 = ref.watch(phd2SettingsProvider);
    final unconfigured = looksUnconfigured(phd2);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          unconfigured
              ? 'The guider hasn\'t been set up yet — polar alignment and '
                  'guiding both need it. The wizard walks through camera, '
                  'optics, mount and darks in a couple of minutes.'
              : 'Polar alignment also needs the guide camera (through the '
                  'guider daemon).',
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
    return const SingleChildScrollView(
      padding: EdgeInsets.all(12),
      child: PolarAlignPanel(),
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
