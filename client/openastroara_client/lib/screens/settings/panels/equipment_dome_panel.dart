import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../widgets/status_indicator.dart' show StatusBlink;
import '../../../models/dome_settings.dart';
import '../../../models/dome_status.dart';
import '../../../models/equipment_device_status.dart';
import '../../../state/equipment/dome_state.dart';
import '../../../state/settings/dome_settings_state.dart';
import '../../../state/settings/equipment_connection_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/help_icon.dart';
import '../../../widgets/equipment/equipment_connection_card.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';
import '../../../util/friendly_error.dart';
import '../../../l10n/l10n.dart';

/// §37.4 Dome panel. Shows the connected dome's live shutter / azimuth / park
/// state with shutter, slew, and park controls (each gated on the device's
/// capabilities) via the shared connection card.
class EquipmentDomePanel extends ConsumerWidget {
  const EquipmentDomePanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connection = ref.watch(equipmentConnectionProvider);
    final n = ref.read(equipmentConnectionProvider.notifier);
    final status = ref.watch(domeProvider);
    final notifier = ref.read(domeProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Connection'),
        EquipmentConnectionCard<DomeStatus>(
          status: status,
          deviceType: EquipmentDeviceType.dome,
          deviceTypeLabel: 'dome',
          emptyLabel: 'No dome connected.',
          onConnect: notifier.connect,
          onDisconnect: notifier.disconnect,
          onReconnect: notifier.reconnect,
          onForget: notifier.forget,
          onRetry: notifier.refresh,
          connectedBody: (context, s) => _DomeBody(status: s),
        ),
        SettingsSwitchRow(
          label: 'Auto-connect on boot',
          helpKey: 'eq.auto_connect_on_boot',
          value: connection.autoConnect(EquipmentDeviceType.dome),
          onChanged: (v) => n.setAutoConnect(EquipmentDeviceType.dome, v),
          hint: 'Off by default — dome connect actuates shutter',
        ),
        const SettingsSectionHeader('Following the mount'),
        const _DomeFollowSection(),
      ],
    );
  }
}

/// #1689 — the dome follows the mount: the switch, then the geometry that
/// places the slit right (the scope's place in the dome and the mount's axis
/// offsets), the tolerance and whether the dome also turns during a mount slew.
/// The daemon refuses a scope outside the dome when it saves.
class _DomeFollowSection extends ConsumerWidget {
  const _DomeFollowSection();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final settings = ref.watch(domeSettingsProvider).value;
    if (settings == null) return const Text('Reading…');
    final dome = ref.watch(domeProvider).value;
    final canTurn = dome?.capabilities?.canSetAzimuth ?? false;

    Future<void> save(DomeSettings next) async {
      final messenger = ScaffoldMessenger.of(context);
      final l10n = context.l10n;
      try {
        await ref.read(domeSettingsProvider.notifier).save(next);
      } catch (e) {
        messenger.showSnackBar(
          SnackBar(
            content: Text("Couldn't save the dome: ${failureReason(l10n, e)}"),
            backgroundColor: AraColors.accentError,
          ),
        );
      }
    }

    Future<void> follow(bool on) async {
      final messenger = ScaffoldMessenger.of(context);
      final l10n = context.l10n;
      try {
        await ref.read(domeProvider.notifier).followMount(on);
        ref.invalidate(domeSettingsProvider);
      } catch (e) {
        messenger.showSnackBar(
          SnackBar(
            content: Text(
              "Couldn't change following: ${failureReason(l10n, e)}",
            ),
            backgroundColor: AraColors.accentError,
          ),
        );
      }
    }

