import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/filter_wheel_status.dart';
import '../../models/server.dart';
import '../../services/equipment_device_api.dart';
import '../saved_server_state.dart';
import 'equipment_device_state.dart';

/// Builds an [EquipmentDeviceClient] for the FilterWheel on a server. Overridable
/// in tests so a pure fake can be injected.
final filterWheelApiFactoryProvider =
    Provider<EquipmentDeviceClient<FilterWheelStatus> Function(AraServer)>(
  (ref) => (server) => EquipmentDeviceApi<FilterWheelStatus>(
        server,
        path: 'filterwheel',
        fromJson: FilterWheelStatus.fromJson,
      ),
);

/// FilterWheel client bound to the **active** server, or `null` when none saved.
final filterWheelApiProvider =
    Provider<EquipmentDeviceClient<FilterWheelStatus>?>((ref) {
  final server = ref.watch(savedServersProvider.select((async) => async.maybeWhen(
        data: (list) => list.isEmpty ? null : list.last,
        orElse: () => null,
      )));
  if (server == null) return null;
  final api = ref.watch(filterWheelApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// Live filter-wheel status for the active server (or `null` when none is
/// connected), plus a [changeFilter] control. Connect/disconnect + the
/// liveness/busy poll come from the generic core.
class FilterWheelNotifier extends EquipmentDeviceNotifier<FilterWheelStatus> {
  @override
  EquipmentDeviceClient<FilterWheelStatus>? watchClient() =>
      ref.watch(filterWheelApiProvider);

  @override
  EquipmentDeviceClient<FilterWheelStatus>? readClient() =>
      ref.read(filterWheelApiProvider);

  /// Select the filter at [position]. 202-accepted; the device reports `moving`
  /// and the busy poll tracks it to the new slot.
  Future<bool> changeFilter(int position) =>
      performAction((api) => api.command('change', {'position': position}));
}

final filterWheelProvider =
    AsyncNotifierProvider<FilterWheelNotifier, FilterWheelStatus?>(
        FilterWheelNotifier.new);
