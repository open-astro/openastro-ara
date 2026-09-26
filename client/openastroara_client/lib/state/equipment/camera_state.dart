import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/camera_status.dart';
import '../settings/equipment_connection_state.dart';
import '../../models/server.dart';
import '../../models/switch_device.dart';
import '../../services/equipment_device_api.dart';
import '../saved_server_state.dart';
import 'equipment_device_state.dart';
import 'switch_state.dart';

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
  Future<bool> setCooler(bool enabled, {double? targetTemperatureC}) async {
    // §25.5.6 / #1065 / #1076 — the cooling fan follows the cooler DAEMON-side:
    // the server starts the bridge's Thermal-Switch Fan port BEFORE a cooler-on
    // (a fan that cannot be started refuses to start cooling, a 409 rendered
    // via the error detail) and stops it after a cooler-off (a failed fan-off
    // is an equipment fault in the notification center).
    final performed = await performAction((api) => api.command('cooler', {
          'enabled': enabled,
          'target_temperature_c': targetTemperatureC,
        }));
    if (performed && await _hasKnownFanPort()) {
      // The switch list is pull-on-demand (no value push from the daemon), so
      // re-read it once the cooler command landed: FanSwitchRow and the
      // Switches panel then show the fan value the daemon just wrote instead
      // of going stale until the next visit. Only when the last read showed a
      // fan port at all (most rigs have none — no extra GET for them).
      // Best-effort — a failed re-read must not turn a committed cooler
      // change into an error.
      try {
        await ref.read(switchListProvider.notifier).refresh();
      } catch (_) {
        // The list keeps its last read; the cooler change itself succeeded.
      }
    }
    return performed;
  }

  /// Awaits the list (on first use the provider is AsyncLoading and a
  /// synchronous read would see an empty list and skip the re-read), BOUNDED:
  /// Riverpod 3 auto-retries a failing provider and `.future` stays pending
  /// across retries, so an unreachable switch list must not hang the cooler
  /// toggle. Unknown reads as "no fan port" — no extra GET.
  Future<bool> _hasKnownFanPort() async {
    try {
      final switches = await ref
          .read(switchListProvider.future)
          .timeout(const Duration(seconds: 2));
      return findThermalSwitchFanPort(switches) != null;
    } catch (_) {
      return false;
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
