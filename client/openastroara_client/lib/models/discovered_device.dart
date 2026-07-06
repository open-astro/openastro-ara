import '../state/settings/equipment_connection_state.dart';

/// Mirrors `DiscoveredDeviceDto` from `OpenAstroAra.Server/Contracts/EquipmentDtos.cs`
/// — what `GET /api/v1/equipment/discover/{type}` returns per device.
class DiscoveredDevice {
  final String uniqueId;
  final String name;
  final EquipmentDeviceType deviceType;
  final String hostName;
  final String ipAddress;
  final int ipPort;
  final int alpacaDeviceNumber;
  final bool useHttps;

  const DiscoveredDevice({
    required this.uniqueId,
    required this.name,
    required this.deviceType,
    required this.hostName,
    required this.ipAddress,
    required this.ipPort,
    required this.alpacaDeviceNumber,
    required this.useHttps,
  });

  factory DiscoveredDevice.fromJson(Map<String, dynamic> json) {
    // The daemon serializes with System.Text.Json's SnakeCaseLower policy
    // (`OpenAstroAra.Server/Program.cs` ConfigureHttpJsonOptions +
    // `AraJsonSerializerContext`), so keys are snake_case (`unique_id`,
    // `host_name`, `alpaca_device_number`, …) — NOT camelCase. Reading camelCase
    // here parsed every field to its default (blank name / id, device number 0),
    // which broke the whole discover→select→connect flow. Read snake_case, with a
    // camelCase fallback so either shape parses.
    String str(String snake, String camel) =>
        (json[snake] ?? json[camel]) as String? ?? '';
    int integer(String snake, String camel) =>
        ((json[snake] ?? json[camel]) as num?)?.toInt() ?? 0;
    return DiscoveredDevice(
      uniqueId: str('unique_id', 'uniqueId'),
      name: json['name'] as String? ?? '',
      deviceType: _parseType(json['type']),
      hostName: str('host_name', 'hostName'),
      ipAddress: str('ip_address', 'ipAddress'),
      ipPort: integer('ip_port', 'ipPort'),
      alpacaDeviceNumber: integer('alpaca_device_number', 'alpacaDeviceNumber'),
      useHttps: (json['use_https'] ?? json['useHttps']) as bool? ?? false,
    );
  }

  /// Tolerant daemon-token → type lookup (lowercase-concatenated tokens like
  /// `filterwheel`, `safetymonitor`, plus historic aliases). Null for an
  /// unknown token — WS handlers use this so a stray `equipment.*` frame from
  /// a newer daemon is IGNORED, never asserted on (see _parseType for the
  /// strict discovery-path variant).
  static EquipmentDeviceType? tryParseDeviceType(Object? raw) {
    final s = raw?.toString().toLowerCase() ?? '';
    switch (s) {
      case 'camera':
        return EquipmentDeviceType.camera;
      case 'telescope':
      case 'mount':
        return EquipmentDeviceType.mount;
      case 'focuser':
        return EquipmentDeviceType.focuser;
      case 'filterwheel':
        return EquipmentDeviceType.filterWheel;
      case 'rotator':
        return EquipmentDeviceType.rotator;
      case 'guider':
        return EquipmentDeviceType.guider;
      case 'covercalibrator':
      case 'flatdevice': // the §60.9 equipment.* events' token (DeviceType.FlatDevice)
      case 'flatpanel':
      case 'flat':
        return EquipmentDeviceType.flatPanel;
      case 'dome':
        return EquipmentDeviceType.dome;
      case 'observingconditions':
      case 'weather':
        return EquipmentDeviceType.weather;
      case 'safetymonitor':
      case 'safety':
        return EquipmentDeviceType.safetyMonitor;
      case 'switch':
        return EquipmentDeviceType.switchDevice;
      default:
        return null;
    }
  }

  static EquipmentDeviceType _parseType(Object? raw) {
    final parsed = tryParseDeviceType(raw);
    if (parsed != null) return parsed;
    // Fallback so a stray daemon value doesn't crash the chooser, but
    // assert in debug builds so future daemon-added types surface
    // immediately rather than silently misclassifying as camera.
    assert(false, 'Unknown device type from daemon: $raw');
    return EquipmentDeviceType.camera;
  }

  /// URL path segment used by `/api/v1/equipment/discover/{type}`. Matches
  /// the daemon's `DeviceType.ToString().ToLowerInvariant()` convention from
  /// Phase 6 `EquipmentEndpoints.cs`.
  static String pathSegmentFor(EquipmentDeviceType t) {
    switch (t) {
      case EquipmentDeviceType.camera:
        return 'camera';
      case EquipmentDeviceType.mount:
        return 'telescope';
      case EquipmentDeviceType.focuser:
        return 'focuser';
      case EquipmentDeviceType.filterWheel:
        return 'filterwheel';
      case EquipmentDeviceType.rotator:
        return 'rotator';
      case EquipmentDeviceType.guider:
        return 'guider';
      case EquipmentDeviceType.flatPanel:
        return 'covercalibrator';
      case EquipmentDeviceType.dome:
        return 'dome';
      case EquipmentDeviceType.weather:
        return 'observingconditions';
      case EquipmentDeviceType.safetyMonitor:
        return 'safetymonitor';
      case EquipmentDeviceType.switchDevice:
        return 'switch';
    }
  }

  /// Snake_case body for the daemon's `ConnectRequestDto.Device` (System.Text.Json
  /// `SnakeCaseLower`). `type` is the daemon's lowercase `DeviceType` token, which
  /// matches the discovery URL segment for every type. Named for its one use (the
  /// connect endpoint) so a future divergent connect body doesn't silently reuse it.
  Map<String, dynamic> toConnectRequestJson() => <String, dynamic>{
    'unique_id': uniqueId,
    'name': name,
    'type': pathSegmentFor(deviceType),
    'host_name': hostName,
    'ip_address': ipAddress,
    'ip_port': ipPort,
    'alpaca_device_number': alpacaDeviceNumber,
    'use_https': useHttps,
  };

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is DiscoveredDevice && other.uniqueId == uniqueId);

  @override
  int get hashCode => uniqueId.hashCode;
}
