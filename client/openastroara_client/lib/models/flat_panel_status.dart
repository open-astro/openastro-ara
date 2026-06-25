import 'equipment_device_status.dart';

/// Live status of the connected ASCOM CoverCalibrator / flat (light) panel
/// (`GET /api/v1/equipment/flatdevice` → `FlatDeviceDto`). Carries the cover
/// position, calibrator light on/off, and brightness.
class FlatPanelStatus extends EquipmentDeviceStatus {
  final String deviceId;
  @override
  final String name;
  @override
  final EquipmentConnectionState connectionState;

  /// `"cover_open" | "cover_moving" | "cover_closed" | "light_on" | "error"`.
  final String runtimeState;
  final bool coverOpen;
  final bool lightOn;
  final int brightness;

  FlatPanelStatus({
    required this.deviceId,
    required this.name,
    required this.connectionState,
    required this.runtimeState,
    required this.coverOpen,
    required this.lightOn,
    required this.brightness,
  });

  /// The cover is in motion (opening/closing) — drives the chip's amber dot.
  bool get isMoving => runtimeState == 'cover_moving';

  @override
  bool get isBusy => isMoving;

  factory FlatPanelStatus.fromJson(Map<String, dynamic> json) {
    final runtime = json['runtime'];
    final r =
        runtime is Map<String, dynamic> ? runtime : const <String, dynamic>{};
    return FlatPanelStatus(
      deviceId: json['device_id'] as String? ?? '',
      name: json['name'] as String? ?? '',
      connectionState: equipmentConnectionStateFromWire(json['state'] as String?),
      runtimeState: r['state'] as String? ?? '',
      coverOpen: r['cover_open'] as bool? ?? false,
      lightOn: r['light_on'] as bool? ?? false,
      brightness: (r['brightness'] as num?)?.toInt() ?? 0,
    );
  }

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is FlatPanelStatus &&
          other.deviceId == deviceId &&
          other.name == name &&
          other.connectionState == connectionState &&
          other.runtimeState == runtimeState &&
          other.coverOpen == coverOpen &&
          other.lightOn == lightOn &&
          other.brightness == brightness);

  @override
  int get hashCode => Object.hash(deviceId, name, connectionState, runtimeState,
      coverOpen, lightOn, brightness);
}
