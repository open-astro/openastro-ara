import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/camera_status.dart';
import '../settings/equipment_connection_state.dart';
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
      performAction(
        (api) => api.command('cooler', {
          'enabled': enabled,
          'target_temperature_c': targetTemperatureC,
        }),
      );

  /// §25.5.5 — select a readout mode by index into capabilities.readoutModes.
  Future<bool> setReadoutMode(int modeIndex) => performAction(
    (api) => api.command('readoutmode', {'mode_index': modeIndex}),
  );
}

final cameraStatusProvider =
    AsyncNotifierProvider<CameraStatusNotifier, CameraStatus?>(
      CameraStatusNotifier.new,
    );
