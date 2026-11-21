import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'loaded_section.dart';

import '../../services/profile_api.dart';

/// NEXTGEN §3/§4 camera electronics — the exposure-planning inputs behind the
/// Optimal-Sub calculator (criterion popularised by Dr. Robin Glover of
/// SharpCap): read noise, full well, e⁻/ADU, gain, QE peak. Backed by the
/// daemon's `GET`/`PUT /api/v1/profile/camera-electronics`.
///
/// The ASCOM-sourced fields (sensor name, full well, e⁻/ADU, gain — all for
/// the camera's CURRENT readout mode) auto-capture on camera connect;
/// [autoCaptured] records that provenance. Read noise and QE peak are never
/// in ASCOM and are always user-entered (manufacturer gain chart / SharpCap
/// sensor analysis). 0 / −1 / '' mean unset — the daemon's planning falls
/// back to generic-CMOS defaults and says so via `assumed_defaults`.
class CameraElectronics {
  /// Sensor model reported by the camera driver (e.g. 'IMX571'). '' = unset.
  final String sensorName;

  /// Read noise in e⁻ RMS at [gain]. 0 = unset. Never ASCOM-sourced.
  final double readNoiseE;

  /// Full-well capacity in e⁻ for the current readout mode. 0 = unset.
  final double fullWellE;

  /// Conversion gain in e⁻/ADU. 0 = unset.
  final double electronsPerAdu;

  /// The gain setting the electronics values apply at. −1 = unset.
  final int gain;

  /// Peak quantum efficiency as a fraction (0–1). 0 = unset. User-entered.
  final double quantumEfficiencyPeak;

  /// True when the ASCOM-sourced fields came from a connected camera.
  final bool autoCaptured;

  const CameraElectronics({
    this.sensorName = '',
    this.readNoiseE = 0,
    this.fullWellE = 0,
    this.electronsPerAdu = 0,
    this.gain = -1,
    this.quantumEfficiencyPeak = 0,
    this.autoCaptured = false,
  });

  CameraElectronics copyWith({
    String? sensorName,
    double? readNoiseE,
    double? fullWellE,
    double? electronsPerAdu,
    int? gain,
    double? quantumEfficiencyPeak,
    bool? autoCaptured,
  }) => CameraElectronics(
    sensorName: sensorName ?? this.sensorName,
    readNoiseE: readNoiseE ?? this.readNoiseE,
    fullWellE: fullWellE ?? this.fullWellE,
    electronsPerAdu: electronsPerAdu ?? this.electronsPerAdu,
    gain: gain ?? this.gain,
    quantumEfficiencyPeak: quantumEfficiencyPeak ?? this.quantumEfficiencyPeak,
    autoCaptured: autoCaptured ?? this.autoCaptured,
  );
}

class CameraElectronicsNotifier extends SectionNotifier<CameraElectronics> {
  // Boundary validation mirrors the daemon's PUT guards (finite, >= 0; QE in
  // [0, 1]; gain >= -1) so a rejected value can't silently desync field/state.
  void setSensorName(String v) => edit((c) => c.copyWith(sensorName: v));

  void setReadNoiseE(double v) {
    if (v < 0) return;
    // A manual edit takes ownership — but read noise was never ASCOM's, so it
    // doesn't clear the auto-captured provenance of the other fields.
    edit((c) => c.copyWith(readNoiseE: v));
  }

  void setFullWellE(double v) {
    if (v < 0) return;
    edit((c) => c.copyWith(fullWellE: v, autoCaptured: false));
  }

  void setElectronsPerAdu(double v) {
    if (v < 0) return;
    edit((c) => c.copyWith(electronsPerAdu: v, autoCaptured: false));
  }

  void setGain(int v) {
    if (v < -1) return;
    edit((c) => c.copyWith(gain: v, autoCaptured: false));
  }

  void setQuantumEfficiencyPeak(double v) {
    if (v < 0 || v > 1) return;
    edit((c) => c.copyWith(quantumEfficiencyPeak: v));
  }

  @override
  Future<Tagged<CameraElectronics>> fetch(ProfileApi api) =>
      api.getCameraElectronicsSection();

  @override
  Future<Tagged<CameraElectronics>> send(
    ProfileApi api,
    CameraElectronics value,
    String? etag,
  ) => api.putCameraElectronicsSection(value, ifMatch: etag);
}

final cameraElectronicsProvider =
    NotifierProvider<
      CameraElectronicsNotifier,
      LoadedSection<CameraElectronics>
    >(CameraElectronicsNotifier.new);
