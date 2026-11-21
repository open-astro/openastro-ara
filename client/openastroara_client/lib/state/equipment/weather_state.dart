import 'dart:math' as math;

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../settings/equipment_connection_state.dart';
import '../../models/weather_status.dart';
import '../../services/equipment_device_api.dart';
import '../saved_server_state.dart';
import '../settings/safety_policies_state.dart';
import 'equipment_device_state.dart';

/// Builds an [EquipmentDeviceClient] for the ObservingConditions (weather) device
/// on a server. Overridable in tests so a pure fake can be injected.
final weatherApiFactoryProvider =
    Provider<EquipmentDeviceClient<WeatherStatus> Function(AraServer)>(
      (ref) =>
          (server) => EquipmentDeviceApi<WeatherStatus>(
            server,
            path: 'observingconditions',
            fromJson: WeatherStatus.fromJson,
          ),
    );

/// Weather client bound to the **active** server ([activeServerProvider]), or `null`
/// when no server is saved.
final weatherApiProvider = Provider<EquipmentDeviceClient<WeatherStatus>?>((
  ref,
) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(weatherApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// Live weather status for the active server (or `null` when none is connected).
/// Connect/disconnect + the background liveness poll come from the generic core.
class WeatherNotifier extends EquipmentDeviceNotifier<WeatherStatus> {
  @override
  EquipmentDeviceType get deviceType => EquipmentDeviceType.weather;

  @override
  EquipmentDeviceClient<WeatherStatus>? watchClient() =>
      ref.watch(weatherApiProvider);

  @override
  EquipmentDeviceClient<WeatherStatus>? readClient() =>
      ref.read(weatherApiProvider);
}

final weatherProvider = AsyncNotifierProvider<WeatherNotifier, WeatherStatus?>(
  WeatherNotifier.new,
);

/// Which weather reading is past the rig's safety limit.
enum WeatherBreach { wind, humidity, dewMargin }

/// The weather readings past the rig's safety limits, as the daemon checks
/// them (§35.1): the worse of wind speed and gust (km/h), humidity (%), and
/// the air's margin over the dew point (°C), each with its reading and limit.
/// A reading the station doesn't report, or a NaN, breaches nothing.
List<({WeatherBreach kind, double value, double limit})> weatherBreaches(
  WeatherStatus w,
  SafetyPolicies p,
) {
  bool ok(double? v) => v != null && v.isFinite;
  final speed = w.windSpeedMs, gust = w.windGustMs;
  final nan =
      (speed != null && !speed.isFinite) || (gust != null && !gust.isFinite);
  final wind = nan
      ? null
      : ok(speed) && ok(gust)
      ? math.max(speed!, gust!)
      : ok(speed)
      ? speed
      : gust;
  final t = w.temperatureC, dew = w.dewPointC, hum = w.humidityPct;
  return [
    if (wind != null && wind * 3.6 > p.maxWindKmh)
      (kind: WeatherBreach.wind, value: wind * 3.6, limit: p.maxWindKmh.toDouble()),
    if (ok(hum) && hum! > p.maxHumidityPct)
      (kind: WeatherBreach.humidity, value: hum, limit: p.maxHumidityPct.toDouble()),
    if (ok(t) && ok(dew) && t! - dew! < p.minDewDeltaC)
      (kind: WeatherBreach.dewMargin, value: t - dew, limit: p.minDewDeltaC.toDouble()),
  ];
}
