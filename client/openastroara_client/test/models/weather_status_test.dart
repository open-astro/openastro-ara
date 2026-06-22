import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/weather_status.dart';

void main() {
  test('fromJson reads the daemon snake_case ObservingConditionsDto wire shape', () {
    final w = WeatherStatus.fromJson(const {
      'device_id': 'oc-0',
      'name': 'CloudWatcher',
      'state': 'connected',
      'temperature_c': 4.5,
      'humidity_pct': 72,
      'dew_point_c': -0.5,
      'pressure_hpa': 1013,
      'cloud_cover_pct': 20,
      'wind_speed_ms': 3.2,
      'wind_gust_ms': 6.1,
      'wind_direction_deg': 180,
      'rain_rate': 0,
      'captured_at': '2026-06-22T06:00:00Z',
    });
    expect(w.deviceId, 'oc-0');
    expect(w.name, 'CloudWatcher');
    expect(w.connectionState, EquipmentConnectionState.connected);
    expect(w.temperatureC, 4.5);
    expect(w.dewPointC, -0.5);
    expect(w.windGustMs, 6.1);
    expect(w.rainRate, 0);
    expect(w.capturedAt, DateTime.utc(2026, 6, 22, 6));
  });

  test('a sensor the device does not implement is null, not a throw', () {
    // Only temperature present; the rest absent.
    final w = WeatherStatus.fromJson(const {
      'state': 'connected',
      'temperature_c': 10,
    });
    expect(w.temperatureC, 10);
    expect(w.humidityPct, isNull);
    expect(w.dewPointC, isNull);
    expect(w.windGustMs, isNull);
    expect(w.capturedAt, isNull);
  });

  test('captured_at is normalized to UTC', () {
    final w = WeatherStatus.fromJson(
        const {'state': 'connected', 'captured_at': '2026-06-22T06:00:00+00:00'});
    expect(w.capturedAt, DateTime.utc(2026, 6, 22, 6));
    expect(w.capturedAt!.isUtc, isTrue);
  });
}
