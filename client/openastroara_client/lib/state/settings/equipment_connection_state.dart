import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'loaded_section.dart';

import '../../services/profile_api.dart';

/// §52.1 connection-lifecycle defaults: which equipment device types
/// auto-connect when the daemon boots. Phase 12h.6L wires the daemon
/// round-trip via [ProfileApi] (`/api/v1/profile/equipment-connection`).
///
/// Each of the 10 equipment panels has an auto-connect toggle but no
/// Save button. To keep the existing UX intact, the notifier handles
/// persistence itself: [build] schedules a one-shot hydrate from the
/// daemon, and [setAutoConnect] fires-and-forgets a PUT after the
/// optimistic local update. Save errors are silent (best-effort) — the
/// trade-off is acceptable for v0.0.1 trusted-LAN. Phase 14 can replace
/// the silent failure with an error-stream provider the panels watch
/// for snackbars.

enum EquipmentDeviceType {
  camera,
  mount,
  focuser,
  filterWheel,
  rotator,
  guider,
  flatPanel,
  dome,
  weather,
  safetyMonitor,
  // Multi-instance: unlike the others, several Switch devices can be connected at
  // once (addressed by Alpaca device number). The selection/connect UI handles that.
  switchDevice,
}

class EquipmentConnectionSettings {
  // Single map keyed by device type. EVERY type defaults to auto-connect on:
  // boot-time auto-connect only attempts devices the user actually connected
  // before (the daemon's remembered store), so an all-on default is safe for
  // unconfigured types and a working rig comes back by itself after a reboot.
  final Map<EquipmentDeviceType, bool> autoConnectOnBoot;

  const EquipmentConnectionSettings({
    this.autoConnectOnBoot = const {
      EquipmentDeviceType.camera: true,
      EquipmentDeviceType.mount: true,
      EquipmentDeviceType.focuser: true,
      EquipmentDeviceType.filterWheel: true,
      EquipmentDeviceType.rotator: true,
      EquipmentDeviceType.guider: true,
      EquipmentDeviceType.flatPanel: true,
      EquipmentDeviceType.dome: true,
      EquipmentDeviceType.weather: true,
      EquipmentDeviceType.safetyMonitor: true,
      EquipmentDeviceType.switchDevice: true,
    },
  });

  bool autoConnect(EquipmentDeviceType t) => autoConnectOnBoot[t] ?? true;

  EquipmentConnectionSettings copyWithAutoConnect(
      EquipmentDeviceType t, bool v) {
    return EquipmentConnectionSettings(
      autoConnectOnBoot: {...autoConnectOnBoot, t: v},
    );
  }
}

class EquipmentConnectionNotifier
    extends SelfLoadingSection<EquipmentConnectionSettings> {
  /// Saves on change; throws when the rig did not take it (not loaded, 412,
  /// unreachable) so the panel can say so. ADR 0015: nothing is changed or
  /// sent while the section is not loaded from the rig.
  Future<void> setAutoConnect(EquipmentDeviceType t, bool v) =>
      editAndSave((c) => c.copyWithAutoConnect(t, v));

  @override
  Future<Tagged<EquipmentConnectionSettings>> fetch(ProfileApi api) =>
      api.getEquipmentConnectionSection();

  @override
  Future<Tagged<EquipmentConnectionSettings>> send(
    ProfileApi api,
    EquipmentConnectionSettings value,
    String? etag,
  ) => api.putEquipmentConnectionSection(value, ifMatch: etag);
}

final equipmentConnectionProvider =
    NotifierProvider<
      EquipmentConnectionNotifier,
      LoadedSection<EquipmentConnectionSettings>
    >(EquipmentConnectionNotifier.new);
