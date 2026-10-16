/// Client mirror of the daemon's `GET /api/v1/equipment/guider` descriptor
/// (`GuiderDto` + nested `GuiderStateDto`). Wire format is snake_case with
/// lowercased enum tokens (the daemon serializes `EquipmentConnectionState`
/// via `LowerCaseNamingPolicy`).
library;

import 'pa_residual.dart';

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
  /// #1364 — exposures looping with no star chosen (the guide camera is busy).
  looping,
  /// #1364 — looping with a star chosen, not guiding yet.
  starSelected,
  calibrating,
  guiding,
  /// #1364 — guiding, settling after a dither or a start.
  settling,
  paused,
  starLost,
  dithering,
  unknown,
}

/// #1364 item 6 — the one wording of a guider phase, used by the guiding
/// strip, the guider dialog and the Run tab alike.
String guiderRuntimeText(GuiderRuntimeState state) => switch (state) {
      GuiderRuntimeState.stopped => 'Stopped',
      GuiderRuntimeState.looping => 'Looping',
      GuiderRuntimeState.starSelected => 'Star selected',
      GuiderRuntimeState.calibrating => 'Calibrating',
      GuiderRuntimeState.guiding => 'Guiding',
      GuiderRuntimeState.settling => 'Settling',
      GuiderRuntimeState.paused => 'Paused',
      GuiderRuntimeState.starLost => 'Star lost',
      GuiderRuntimeState.dithering => 'Dithering',
      GuiderRuntimeState.unknown => 'Unknown',
    };

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
    case 'looping':
      return GuiderRuntimeState.looping;
    case 'star_selected':
      return GuiderRuntimeState.starSelected;
    case 'settling':
      return GuiderRuntimeState.settling;
    default:
      return GuiderRuntimeState.unknown;
  }
}

/// Snapshot of the guider's link + runtime state, with the latest guiding RMS
/// (total/RA/Dec, arcsec) and the active OpenAstro Guider profile name when connected.
class GuiderStatus {
  /// Daemon device id; `null` when the descriptor omitted it (distinguishable
  /// from an explicitly-empty id).
  final String? deviceId;
  final String name;
  final GuiderConnectionState connectionState;
  final GuiderRuntimeState runtimeState;
  /// Windowed RMS in guide-camera PIXELS (`rms_total` / `rms_ra` / `rms_dec`).
  final double? rmsTotal;
  final double? rmsRa;
  final double? rmsDec;
  /// The same window in arcsec (`rms_*_arcsec`), present once the guider has
  /// reported a pixel scale. Parsed for completeness: the Live tab's guiding
  /// strip computes its own figures from `guider.step`, not from these.
  final double? rmsTotalArcsec;
  final double? rmsRaArcsec;
  final double? rmsDecArcsec;
  final String? currentProfile;
  /// #1311 — the polar alignment residual from guiding (`runtime.pa_residual`),
  /// so a client that (re)connects mid-run shows it before the next WS event.
  final PaResidual? paResidual;
  /// While calibrating: the leg under way (`West`, `East`, `North`, `South`…),
  /// its step and the star's distance from the leg's start in guide pixels
  /// (`runtime.calibration_*`). Null otherwise.
  final String? calibrationDirection;
  final int? calibrationStep;
  final double? calibrationDistancePx;

  const GuiderStatus({
    this.deviceId,
    required this.name,
    required this.connectionState,
    required this.runtimeState,
    this.rmsTotal,
    this.rmsRa,
    this.rmsDec,
    this.rmsTotalArcsec,
    this.rmsRaArcsec,
    this.rmsDecArcsec,
    this.currentProfile,
    this.paResidual,
    this.calibrationDirection,
    this.calibrationStep,
    this.calibrationDistancePx,
  });

  /// "North step 7 · 11.9 px" while a calibration leg is reported; null otherwise.
  String? get calibrationText {
    final dir = calibrationDirection;
    if (dir == null || dir.isEmpty) return null;
    final parts = [
      calibrationStep == null ? dir : '$dir step $calibrationStep',
      if (calibrationDistancePx != null) '${calibrationDistancePx!.toStringAsFixed(1)} px',
    ];
    return parts.join(' · ');
  }

