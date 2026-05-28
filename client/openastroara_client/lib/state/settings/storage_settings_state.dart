import 'package:flutter_riverpod/flutter_riverpod.dart';

/// §29 Storage settings — save directory + file format + compression +
/// filename template. Phase 12h.2-storage holds the values in memory;
/// 12h.2b will wire `/api/v1/profile/storage` for daemon round-trip.

enum StorageFileFormat { fits, xisf, fitsRice, fitsGzip }
enum StorageCompression { off, rice, gzip }

class StorageSettings {
  final String saveDirectory;
  final StorageFileFormat fileFormat;
  final StorageCompression compression;
  final String filenameTemplate;

  const StorageSettings({
    this.saveDirectory = '/media/openastroara',
    this.fileFormat = StorageFileFormat.fits,
    this.compression = StorageCompression.rice,
    this.filenameTemplate =
        r'$$DATEMINUS12$$\\$$IMAGETYPE$$\\$$DATETIME$$_$$FILTER$$_$$EXPOSURETIME$$s',
  });

  StorageSettings copyWith({
    String? saveDirectory,
    StorageFileFormat? fileFormat,
    StorageCompression? compression,
    String? filenameTemplate,
  }) =>
      StorageSettings(
        saveDirectory: saveDirectory ?? this.saveDirectory,
        fileFormat: fileFormat ?? this.fileFormat,
        compression: compression ?? this.compression,
        filenameTemplate: filenameTemplate ?? this.filenameTemplate,
      );
}

class StorageSettingsNotifier extends Notifier<StorageSettings> {
  @override
  StorageSettings build() => const StorageSettings();

  void setSaveDirectory(String s) {
    // Empty save directory would write to current dir at runtime — reject.
    if (s.isEmpty) return;
    state = state.copyWith(saveDirectory: s);
  }

  void setFileFormat(StorageFileFormat f) =>
      state = state.copyWith(fileFormat: f);

  void setCompression(StorageCompression c) =>
      state = state.copyWith(compression: c);

  void setFilenameTemplate(String t) {
    if (t.isEmpty) return;
    state = state.copyWith(filenameTemplate: t);
  }
}

final storageSettingsProvider =
    NotifierProvider<StorageSettingsNotifier, StorageSettings>(
        StorageSettingsNotifier.new);
