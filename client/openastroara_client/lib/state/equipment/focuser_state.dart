import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/focuser_status.dart';
import '../../models/server.dart';
import '../../services/equipment_device_api.dart';
import '../saved_server_state.dart';
import 'equipment_device_state.dart';

/// Builds an [EquipmentDeviceClient] for the Focuser on a server. Overridable in
/// tests so a pure fake can be injected.
final focuserApiFactoryProvider =
    Provider<EquipmentDeviceClient<FocuserStatus> Function(AraServer)>(
  (ref) => (server) => EquipmentDeviceApi<FocuserStatus>(
        server,
        path: 'focuser',
        fromJson: FocuserStatus.fromJson,
      ),
);

/// Focuser client bound to the **active** server, or `null` when none is saved.
final focuserApiProvider =
    Provider<EquipmentDeviceClient<FocuserStatus>?>((ref) {
  final server = ref.watch(savedServersProvider.select((async) => async.maybeWhen(
        data: (list) => list.isEmpty ? null : list.last,
        orElse: () => null,
      )));
  if (server == null) return null;
  final api = ref.watch(focuserApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// Live focuser status for the active server (or `null` when none is connected),
/// plus a [move] control. Connect/disconnect + the liveness poll come from the
/// generic core.
class FocuserNotifier extends EquipmentDeviceNotifier<FocuserStatus> {
  @override
  EquipmentDeviceClient<FocuserStatus>? watchClient() =>
      ref.watch(focuserApiProvider);

  @override
  EquipmentDeviceClient<FocuserStatus>? readClient() =>
      ref.read(focuserApiProvider);

  /// Move to [targetPosition] (absolute focuser) / by it (relative — the daemon
  /// maps per the device's `absolute_focuser` capability). 202-accepted; the
  /// re-read reflects the new state. [useTempComp] applies the device's
  /// temperature compensation for this move.
  Future<bool> move(int targetPosition, {bool useTempComp = false}) =>
      performAction((api) => api.command(
            'move',
            {'target_position': targetPosition, 'use_temp_comp': useTempComp},
          ));
}

final focuserProvider =
    AsyncNotifierProvider<FocuserNotifier, FocuserStatus?>(FocuserNotifier.new);
