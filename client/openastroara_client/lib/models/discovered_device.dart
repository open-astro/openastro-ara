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
    return DiscoveredDevice(
      uniqueId: json['uniqueId'] as String? ?? json['UniqueId'] as String? ?? '',
      name: json['name'] as String? ?? json['Name'] as String? ?? '',
      deviceType: _parseType(json['type'] ?? json['Type']),
      hostName:
          json['hostName'] as String? ?? json['HostName'] as String? ?? '',
      ipAddress:
          json['ipAddress'] as String? ?? json['IpAddress'] as String? ?? '',
      ipPort: (json['ipPort'] ?? json['IpPort'] ?? 0) as int,
      alpacaDeviceNumber: (json['alpacaDeviceNumber'] ??
          json['AlpacaDeviceNumber'] ??
          0) as int,
      useHttps: (json['useHttps'] ?? json['UseHttps'] ?? false) as bool,
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
        // Fallback so a stray daemon value doesn't crash the chooser; the
        // device just won't match any client device-type filter.
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
