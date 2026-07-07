import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/profile_api.dart';

/// §29 Storage settings — save directory + file format + compression +
/// filename template. Phase 12h.6c wires the daemon round-trip via
/// [ProfileApi]: [StorageSettingsNotifier.hydrateFromServer] runs on
/// panel mount, [StorageSettingsNotifier.persistToServer] runs from the
/// Save button. Local state is still the source of truth between syncs.

enum StorageFileFormat { fits, xisf, fitsRice, fitsGzip }
enum StorageCompression { off, rice, gzip }

class StorageSettings {
  final String saveDirectory;
  final StorageFileFormat fileFormat;
  final StorageCompression compression;
  final String filenameTemplate;

  // §29 disk-space monitor thresholds (whole GiB free on the save volume).
  final int minFreeDiskWarnGb;
  final int minFreeDiskCriticalGb;

  // §43-2b — how many backup snapshots the daemon keeps; older ones are pruned
  // after each new backup. 0 = keep everything.
  final int backupRetentionCount;

  const StorageSettings({
    this.saveDirectory = '/media/openastroara',
    this.fileFormat = StorageFileFormat.fits,
    this.compression = StorageCompression.rice,
    this.filenameTemplate =
        r'$$DATEMINUS12$$\\$$IMAGETYPE$$\\$$DATETIME$$_$$FILTER$$_$$EXPOSURETIME$$s',
    this.minFreeDiskWarnGb = 10,
    this.minFreeDiskCriticalGb = 2,
    this.backupRetentionCount = 20,
  });

  StorageSettings copyWith({
    String? saveDirectory,
    StorageFileFormat? fileFormat,
    StorageCompression? compression,
    String? filenameTemplate,
    int? minFreeDiskWarnGb,
    int? minFreeDiskCriticalGb,
    int? backupRetentionCount,
  }) =>
      StorageSettings(
        saveDirectory: saveDirectory ?? this.saveDirectory,
        fileFormat: fileFormat ?? this.fileFormat,
        compression: compression ?? this.compression,
        filenameTemplate: filenameTemplate ?? this.filenameTemplate,
        minFreeDiskWarnGb: minFreeDiskWarnGb ?? this.minFreeDiskWarnGb,
        minFreeDiskCriticalGb: minFreeDiskCriticalGb ?? this.minFreeDiskCriticalGb,
        backupRetentionCount: backupRetentionCount ?? this.backupRetentionCount,
      );
}

class StorageSettingsNotifier extends Notifier<StorageSettings> {
  @override
  StorageSettings build() => const StorageSettings();

  void setSaveDirectory(String s) {
    // Empty save directory would write to current dir at runtime — reject.
    final v = s.trim();
    if (v.isEmpty) return;
    state = state.copyWith(saveDirectory: v);
  }

  void setFileFormat(StorageFileFormat f) =>
      state = state.copyWith(fileFormat: f);

  void setCompression(StorageCompression c) =>
      state = state.copyWith(compression: c);

  void setFilenameTemplate(String t) {
    final v = t.trim();
    if (v.isEmpty) return;
    state = state.copyWith(filenameTemplate: v);
  }

  // §29 — thresholds are whole GiB ≥ 1. Each field validates independently (so editing one doesn't snap back
  // just because it momentarily crosses the other); the critical-below-warn invariant is checked at save time
  // ([thresholdsValid]) with a visible error, and the server rejects an invalid pair with a 400.
  void setMinFreeDiskWarnGb(int v) {
    if (v < 1) return;
    state = state.copyWith(minFreeDiskWarnGb: v);
  }

  void setMinFreeDiskCriticalGb(int v) {
    if (v < 1) return;
    state = state.copyWith(minFreeDiskCriticalGb: v);
  }

  // §43-2b — 0 is meaningful (keep everything); negatives are rejected like the
  // server's 400 (backup_retention_count must be >= 0).
  void setBackupRetentionCount(int v) {
    if (v < 0) return;
    state = state.copyWith(backupRetentionCount: v);
  }

  /// Whether the current pair satisfies critical &lt; warn (checked before persisting).
  bool get thresholdsValid => state.minFreeDiskCriticalGb < state.minFreeDiskWarnGb;

  Future<void> hydrateFromServer(ProfileApi api) async {
    state = await api.getStorageSettings();
  }

  Future<StorageSettings> persistToServer(ProfileApi api) async {
    final echoed = await api.putStorageSettings(state);
    state = echoed;
    return echoed;
  }
}

final storageSettingsProvider =
    NotifierProvider<StorageSettingsNotifier, StorageSettings>(
        StorageSettingsNotifier.new);
