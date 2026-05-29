import 'package:flutter_riverpod/flutter_riverpod.dart';

/// §52.1 connection-lifecycle defaults: which equipment device types
/// auto-connect when the daemon boots. Phase 12h.2-equipment-connect holds
/// state in memory; 12h.2b wires `/api/v1/profile/equipment-connection`
/// for daemon round-trip.

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
}

class EquipmentConnectionSettings {
  // Single map keyed by device type. Defaults match prior panel stubs:
  // camera + flat + safety on; dome + weather + guider off (the user usually
  // wants to connect those manually because they involve dome shutter actuation
  // and PHD2 startup).
  final Map<EquipmentDeviceType, bool> autoConnectOnBoot;

  const EquipmentConnectionSettings({
    this.autoConnectOnBoot = const {
      EquipmentDeviceType.camera: true,
      EquipmentDeviceType.mount: true,
      EquipmentDeviceType.focuser: true,
      EquipmentDeviceType.filterWheel: true,
      EquipmentDeviceType.rotator: true,
      EquipmentDeviceType.guider: false,
      EquipmentDeviceType.flatPanel: true,
      EquipmentDeviceType.dome: false,
      EquipmentDeviceType.weather: false,
      EquipmentDeviceType.safetyMonitor: true,
    },
  });

  bool autoConnect(EquipmentDeviceType t) => autoConnectOnBoot[t] ?? false;

  EquipmentConnectionSettings copyWithAutoConnect(
      EquipmentDeviceType t, bool v) {
    return EquipmentConnectionSettings(
      autoConnectOnBoot: {...autoConnectOnBoot, t: v},
    );
  }
}

class EquipmentConnectionNotifier
    extends Notifier<EquipmentConnectionSettings> {
  @override
  EquipmentConnectionSettings build() => const EquipmentConnectionSettings();

  void setAutoConnect(EquipmentDeviceType t, bool v) =>
      state = state.copyWithAutoConnect(t, v);
}

final equipmentConnectionProvider = NotifierProvider<
    EquipmentConnectionNotifier,
    EquipmentConnectionSettings>(EquipmentConnectionNotifier.new);
