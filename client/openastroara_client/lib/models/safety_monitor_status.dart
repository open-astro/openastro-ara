import 'equipment_device_status.dart';

/// Live status of the connected ASCOM SafetyMonitor
/// (`GET /api/v1/equipment/safetymonitor` → `SafetyMonitorDto`). The device's
/// entire Alpaca surface is a single `is_safe` boolean; the daemon adds the
/// connection state and the timestamp of the last safe⇄unsafe transition.
class SafetyMonitorStatus extends EquipmentDeviceStatus {
  final String deviceId;
  @override
  final String name;
  @override
  final EquipmentConnectionState connectionState;

  /// The device's live `IsSafe` reading. Only meaningful while [isConnected];
  /// reads `false` in any other state (the daemon's default).
  final bool safe;

  /// Instant of the last safe⇄unsafe flip (UTC), or `null` if never observed /
  /// unparseable. Normalized to UTC at parse time so equality doesn't trip on
  /// RFC3339 format variation (`Z` vs `+00:00`) for the same instant — which would
  /// otherwise flag two identical-state objects as unequal and flicker the panel.
  final DateTime? lastTransitionAt;

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
        // Tolerate a non-bool `safe` (a schema quirk sending 0/1) the same way the
        // string fields tolerate a missing key. Degrading to `false` (Unsafe) is the
        // deliberate FAIL-SAFE direction for a safety monitor — when the reading is
        // unparseable, block (show Unsafe) rather than green-light an exposure.
        safe: json['safe'] is bool ? json['safe'] as bool : false,
        lastTransitionAt: _parseUtc(json['last_transition_at']),
      );

  static DateTime? _parseUtc(Object? raw) =>
      raw is String ? DateTime.tryParse(raw)?.toUtc() : null;

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
