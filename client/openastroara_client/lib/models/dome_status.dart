import 'equipment_device_status.dart';

/// Dome capabilities (read once on connect, nullable until then): which actions
/// the dome supports, so the panel enables only the controls that will work.
class DomeCapabilities {
  final bool canSetShutter;
  final bool canSetAzimuth;
  final bool canSyncAzimuth;
  final bool canPark;
  final bool canFindHome;

  const DomeCapabilities({
    required this.canSetShutter,
    required this.canSetAzimuth,
    required this.canSyncAzimuth,
    required this.canPark,
    required this.canFindHome,
  });

  factory DomeCapabilities.fromJson(Map<String, dynamic> json) => DomeCapabilities(
        canSetShutter: json['can_set_shutter'] as bool? ?? false,
        canSetAzimuth: json['can_set_azimuth'] as bool? ?? false,
        canSyncAzimuth: json['can_sync_azimuth'] as bool? ?? false,
        canPark: json['can_park'] as bool? ?? false,
        canFindHome: json['can_find_home'] as bool? ?? false,
      );

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is DomeCapabilities &&
          other.canSetShutter == canSetShutter &&
          other.canSetAzimuth == canSetAzimuth &&
          other.canSyncAzimuth == canSyncAzimuth &&
          other.canPark == canPark &&
          other.canFindHome == canFindHome);

  @override
  int get hashCode => Object.hash(
      canSetShutter, canSetAzimuth, canSyncAzimuth, canPark, canFindHome);
}

/// Live status of the connected ASCOM Dome
/// (`GET /api/v1/equipment/dome` → `DomeDto`). `runtimeState` is the composite
/// activity sub-state the daemon derives (idle / slewing / shutter_moving /
/// parked / shutter_open / error).
class DomeStatus extends EquipmentDeviceStatus {
  final String deviceId;
  @override
  final String name;
  @override
  final EquipmentConnectionState connectionState;

  final DomeCapabilities? capabilities;
  final String runtimeState;
  final double? azimuthDeg;
  final bool shutterOpen;
  final bool atHome;
  final bool parked;

  DomeStatus({
    required this.deviceId,
    required this.name,
    required this.connectionState,
    required this.capabilities,
    required this.runtimeState,
    required this.azimuthDeg,
    required this.shutterOpen,
    required this.atHome,
    required this.parked,
  });

  /// The dome is mid-operation (rotating or moving the shutter).
  @override
  bool get isBusy => runtimeState == 'slewing' || runtimeState == 'shutter_moving';

  factory DomeStatus.fromJson(Map<String, dynamic> json) {
    final caps = json['capabilities'];
    final runtime = json['runtime'];
    final r = runtime is Map<String, dynamic> ? runtime : const <String, dynamic>{};
    return DomeStatus(
      deviceId: json['device_id'] as String? ?? '',
      name: json['name'] as String? ?? '',
      connectionState: equipmentConnectionStateFromWire(json['state'] as String?),
      capabilities:
          caps is Map<String, dynamic> ? DomeCapabilities.fromJson(caps) : null,
      runtimeState: r['state'] as String? ?? '',
      azimuthDeg: (r['azimuth_deg'] as num?)?.toDouble(),
      shutterOpen: r['shutter_open'] as bool? ?? false,
      atHome: r['at_home'] as bool? ?? false,
      parked: r['parked'] as bool? ?? false,
    );
  }

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is DomeStatus &&
          other.deviceId == deviceId &&
          other.name == name &&
          other.connectionState == connectionState &&
          other.capabilities == capabilities &&
          other.runtimeState == runtimeState &&
          other.azimuthDeg == azimuthDeg &&
          other.shutterOpen == shutterOpen &&
          other.atHome == atHome &&
          other.parked == parked);

  @override
  int get hashCode => Object.hash(deviceId, name, connectionState, capabilities,
      runtimeState, azimuthDeg, shutterOpen, atHome, parked);
}
