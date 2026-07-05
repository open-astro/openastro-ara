import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/equipment_device_status.dart';
import '../../models/switch_device.dart';
import '../../state/equipment/camera_state.dart';
import '../../state/equipment/dome_state.dart';
import '../../state/equipment/filter_wheel_state.dart';
import '../../state/equipment/flat_panel_state.dart';
import '../../state/equipment/focuser_state.dart';
import '../../state/equipment/mount_state.dart';
import '../../state/equipment/rotator_state.dart';
import '../../state/equipment/safety_monitor_state.dart';
import '../../state/equipment/switch_state.dart';
import '../../state/equipment/weather_state.dart';
import '../../state/settings/settings_nav.dart';
import '../../state/ws/ws_providers.dart';
import '../equipment_chip.dart';
import '../status_indicator.dart';
import 'guider_chip.dart';

/// Maps a single-instance device's resolved status to the top-bar chip's dot.
/// Pure → unit-testable. Connected → green (amber while a move/op is in flight),
/// connecting → info, error → error, disconnected/unknown/null → grey.
StatusLevel equipmentStatusLevel(EquipmentDeviceStatus? status) {
  if (status == null) return StatusLevel.disconnected;
  return switch (status.connectionState) {
    EquipmentConnectionState.connected =>
      status.isBusy ? StatusLevel.busy : StatusLevel.connected,
    EquipmentConnectionState.connecting => StatusLevel.info,
    EquipmentConnectionState.error => StatusLevel.error,
    EquipmentConnectionState.disconnected ||
    EquipmentConnectionState.unknown =>
      StatusLevel.disconnected,
  };
}

/// Maps a device's async status (any [EquipmentDeviceStatus] subtype) to the
/// chip dot: loading → info (a connect/poll is in flight), error → error,
/// resolved → [equipmentStatusLevel]. Generic so each call site can hand it the
/// device's own `AsyncValue<XStatus?>` without an unsafe cast.
StatusLevel equipmentChipLevel<T extends EquipmentDeviceStatus>(
        AsyncValue<T?> async) =>
    async.when(
      data: (s) => equipmentStatusLevel(s),
      loading: () => StatusLevel.info,
      error: (_, _) => StatusLevel.error,
    );

/// The §25.3 top-bar equipment chips, in device-type order: each shows live
/// connection status (green dot when connected) and, on tap, jumps to that
/// device's Settings panel to connect/control it. GUIDE keeps its bespoke
/// [GuiderChip] (own status type + PHD2 dialog); SW is multi-instance.
class TopEquipmentChips extends StatelessWidget {
  const TopEquipmentChips({super.key});

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        EquipmentStatusChip(
            icon: Icons.camera_alt,
            label: 'CAM',
            panelId: 'eq.camera',
            watchLevel: (ref) => equipmentChipLevel(ref.watch(cameraStatusProvider))),
        EquipmentStatusChip(
            icon: Icons.filter_alt,
            label: 'FW',
            panelId: 'eq.filterwheel',
            watchLevel: (ref) => equipmentChipLevel(ref.watch(filterWheelProvider))),
        EquipmentStatusChip(
            icon: Icons.adjust,
            label: 'FOC',
            panelId: 'eq.focuser',
            watchLevel: (ref) => equipmentChipLevel(ref.watch(focuserProvider))),
        EquipmentStatusChip(
            icon: Icons.public,
            label: 'MOUNT',
            panelId: 'eq.mount',
            watchLevel: (ref) => equipmentChipLevel(ref.watch(mountProvider))),
        EquipmentStatusChip(
            icon: Icons.rotate_right,
            label: 'ROT',
            panelId: 'eq.rotator',
            watchLevel: (ref) => equipmentChipLevel(ref.watch(rotatorProvider))),
        const GuiderChip(),
        EquipmentStatusChip(
            icon: Icons.wb_sunny,
            label: 'FLAT',
            panelId: 'eq.flat',
            watchLevel: (ref) =>
                equipmentChipLevel(ref.watch(flatPanelProvider))),
        const SwitchStatusChip(icon: Icons.power, label: 'SW'),
        EquipmentStatusChip(
            icon: Icons.cloud_outlined,
            label: 'WX',
            panelId: 'eq.weather',
            watchLevel: (ref) => equipmentChipLevel(ref.watch(weatherProvider))),
        EquipmentStatusChip(
            icon: Icons.shield_outlined,
            label: 'SAFE',
            panelId: 'eq.safety',
            watchLevel: (ref) => equipmentChipLevel(ref.watch(safetyMonitorProvider))),
        EquipmentStatusChip(
            icon: Icons.home_outlined,
            label: 'DOME',
            panelId: 'eq.dome',
            watchLevel: (ref) => equipmentChipLevel(ref.watch(domeProvider))),
      ],
    );
  }
}

/// A top-bar equipment status chip: [watchLevel] watches the device's status
/// provider (called in build, so the watch registers a dependency) and maps it
/// to the dot; on tap it jumps to the device's Settings panel. One widget serves
/// every single-instance device — mirroring [GuiderChip], which is bespoke only
/// because the guider has its own status type + PHD2 dialog.
class EquipmentStatusChip extends ConsumerWidget {
  final IconData icon;
  final String label;
  final String panelId;
  final StatusLevel Function(WidgetRef ref) watchLevel;

  const EquipmentStatusChip({
    super.key,
    required this.icon,
    required this.label,
    required this.panelId,
    required this.watchLevel,
  });

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    // Stale-guard: while the server link is down the last device status can't be
    // trusted — show the dot grey (disconnected) instead of a false green.
    final level = ref.watch(serverLinkUpProvider)
        ? watchLevel(ref)
        : StatusLevel.disconnected;
    return EquipmentChip(
      icon: icon,
      label: label,
      status: level,
      onTap: () => openSettingsPanel(ref, panelId),
    );
  }
}

/// Maps the multi-instance Switch list to the chip dot: busy (amber) while an
/// action — a connect/disconnect or a port write — is in flight ([acting], the
/// §25.3 derived signal, since the daemon reports no per-port actuation state),
/// else connected (green) when *any* switch is connected, else error (red) when
/// any device is in an error state (a failed auto-connect — matches how the
/// single-instance chips surface a device error, so SW turns red alongside them
/// instead of reading a misleading grey), else disconnected; loading → info,
/// list-fetch error → error. Pure → unit-testable.
StatusLevel switchChipLevel(AsyncValue<List<SwitchDevice>> async,
        {bool acting = false}) =>
    async.when(
      data: (list) => acting
          ? StatusLevel.busy
          : list.any((s) => s.isConnected)
              ? StatusLevel.connected
              : list.any((s) => s.connectionState == SwitchConnectionState.error)
                  ? StatusLevel.error
                  : StatusLevel.disconnected,
      loading: () => StatusLevel.info,
      error: (_, _) => StatusLevel.error,
    );

/// The Switch top-bar chip. Switch is multi-instance (a list of devices, each
/// with its own connection), so it shows connected when *any* switch is connected.
class SwitchStatusChip extends ConsumerWidget {
  final IconData icon;
  final String label;
  const SwitchStatusChip({super.key, required this.icon, required this.label});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final level = ref.watch(serverLinkUpProvider)
        ? switchChipLevel(ref.watch(switchListProvider),
            acting: ref.watch(switchActingProvider))
        : StatusLevel.disconnected;
    return EquipmentChip(
      icon: icon,
      label: label,
      status: level,
      onTap: () => openSettingsPanel(ref, 'eq.switch'),
    );
  }
}
