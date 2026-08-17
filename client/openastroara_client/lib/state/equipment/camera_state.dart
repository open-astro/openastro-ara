import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/camera_status.dart';
import '../../models/switch_device.dart';
import '../settings/equipment_connection_state.dart';
import 'switch_state.dart';
import '../../models/server.dart';
import '../../services/equipment_device_api.dart';
import '../saved_server_state.dart';
import 'equipment_device_state.dart';

/// Builds an [EquipmentDeviceClient] for the Camera on a server. Overridable in
/// tests so a pure fake can be injected. (Distinct from `cameraGeometryApi`, which
/// reads the same endpoint only for the Optics-tab sensor geometry.)
final cameraStatusApiFactoryProvider =
    Provider<EquipmentDeviceClient<CameraStatus> Function(AraServer)>(
      (ref) =>
          (server) => EquipmentDeviceApi<CameraStatus>(
            server,
            path: 'camera',
            fromJson: CameraStatus.fromJson,
          ),
    );

/// Camera client bound to the **active** server, or `null` when none is saved.
final cameraStatusApiProvider = Provider<EquipmentDeviceClient<CameraStatus>?>((
  ref,
) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(cameraStatusApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// Live camera status for the active server (or `null` when none is connected),
/// plus cooler control. Connect/disconnect + the liveness/busy poll come from the
/// generic core.
class CameraStatusNotifier extends EquipmentDeviceNotifier<CameraStatus> {
  @override
  EquipmentDeviceType get deviceType => EquipmentDeviceType.camera;

  @override
  EquipmentDeviceClient<CameraStatus>? watchClient() =>
      ref.watch(cameraStatusApiProvider);

  @override
  EquipmentDeviceClient<CameraStatus>? readClient() =>
      ref.read(cameraStatusApiProvider);

  /// Turn the cooler on/off and, when on, set the target CCD temperature (°C).
  Future<bool> setCooler(bool enabled, {double? targetTemperatureC}) =>
      performAction((api) async {
        await api.command('cooler', {
          'enabled': enabled,
          'target_temperature_c': targetTemperatureC,
        });
        // §25.5.6 — the cooling fan must follow the cooler (it vents the TEC's
        // heat sink). Centralized HERE (the single cooler entry point both
        // panels use) so the fan write happens exactly once per transition —
        // a widget-level listener would fire once per mounted CoolerControls
        // (Settings + Imaging are both alive in the tab IndexedStack) and
        // duplicate the hardware write. A failed fan-sync propagates through
        // performAction, so the caller's error handling surfaces it.
        await _syncFanPort(enabled);
      });

  /// Sets the bridge's ToupTek Thermal-Switch Fan port to match the cooler
  /// state (on → fan on, off → fan off). No-op when no connected switch
  /// exposes a writable "Fan" port.
  Future<void> _syncFanPort(bool cooling) async {
    // Await the list: on first use the provider is AsyncLoading, and a
    // synchronous read would see an empty list and skip the fan entirely.
    final List<SwitchDevice> switches;
    try {
      switches = await ref.read(switchListProvider.future);
    } catch (_) {
      return; // no switch list available — nothing to sync
    }
    for (final device in switches) {
      if (!device.isConnected) continue;
      for (final port in device.ports) {
        if (port.name == 'Fan' && port.canWrite) {
          await ref.read(switchListProvider.notifier).setValue(
                deviceId: device.deviceId,
                portId: port.id,
                value: cooling ? 1.0 : 0.0,
              );
          return;
        }
      }
    }
  }

  /// §25.5.5 — select a readout mode by index into capabilities.readoutModes.
  Future<bool> setReadoutMode(int modeIndex) => performAction(
    (api) => api.command('readoutmode', {'mode_index': modeIndex}),
  );

}

final cameraStatusProvider =
    AsyncNotifierProvider<CameraStatusNotifier, CameraStatus?>(
      CameraStatusNotifier.new,
    );
