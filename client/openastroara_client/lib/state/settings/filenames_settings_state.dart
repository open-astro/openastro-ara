import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/profile_api.dart';

/// §29.2 File-saving naming options that don't already live in
/// `StorageSettings`. Keeps state non-overlapping with the storage panel:
///   - dateSeparator: how `$$DATE*$$` tokens render
///   - compressDarksAndBias: applies RICE to bias/dark frames
///
/// Phase 12h.6f wires the daemon round-trip via [ProfileApi]
/// (`/api/v1/profile/filenames`). The main filename template + file
/// format live in `StorageSettings` and round-trip via 12h.6c.

enum DateSeparator { forwardSlash, underscore, dash }

class FilenamesSettings {
  final DateSeparator dateSeparator;
  final bool compressDarksAndBias;

  const FilenamesSettings({
    this.dateSeparator = DateSeparator.forwardSlash,
    this.compressDarksAndBias = true,
  });

  FilenamesSettings copyWith({
    DateSeparator? dateSeparator,
    bool? compressDarksAndBias,
  }) =>
      FilenamesSettings(
        dateSeparator: dateSeparator ?? this.dateSeparator,
        compressDarksAndBias:
            compressDarksAndBias ?? this.compressDarksAndBias,
      );
}

class FilenamesSettingsNotifier extends Notifier<FilenamesSettings> {
  @override
  FilenamesSettings build() => const FilenamesSettings();

  void setDateSeparator(DateSeparator d) =>
      state = state.copyWith(dateSeparator: d);
  void setCompressDarksAndBias(bool v) =>
      state = state.copyWith(compressDarksAndBias: v);

  Future<void> hydrateFromServer(ProfileApi api) async {
    state = await api.getFilenamesSettings();
  }

  Future<FilenamesSettings> persistToServer(ProfileApi api) async {
    final echoed = await api.putFilenamesSettings(state);
    state = echoed;
    return echoed;
  }
}

final filenamesSettingsProvider =
    NotifierProvider<FilenamesSettingsNotifier, FilenamesSettings>(
        FilenamesSettingsNotifier.new);
