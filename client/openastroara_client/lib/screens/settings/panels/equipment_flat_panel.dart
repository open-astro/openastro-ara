import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/equipment_device_status.dart';
import '../../../services/equipment_device_api.dart';
import '../../../models/flat_panel_status.dart';
import '../../../state/equipment/flat_panel_state.dart';
import '../../../state/settings/equipment_connection_state.dart';
import '../../../state/settings/safety_policies_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/help_icon.dart';
import '../../../widgets/equipment/equipment_connection_card.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.4 Flat Panel (CoverCalibrator under Alpaca). Connect/disconnect/reconnect
/// run through the shared [EquipmentConnectionCard] on the live [flatPanelProvider]
/// — the same provider the top-bar FLAT chip watches, so connecting here turns the
/// chip green. The connected body is the live cover/light readout plus its
/// controls (open/close the cover, the light on/off, brightness); the §29.3
/// capture rows below stay as profile-policy references.
class EquipmentFlatPanel extends ConsumerWidget {
  const EquipmentFlatPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connection = ref.watch(equipmentConnectionProvider);
    final policies = ref.watch(safetyPoliciesProvider);
    final n = ref.read(equipmentConnectionProvider.notifier);
    final status = ref.watch(flatPanelProvider);
    final notifier = ref.read(flatPanelProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Connection'),
        EquipmentConnectionCard<FlatPanelStatus>(
          status: status,
          deviceType: EquipmentDeviceType.flatPanel,
          deviceTypeLabel: 'flat panel',
          emptyLabel: 'No flat panel connected.',
          onConnect: notifier.connect,
          onDisconnect: notifier.disconnect,
          onReconnect: notifier.reconnect,
          onRetry: notifier.refresh,
          connectedBody: (context, s) => _FlatBody(status: s),
        ),
        SettingsSwitchRow(
          label: 'Auto-connect on boot',
          helpKey: 'eq.auto_connect_on_boot',
          value: connection.autoConnect(EquipmentDeviceType.flatPanel),
          onChanged: (v) => n.setAutoConnect(EquipmentDeviceType.flatPanel, v),
        ),
        const SettingsSectionHeader('Flat capture'),
        SettingsRow(
          label: 'Auto-brightness target (ADU)',
          helpKey: 'eq.flat.auto_brightness_target',
          value: policies.flatTargetAdu.toString(),
          hint: 'Edit in Settings → Safety → Policies (flat sets)',
        ),
        SettingsRow(
          label: 'Target tolerance (%)',
          helpKey: 'eq.flat.target_tolerance',
          value: policies.flatTargetAduTolerancePct.toString(),
          hint: 'Edit in Settings → Safety → Policies (flat sets)',
        ),
        SettingsRow(
          label: 'Frames per filter',
          helpKey: 'eq.flat.frames_per_filter',
          value: policies.flatFramesPerFilter.toString(),
          hint: 'Edit in Settings → Safety → Policies (flat sets)',
        ),
      ],
    );
  }
}

/// The connected flat panel's live body: cover position + calibrator light state,
/// each with its control. While connecting it's the daemon default (transient →
/// "Reading…"); `error` is a failed read, not a flag.
class _FlatBody extends StatelessWidget {
  final FlatPanelStatus status;
  const _FlatBody({required this.status});

  @override
  Widget build(BuildContext context) {
    return switch (status.connectionState) {
      EquipmentConnectionState.connected => _FlatControls(status: status),
      EquipmentConnectionState.error => const Row(
        children: [
          Icon(Icons.error_outline, color: AraColors.accentError, size: 20),
          SizedBox(width: 8),
          Expanded(child: Text('Flat panel read failed — check the device.')),
        ],
      ),
      _ => const Text('Reading…'),
    };
  }
}

/// Cover state (open / closed / moving) + calibrator light (on at brightness, or
/// off), each row carrying the control that drives it through
/// `POST /equipment/flatdevice/apply`. Devices that report a part as NotPresent
/// (a bare light panel, a plain dust cover) hide that row rather than show a dead
/// control.
class _FlatControls extends ConsumerStatefulWidget {
  final FlatPanelStatus status;
  const _FlatControls({required this.status});

  @override
  ConsumerState<_FlatControls> createState() => _FlatControlsState();
}

class _FlatControlsState extends ConsumerState<_FlatControls> {
  /// The light state we just commanded, held only while the apply is in flight so
  /// the switch moves under the finger instead of waiting for the device to be
  /// re-read. Cleared once the daemon confirms (or the command fails), and the
  /// device's own reading takes over again.
  bool? _pendingLight;

  /// Brightness being dragged. Non-null only between drag start and commit, so
  /// the slider follows the finger without the 2s poll yanking it back; cleared
  /// on commit and the daemon's reading takes over again.
  int? _dragging;

