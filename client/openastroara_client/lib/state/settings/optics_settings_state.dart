import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'loaded_section.dart';

import '../../services/profile_api.dart';

/// §36/§25.5 imaging-train optics + sensor geometry — the inputs the Planning
/// tab's Frame mode needs to draw the field-of-view box without a live camera.
/// Backed by the daemon's `GET`/`PUT /api/v1/profile/optics` section (#467),
/// through the section module (loaded_section.dart): the rig's copy, loaded
/// before it is saved. This is the model + geometry the FOV overlay consumes.
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

  // §29.2 — goes in every frame's TELESCOP header. Empty = header omitted.
  final String telescopeName;

  const OpticsSettings({
    this.focalLengthMm = 0,
    this.reducerFactor = 1.0,
    this.sensorWidthPx = 0,
    this.sensorHeightPx = 0,
    this.pixelSizeUm = 0,
    this.apertureMm = 0,
    this.telescopeName = '',
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
    String? telescopeName,
  }) => OpticsSettings(
    focalLengthMm: focalLengthMm ?? this.focalLengthMm,
    reducerFactor: reducerFactor ?? this.reducerFactor,
    sensorWidthPx: sensorWidthPx ?? this.sensorWidthPx,
    sensorHeightPx: sensorHeightPx ?? this.sensorHeightPx,
    pixelSizeUm: pixelSizeUm ?? this.pixelSizeUm,
    apertureMm: apertureMm ?? this.apertureMm,
    telescopeName: telescopeName ?? this.telescopeName,
  );
}

/// ADR 0015 — a value only once the rig has answered. Loads itself: planning,
/// Tonight's Sky, Simple Mode's Rig page and the guider's OAG focal length
/// read it with no Settings panel open first. The daemon's own writes (a
/// camera or mount connect, a plate solve) reach it as `profile.updated`.
class OpticsSettingsNotifier extends SelfLoadingSection<OpticsSettings> {
  // Setters validate at the boundary so the daemon never sees a physically
  // impossible value. 0 is allowed (it means "unset") except for the reducer,
  // which multiplies the focal length in the pixel-scale denominator.
  // NOTE: a rejection here is silent (the setter no-ops), so the Settings UI
  // mirrors these guards in its own input validation — otherwise a rejected
  // value leaves the text field and the stored state showing different numbers.
  void setFocalLengthMm(double v) {
    if (v < 0) return;
    edit((c) => c.copyWith(focalLengthMm: v));
  }

  void setReducerFactor(double v) {
    if (v <= 0) return;
    edit((c) => c.copyWith(reducerFactor: v));
  }

  void setSensorWidthPx(int v) {
    if (v < 0) return;
    edit((c) => c.copyWith(sensorWidthPx: v));
  }

  void setSensorHeightPx(int v) {
    if (v < 0) return;
    edit((c) => c.copyWith(sensorHeightPx: v));
  }

  void setPixelSizeUm(double v) {
    if (v < 0) return;
    edit((c) => c.copyWith(pixelSizeUm: v));
  }

  void setApertureMm(double v) {
    if (v < 0) return;
    edit((c) => c.copyWith(apertureMm: v));
  }

  void setTelescopeName(String v) =>
      edit((c) => c.copyWith(telescopeName: v.trim()));

  @override
  Future<Tagged<OpticsSettings>> fetch(ProfileApi api) =>
      api.getOpticsSection();

  @override
  Future<Tagged<OpticsSettings>> send(
    ProfileApi api,
    OpticsSettings value,
    String? etag,
  ) => api.putOpticsSection(value, ifMatch: etag);
}

final opticsSettingsProvider =
    NotifierProvider<OpticsSettingsNotifier, LoadedSection<OpticsSettings>>(
      OpticsSettingsNotifier.new,
    );