  /// "Calibrating · North step 7 · 11.9 px", "Guiding", "Not connected".
  String get runtimeSummary {
    if (!isConnected) return 'Not connected';
    final base = guiderRuntimeText(runtimeState);
    final leg = runtimeState == GuiderRuntimeState.calibrating ? calibrationText : null;
    return leg == null ? base : '$base · $leg';
  }

  bool get isConnected => connectionState == GuiderConnectionState.connected;
  bool get isGuiding => runtimeState == GuiderRuntimeState.guiding;

  /// Parse a `GuiderDto` frame. The nested `runtime` object is optional/defensive
  /// — a malformed or absent runtime degrades to [GuiderRuntimeState.unknown]
  /// rather than throwing, since the link state is the primary signal.
  factory GuiderStatus.fromJson(Map<String, dynamic> json) {
    final runtime = json['runtime'];
    final runtimeMap = runtime is Map<String, dynamic> ? runtime : const <String, dynamic>{};
    return GuiderStatus(
      deviceId: _str(json['device_id']),
      // The guider daemon self-reports its upstream product name ("PHD2");
      // the client brands it OpenAstro Guider everywhere (Joey's ruling), so
      // normalize once here rather than at every display site.
      name: _brandName(_str(json['name'])),
      connectionState: _connectionFromWire(_str(json['state'])),
      runtimeState: _runtimeFromWire(_str(runtimeMap['state'])),
      rmsTotal: _asDouble(runtimeMap['rms_total']),
      rmsRa: _asDouble(runtimeMap['rms_ra']),
      rmsDec: _asDouble(runtimeMap['rms_dec']),
      rmsTotalArcsec: _asDouble(runtimeMap['rms_total_arcsec']),
      rmsRaArcsec: _asDouble(runtimeMap['rms_ra_arcsec']),
      rmsDecArcsec: _asDouble(runtimeMap['rms_dec_arcsec']),
      currentProfile: _str(runtimeMap['current_profile']),
      paResidual: PaResidual.fromJson(runtimeMap['pa_residual']),
      calibrationDirection: _str(runtimeMap['calibration_direction']),
      calibrationStep: (runtimeMap['calibration_step'] as num?)?.toInt(),
      calibrationDistancePx: _asDouble(runtimeMap['calibration_distance_px']),
    );
  }

  // Type-guarded extractors so a wrong-typed field (e.g. a number where a string
  // is expected) degrades to null rather than throwing — the factory tolerates
  // any malformed value, not only nulls.
  static String? _str(dynamic v) => v is String ? v : null;

  static String _brandName(String? raw) {
    if (raw == null || raw.isEmpty) return 'Guider';
    // Word-boundary so a fork name embedding "phd2" mid-token isn't spliced.
    return raw.replaceAll(
        RegExp(r'\bphd2\b', caseSensitive: false), 'OpenAstro Guider');
  }

  static double? _asDouble(dynamic v) {
    final d = v is num
        ? v.toDouble()
        : v is String
            ? num.tryParse(v)?.toDouble()
            : null;
    // Filter NaN/Infinity (incl. from "NaN"/"Infinity" strings) — a non-finite
    // RMS is meaningless and would break value equality (NaN != NaN → churn).
    return (d != null && d.isFinite) ? d : null;
  }

  // Value equality so an unchanged poll result doesn't churn Riverpod listeners
  // (each parse builds a fresh object). Matches the manual ==/hashCode pattern
  // used by the project's other models.
  @override
  bool operator ==(Object other) =>
      other is GuiderStatus &&
      other.deviceId == deviceId &&
      other.name == name &&
      other.connectionState == connectionState &&
      other.runtimeState == runtimeState &&
      other.rmsTotal == rmsTotal &&
      other.rmsRa == rmsRa &&
      other.rmsDec == rmsDec &&
      other.rmsTotalArcsec == rmsTotalArcsec &&
      other.rmsRaArcsec == rmsRaArcsec &&
      other.rmsDecArcsec == rmsDecArcsec &&
      other.currentProfile == currentProfile &&
      other.paResidual == paResidual;

  @override
  int get hashCode => Object.hash(deviceId, name, connectionState, runtimeState,
      rmsTotal, rmsRa, rmsDec, rmsTotalArcsec, rmsRaArcsec, rmsDecArcsec,
      currentProfile, paResidual);
}
