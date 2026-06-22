import 'equipment_device_status.dart';

/// Capabilities of the connected focuser (read once on connect, so nullable until
/// then): position range, step size, temperature-compensation support, and whether
/// it's an absolute or relative focuser.
class FocuserCapabilities {
  final int minPosition;
  final int maxPosition;
  final double stepSizeUm;
  final bool canTempComp;
  final bool absoluteFocuser;

  const FocuserCapabilities({
    required this.minPosition,
    required this.maxPosition,
    required this.stepSizeUm,
    required this.canTempComp,
    required this.absoluteFocuser,
  });

  factory FocuserCapabilities.fromJson(Map<String, dynamic> json) =>
      FocuserCapabilities(
        minPosition: (json['min_position'] as num?)?.toInt() ?? 0,
        maxPosition: (json['max_position'] as num?)?.toInt() ?? 0,
        stepSizeUm: (json['step_size_um'] as num?)?.toDouble() ?? 0,
        canTempComp: json['can_temp_comp'] as bool? ?? false,
        absoluteFocuser: json['absolute_focuser'] as bool? ?? true,
      );

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is FocuserCapabilities &&
          other.minPosition == minPosition &&
          other.maxPosition == maxPosition &&
          other.stepSizeUm == stepSizeUm &&
          other.canTempComp == canTempComp &&
          other.absoluteFocuser == absoluteFocuser);

  @override
  int get hashCode => Object.hash(
      minPosition, maxPosition, stepSizeUm, canTempComp, absoluteFocuser);
}

/// Live status of the connected ASCOM Focuser
/// (`GET /api/v1/equipment/focuser` → `FocuserDto`). `runtimeState` is the
/// device-activity sub-state (idle/moving/settled/…), distinct from the
/// connection [connectionState].
class FocuserStatus extends EquipmentDeviceStatus {
  final String deviceId;
  @override
  final String name;
  @override
  final EquipmentConnectionState connectionState;

  final FocuserCapabilities? capabilities;
  final String runtimeState;
  final int? position;
  final double? temperature;
  final bool tempCompEnabled;

  FocuserStatus({
    required this.deviceId,
    required this.name,
    required this.connectionState,
    required this.capabilities,
    required this.runtimeState,
    required this.position,
    required this.temperature,
    required this.tempCompEnabled,
  });

  bool get isMoving => runtimeState == 'moving';

  @override
  bool get isBusy => isMoving;

  factory FocuserStatus.fromJson(Map<String, dynamic> json) {
    final caps = json['capabilities'];
    final runtime = json['runtime'];
    final r = runtime is Map<String, dynamic> ? runtime : const <String, dynamic>{};
    return FocuserStatus(
      deviceId: json['device_id'] as String? ?? '',
      name: json['name'] as String? ?? '',
      connectionState: equipmentConnectionStateFromWire(json['state'] as String?),
      capabilities:
          caps is Map<String, dynamic> ? FocuserCapabilities.fromJson(caps) : null,
      runtimeState: r['state'] as String? ?? '',
      position: (r['position'] as num?)?.toInt(),
      temperature: (r['temperature'] as num?)?.toDouble(),
      tempCompEnabled: r['temp_comp_enabled'] as bool? ?? false,
    );
  }

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is FocuserStatus &&
          other.deviceId == deviceId &&
          other.name == name &&
          other.connectionState == connectionState &&
          other.capabilities == capabilities &&
          other.runtimeState == runtimeState &&
          other.position == position &&
          other.temperature == temperature &&
          other.tempCompEnabled == tempCompEnabled);

  @override
  int get hashCode => Object.hash(deviceId, name, connectionState, capabilities,
      runtimeState, position, temperature, tempCompEnabled);
}
