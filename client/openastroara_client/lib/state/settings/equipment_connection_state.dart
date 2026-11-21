import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'loaded_section.dart';

import '../../services/profile_api.dart';

/// §52.1 connection-lifecycle defaults: which equipment device types
/// auto-connect when the daemon boots (`/api/v1/profile/equipment-connection`).
///
/// Each equipment panel has an auto-connect switch and no Save button, so a
/// change saves itself (ADR 0015): nothing is shown or sent until the rig's
/// copy is loaded, and [EquipmentConnectionNotifier.setAutoConnect] throws
/// when the rig did not take the change, for the panel to say so.

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
  // Single map keyed by device type, as the rig holds it (ADR 0015: no client
  // default; the daemon's normalizer fills every type).
  final Map<EquipmentDeviceType, bool> autoConnectOnBoot;

  const EquipmentConnectionSettings({required this.autoConnectOnBoot});

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
  /// sent while the section is not loaded from the rig. A failed save puts
  /// the rig's copy back; a 412 keeps the edit (rule 6).
  Future<void> setAutoConnect(EquipmentDeviceType t, bool v) {
    final c = state.value;
    return c == null
        ? editAndSave((c) => c.copyWithAutoConnect(t, v))
        : replaceAndSave(c.copyWithAutoConnect(t, v));
  }

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
