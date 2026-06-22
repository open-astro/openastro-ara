import 'equipment_device_status.dart';

/// Live status of the connected ASCOM ObservingConditions (weather) device
/// (`GET /api/v1/equipment/observingconditions` → `ObservingConditionsDto`).
/// Every sensor is nullable — a device implements only the sensors it has, and an
/// unimplemented one reads `null` rather than failing the whole snapshot.
class WeatherStatus extends EquipmentDeviceStatus {
  final String deviceId;
  @override
  final String name;
  @override
  final EquipmentConnectionState connectionState;

  final double? temperatureC;
  final double? humidityPct;
  final double? dewPointC;
  final double? pressureHpa;
  final double? cloudCoverPct;
  final double? windSpeedMs;
  final double? windGustMs;
  final double? windDirectionDeg;
  final double? rainRate;

  /// When the daemon last refreshed these readings (UTC), or `null` if unparseable.
  final DateTime? capturedAt;

  WeatherStatus({
    required this.deviceId,
    required this.name,
    required this.connectionState,
    required this.temperatureC,
    required this.humidityPct,
    required this.dewPointC,
    required this.pressureHpa,
    required this.cloudCoverPct,
    required this.windSpeedMs,
    required this.windGustMs,
    required this.windDirectionDeg,
    required this.rainRate,
    required this.capturedAt,
  });

  factory WeatherStatus.fromJson(Map<String, dynamic> json) {
    double? d(String key) => (json[key] as num?)?.toDouble();
    final rawCaptured = json['captured_at'];
    return WeatherStatus(
      deviceId: json['device_id'] as String? ?? '',
      name: json['name'] as String? ?? '',
      connectionState: equipmentConnectionStateFromWire(json['state'] as String?),
      temperatureC: d('temperature_c'),
      humidityPct: d('humidity_pct'),
      dewPointC: d('dew_point_c'),
      pressureHpa: d('pressure_hpa'),
      cloudCoverPct: d('cloud_cover_pct'),
      windSpeedMs: d('wind_speed_ms'),
      windGustMs: d('wind_gust_ms'),
      windDirectionDeg: d('wind_direction_deg'),
      rainRate: d('rain_rate'),
      capturedAt:
          rawCaptured is String ? DateTime.tryParse(rawCaptured)?.toUtc() : null,
    );
  }

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is WeatherStatus &&
          other.deviceId == deviceId &&
          other.name == name &&
          other.connectionState == connectionState &&
          other.temperatureC == temperatureC &&
          other.humidityPct == humidityPct &&
          other.dewPointC == dewPointC &&
          other.pressureHpa == pressureHpa &&
          other.cloudCoverPct == cloudCoverPct &&
          other.windSpeedMs == windSpeedMs &&
          other.windGustMs == windGustMs &&
          other.windDirectionDeg == windDirectionDeg &&
          other.rainRate == rainRate &&
          other.capturedAt == capturedAt);

  @override
  int get hashCode => Object.hash(
        deviceId,
        name,
        connectionState,
        temperatureC,
        humidityPct,
        dewPointC,
        pressureHpa,
        cloudCoverPct,
        windSpeedMs,
        windGustMs,
        windDirectionDeg,
        rainRate,
        capturedAt,
      );
}
