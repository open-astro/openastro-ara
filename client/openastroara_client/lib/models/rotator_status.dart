import 'equipment_device_status.dart';

/// Rotator capabilities (read once on connect, nullable until then): whether the
/// rotator supports the Reverse property, and its minimum step size in degrees.
class RotatorCapabilities {
  final bool canReverse;
  final double stepSize;

  const RotatorCapabilities({required this.canReverse, required this.stepSize});

  factory RotatorCapabilities.fromJson(Map<String, dynamic> json) =>
      RotatorCapabilities(
        canReverse: json['can_reverse'] as bool? ?? false,
        stepSize: (json['step_size'] as num?)?.toDouble() ?? 0,
      );

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is RotatorCapabilities &&
          other.canReverse == canReverse &&
          other.stepSize == stepSize);

  @override
  int get hashCode => Object.hash(canReverse, stepSize);
}

/// Live status of the connected ASCOM Rotator
/// (`GET /api/v1/equipment/rotator` → `RotatorDto`). Carries both the
/// mechanical angle and the offset-corrected sky angle, plus the reverse flag.
class RotatorStatus extends EquipmentDeviceStatus {
  final String deviceId;
  @override
  final String name;
  @override
  final EquipmentConnectionState connectionState;

  final RotatorCapabilities? capabilities;
  final String runtimeState;
  final double? mechanicalAngleDeg;
  final double? skyAngleDeg;
  final bool reverse;

  RotatorStatus({
    required this.deviceId,
    required this.name,
    required this.connectionState,
    required this.capabilities,
    required this.runtimeState,
    required this.mechanicalAngleDeg,
    required this.skyAngleDeg,
    required this.reverse,
  });

  bool get isMoving => runtimeState == 'moving';

  @override
  bool get isBusy => isMoving;

  factory RotatorStatus.fromJson(Map<String, dynamic> json) {
    final caps = json['capabilities'];
    final runtime = json['runtime'];
    final r = runtime is Map<String, dynamic> ? runtime : const <String, dynamic>{};
    return RotatorStatus(
      deviceId: json['device_id'] as String? ?? '',
      name: json['name'] as String? ?? '',
      connectionState: equipmentConnectionStateFromWire(json['state'] as String?),
      capabilities:
          caps is Map<String, dynamic> ? RotatorCapabilities.fromJson(caps) : null,
      runtimeState: r['state'] as String? ?? '',
      mechanicalAngleDeg: (r['mechanical_angle_deg'] as num?)?.toDouble(),
      skyAngleDeg: (r['sky_angle_deg'] as num?)?.toDouble(),
      reverse: r['reverse'] as bool? ?? false,
    );
  }

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is RotatorStatus &&
          other.deviceId == deviceId &&
          other.name == name &&
          other.connectionState == connectionState &&
          other.capabilities == capabilities &&
          other.runtimeState == runtimeState &&
          other.mechanicalAngleDeg == mechanicalAngleDeg &&
          other.skyAngleDeg == skyAngleDeg &&
          other.reverse == reverse);

  @override
  int get hashCode => Object.hash(deviceId, name, connectionState, capabilities,
      runtimeState, mechanicalAngleDeg, skyAngleDeg, reverse);
}
