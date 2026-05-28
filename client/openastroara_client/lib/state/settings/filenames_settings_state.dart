import 'package:flutter_riverpod/flutter_riverpod.dart';

/// §29.2 File-saving naming options that don't already live in
/// `StorageSettings`. Keeps state non-overlapping with the storage panel:
///   - dateSeparator: how `$$DATE*$$` tokens render (`/` = directory hop,
///     `_` = flat filename)
///   - compressDarksAndBias: applies RICE to bias/dark frames (default on
///     since they're highly compressible and don't lose information)
///
/// The actual filename template + file format + compression live in
/// `StorageSettings` (the storage panel is the canonical edit point).
/// 12h.2b round-trips both via `/api/v1/profile/storage`.

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
}

final filenamesSettingsProvider =
    NotifierProvider<FilenamesSettingsNotifier, FilenamesSettings>(
        FilenamesSettingsNotifier.new);
