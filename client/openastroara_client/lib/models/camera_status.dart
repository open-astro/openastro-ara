import 'equipment_device_status.dart';

bool _listEq(List<String> a, List<String> b) {
  if (identical(a, b)) return true;
  if (a.length != b.length) return false;
  for (var i = 0; i < a.length; i++) {
    if (a[i] != b[i]) return false;
  }
  return true;
}

/// Camera capabilities (read once on connect, nullable until then): sensor
/// geometry, gain/offset/bin ranges, exposure bounds, cooler support, and the
/// Bayer pattern (null on a mono sensor).
class CameraCapabilities {
  final int sensorWidth;
  final int sensorHeight;
  final double pixelSizeUm;
  final bool canSetTemperature;

  /// §25.5.5 — whether the camera has a cooler at all (independent of
  /// [canSetTemperature]: a "dumb" on/off cooler with no TEC set-point is
  /// hasCooler=true + canSetTemperature=false). A pre-slice daemon omits the
  /// field; the parse falls back to [canSetTemperature] so older daemons keep
  /// exactly the old gating.
  final bool hasCooler;
  // §25.5.5 — readout-mode display names in driver index order (select by index);
  // empty = no readout-mode support. Y pixel pitch: 0 = not reported (assume square).
  final List<String> readoutModes;
  final double pixelSizeUmY;
  final int minGain;
  final int maxGain;
  final int minOffset;
  final int maxOffset;
  final int minBinX;
  final int maxBinX;
  final int minBinY;
  final int maxBinY;
  final double minExposureSec;
  final double maxExposureSec;
  final String? bayerPattern;

  const CameraCapabilities({
    required this.sensorWidth,
    required this.sensorHeight,
    required this.pixelSizeUm,
    required this.canSetTemperature,
    this.hasCooler = false,
    this.readoutModes = const [],
    this.pixelSizeUmY = 0,
    required this.minGain,
    required this.maxGain,
    required this.minOffset,
    required this.maxOffset,
    required this.minBinX,
    required this.maxBinX,
    required this.minBinY,
    required this.maxBinY,
    required this.minExposureSec,
    required this.maxExposureSec,
    required this.bayerPattern,
  });

  /// Whether this is a one-shot-colour sensor (has a Bayer pattern) vs mono.
  bool get isColor => bayerPattern?.isNotEmpty == true;

  factory CameraCapabilities.fromJson(Map<String, dynamic> json) {
    int i(String k) => (json[k] as num?)?.toInt() ?? 0;
    double d(String k) => (json[k] as num?)?.toDouble() ?? 0;
    final canSetTemperature = json['can_set_temperature'] as bool? ?? false;
    return CameraCapabilities(
      sensorWidth: i('sensor_width'),
      sensorHeight: i('sensor_height'),
      pixelSizeUm: d('pixel_size_um'),
      canSetTemperature: canSetTemperature,
      // Absent on a pre-§25.5.5 daemon → fall back to canSetTemperature, which
      // reproduces the old cooler-UI gating exactly.
      hasCooler: json['has_cooler'] as bool? ?? canSetTemperature,
      readoutModes: (json['readout_modes'] as List?)?.whereType<String>().toList() ?? const [],
      pixelSizeUmY: d('pixel_size_um_y'),
      minGain: i('min_gain'),
      maxGain: i('max_gain'),
      minOffset: i('min_offset'),
      maxOffset: i('max_offset'),
      minBinX: i('min_bin_x'),
      maxBinX: i('max_bin_x'),
      minBinY: i('min_bin_y'),
      maxBinY: i('max_bin_y'),
      minExposureSec: d('min_exposure_sec'),
      maxExposureSec: d('max_exposure_sec'),
      bayerPattern: json['bayer_pattern'] as String?,
    );
  }

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is CameraCapabilities &&
          other.sensorWidth == sensorWidth &&
          other.sensorHeight == sensorHeight &&
          other.pixelSizeUm == pixelSizeUm &&
          other.canSetTemperature == canSetTemperature &&
          other.hasCooler == hasCooler &&
          _listEq(other.readoutModes, readoutModes) &&
          other.pixelSizeUmY == pixelSizeUmY &&
          other.minGain == minGain &&
          other.maxGain == maxGain &&
          other.minOffset == minOffset &&
          other.maxOffset == maxOffset &&
          other.minBinX == minBinX &&
          other.maxBinX == maxBinX &&
          other.minBinY == minBinY &&
          other.maxBinY == maxBinY &&
          other.minExposureSec == minExposureSec &&
          other.maxExposureSec == maxExposureSec &&
          other.bayerPattern == bayerPattern);

  @override
  int get hashCode => Object.hashAll([
        sensorWidth,
        sensorHeight,
        pixelSizeUm,
        canSetTemperature,
        hasCooler,
        Object.hashAll(readoutModes),
        pixelSizeUmY,
        minGain,
        maxGain,
        minOffset,
        maxOffset,
        minBinX,
        maxBinX,
        minBinY,
        maxBinY,
        minExposureSec,
        maxExposureSec,
        bayerPattern,
      ]);
}

