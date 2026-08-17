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
  Future<bool> setCooler(bool enabled, {double? targetTemperatureC}) async {
    // §25.5.6 — the cooling fan must follow the cooler (it vents the TEC's
    // heat sink). Centralized HERE (the single cooler entry point both
    // panels use) so the fan write happens exactly once per transition — a
    // widget-level listener would fire once per mounted CoolerControls
    // (Settings + Imaging are both alive in the tab IndexedStack) and
    // duplicate the hardware write.
    //
    // Order matters: run the cooler command + let performAction refresh the
    // status FIRST, so the UI reflects the (committed) cooler change even if
    // the fan sync then fails. A sync failure throws AFTER the cooler is on —
    // the caller's toast must read as "cooler changed, fan sync failed", not
    // "nothing happened".
    final performed = await performAction((api) => api.command('cooler', {
          'enabled': enabled,
          'target_temperature_c': targetTemperatureC,
        }));
    if (!performed) return false;
    await _syncFanPort(enabled);
    return true;
  }

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
      // At this point we don't yet know whether a fan-capable switch even
      // exists — most rigs have none, and the switch list comes from a
      // separate endpoint that can fail for reasons unrelated to cooling.
      // Treat a list-read failure like "no matching device" (a no-op) rather
      // than alarming every cooler toggle; a real fan write failure below is
      // still surfaced.
      return;
    }
    // Shared lookup with FanSwitchRow (findThermalSwitchFanPort): scoped to
    // the bridge's ToupTek Thermal Switch so an unrelated switch with a port
    // literally named "Fan" is never actuated, and so the row's interlock and
    // this sync always agree on which device is the cooling fan.
    final fan = findThermalSwitchFanPort(switches);
    if (fan == null) return;
    final written = await ref.read(switchListProvider.notifier).setValue(
          deviceId: fan.device.deviceId,
          portId: fan.port.id,
          value: cooling ? 1.0 : 0.0,
        );
    if (!written) {
      // The switch's own re-entrancy guard dropped the write (another
      // switch action in flight) — a silently-missed fan sync is exactly
      // the safety-relevant gap the interlock exists to prevent. The
      // message states the cooler DID change so the toast isn't read as
      // a full failure.
      // Plain Exception, not StateError: describeEquipmentError strips
      // "Exception: " but StateError's "Bad state:" prefix would leak.
      throw Exception(
          'the cooler is ${cooling ? "on" : "off"}, but the cooling fan '
          'could not be synced (the switch is busy) — check the fan');
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

/// Tri-state cooler flag for the fan-off interlock: `true`/`false` only when
/// the camera status actually RESOLVED (a no-camera state reads as not
/// cooling — no camera connected means no TEC this client started); `null`
/// when it can't be determined, i.e. "cooler state unknown". Awaits the
/// status (pass `ref.read(cameraStatusProvider.future)`) rather than peeking
/// at the AsyncValue, so a merely-uninitialized provider resolves instead of
/// reading as unknown — but BOUNDED: Riverpod 3 auto-retries a failing
/// provider with backoff and `.future` stays pending across retries, so an
/// unreachable camera would otherwise hang the interlock forever.
Future<bool?> coolerOnTriState(Future<CameraStatus?> status) async {
  try {
    final s = await status.timeout(const Duration(seconds: 2));
    return s?.coolerOn ?? false;
  } catch (_) {
    return null;
  }
}

/// §25.5.6 fan-off interlock, shared by [FanSwitchRow] in Settings → Camera
/// and the generic Switches panel — every UI path to the Thermal-Switch Fan
/// port must refuse the same way. Returns `null` when turning the fan off is
/// allowed (cooler known off), else the user-facing refusal message. Fails
/// CLOSED: an unknown cooler state also refuses.
String? fanOffRefusal(bool? coolerOn) {
  if (coolerOn == false) return null;
  return coolerOn == true
      ? 'Turn the cooler off before stopping the fan — cooling with the fan '
          'off can damage the camera.'
      : "The camera's cooler state is unknown — not stopping the fan while "
          'the TEC may be cooling. Check the camera connection and try again.';
}
