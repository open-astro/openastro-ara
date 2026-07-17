import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'settings_sync_mixin.dart';

import '../../services/profile_api.dart';
import '../saved_server_state.dart';

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
    extends Notifier<EquipmentConnectionSettings>
    with SettingsSyncMixin<EquipmentConnectionSettings> {
  @override
  EquipmentConnectionSettings build() {
    // Schedule one-shot hydration after the current build pass. Calling
    // ref.read inside build() synchronously would re-enter the notifier
    // and trip an assertion; Future.microtask defers it cleanly.
    Future.microtask(_tryHydrate);
    return const EquipmentConnectionSettings();
  }

  void setAutoConnect(EquipmentDeviceType t, bool v) {
    state = state.copyWithAutoConnect(t, v);
    // Fire-and-forget PUT. Failures are silent (see class docstring).
    Future.microtask(_tryPersist);
  }

  /// Manual hydrate — exposed for tests that want to inject a mocked api.
  Future<void> hydrateFromServer(ProfileApi api) =>
      hydrateGuarded(() => api.getEquipmentConnection());

  /// Manual persist — exposed for tests + future error-stream wiring.
  Future<EquipmentConnectionSettings> persistToServer(ProfileApi api) =>
      persistGuarded((sent) => api.putEquipmentConnection(sent));

  ProfileApi? _activeApi() {
    final server = ref.read(activeServerProvider);
    return server == null ? null : ProfileApi(server);
  }

  Future<void> _tryHydrate() async {
    final api = _activeApi();
    if (api == null) return;
    try {
      await hydrateGuarded(() => api.getEquipmentConnection());
    } catch (_) {
      // Silent — defaults remain.
    }
  }

  Future<void> _tryPersist() async {
    final api = _activeApi();
    if (api == null) return;
    try {
      // Serialized + fenced through the mixin so rapid toggles can't let an
      // out-of-order PUT echo revert a newer optimistic local update.
      await persistGuarded((sent) => api.putEquipmentConnection(sent));
    } catch (_) {
      // Silent — local optimistic update stays.
    }
  }
}

final equipmentConnectionProvider = NotifierProvider<
    EquipmentConnectionNotifier,
    EquipmentConnectionSettings>(EquipmentConnectionNotifier.new);
