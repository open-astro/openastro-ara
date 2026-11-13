import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'loaded_section.dart';

import '../../services/profile_api.dart';

/// §29 Storage settings — save directory + file format + compression +
/// filename template. ADR 0015: the section is loaded from the rig before it
/// can be saved ([SectionNotifier]); until then it holds no value, not defaults.

/// The on-disk frame container (#1148). FITS is always written
/// uncompressed; XISF is PixInsight's native format and the one that
/// compresses. The FITS + RICE / FITS + gzip formats are gone (PixInsight
/// can't open tile-compressed FITS): a stored `fits_rice`/`fits_gzip` reads
/// as plain FITS, as the daemon does.
enum StorageFileFormat {
  fits('fits'),
  xisf('xisf');

  const StorageFileFormat(this.wire);

  /// The §29 wire token.
  final String wire;

  /// Any token but `xisf` (incl. the legacy `fits_rice`/`fits_gzip`) is FITS.
  static StorageFileFormat fromWire(String? s) =>
      s?.trim().toLowerCase() == 'xisf' ? xisf : fits;
}

/// XISF compression (#1148): off, or lossless Zstandard with byte shuffling.
/// Legacy `rice`/`gzip` meant "compress", so they read as [zstd].
enum StorageCompression {
  off('off'),
  zstd('zstd');

  const StorageCompression(this.wire);

  /// The §29 wire token.
  final String wire;

  /// `off` stays off; anything else (incl. `rice`/`gzip`, or no value) is [zstd].
  static StorageCompression fromWire(String? s) =>
      s?.trim().toLowerCase() == 'off' ? off : zstd;
}

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

  // §42.5 — how many days of fault-log rows the daemon keeps; older rows are
  // pruned once a day. 0 = keep everything (#1145).
  final int faultLogRetentionDays;

  // No defaults: the daemon's normalizer is the one owner (ADR 0012).
  const StorageSettings({
    required this.saveDirectory,
    required this.fileFormat,
    required this.compression,
    required this.filenameTemplate,
    required this.minFreeDiskWarnGb,
    required this.minFreeDiskCriticalGb,
    required this.backupRetentionCount,
    required this.faultLogRetentionDays,
  });

  StorageSettings copyWith({
    String? saveDirectory,
    StorageFileFormat? fileFormat,
    StorageCompression? compression,
    String? filenameTemplate,
    int? minFreeDiskWarnGb,
    int? minFreeDiskCriticalGb,
    int? backupRetentionCount,
    int? faultLogRetentionDays,
  }) =>
      StorageSettings(
        saveDirectory: saveDirectory ?? this.saveDirectory,
        fileFormat: fileFormat ?? this.fileFormat,
        compression: compression ?? this.compression,
        filenameTemplate: filenameTemplate ?? this.filenameTemplate,
        minFreeDiskWarnGb: minFreeDiskWarnGb ?? this.minFreeDiskWarnGb,
        minFreeDiskCriticalGb: minFreeDiskCriticalGb ?? this.minFreeDiskCriticalGb,
        backupRetentionCount: backupRetentionCount ?? this.backupRetentionCount,
        faultLogRetentionDays:
            faultLogRetentionDays ?? this.faultLogRetentionDays,
      );
}

class StorageSettingsNotifier extends SectionNotifier<StorageSettings> {
  @override
  Future<Tagged<StorageSettings>> fetch(ProfileApi api) =>
      api.getStorageSection();

  @override
  Future<Tagged<StorageSettings>> send(
    ProfileApi api,
    StorageSettings value,
    String? etag,
  ) => api.putStorageSection(value, ifMatch: etag);

  void setSaveDirectory(String s) {
    // Empty save directory would write to current dir at runtime — reject.
    final v = s.trim();
    if (v.isEmpty) return;
    edit((c) => c.copyWith(saveDirectory: v));
  }

  void setFileFormat(StorageFileFormat f) =>
      edit((c) => c.copyWith(fileFormat: f));

  void setCompression(StorageCompression c) =>
      edit((s) => s.copyWith(compression: c));

  void setFilenameTemplate(String t) {
    final v = t.trim();
    if (v.isEmpty) return;
    edit((c) => c.copyWith(filenameTemplate: v));
  }

  // §29 — thresholds are whole GiB ≥ 1. Each field validates independently (so editing one doesn't snap back
  // just because it momentarily crosses the other); the critical-below-warn invariant is checked at save time
  // ([thresholdsValid]) with a visible error, and the server rejects an invalid pair with a 400.
  void setMinFreeDiskWarnGb(int v) {
    if (v < 1) return;
    edit((c) => c.copyWith(minFreeDiskWarnGb: v));
  }

  void setMinFreeDiskCriticalGb(int v) {
    if (v < 1) return;
    edit((c) => c.copyWith(minFreeDiskCriticalGb: v));
  }

  // §43-2b — 0 is meaningful (keep everything); negatives are rejected like the
  // server's 400 (backup_retention_count must be >= 0).
  void setBackupRetentionCount(int v) {
    if (v < 0) return;
    edit((c) => c.copyWith(backupRetentionCount: v));
  }

  // §42.5 — same contract as the backup count: 0 keeps everything, negatives
  // are rejected like the server's 400 (fault_log_retention_days must be >= 0).
  void setFaultLogRetentionDays(int v) {
    if (v < 0) return;
    edit((c) => c.copyWith(faultLogRetentionDays: v));
  }

  /// Whether the current pair satisfies critical &lt; warn (checked before persisting).
  /// True while nothing is loaded: the load-before-save rule refuses that case.
  bool get thresholdsValid {
    final v = state.value;
    return v == null || v.minFreeDiskCriticalGb < v.minFreeDiskWarnGb;
  }
}

final storageSettingsProvider =
    NotifierProvider<StorageSettingsNotifier, LoadedSection<StorageSettings>>(
      StorageSettingsNotifier.new,
    );
