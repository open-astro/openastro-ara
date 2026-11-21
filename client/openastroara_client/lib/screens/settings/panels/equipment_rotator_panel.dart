import 'dart:async';

import 'package:flutter/material.dart';

import '../../../widgets/status_indicator.dart' show StatusBlink;

import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/equipment_device_status.dart';
import '../../../models/rotator_status.dart';
import '../../../state/equipment/device_command_state.dart';
import '../../../state/equipment/rotator_command_state.dart';
import '../../../state/equipment/rotator_state.dart';
import '../../../state/settings/equipment_connection_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/help_icon.dart';
import '../../../widgets/equipment/equipment_connection_card.dart';
import '../../../widgets/settings/auto_connect_row.dart';
import '../../../widgets/settings/settings_row.dart';
import '../../../util/friendly_error.dart';
import '../../../l10n/l10n.dart';
import '../../../widgets/adaptive_controls.dart';

/// §37.4 Rotator panel. Shows the connected rotator's live sky/mechanical angle
/// with move, reverse, and sky-angle sync controls, via the shared connection
/// card. The §38 plate-solve framing hook drives sync programmatically.
class EquipmentRotatorPanel extends ConsumerWidget {
  const EquipmentRotatorPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final status = ref.watch(rotatorProvider);
    final notifier = ref.read(rotatorProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Connection'),
        EquipmentConnectionCard<RotatorStatus>(
          status: status,
          deviceType: EquipmentDeviceType.rotator,
          deviceTypeLabel: 'rotator',
          emptyLabel: 'No rotator connected.',
          onConnect: notifier.connect,
          onDisconnect: notifier.disconnect,
          onReconnect: notifier.reconnect,
          onForget: notifier.forget,
          onRetry: notifier.refresh,
          connectedBody: (context, s) => _RotatorBody(status: s),
        ),
        AutoConnectRow(EquipmentDeviceType.rotator),
        const SettingsSectionHeader('Framing'),
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Text(
            'Rotator angle is set by the centre-and-rotate step whenever a target carries a position angle; there is no separate toggle.',
            style: Theme.of(
              context,
            ).textTheme.bodySmall?.copyWith(color: AraColors.textSecondary),
          ),
        ),
      ],
    );
  }
}

/// The connected rotator's live body: sky + mechanical angle, a reverse toggle,
/// and move / sync controls.
class _RotatorBody extends ConsumerStatefulWidget {
  final RotatorStatus status;
  const _RotatorBody({required this.status});

  @override
  ConsumerState<_RotatorBody> createState() => _RotatorBodyState();
}

class _RotatorBodyState extends ConsumerState<_RotatorBody> {
  // Seeded once from the current sky angle; the user's target, not a live value.
  late final TextEditingController _target = TextEditingController(
    text: widget.status.skyAngleDeg?.toStringAsFixed(1) ?? '',
  );
  bool _useSkyAngle = true;

  @override
  void dispose() {
    _target.dispose();
    super.dispose();
  }

