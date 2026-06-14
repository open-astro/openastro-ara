/// Client model for the §43 backup feature: one ZIP [BackupSnapshot] from
/// `GET /api/v1/backup/snapshots` (the daemon's `BackupZipDto`). Snake_case wire;
/// defensive parse — missing/wrong-typed fields degrade rather than throw.
library;

String? _str(dynamic v) => v is String ? v : null;
int _int(dynamic v) => v is int ? v : (v is num ? v.toInt() : 0);
DateTime? _dt(dynamic v) => v is String ? DateTime.tryParse(v)?.toUtc() : null;

/// One backup ZIP snapshot — client mirror of the daemon's `BackupZipDto`.
class BackupSnapshot {
  final String backupId;
  final DateTime? createdUtc;
  final int sizeBytes;
  final String sha256;

  /// The relative download path the daemon hands back
  /// (`/api/v1/backup/snapshot/{id}/download`). Resolve against the server base
  /// URL to fetch; it's also the supported restore source URL.
  final String downloadUrl;

  /// Which config areas this snapshot captured (e.g. `["profiles","sequences"]`).
  final List<String> includedAreas;

  const BackupSnapshot({
    required this.backupId,
    this.createdUtc,
    this.sizeBytes = 0,
    this.sha256 = '',
    this.downloadUrl = '',
    this.includedAreas = const <String>[],
  });

  factory BackupSnapshot.fromJson(Map<String, dynamic> json) {
    final areas = json['included_areas'];
    return BackupSnapshot(
      backupId: _str(json['backup_id']) ?? '',
      createdUtc: _dt(json['created_utc']),
      sizeBytes: _int(json['size_bytes']),
      sha256: _str(json['sha256']) ?? '',
      downloadUrl: _str(json['download_url']) ?? '',
      includedAreas: areas is List
          ? areas.whereType<String>().toList(growable: false)
          : const <String>[],
    );
  }

  @override
  bool operator ==(Object other) =>
      other is BackupSnapshot &&
      other.backupId == backupId &&
      other.createdUtc == createdUtc &&
      other.sizeBytes == sizeBytes &&
      other.sha256 == sha256 &&
      other.downloadUrl == downloadUrl &&
      _listEq(other.includedAreas, includedAreas);

  @override
  int get hashCode => Object.hash(
      backupId, createdUtc, sizeBytes, sha256, downloadUrl, Object.hashAll(includedAreas));

  static bool _listEq(List<String> a, List<String> b) {
    if (a.length != b.length) return false;
    for (var i = 0; i < a.length; i++) {
      if (a[i] != b[i]) return false;
    }
    return true;
  }
}
