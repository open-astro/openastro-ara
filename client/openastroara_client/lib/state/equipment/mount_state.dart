import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/mount_status.dart';
import '../../models/server.dart';
import '../../services/equipment_device_api.dart';
import '../saved_server_state.dart';
import 'equipment_device_state.dart';

/// Builds an [EquipmentDeviceClient] for the Mount (telescope) on a server.
/// Overridable in tests so a pure fake can be injected.
final mountApiFactoryProvider =
    Provider<EquipmentDeviceClient<MountStatus> Function(AraServer)>(
  (ref) => (server) => EquipmentDeviceApi<MountStatus>(
        server,
        path: 'telescope',
        fromJson: MountStatus.fromJson,
      ),
);

/// Mount client bound to the **active** server, or `null` when none is saved.
final mountApiProvider = Provider<EquipmentDeviceClient<MountStatus>?>((ref) {
  final server = ref.watch(savedServersProvider.select((async) => async.maybeWhen(
        data: (list) => list.isEmpty ? null : list.last,
        orElse: () => null,
      )));
  if (server == null) return null;
  final api = ref.watch(mountApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// Live mount status for the active server (or `null` when none is connected),
/// plus tracking / park / abort controls. Connect/disconnect + the
/// liveness/busy poll come from the generic core.
class MountNotifier extends EquipmentDeviceNotifier<MountStatus> {
  @override
  EquipmentDeviceClient<MountStatus>? watchClient() =>
      ref.watch(mountApiProvider);

  @override
  EquipmentDeviceClient<MountStatus>? readClient() => ref.read(mountApiProvider);

  /// Start/stop sidereal tracking.
  Future<bool> setTracking(bool enabled) =>
      performAction((api) => api.command('tracking', {'enabled': enabled}));

  /// Park the mount (slew to the park position and stop tracking).
  Future<bool> park() => performAction((api) => api.command('park'));

  /// Release the mount from the parked state.
  Future<bool> unpark() => performAction((api) => api.command('unpark'));

  /// Abort an in-progress slew (panic stop).
  Future<bool> abortSlew() => performAction((api) => api.command('abort'));
}

final mountProvider =
    AsyncNotifierProvider<MountNotifier, MountStatus?>(MountNotifier.new);
