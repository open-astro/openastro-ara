import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/profile_api.dart';

/// §36/§25.5 imaging-train optics + sensor geometry — the inputs the Planning
/// tab's Frame mode needs to draw the field-of-view box without a live camera.
/// Backed by the daemon's `GET`/`PUT /api/v1/profile/optics` section (#467):
/// [OpticsSettingsNotifier.hydrateFromServer] runs when the Planning/Settings
/// surface mounts, [persistToServer] from the Settings Save button. The
/// on-first-camera-connect auto-population is a later slice; this is the model
/// + geometry the FOV overlay consumes.
///
/// Framing math (the FOV box this feeds):
///   pixelScale[arcsec/px] = 206.265 × pixelSize_µm ÷ (focalLength_mm × reducer)
///   fov[arcsec]           = sensor_px × pixelScale
class OpticsSettings {
  /// Telescope focal length in mm (before the reducer/barlow). 0 = unset.
  final double focalLengthMm;

  /// Reducer/barlow multiplier on the focal length: 1.0 none, 0.8 a 0.8×
  /// reducer, 2.0 a 2× barlow. The daemon rejects ≤ 0 (it divides the scale).
  final double reducerFactor;

  /// Camera sensor width/height in pixels. 0 = unset.
  final int sensorWidthPx;
  final int sensorHeightPx;

  /// Sensor pixel pitch in microns. 0 = unset.
  final double pixelSizeUm;

  const OpticsSettings({
    this.focalLengthMm = 0,
    this.reducerFactor = 1.0,
    this.sensorWidthPx = 0,
    this.sensorHeightPx = 0,
    this.pixelSizeUm = 0,
  });

  /// True once every input the FOV box needs is a usable positive value. The
  /// daemon's "unset" defaults (focal length / sensor / pixel size 0) make this
  /// false so the overlay shows a "set up your optics" prompt, not a fake box.
  bool get isConfigured =>
      focalLengthMm > 0 &&
      reducerFactor > 0 &&
      sensorWidthPx > 0 &&
      sensorHeightPx > 0 &&
      pixelSizeUm > 0;

  /// Effective focal length after the reducer/barlow, in mm.
  double get effectiveFocalLengthMm => focalLengthMm * reducerFactor;

  /// Image scale in arcseconds per pixel, or null when [isConfigured] is false.
  double? get pixelScaleArcsecPerPx => isConfigured
      ? 206.265 * pixelSizeUm / effectiveFocalLengthMm
      : null;

  /// Field-of-view width in arcminutes, or null when not configured.
  double? get fovWidthArcmin {
    final scale = pixelScaleArcsecPerPx;
    return scale == null ? null : sensorWidthPx * scale / 60.0;
  }

  /// Field-of-view height in arcminutes, or null when not configured.
  double? get fovHeightArcmin {
    final scale = pixelScaleArcsecPerPx;
    return scale == null ? null : sensorHeightPx * scale / 60.0;
  }

  OpticsSettings copyWith({
    double? focalLengthMm,
    double? reducerFactor,
    int? sensorWidthPx,
    int? sensorHeightPx,
    double? pixelSizeUm,
  }) =>
      OpticsSettings(
        focalLengthMm: focalLengthMm ?? this.focalLengthMm,
        reducerFactor: reducerFactor ?? this.reducerFactor,
        sensorWidthPx: sensorWidthPx ?? this.sensorWidthPx,
        sensorHeightPx: sensorHeightPx ?? this.sensorHeightPx,
        pixelSizeUm: pixelSizeUm ?? this.pixelSizeUm,
      );
}

class OpticsSettingsNotifier extends Notifier<OpticsSettings> {
  @override
  OpticsSettings build() => const OpticsSettings();

  // Setters validate at the boundary so the daemon never sees a physically
  // impossible value. 0 is allowed (it means "unset") except for the reducer,
  // which multiplies the focal length in the pixel-scale denominator.
  // NOTE: a rejection here is silent (the setter no-ops), so the future Settings
  // UI must mirror these guards in its own input validation — otherwise a rejected
  // value leaves the text field and the stored state showing different numbers.
  void setFocalLengthMm(double v) {
    if (v < 0) return;
    state = state.copyWith(focalLengthMm: v);
  }

  void setReducerFactor(double v) {
    if (v <= 0) return;
    state = state.copyWith(reducerFactor: v);
  }

  void setSensorWidthPx(int v) {
    if (v < 0) return;
    state = state.copyWith(sensorWidthPx: v);
  }

  void setSensorHeightPx(int v) {
    if (v < 0) return;
    state = state.copyWith(sensorHeightPx: v);
  }

  void setPixelSizeUm(double v) {
    if (v < 0) return;
    state = state.copyWith(pixelSizeUm: v);
  }

  /// Replace local state with what the daemon currently holds. Called when the
  /// Planning/Settings surface mounts. Errors bubble up for snackbar UI.
  Future<void> hydrateFromServer(ProfileApi api) async {
    state = await api.getOptics();
  }

  /// Send the current local state to the daemon; returns its echo so the caller
  /// can confirm (and update state if the daemon normalized any field).
  Future<OpticsSettings> persistToServer(ProfileApi api) async {
    final echoed = await api.putOptics(state);
    state = echoed;
    return echoed;
  }
}

final opticsSettingsProvider =
    NotifierProvider<OpticsSettingsNotifier, OpticsSettings>(
        OpticsSettingsNotifier.new);