/// Live status of the connected ASCOM Camera
/// (`GET /api/v1/equipment/camera` → `CameraDto`). `runtimeState` is the device
/// activity (idle / exposing / downloading / error), distinct from the
/// connection [connectionState].
class CameraStatus extends EquipmentDeviceStatus {
  final String deviceId;
  @override
  final String name;
  @override
  final EquipmentConnectionState connectionState;

  final CameraCapabilities? capabilities;
  final String runtimeState;
  final double? ccdTemperature;
  final double? coolerPowerPct;
  final bool coolerOn;
  final double? exposureProgressPct;
  // §25.5.5 — the target the TEC is cooling TO (null: driver doesn't report one)
  // and the current readout-mode display name (null: no readout-mode support).
  final double? coolerSetpointC;
  final String? readoutMode;

  CameraStatus({
    required this.deviceId,
    required this.name,
    required this.connectionState,
    required this.capabilities,
    required this.runtimeState,
    required this.ccdTemperature,
    required this.coolerPowerPct,
    required this.coolerOn,
    required this.exposureProgressPct,
    this.coolerSetpointC,
    this.readoutMode,
  });

  bool get isExposing => runtimeState == 'exposing' || runtimeState == 'downloading';

  @override
  bool get isBusy => isExposing;

  factory CameraStatus.fromJson(Map<String, dynamic> json) {
    final caps = json['capabilities'];
    final runtime = json['runtime'];
    final r = runtime is Map<String, dynamic> ? runtime : const <String, dynamic>{};
    return CameraStatus(
      deviceId: json['device_id'] as String? ?? '',
      name: json['name'] as String? ?? '',
      connectionState: equipmentConnectionStateFromWire(json['state'] as String?),
      capabilities:
          caps is Map<String, dynamic> ? CameraCapabilities.fromJson(caps) : null,
      runtimeState: r['state'] as String? ?? '',
      ccdTemperature: (r['ccd_temperature'] as num?)?.toDouble(),
      coolerPowerPct: (r['cooler_power_pct'] as num?)?.toDouble(),
      coolerOn: r['cooler_on'] as bool? ?? false,
      exposureProgressPct: (r['exposure_progress_pct'] as num?)?.toDouble(),
      coolerSetpointC: (r['cooler_setpoint_c'] as num?)?.toDouble(),
      readoutMode: r['readout_mode'] as String?,
    );
  }

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is CameraStatus &&
          other.deviceId == deviceId &&
          other.name == name &&
          other.connectionState == connectionState &&
          other.capabilities == capabilities &&
          other.runtimeState == runtimeState &&
          other.ccdTemperature == ccdTemperature &&
          other.coolerPowerPct == coolerPowerPct &&
          other.coolerOn == coolerOn &&
          other.exposureProgressPct == exposureProgressPct);

  @override
  int get hashCode => Object.hash(deviceId, name, connectionState, capabilities,
      runtimeState, ccdTemperature, coolerPowerPct, coolerOn, exposureProgressPct);
}
