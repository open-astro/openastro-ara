import 'equipment_device_status.dart';

/// Live status of the connected ASCOM SafetyMonitor
/// (`GET /api/v1/equipment/safetymonitor` → `SafetyMonitorDto`). The device's
/// entire Alpaca surface is a single `is_safe` boolean; the daemon adds the
/// connection state and the timestamp of the last safe⇄unsafe transition.
class SafetyMonitorStatus extends EquipmentDeviceStatus {
  final String deviceId;
  final String name;
  @override
  final EquipmentConnectionState connectionState;

  /// The device's live `IsSafe` reading. Only meaningful while [isConnected];
  /// reads `false` in any other state (the daemon's default).
  final bool safe;

  /// RFC3339 timestamp of the last safe⇄unsafe flip, or `''` if never observed.
  final String lastTransitionAt;

  SafetyMonitorStatus({
    required this.deviceId,
    required this.name,
    required this.connectionState,
    required this.safe,
    required this.lastTransitionAt,
  });

  factory SafetyMonitorStatus.fromJson(Map<String, dynamic> json) =>
      SafetyMonitorStatus(
        deviceId: json['device_id'] as String? ?? '',
        name: json['name'] as String? ?? '',
        connectionState:
            equipmentConnectionStateFromWire(json['state'] as String?),
        safe: json['safe'] as bool? ?? false,
        lastTransitionAt: json['last_transition_at'] as String? ?? '',
      );

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is SafetyMonitorStatus &&
          other.deviceId == deviceId &&
          other.name == name &&
          other.connectionState == connectionState &&
          other.safe == safe &&
          other.lastTransitionAt == lastTransitionAt);

  @override
  int get hashCode =>
      Object.hash(deviceId, name, connectionState, safe, lastTransitionAt);
}
