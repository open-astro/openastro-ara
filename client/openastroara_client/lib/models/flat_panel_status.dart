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

  /// The device's maximum calibrator brightness, or 0 until the daemon has read
  /// it (or when the device has no calibrator) — the brightness control scales
  /// to this and stays disabled while it's 0.
  final int maxBrightness;

  /// False for a bare light panel with no motorised cover — the cover controls
  /// are hidden rather than shown dead.
  final bool hasCover;

  /// False for a plain dust cover with no light — the light/brightness controls
  /// are hidden rather than shown dead.
  final bool hasCalibrator;

  /// The light is warming up / changing level (ASCOM `CalibratorStatus.NotReady`)
  /// — not on yet, but on its way. EL panels sit here for a second or two after a
  /// command, so it counts as "still working", never as a failure.
  final bool lightWarming;

  FlatPanelStatus({
    required this.deviceId,
    required this.name,
    required this.connectionState,
    required this.runtimeState,
    required this.coverOpen,
    required this.lightOn,
    required this.brightness,
    this.maxBrightness = 0,
    this.hasCover = true,
    this.hasCalibrator = true,
    this.lightWarming = false,
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
      maxBrightness: (r['max_brightness'] as num?)?.toInt() ?? 0,
      // Absent (an older daemon) means "assume present" — same default as the DTO.
      hasCover: r['has_cover'] as bool? ?? true,
      hasCalibrator: r['has_calibrator'] as bool? ?? true,
      lightWarming: r['light_warming'] as bool? ?? false,
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
          other.brightness == brightness &&
          other.maxBrightness == maxBrightness &&
          other.hasCover == hasCover &&
          other.hasCalibrator == hasCalibrator &&
          other.lightWarming == lightWarming);

  @override
  int get hashCode => Object.hash(deviceId, name, connectionState, runtimeState,
      coverOpen, lightOn, brightness, maxBrightness, hasCover, hasCalibrator,
      lightWarming);
}
