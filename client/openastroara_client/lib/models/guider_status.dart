/// Client mirror of the daemon's `GET /api/v1/equipment/guider` descriptor
/// (`GuiderDto` + nested `GuiderStateDto`). Wire format is snake_case with
/// lowercased enum tokens (the daemon serializes `EquipmentConnectionState`
/// via `LowerCaseNamingPolicy`).
library;

/// Link state of the guider device itself (mirrors the server
/// `EquipmentConnectionState`). `unknown` is a client-only fallback for an
/// unrecognised token so a new server value never throws.
enum GuiderConnectionState { disconnected, connecting, connected, error, unknown }

GuiderConnectionState _connectionFromWire(String? token) {
  switch (token) {
    case 'disconnected':
      return GuiderConnectionState.disconnected;
    case 'connecting':
      return GuiderConnectionState.connecting;
    case 'connected':
      return GuiderConnectionState.connected;
    case 'error':
      return GuiderConnectionState.error;
    default:
      return GuiderConnectionState.unknown;
  }
}

/// Guiding runtime phase (the nested `GuiderStateDto.state`). Distinct from the
/// link state: a guider can be `connected` while runtime is `stopped`.
enum GuiderRuntimeState {
  stopped,
  calibrating,
  guiding,
  paused,
  starLost,
  dithering,
  unknown,
}

GuiderRuntimeState _runtimeFromWire(String? token) {
  switch (token) {
    case 'stopped':
      return GuiderRuntimeState.stopped;
    case 'calibrating':
      return GuiderRuntimeState.calibrating;
    case 'guiding':
      return GuiderRuntimeState.guiding;
    case 'paused':
      return GuiderRuntimeState.paused;
    case 'star_lost':
      return GuiderRuntimeState.starLost;
    case 'dithering':
      return GuiderRuntimeState.dithering;
    default:
      return GuiderRuntimeState.unknown;
  }
}

/// Snapshot of the guider's link + runtime state, with the latest guiding RMS
/// (total/RA/Dec, arcsec) and the active PHD2 profile name when connected.
class GuiderStatus {
  final String deviceId;
  final String name;
  final GuiderConnectionState connectionState;
  final GuiderRuntimeState runtimeState;
  final double? rmsTotal;
  final double? rmsRa;
  final double? rmsDec;
  final String? currentProfile;

  const GuiderStatus({
    required this.deviceId,
    required this.name,
    required this.connectionState,
    required this.runtimeState,
    this.rmsTotal,
    this.rmsRa,
    this.rmsDec,
    this.currentProfile,
  });

  bool get isConnected => connectionState == GuiderConnectionState.connected;
  bool get isGuiding => runtimeState == GuiderRuntimeState.guiding;

  /// Parse a `GuiderDto` frame. The nested `runtime` object is optional/defensive
  /// — a malformed or absent runtime degrades to [GuiderRuntimeState.unknown]
  /// rather than throwing, since the link state is the primary signal.
  factory GuiderStatus.fromJson(Map<String, dynamic> json) {
    final runtime = json['runtime'];
    final runtimeMap = runtime is Map<String, dynamic> ? runtime : const <String, dynamic>{};
    return GuiderStatus(
      deviceId: json['device_id'] as String? ?? '',
      name: json['name'] as String? ?? 'Guider',
      connectionState: _connectionFromWire(json['state'] as String?),
      runtimeState: _runtimeFromWire(runtimeMap['state'] as String?),
      rmsTotal: _asDouble(runtimeMap['rms_total']),
      rmsRa: _asDouble(runtimeMap['rms_ra']),
      rmsDec: _asDouble(runtimeMap['rms_dec']),
      currentProfile: runtimeMap['current_profile'] as String?,
    );
  }

  static double? _asDouble(dynamic v) {
    if (v is num) return v.toDouble();
    return null;
  }
}
