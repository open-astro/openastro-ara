import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/rotator_status.dart';
import '../settings/equipment_connection_state.dart';
import '../../models/server.dart';
import '../../services/equipment_device_api.dart';
import '../saved_server_state.dart';
import 'equipment_device_state.dart';

/// Builds an [EquipmentDeviceClient] for the Rotator on a server. Overridable in
/// tests so a pure fake can be injected.
final rotatorApiFactoryProvider =
    Provider<EquipmentDeviceClient<RotatorStatus> Function(AraServer)>(
      (ref) =>
          (server) => EquipmentDeviceApi<RotatorStatus>(
            server,
            path: 'rotator',
            fromJson: RotatorStatus.fromJson,
          ),
    );

/// Rotator client bound to the **active** server, or `null` when none is saved.
final rotatorApiProvider = Provider<EquipmentDeviceClient<RotatorStatus>?>((
  ref,
) {
  final server = ref.watch(
    savedServersProvider.select(
      (async) => async.maybeWhen(
        data: (list) => list.isEmpty ? null : list.last,
        orElse: () => null,
      ),
    ),
  );
  if (server == null) return null;
  final api = ref.watch(rotatorApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// Live rotator status for the active server (or `null` when none is connected),
/// plus move / reverse / sync controls. Connect/disconnect + the liveness/busy
/// poll come from the generic core.
class RotatorNotifier extends EquipmentDeviceNotifier<RotatorStatus> {
  @override
  EquipmentDeviceType get deviceType => EquipmentDeviceType.rotator;

  @override
  EquipmentDeviceClient<RotatorStatus>? watchClient() =>
      ref.watch(rotatorApiProvider);

  @override
  EquipmentDeviceClient<RotatorStatus>? readClient() =>
      ref.read(rotatorApiProvider);

  /// Rotate to [targetAngleDeg] (0–360). [useSkyAngle] moves to the
  /// offset-corrected sky angle; otherwise the raw mechanical angle.
  Future<bool> move(double targetAngleDeg, {bool useSkyAngle = false}) =>
      performAction(
        (api) => api.command('move', {
          'target_angle_deg': targetAngleDeg,
          'use_sky_angle': useSkyAngle,
        }),
      );

  /// Set the rotator's Reverse flag.
  Future<bool> setReverse(bool reverse) =>
      performAction((api) => api.command('reverse', {'reverse': reverse}));

  /// Sync the rotator's sky-angle reference to [skyAngleDeg] (e.g. from a plate
  /// solve) without physically moving.
  Future<bool> sync(double skyAngleDeg) => performAction(
    (api) => api.command('sync', {'sky_angle_deg': skyAngleDeg}),
  );
}

final rotatorProvider = AsyncNotifierProvider<RotatorNotifier, RotatorStatus?>(
  RotatorNotifier.new,
);
