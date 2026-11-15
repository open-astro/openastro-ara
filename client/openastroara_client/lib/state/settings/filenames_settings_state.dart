import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'loaded_section.dart';

import '../../services/profile_api.dart';

/// §29.2 File-saving naming options that don't already live in
/// `StorageSettings`. Keeps state non-overlapping with the storage panel:
///   - dateSeparator: how `$$DATE*$$` tokens render (`/` = directory hop,
///     `_` = flat filename)
///   - compressDarksAndBias: XISF only — bias/dark frames are written with
///     lossless Zstandard even when Storage compression is off (default on
///     since they're highly compressible and don't lose information); FITS
///     is always uncompressed (#1148)
///
/// Phase 12h.6f wires the daemon round-trip via [ProfileApi]
/// (`/api/v1/profile/filenames`). The main filename template + file
/// format live in `StorageSettings` and round-trip via 12h.6c.

enum DateSeparator { forwardSlash, underscore, dash }

class FilenamesSettings {
  final DateSeparator dateSeparator;
  final bool compressDarksAndBias;

  // §29.2 — which optional FITS header groups every frame carries. All on by
  // default; each off-switch exists for a reason (Site above all: coordinates
  // in a shared frame reveal where you live).
  final bool headerIdentity;
  final bool headerSite;
  final bool headerOptics;
  final bool headerTemperature;
  final bool headerWeather;
  final bool headerEphemeris;

  const FilenamesSettings({
    this.dateSeparator = DateSeparator.forwardSlash,
    this.compressDarksAndBias = true,
    this.headerIdentity = true,
    this.headerSite = true,
    this.headerOptics = true,
    this.headerTemperature = true,
    this.headerWeather = true,
    this.headerEphemeris = true,
  });

  FilenamesSettings copyWith({
    DateSeparator? dateSeparator,
    bool? compressDarksAndBias,
    bool? headerIdentity,
    bool? headerSite,
    bool? headerOptics,
    bool? headerTemperature,
    bool? headerWeather,
    bool? headerEphemeris,
  }) =>
      FilenamesSettings(
        dateSeparator: dateSeparator ?? this.dateSeparator,
        compressDarksAndBias:
            compressDarksAndBias ?? this.compressDarksAndBias,
        headerIdentity: headerIdentity ?? this.headerIdentity,
        headerSite: headerSite ?? this.headerSite,
        headerOptics: headerOptics ?? this.headerOptics,
        headerTemperature: headerTemperature ?? this.headerTemperature,
        headerWeather: headerWeather ?? this.headerWeather,
        headerEphemeris: headerEphemeris ?? this.headerEphemeris,
      );
}

class FilenamesSettingsNotifier extends SectionNotifier<FilenamesSettings> {
  void setDateSeparator(DateSeparator d) =>
      edit((c) => c.copyWith(dateSeparator: d));
  void setCompressDarksAndBias(bool v) =>
      edit((c) => c.copyWith(compressDarksAndBias: v));
  void setHeaderIdentity(bool v) => edit((c) => c.copyWith(headerIdentity: v));
  void setHeaderSite(bool v) => edit((c) => c.copyWith(headerSite: v));
  void setHeaderOptics(bool v) => edit((c) => c.copyWith(headerOptics: v));
  void setHeaderTemperature(bool v) =>
      edit((c) => c.copyWith(headerTemperature: v));
  void setHeaderWeather(bool v) => edit((c) => c.copyWith(headerWeather: v));
  void setHeaderEphemeris(bool v) =>
      edit((c) => c.copyWith(headerEphemeris: v));

  @override
  Future<Tagged<FilenamesSettings>> fetch(ProfileApi api) =>
      api.getFilenamesSection();

  @override
  Future<Tagged<FilenamesSettings>> send(
    ProfileApi api,
    FilenamesSettings value,
    String? etag,
  ) => api.putFilenamesSection(value, ifMatch: etag);
}

final filenamesSettingsProvider =
    NotifierProvider<
      FilenamesSettingsNotifier,
      LoadedSection<FilenamesSettings>
    >(FilenamesSettingsNotifier.new);
