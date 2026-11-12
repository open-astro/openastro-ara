import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/dome_status.dart';
import '../settings/equipment_connection_state.dart';
import '../../models/server.dart';
import '../../services/equipment_device_api.dart';
import '../saved_server_state.dart';
import 'equipment_device_state.dart';

/// Builds an [EquipmentDeviceClient] for the Dome on a server. Overridable in
/// tests so a pure fake can be injected.
final domeApiFactoryProvider =
    Provider<EquipmentDeviceClient<DomeStatus> Function(AraServer)>(
      (ref) =>
          (server) => EquipmentDeviceApi<DomeStatus>(
            server,
            path: 'dome',
            fromJson: DomeStatus.fromJson,
          ),
    );

/// Dome client bound to the **active** server, or `null` when none is saved.
final domeApiProvider = Provider<EquipmentDeviceClient<DomeStatus>?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(domeApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// Live dome status for the active server (or `null` when none is connected),
/// plus shutter / slew / park controls. Connect/disconnect + the liveness/busy
/// poll come from the generic core.
class DomeNotifier extends EquipmentDeviceNotifier<DomeStatus> {
  @override
  EquipmentDeviceType get deviceType => EquipmentDeviceType.dome;

  @override
  EquipmentDeviceClient<DomeStatus>? watchClient() =>
      ref.watch(domeApiProvider);

  @override
  EquipmentDeviceClient<DomeStatus>? readClient() => ref.read(domeApiProvider);

  /// Read every 1.5 s for up to 5 min of a move, not ~60 s: a shutter or a
  /// long turn outlasts that, and the Simple Mode Dome window times the
  /// shutter from its reads (#1662). A dome stuck moving still drops to the
  /// liveness poll after it.
  @override
  int get settlePollCap => 200;

  Future<bool> openShutter() =>
      performAction((api) => api.command('shutter/open'));

  Future<bool> closeShutter() =>
      performAction((api) => api.command('shutter/close'));

  /// Rotate the dome to [targetAzimuthDeg] (0–360).
  Future<bool> slew(double targetAzimuthDeg) => performAction(
    (api) => api.command('slew', {'target_azimuth_deg': targetAzimuthDeg}),
  );

  /// Turn "the dome follows the mount" on or off (#1689). Throws the daemon's
  /// reason when it can't be turned on (no settable azimuth, no dome radius).
  Future<bool> followMount(bool on) =>
      performAction((api) => api.command('follow', {'enabled': on}));

  Future<bool> park() => performAction((api) => api.command('park'));

  // §25.5.5 — the remaining dome motions (each button gates on its capability).
  Future<bool> findHome() => performAction((api) => api.command('findhome'));

  /// §57-style stop for a rotating dome — always available while connected.
  Future<bool> abortSlew() => performAction((api) => api.command('abort'));

  /// Store the CURRENT position as the park position (no motion).
  Future<bool> setPark() => performAction((api) => api.command('setpark'));

  /// Re-label the current physical position as [azimuthDeg] (no motion).
  Future<bool> syncToAzimuth(double azimuthDeg) => performAction(
    (api) => api.command('sync', {'target_azimuth_deg': azimuthDeg}),
  );
}

final domeProvider = AsyncNotifierProvider<DomeNotifier, DomeStatus?>(
  DomeNotifier.new,
);
