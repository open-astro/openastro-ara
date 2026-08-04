import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'settings_sync_mixin.dart';

import '../../services/profile_api.dart';

/// §29.2 File-saving naming options that don't already live in
/// `StorageSettings`. Keeps state non-overlapping with the storage panel:
///   - dateSeparator: how `$$DATE*$$` tokens render (`/` = directory hop,
///     `_` = flat filename)
///   - compressDarksAndBias: applies RICE to bias/dark frames (default on
///     since they're highly compressible and don't lose information)
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

  const FilenamesSettings({
    this.dateSeparator = DateSeparator.forwardSlash,
    this.compressDarksAndBias = true,
    this.headerIdentity = true,
    this.headerSite = true,
    this.headerOptics = true,
    this.headerTemperature = true,
    this.headerWeather = true,
  });

  FilenamesSettings copyWith({
    DateSeparator? dateSeparator,
    bool? compressDarksAndBias,
    bool? headerIdentity,
    bool? headerSite,
    bool? headerOptics,
    bool? headerTemperature,
    bool? headerWeather,
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
      );
}

class FilenamesSettingsNotifier extends Notifier<FilenamesSettings>
    with SettingsSyncMixin<FilenamesSettings> {
  @override
  FilenamesSettings build() => const FilenamesSettings();

  void setDateSeparator(DateSeparator d) =>
      state = state.copyWith(dateSeparator: d);
  void setCompressDarksAndBias(bool v) =>
      state = state.copyWith(compressDarksAndBias: v);
  void setHeaderIdentity(bool v) => state = state.copyWith(headerIdentity: v);
  void setHeaderSite(bool v) => state = state.copyWith(headerSite: v);
  void setHeaderOptics(bool v) => state = state.copyWith(headerOptics: v);
  void setHeaderTemperature(bool v) =>
      state = state.copyWith(headerTemperature: v);
  void setHeaderWeather(bool v) => state = state.copyWith(headerWeather: v);

  Future<void> hydrateFromServer(ProfileApi api) =>
      hydrateGuarded(() => api.getFilenamesSettings());

  Future<FilenamesSettings> persistToServer(ProfileApi api) =>
      persistGuarded((sent) => api.putFilenamesSettings(sent));
}

final filenamesSettingsProvider =
    NotifierProvider<FilenamesSettingsNotifier, FilenamesSettings>(
        FilenamesSettingsNotifier.new);
