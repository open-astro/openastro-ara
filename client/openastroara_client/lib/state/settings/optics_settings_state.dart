import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'settings_sync_mixin.dart';

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

  /// Telescope objective diameter in mm (NEXTGEN §4). Feeds the Optimal-Sub
  /// sky-flux aperture-area term — NOT the FOV box, so it deliberately does
  /// not participate in [isConfigured]. 0 = unset (exposure advice is simply
  /// unavailable until it's set; framing keeps working without it).
  final double apertureMm;

  const OpticsSettings({
    this.focalLengthMm = 0,
    this.reducerFactor = 1.0,
    this.sensorWidthPx = 0,
    this.sensorHeightPx = 0,
    this.pixelSizeUm = 0,
    this.apertureMm = 0,
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

  /// True once the aperture is set — the extra gate the Optimal-Sub exposure
  /// advice needs on top of [isConfigured] (kept separate so an aperture-less
  /// profile still frames normally).
  bool get hasAperture => apertureMm > 0;

  /// Effective focal length after the reducer/barlow, in mm — or null when the
  /// rig isn't fully configured. Nullable (not 0-when-unset) so a caller can't
  /// silently divide by an unset focal length; the whole geometry API is
  /// uniformly null until [isConfigured].
  double? get effectiveFocalLengthMm =>
      isConfigured ? focalLengthMm * reducerFactor : null;

  /// Image scale in arcseconds per pixel, or null when [isConfigured] is false.
  double? get pixelScaleArcsecPerPx {
    final efl = effectiveFocalLengthMm;
    return efl == null ? null : 206.265 * pixelSizeUm / efl;
  }

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
    double? apertureMm,
  }) =>
      OpticsSettings(
        focalLengthMm: focalLengthMm ?? this.focalLengthMm,
        reducerFactor: reducerFactor ?? this.reducerFactor,
        sensorWidthPx: sensorWidthPx ?? this.sensorWidthPx,
        sensorHeightPx: sensorHeightPx ?? this.sensorHeightPx,
        pixelSizeUm: pixelSizeUm ?? this.pixelSizeUm,
        apertureMm: apertureMm ?? this.apertureMm,
      );
}

class OpticsSettingsNotifier extends Notifier<OpticsSettings>
    with SettingsSyncMixin<OpticsSettings> {
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

  void setApertureMm(double v) {
    if (v < 0) return;
    state = state.copyWith(apertureMm: v);
  }

  /// Replace local state with what the daemon currently holds. Called when the
  /// Planning/Settings surface mounts. Errors bubble up for snackbar UI.
  Future<void> hydrateFromServer(ProfileApi api) =>
      hydrateGuarded(() => api.getOptics());

  /// Send the current local state to the daemon; returns its echo so the caller
  /// can confirm (and update state if the daemon normalized any field).
  Future<OpticsSettings> persistToServer(ProfileApi api) =>
      persistGuarded((sent) => api.putOptics(sent));
}

final opticsSettingsProvider =
    NotifierProvider<OpticsSettingsNotifier, OpticsSettings>(
        OpticsSettingsNotifier.new);