    Widget number(
      String label,
      double value,
      DomeSettings Function(double) apply, {
      String? helpKey,
    }) => EditableNumberRow(
      label: label,
      helpKey: helpKey,
      currentValue: _num(value),
      getCanonical: () => _num(value),
      parse: (text) {
        final v = double.tryParse(text.trim());
        if (v != null && v != value) save(apply(v));
      },
    );

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Text(
            'With this on, Ara turns the dome so its slit stays in front of '
            'the telescope: after a mount slew, after a meridian flip, and '
            'while tracking whenever the slit falls behind by more than the '
            'tolerance. Turning the dome by hand, parking it, sending it home '
            'or disconnecting it turns this off. The dome must be able to '
            'turn to an azimuth.',
            style: Theme.of(context).textTheme.bodySmall
                ?.copyWith(color: AraColors.textSecondary),
          ),
        ),
        SettingsSwitchRow(
          label: 'Follow the mount',
          helpKey: 'eq.dome.follow',
          value: settings.followMount,
          onChanged: canTurn || settings.followMount ? follow : (_) {},
          hint: canTurn || settings.followMount
              ? null
              : 'Connect a dome that can turn to an azimuth first',
        ),
        const SettingsSectionHeader('Geometry'),
        SettingsDropdownRow<String>(
          label: 'Mount type',
          helpKey: 'eq.dome.geometry',
          value: settings.mountType,
          items: const {
            'equatorial': 'German equatorial',
            'fork_on_wedge': 'Fork or alt-az (on a wedge)',
          },
          onChanged: (v) {
            if (v != null && v != settings.mountType) {
              save(settings.copyWith(mountType: v));
            }
          },
        ),
        number(
          'Dome radius (mm)',
          settings.domeRadiusMm,
          (v) => settings.copyWith(domeRadiusMm: v),
          helpKey: 'eq.dome.geometry',
        ),
        number(
          'Scope north of dome centre (mm)',
          settings.scopePositionNorthSouthMm,
          (v) => settings.copyWith(scopePositionNorthSouthMm: v),
        ),
        number(
          'Scope east of dome centre (mm)',
          settings.scopePositionEastWestMm,
          (v) => settings.copyWith(scopePositionEastWestMm: v),
        ),
        number(
          'Scope above dome centre (mm)',
          settings.scopePositionUpDownMm,
          (v) => settings.copyWith(scopePositionUpDownMm: v),
        ),
        if (settings.isEquatorial) ...[
          number(
            'Mount axis length (mm)',
            settings.gemAxisMm,
            (v) => settings.copyWith(gemAxisMm: v),
          ),
          number(
            'Mount lateral offset (mm)',
            settings.lateralAxisMm,
            (v) => settings.copyWith(lateralAxisMm: v),
          ),
          number(
            'Dec axis horizontal offset (mm)',
            settings.decOffsetHorizontalMm,
            (v) => settings.copyWith(decOffsetHorizontalMm: v),
          ),
        ],
        number(
          'Azimuth tolerance (°)',
          settings.azimuthToleranceDegrees,
          (v) => settings.copyWith(azimuthToleranceDegrees: v),
          helpKey: 'eq.dome.tolerance',
        ),
        SettingsSwitchRow(
          label: 'Turn during mount slews',
          helpKey: 'eq.dome.tolerance',
          value: settings.synchronizeDuringMountSlew,
          onChanged: (v) =>
              save(settings.copyWith(synchronizeDuringMountSlew: v)),
          hint: 'Off: the dome turns when the mount has finished slewing',
        ),
      ],
    );
  }

  static String _num(double v) =>
      v == v.roundToDouble() ? v.toStringAsFixed(0) : v.toString();
}

/// The connected dome's live body: shutter/azimuth/home/park state + shutter,
/// slew, and park controls gated on capabilities.
class _DomeBody extends ConsumerStatefulWidget {
  final DomeStatus status;
  const _DomeBody({required this.status});

  @override
  ConsumerState<_DomeBody> createState() => _DomeBodyState();
}

class _DomeBodyState extends ConsumerState<_DomeBody> {
  late final TextEditingController _az = TextEditingController(
    text: widget.status.azimuthDeg?.toStringAsFixed(0) ?? '',
  );