  @override
  Widget build(BuildContext context) {
    final s = widget.status;
    final (coverIcon, coverText) = s.isMoving
        ? (Icons.sync, 'Cover moving…')
        : s.coverOpen
        ? (Icons.unfold_more, 'Cover open')
        : (Icons.unfold_less, 'Cover closed');
    final max = s.maxBrightness;
    final level = (_dragging ?? s.brightness).clamp(0, max == 0 ? 0 : max);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        if (s.hasCover) ...[
          Row(
            children: [
              Icon(
                coverIcon,
                size: 20,
                color: s.isMoving
                    ? AraColors.accentBusy
                    : AraColors.textSecondary,
              ),
              const SizedBox(width: 8),
              Expanded(child: Text(coverText)),
              OutlinedButton(
                // The cover is a single motor: block both buttons while it moves
                // rather than queue a reversal mid-travel.
                onPressed: s.isMoving ? null : () => _apply(openCover: true),
                child: const Text('Open'),
              ),
              const SizedBox(width: 8),
              OutlinedButton(
                onPressed: s.isMoving ? null : () => _apply(openCover: false),
                child: const Text('Close'),
              ),
              HelpIcon(helpKey: 'eq.flat.cover', device: s.name),
            ],
          ),
          const SizedBox(height: 6),
        ],
        if (s.hasCalibrator) ...[
          Row(
            children: [
              Icon(
                s.lightOn ? Icons.lightbulb : Icons.lightbulb_outline,
                size: 20,
                color: s.lightOn
                    ? AraColors.accentConnected
                    : AraColors.textSecondary,
              ),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  _pendingLight != null
                      ? (s.isMoving
                            ? 'Waiting for the cover, then turning the light '
                                  '${_pendingLight! ? 'on' : 'off'}…'
                            : s.lightWarming
                            ? 'Light warming up…'
                            : _pendingLight!
                            ? 'Turning the light on…'
                            : 'Turning the light off…')
                      : s.lightWarming
                      ? 'Light warming up…'
                      : s.lightOn
                      ? 'Light on · brightness ${s.brightness}'
                      : 'Light off',
                  style: _pendingLight != null
                      ? const TextStyle(color: AraColors.accentBusy)
                      : null,
                ),
              ),
              Switch(
                // Keyed: the panel also hosts the auto-connect switch, and both
                // the tests and any driver need to target this one unambiguously.
                key: const Key('flat-light-switch'),
                value: _pendingLight ?? s.lightOn,
                // Dead while its own command is in flight: the panel is a single
                // device and a second tap would only be refused by the notifier's
                // re-entrancy guard.
                onChanged: _pendingLight != null
                    ? null
                    : (on) => _apply(lightOn: on),
              ),
              HelpIcon(helpKey: 'eq.flat.light', device: s.name),
            ],
          ),
          Row(
            children: [
              const SizedBox(width: 28, child: Text('0')),
              Expanded(
                child: Slider(
                  value: level.toDouble(),
                  min: 0,
                  // A device whose MaxBrightness hasn't been read yet reports 0;
                  // Slider needs max > min, so hold at 1 and disable below.
                  max: (max > 0 ? max : 1).toDouble(),
                  divisions: max > 0 ? max.clamp(1, 100) : null,
                  label: level.toString(),
                  // Disabled until the daemon has read a real max — otherwise the
                  // control would command brightnesses the device rejects.
                  onChanged: max > 0
                      ? (v) => setState(() => _dragging = v.round())
                      : null,
                  // Commit on release only: dragging fires continuously and each
                  // change is a blocking ASCOM CalibratorOn round-trip.
                  onChangeEnd: max > 0
                      ? (v) => _apply(brightness: v.round())
                      : null,
                ),
              ),
              SizedBox(
                width: 48,
                child: Text(
                  max > 0 ? '$level' : '—',
                  textAlign: TextAlign.end,
                ),
              ),
              HelpIcon(helpKey: 'eq.flat.brightness', device: s.name),
            ],
          ),
          if (max == 0)
            const Padding(
              padding: EdgeInsets.only(left: 28),
              child: Text(
                'Reading the panel\'s brightness range…',
                style: TextStyle(color: AraColors.textSecondary, fontSize: 12),
              ),
            ),
        ],
      ],
    );
  }

  /// Sends one apply and reports the outcome. Any thrown error surfaces in a
  /// SnackBar — a silent failure here reads as "the control does nothing", the
  /// exact complaint this panel exists to fix.
  Future<void> _apply({bool? openCover, bool? lightOn, int? brightness}) async {
    final messenger = ScaffoldMessenger.of(context);
    if (lightOn != null) setState(() => _pendingLight = lightOn);
    try {
      final performed = await ref
          .read(flatPanelProvider.notifier)
          .apply(openCover: openCover, lightOn: lightOn, brightness: brightness);
      if (!performed) {
        messenger.showSnackBar(
          const SnackBar(content: Text('Another action is still in progress.')),
        );
      }
    } catch (e) {
      messenger.showSnackBar(
        SnackBar(
          content: Text("Couldn't drive the flat panel: ${describeEquipmentError(e)}"),
          backgroundColor: AraColors.accentError,
        ),
      );
    } finally {
      // Drop both overrides either way: on success the confirmed reading owns the
      // value again, on failure the controls must snap back to the device's truth.
      if (mounted) {
        setState(() {
          _dragging = null;
          _pendingLight = null;
        });
      }
    }
  }
}
