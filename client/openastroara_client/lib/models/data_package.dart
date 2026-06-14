/// Client models for the §36 Data Manager: a curated sky-data [DataPackage]
/// (from `GET /api/v1/data-manager/packages`) and live [DownloadProgress] folded
/// from the `data_manager.download.*` WS stream. Snake_case wire; defensive
/// parse — missing/wrong-typed fields degrade rather than throw.
library;

// File-level defensive extractors shared by both models below.
String? _str(dynamic v) => v is String ? v : null;
bool _bool(dynamic v) => v is bool ? v : false;
int? _int(dynamic v) => v is int ? v : (v is num ? v.toInt() : null);
DateTime? _dt(dynamic v) => v is String ? DateTime.tryParse(v)?.toUtc() : null;
double? _double(dynamic v) {
  final d = v is num ? v.toDouble() : null;
  return (d != null && d.isFinite) ? d : null;
}

/// One curated sky-data package — client mirror of the daemon's `DataPackageDto`.
class DataPackage {
  final String id;
  final String name;
  final String description;
  final String category;
  final int sizeBytes;
  final String version;
  final bool isInstalled;
  final DateTime? installedUtc;
  final String? sourceUrl;

  const DataPackage({
    required this.id,
    this.name = '',
    this.description = '',
    this.category = '',
    this.sizeBytes = 0,
    this.version = '',
    this.isInstalled = false,
    this.installedUtc,
    this.sourceUrl,
  });

  factory DataPackage.fromJson(Map<String, dynamic> json) {
    return DataPackage(
      id: _str(json['id']) ?? '',
      name: _str(json['name']) ?? '',
      description: _str(json['description']) ?? '',
      category: _str(json['category']) ?? '',
      sizeBytes: _int(json['size_bytes']) ?? 0,
      version: _str(json['version']) ?? '',
      isInstalled: _bool(json['is_installed']),
      installedUtc: _dt(json['installed_utc']),
      sourceUrl: _str(json['source_url']),
    );
  }

  @override
  bool operator ==(Object other) =>
      other is DataPackage &&
      other.id == id &&
      other.name == name &&
      other.description == description &&
      other.category == category &&
      other.sizeBytes == sizeBytes &&
      other.version == version &&
      other.isInstalled == isInstalled &&
      other.installedUtc == installedUtc &&
      other.sourceUrl == sourceUrl;

  @override
  int get hashCode => Object.hash(
      id, name, description, category, sizeBytes, version, isInstalled, installedUtc, sourceUrl);
}

/// Where a tracked download is in its lifecycle (from the WS event subtype).
enum DownloadPhase { downloading, complete, failed }

/// Live progress for an in-flight or just-finished download, folded from the
/// `data_manager.download.{progress,complete,failed}` WS events. Keyed by
/// package id in the state map; carries the [downloadId] needed to cancel.
class DownloadProgress {
  final String downloadId;
  final String packageId;
  final int downloadedBytes;
  final int totalBytes;
  final double percentComplete;
  final DownloadPhase phase;

  /// Failure reason when [phase] is [DownloadPhase.failed] (e.g. "cancelled",
  /// "stalled", or a transport message); null otherwise.
  final String? error;

  const DownloadProgress({
    required this.downloadId,
    required this.packageId,
    this.downloadedBytes = 0,
    this.totalBytes = -1,
    this.percentComplete = 0,
    this.phase = DownloadPhase.downloading,
    this.error,
  });

  bool get isActive => phase == DownloadPhase.downloading;

  /// Parse a `data_manager.download.*` WS payload into progress for the given
  /// [phase] (derived from the event subtype). Returns null if the payload has
  /// no usable package/download id.
  static DownloadProgress? fromPayload(Map<String, dynamic> payload, DownloadPhase phase) {
    final downloadId = _str(payload['download_id']);
    final packageId = _str(payload['package_id']);
    if (downloadId == null || packageId == null) {
      return null;
    }
    return DownloadProgress(
      downloadId: downloadId,
      packageId: packageId,
      downloadedBytes: _int(payload['downloaded_bytes']) ?? 0,
      totalBytes: _int(payload['total_bytes']) ?? -1,
      percentComplete: _double(payload['percent_complete']) ?? 0,
      phase: phase,
      error: _str(payload['error']),
    );
  }

  @override
  bool operator ==(Object other) =>
      other is DownloadProgress &&
      other.downloadId == downloadId &&
      other.packageId == packageId &&
      other.downloadedBytes == downloadedBytes &&
      other.totalBytes == totalBytes &&
      other.percentComplete == percentComplete &&
      other.phase == phase &&
      other.error == error;

  @override
  int get hashCode =>
      Object.hash(downloadId, packageId, downloadedBytes, totalBytes, percentComplete, phase, error);
}
