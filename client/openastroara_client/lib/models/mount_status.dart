import 'equipment_device_status.dart';

/// Mount (telescope) capabilities (read once on connect, nullable until then):
/// which actions the device supports, so the panel enables only the controls
/// that will work.
class MountCapabilities {
  final bool canSlew;
  final bool canSync;
  final bool canPark;
  final bool canUnpark;
  final bool canSetTracking;
  final bool canFindHome;

  const MountCapabilities({
    required this.canSlew,
    required this.canSync,
    required this.canPark,
    required this.canUnpark,
    required this.canSetTracking,
    required this.canFindHome,
  });

  factory MountCapabilities.fromJson(Map<String, dynamic> json) => MountCapabilities(
        canSlew: json['can_slew'] as bool? ?? false,
        canSync: json['can_sync'] as bool? ?? false,
        canPark: json['can_park'] as bool? ?? false,
        canUnpark: json['can_unpark'] as bool? ?? false,
        canSetTracking: json['can_set_tracking'] as bool? ?? false,
        canFindHome: json['can_find_home'] as bool? ?? false,
      );

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is MountCapabilities &&
          other.canSlew == canSlew &&
          other.canSync == canSync &&
          other.canPark == canPark &&
          other.canUnpark == canUnpark &&
          other.canSetTracking == canSetTracking &&
          other.canFindHome == canFindHome);

  @override
  int get hashCode =>
      Object.hash(canSlew, canSync, canPark, canUnpark, canSetTracking, canFindHome);
}

/// Live status of the connected ASCOM Telescope
/// (`GET /api/v1/equipment/telescope` → `TelescopeDto`). `runtimeState` is the
/// device activity (idle / slewing / tracking / parked / unparking / error),
/// distinct from the connection [connectionState].
class MountStatus extends EquipmentDeviceStatus {
  final String deviceId;
  @override
  final String name;
  @override
  final EquipmentConnectionState connectionState;

  final MountCapabilities? capabilities;
  final String runtimeState;
  final double? rightAscensionHours;
  final double? declinationDegrees;
  final bool tracking;
  final bool parked;
  final bool atHome;

  MountStatus({
    required this.deviceId,
    required this.name,
    required this.connectionState,
    required this.capabilities,
    required this.runtimeState,
    required this.rightAscensionHours,
    required this.declinationDegrees,
    required this.tracking,
    required this.parked,
    required this.atHome,
  });

  /// The mount is mid-slew (the only long-running motion; tracking is steady).
  @override
  bool get isBusy => runtimeState == 'slewing' || runtimeState == 'unparking';

  factory MountStatus.fromJson(Map<String, dynamic> json) {
    final caps = json['capabilities'];
    final runtime = json['runtime'];
    final r = runtime is Map<String, dynamic> ? runtime : const <String, dynamic>{};
    return MountStatus(
      deviceId: json['device_id'] as String? ?? '',
      name: json['name'] as String? ?? '',
      connectionState: equipmentConnectionStateFromWire(json['state'] as String?),
      capabilities:
          caps is Map<String, dynamic> ? MountCapabilities.fromJson(caps) : null,
      runtimeState: r['state'] as String? ?? '',
      rightAscensionHours: (r['right_ascension_hours'] as num?)?.toDouble(),
      declinationDegrees: (r['declination_degrees'] as num?)?.toDouble(),
      tracking: r['tracking'] as bool? ?? false,
      parked: r['parked'] as bool? ?? false,
      atHome: r['at_home'] as bool? ?? false,
    );
  }

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is MountStatus &&
          other.deviceId == deviceId &&
          other.name == name &&
          other.connectionState == connectionState &&
          other.capabilities == capabilities &&
          other.runtimeState == runtimeState &&
          other.rightAscensionHours == rightAscensionHours &&
          other.declinationDegrees == declinationDegrees &&
          other.tracking == tracking &&
          other.parked == parked &&
          other.atHome == atHome);

  @override
  int get hashCode => Object.hash(deviceId, name, connectionState, capabilities,
      runtimeState, rightAscensionHours, declinationDegrees, tracking, parked, atHome);
}

/// Format right ascension (decimal hours, [0, 24)) as `HHh MMm SSs`.
String formatRaHours(double? hours) {
  if (hours == null) return '—';
  final totalSeconds = (hours * 3600).round();
  final h = (totalSeconds ~/ 3600) % 24;
  final m = (totalSeconds % 3600) ~/ 60;
  final s = totalSeconds % 60;
  return '${_two(h)}h ${_two(m)}m ${_two(s)}s';
}

/// Format declination (decimal degrees, [-90, 90]) as `±DD° MM′`.
String formatDecDegrees(double? degrees) {
  if (degrees == null) return '—';
  final sign = degrees < 0 ? '-' : '+';
  final totalArcmin = (degrees.abs() * 60).round();
  final d = totalArcmin ~/ 60;
  final m = totalArcmin % 60;
  return '$sign${_two(d)}° ${_two(m)}′';
}

String _two(int v) => v.toString().padLeft(2, '0');