  @override
  void dispose() {
    _az.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final s = widget.status;
    if (s.isConnecting) return const Text('Reading…');
    if (s.connectionState == EquipmentConnectionState.error) {
      return const Row(
        children: [
          Icon(Icons.error_outline, color: AraColors.accentError, size: 20),
          SizedBox(width: 8),
          Expanded(child: Text('Dome read failed — check the device.')),
        ],
      );
    }
    final caps = s.capabilities;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _row('Shutter', s.shutterOpen ? 'Open' : 'Closed'),
        _row(
          'Azimuth',
          s.azimuthDeg == null ? '—' : '${s.azimuthDeg!.toStringAsFixed(0)}°',
        ),
        _row('At home', s.atHome ? 'Yes' : 'No'),
        _row('Parked', s.parked ? 'Yes' : 'No'),
        if (s.isBusy)
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 4),
            // DESIGN.md status rule: slewing is working (blinking green), the
            // shutter is on its way between open and closed (blue).
            child: StatusBlink(
              on: s.runtimeState == 'slewing',
              child: Text(
                s.runtimeState == 'slewing'
                    ? (s.following ? 'Following the mount…' : 'Slewing…')
                    : 'Shutter moving…',
                style: TextStyle(
                  color: s.runtimeState == 'slewing'
                      ? AraColors.accentConnected
                      : AraColors.accentInfo,
                ),
              ),
            ),
          ),
        const SizedBox(height: 8),
        if (caps?.canSetShutter ?? false)
          Row(
            children: [
              OutlinedButton(
                onPressed: s.isBusy
                    ? null
                    : () => _run(
                        'open shutter',
                        ref.read(domeProvider.notifier).openShutter,
                      ),
                child: const Text('Open shutter'),
              ),
              const SizedBox(width: 8),
              OutlinedButton(
                onPressed: s.isBusy
                    ? null
                    : () => _run(
                        'close shutter',
                        ref.read(domeProvider.notifier).closeShutter,
                      ),
                child: const Text('Close shutter'),
              ),
              const HelpIcon(helpKey: 'eq.dome.shutter'),
            ],
          ),
        Padding(
          padding: const EdgeInsets.only(top: 4),
          child: Wrap(
            spacing: 8,
            runSpacing: 4,
            children: [
              if (caps?.canPark ?? false)
                OutlinedButton(
                  onPressed: s.isBusy
                      ? null
                      : () =>
                            _run('park', ref.read(domeProvider.notifier).park),
                  child: const Text('Park'),
                ),
              if (caps?.canSetPark ?? false)
                OutlinedButton(
                  onPressed: s.isBusy
                      ? null
                      : () => _run(
                          'set park position',
                          ref.read(domeProvider.notifier).setPark,
                        ),
                  child: const Text('Set park here'),
                ),
              if (caps?.canFindHome ?? false)
                OutlinedButton(
                  onPressed: s.isBusy
                      ? null
                      : () => _run(
                          'find home',
                          ref.read(domeProvider.notifier).findHome,
                        ),
                  child: const Text('Find home'),
                ),
              // Deliberately NOT disabled while busy — stopping a moving dome is
              // exactly when you need it (§57 panic-stop shape).
              OutlinedButton(
                onPressed: () =>
                    _run('stop', ref.read(domeProvider.notifier).abortSlew),
                child: const Text('Stop'),
              ),
            ],
          ),
        ),
        if (caps?.canSetAzimuth ?? false)
          Padding(
            padding: const EdgeInsets.only(top: 8),
            child: Row(
              children: [
                SizedBox(
                  width: 130,
                  child: TextField(
                    controller: _az,
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
                      labelText: 'Azimuth (°)',
                      helperText: '0 to <360',
                    ),
                  ),
                ),
                const SizedBox(width: 12),
                FilledButton(
                  onPressed: s.isBusy ? null : _slew,
                  child: const Text('Slew'),
                ),
                if (caps?.canSyncAzimuth ?? false) ...[
                  const SizedBox(width: 8),
                  OutlinedButton(
                    onPressed: s.isBusy ? null : _sync,
                    child: const Text('Sync'),
                  ),
                ],
              ],
            ),
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

  Future<void> _slew() async {
    final messenger = ScaffoldMessenger.of(context);
    // ASCOM azimuth is [0, 360) — 360° ≡ 0° (North), so 360 is rejected (matches
    // the daemon's IsAzimuthOutOfRange).
    final v = double.tryParse(_az.text.trim());
    if (v == null || v < 0 || v >= 360) {
      messenger.showSnackBar(
        const SnackBar(
          content: Text('Enter an azimuth from 0 up to (not including) 360.'),
        ),
      );
      return;
    }
    await _run('slew', () => ref.read(domeProvider.notifier).slew(v));
  }

  Future<void> _sync() async {
    final messenger = ScaffoldMessenger.of(context);
    // Same [0, 360) rule as _slew — sync re-labels the current position, no motion.
    final v = double.tryParse(_az.text.trim());
    if (v == null || v < 0 || v >= 360) {
      messenger.showSnackBar(
        const SnackBar(
          content: Text('Enter an azimuth from 0 up to (not including) 360.'),
        ),
      );
      return;
    }
    await _run('sync', () => ref.read(domeProvider.notifier).syncToAzimuth(v));
  }

  Future<void> _run(String verb, Future<bool> Function() action) async {
    final messenger = ScaffoldMessenger.of(context);
    final l10n = context.l10n;
    try {
      final performed = await action();
      if (!performed) {
        messenger.showSnackBar(
          const SnackBar(content: Text('Another action is still in progress.')),
        );
      }
    } catch (e) {
      messenger.showSnackBar(
        SnackBar(
          content: Text("Couldn't $verb: ${failureReason(l10n, e)}"),
          backgroundColor: AraColors.accentError,
        ),
      );
    }
  }
}
