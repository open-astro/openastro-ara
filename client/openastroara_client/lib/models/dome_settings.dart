/// The profile's dome (`GET/PUT /api/v1/profile/dome`, #1689): where the scope
/// sits in the dome, how close the slit has to be, and whether the dome
/// follows the mount. Distances are mm, angles degrees. An absent key reads
/// as its default, so an older daemon's answer still parses.
class DomeSettings {
  const DomeSettings({
    this.followMount = false,
    this.domeRadiusMm = 0,
    this.scopePositionNorthSouthMm = 0,
    this.scopePositionEastWestMm = 0,
    this.scopePositionUpDownMm = 0,
    this.mountType = 'equatorial',
    this.gemAxisMm = 0,
    this.lateralAxisMm = 0,
    this.decOffsetHorizontalMm = 0,
    this.azimuthToleranceDegrees = 2,
    this.synchronizeDuringMountSlew = false,
    this.domeSyncTimeoutSeconds = 120,
  });

  final bool followMount;
  final double domeRadiusMm;
  final double scopePositionNorthSouthMm;
  final double scopePositionEastWestMm;
  final double scopePositionUpDownMm;

  /// `equatorial` or `fork_on_wedge`.
  final String mountType;
  final double gemAxisMm;
  final double lateralAxisMm;
  final double decOffsetHorizontalMm;
  final double azimuthToleranceDegrees;
  final bool synchronizeDuringMountSlew;
  final int domeSyncTimeoutSeconds;

  bool get isEquatorial => mountType != 'fork_on_wedge';

  factory DomeSettings.fromJson(Map<String, dynamic> j) => DomeSettings(
    followMount: j['follow_mount'] as bool? ?? false,
    domeRadiusMm: (j['dome_radius_mm'] as num?)?.toDouble() ?? 0,
    scopePositionNorthSouthMm:
        (j['scope_position_north_south_mm'] as num?)?.toDouble() ?? 0,
    scopePositionEastWestMm:
        (j['scope_position_east_west_mm'] as num?)?.toDouble() ?? 0,
    scopePositionUpDownMm:
        (j['scope_position_up_down_mm'] as num?)?.toDouble() ?? 0,
    mountType: j['mount_type'] as String? ?? 'equatorial',
    gemAxisMm: (j['gem_axis_mm'] as num?)?.toDouble() ?? 0,
    lateralAxisMm: (j['lateral_axis_mm'] as num?)?.toDouble() ?? 0,
    decOffsetHorizontalMm:
        (j['dec_offset_horizontal_mm'] as num?)?.toDouble() ?? 0,
    azimuthToleranceDegrees:
        (j['azimuth_tolerance_degrees'] as num?)?.toDouble() ?? 2,
    synchronizeDuringMountSlew:
        j['synchronize_during_mount_slew'] as bool? ?? false,
    domeSyncTimeoutSeconds:
        (j['dome_sync_timeout_seconds'] as num?)?.toInt() ?? 120,
  );

  Map<String, dynamic> toJson() => {
    'follow_mount': followMount,
    'dome_radius_mm': domeRadiusMm,
    'scope_position_north_south_mm': scopePositionNorthSouthMm,
    'scope_position_east_west_mm': scopePositionEastWestMm,
    'scope_position_up_down_mm': scopePositionUpDownMm,
    'mount_type': mountType,
    'gem_axis_mm': gemAxisMm,
    'lateral_axis_mm': lateralAxisMm,
    'dec_offset_horizontal_mm': decOffsetHorizontalMm,
    'azimuth_tolerance_degrees': azimuthToleranceDegrees,
    'synchronize_during_mount_slew': synchronizeDuringMountSlew,
    'dome_sync_timeout_seconds': domeSyncTimeoutSeconds,
  };

  DomeSettings copyWith({
    bool? followMount,
    double? domeRadiusMm,
    double? scopePositionNorthSouthMm,
    double? scopePositionEastWestMm,
    double? scopePositionUpDownMm,
    String? mountType,
    double? gemAxisMm,
    double? lateralAxisMm,
    double? decOffsetHorizontalMm,
    double? azimuthToleranceDegrees,
    bool? synchronizeDuringMountSlew,
    int? domeSyncTimeoutSeconds,
  }) => DomeSettings(
    followMount: followMount ?? this.followMount,
    domeRadiusMm: domeRadiusMm ?? this.domeRadiusMm,
    scopePositionNorthSouthMm:
        scopePositionNorthSouthMm ?? this.scopePositionNorthSouthMm,
    scopePositionEastWestMm:
        scopePositionEastWestMm ?? this.scopePositionEastWestMm,
    scopePositionUpDownMm: scopePositionUpDownMm ?? this.scopePositionUpDownMm,
    mountType: mountType ?? this.mountType,
    gemAxisMm: gemAxisMm ?? this.gemAxisMm,
    lateralAxisMm: lateralAxisMm ?? this.lateralAxisMm,
    decOffsetHorizontalMm: decOffsetHorizontalMm ?? this.decOffsetHorizontalMm,
    azimuthToleranceDegrees:
        azimuthToleranceDegrees ?? this.azimuthToleranceDegrees,
    synchronizeDuringMountSlew:
        synchronizeDuringMountSlew ?? this.synchronizeDuringMountSlew,
    domeSyncTimeoutSeconds:
        domeSyncTimeoutSeconds ?? this.domeSyncTimeoutSeconds,
  );
}
