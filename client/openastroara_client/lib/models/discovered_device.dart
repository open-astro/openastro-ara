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
    // The daemon serializes records via System.Text.Json with the default
    // camelCase naming policy (see `OpenAstroAra.Server/Program.cs`), so
    // every key is camelCase. No PascalCase fallback needed.
    return DiscoveredDevice(
      uniqueId: json['uniqueId'] as String? ?? '',
      name: json['name'] as String? ?? '',
      deviceType: _parseType(json['type']),
      hostName: json['hostName'] as String? ?? '',
      ipAddress: json['ipAddress'] as String? ?? '',
      ipPort: (json['ipPort'] as int?) ?? 0,
      alpacaDeviceNumber: (json['alpacaDeviceNumber'] as int?) ?? 0,
      useHttps: (json['useHttps'] as bool?) ?? false,
    );
  }

  static EquipmentDeviceType _parseType(Object? raw) {
    // Daemon returns the enum as lowercase-concatenated (matching the URL
    // path segment): `camera`, `filterwheel`, `safetymonitor`, etc. Map back
    // to `EquipmentDeviceType` — the client uses camelCase variants like
    // `filterWheel`, `safetyMonitor`.
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
      default:
        // Fallback so a stray daemon value doesn't crash the chooser, but
        // assert in debug builds so future daemon-added types surface
        // immediately rather than silently misclassifying as camera.
        assert(false, 'Unknown device type from daemon: $s');
        return EquipmentDeviceType.camera;
    }
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
    }
  }

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is DiscoveredDevice && other.uniqueId == uniqueId);

  @override
  int get hashCode => uniqueId.hashCode;
}
