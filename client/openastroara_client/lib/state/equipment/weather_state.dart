import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../models/weather_status.dart';
import '../../services/equipment_device_api.dart';
import '../saved_server_state.dart';
import 'equipment_device_state.dart';

/// Builds an [EquipmentDeviceClient] for the ObservingConditions (weather) device
/// on a server. Overridable in tests so a pure fake can be injected.
final weatherApiFactoryProvider =
    Provider<EquipmentDeviceClient<WeatherStatus> Function(AraServer)>(
  (ref) => (server) => EquipmentDeviceApi<WeatherStatus>(
        server,
        path: 'observingconditions',
        fromJson: WeatherStatus.fromJson,
      ),
);

/// Weather client bound to the **active** server (`savedServers.last`), or `null`
/// when no server is saved.
final weatherApiProvider =
    Provider<EquipmentDeviceClient<WeatherStatus>?>((ref) {
  final server = ref.watch(savedServersProvider.select((async) => async.maybeWhen(
        data: (list) => list.isEmpty ? null : list.last,
        orElse: () => null,
      )));
  if (server == null) return null;
  final api = ref.watch(weatherApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// Live weather status for the active server (or `null` when none is connected).
/// Connect/disconnect + the background liveness poll come from the generic core.
class WeatherNotifier extends EquipmentDeviceNotifier<WeatherStatus> {
  @override
  EquipmentDeviceClient<WeatherStatus>? watchClient() =>
      ref.watch(weatherApiProvider);

  @override
  EquipmentDeviceClient<WeatherStatus>? readClient() =>
      ref.read(weatherApiProvider);
}

final weatherProvider =
    AsyncNotifierProvider<WeatherNotifier, WeatherStatus?>(WeatherNotifier.new);