  /// While a plan runs a move or sync asks first, in a dialog (the Simple
  /// Mode window asks inline). The answer applies only while the question is
  /// still this panel's: another window may have replaced or ended it.
  Future<void> _ask(RotatorTarget target) async {
    final cmd = ref.read(rotatorCommandProvider.notifier);
    final l10n = context.l10n;
    final go = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        content: Text(
          target.isSync
              ? l10n.devicePlanRunningSync
              : l10n.devicePlanRunningRotate,
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(false),
            child: Text(l10n.planCancel),
          ),
          FilledButton(
            onPressed: () => Navigator.of(ctx).pop(true),
            child: Text(
              target.isSync ? l10n.deviceSyncAnyway : l10n.deviceRotateAnyway,
            ),
          ),
        ],
      ),
    );
    if (!mounted) return;
    final now = ref.read(rotatorCommandProvider);
    if (now.phase != DeviceCommandPhase.asking || now.by != this) return;
    if (go ?? false) {
      cmd.confirm();
    } else {
      cmd.cancel();
    }
  }

  void _onCommand(
    DeviceCommand<RotatorTarget>? prev,
    DeviceCommand<RotatorTarget> next,
  ) {
    // Only a command this panel asked for.
    if (next.by != this) return;
    final target = next.target;
    if (next.phase == DeviceCommandPhase.asking &&
        target != null &&
        (prev?.phase != DeviceCommandPhase.asking || prev?.by != this)) {
      unawaited(_ask(target));
    }
    if (next.outcomes == prev?.outcomes) return;
    final messenger = ScaffoldMessenger.of(context);
    final verb = (prev?.target?.isSync ?? false) ? 'sync' : 'move';
    switch (next.refusal) {
      case DeviceCommandRefusal.busy:
        _busy(messenger);
      case DeviceCommandRefusal.failed:
        _failed(messenger, verb, next.error!);
      case DeviceCommandRefusal.didntMove:
        messenger.showSnackBar(
          SnackBar(
            content: Text(context.l10n.deviceCouldntMoveRotator),
            backgroundColor: AraColors.accentError,
          ),
        );
      case null:
        break;
    }
  }

  @override
  Widget build(BuildContext context) {
    ref.listen(rotatorCommandProvider, _onCommand);
    final busy =
        widget.status.isMoving ||
        ref.watch(rotatorCommandProvider).phase == DeviceCommandPhase.moving;
    final s = widget.status;
    if (s.isConnecting) return const Text('Reading…');
    if (s.connectionState == EquipmentConnectionState.error) {
      return const Row(
        children: [
          Icon(Icons.error_outline, color: AraColors.accentError, size: 20),
          SizedBox(width: 8),
          Expanded(child: Text('Rotator read failed — check the device.')),
        ],
      );
    }
    final caps = s.capabilities;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _row('Sky angle', _fmt(s.skyAngleDeg)),
        _row('Mechanical angle', _fmt(s.mechanicalAngleDeg)),
        if (s.isMoving)
          const Padding(
            padding: EdgeInsets.symmetric(vertical: 4),
            child: StatusBlink(
              on: true,
              child: Text(
                'Moving…',
                style: TextStyle(color: AraColors.accentConnected),
              ),
            ),
          ),
        if (caps?.canReverse ?? false)
          Row(
            children: [
              const Text('Reverse direction'),
              const HelpIcon(helpKey: 'eq.rotator.reverse'),
              const Spacer(),
              Switch(
                value: s.reverse,
                onChanged: s.isMoving ? null : (v) => _setReverse(v),
              ),
            ],
          ),
        const SizedBox(height: 8),
        Row(
          children: [
            SizedBox(
              width: 130,
              child: TextField(
                controller: _target,
                keyboardType: const TextInputType.numberWithOptions(
                  decimal: true,
                ),
                inputFormatters: [
                  FilteringTextInputFormatter.allow(
                    RegExp(r'^[0-9]*\.?[0-9]*$'),
                  ),
                ],
                decoration: const InputDecoration(
                  isDense: true,
                  labelText: 'Angle (°)',
                  helperText: '0–360',
                ),
              ),
            ),
            const SizedBox(width: 12),
            FilledButton(
              onPressed: busy ? null : _move,
              child: const Text('Move'),
            ),
            const SizedBox(width: 8),
            OutlinedButton(
              onPressed: busy ? null : _sync,
              child: const Text('Sync'),
            ),
            HelpIcon(helpKey: 'eq.rotator.move', device: s.name),
          ],
        ),
        Row(
          children: [
            AdaptiveCheckbox(
              value: _useSkyAngle,
              onChanged: (v) => setState(() => _useSkyAngle = v ?? true),
            ),
            const Text('Move to sky angle (off = mechanical)'),
            const HelpIcon(helpKey: 'eq.rotator.sky_angle'),
          ],
        ),
      ],
    );
  }

  Widget _row(String label, String value) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 2),
    child: Row(
      children: [
        Expanded(child: Text(label)),
        Text(value),
      ],
    ),
  );

  static String _fmt(double? deg) =>
      deg == null ? '—' : '${deg.toStringAsFixed(1)}°';

  // Parse + range-check [0, 360); returns null (with a SnackBar) on bad input.
  double? _parseAngle(ScaffoldMessengerState messenger) {
    final v = double.tryParse(_target.text.trim());
    if (v == null || v < 0 || v >= 360) {
      messenger.showSnackBar(
        const SnackBar(content: Text('Enter an angle in 0–360.')),
      );
      return null;
    }
    return v;
  }

  void _move() {
    final angle = _parseAngle(ScaffoldMessenger.of(context));
    if (angle == null) return;
    ref
        .read(rotatorCommandProvider.notifier)
        .request(
          RotatorTarget.move(angle, useSkyAngle: _useSkyAngle),
          by: this,
        );
  }

  void _sync() {
    final angle = _parseAngle(ScaffoldMessenger.of(context));
    if (angle == null) return;
    ref
        .read(rotatorCommandProvider.notifier)
        .request(RotatorTarget.sync(angle), by: this);
  }

  Future<void> _setReverse(bool reverse) async {
    final messenger = ScaffoldMessenger.of(context);
    await _run(
      messenger,
      "set reverse",
      () => ref.read(rotatorProvider.notifier).setReverse(reverse),
    );
  }

  Future<void> _run(
    ScaffoldMessengerState messenger,
    String verb,
    Future<bool> Function() action,
  ) async {
    try {
      if (!await action()) _busy(messenger);
    } catch (e) {
      _failed(messenger, verb, e);
    }
  }

  void _busy(ScaffoldMessengerState messenger) => messenger.showSnackBar(
    const SnackBar(content: Text('Another action is still in progress.')),
  );

  void _failed(ScaffoldMessengerState messenger, String verb, Object e) =>
      messenger.showSnackBar(
        SnackBar(
          content: Text("Couldn't $verb: ${failureReason(context.l10n, e)}"),
          backgroundColor: AraColors.accentError,
        ),
      );
}
