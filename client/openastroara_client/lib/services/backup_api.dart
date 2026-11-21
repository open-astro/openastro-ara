import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';

import '../models/backup_snapshot.dart';
import '../models/clone_status.dart';
import '../models/server.dart';

/// The §43 backup operations the state layer depends on. An interface so tests
/// can supply a pure fake (no Dio); [BackupApi] is the Dio-backed production
/// implementation.
abstract interface class BackupClient {
  /// The profile's ZIP snapshots, newest first.
  Future<List<BackupSnapshot>> listSnapshots();

  /// Create a backup of the profile config areas. Returns the accepted
  /// operation id — since §43-2 the packaging runs on a daemon-side worker, so
  /// poll [createStatus] for the terminal outcome. Throws when there's nothing
  /// to back up (422), another create is running (409), the disk is too full
  /// (507), or on a transport error.
  Future<String> createBackup();

  /// The create worker's live state (§43-2) — poll after [createBackup] until
  /// it reaches a terminal state (`done`/`failed`). Same state machine shape as
  /// [cloneStatus]. Throws on a transport error or a non-object body.
  Future<CloneStatus> createStatus();

  /// Restore the selected areas from a snapshot's [downloadUrl]. Returns the
  /// accepted operation id. Throws on an unknown snapshot (404), an unsupported
  /// source / no area selected (422), or a transport error. A corrupt archive
  /// surfaces via [cloneStatus] `failed` (§43-2b(c): the checksum runs on the
  /// worker now that archives carry the catalog snapshot).
  Future<String> restore({
    required String sourceUrl,
    required bool profiles,
    required bool sequences,
    required bool frameMetadata,
  });

  /// The restore worker's live state (§43-2b) — poll after [restore] until it
  /// reaches a terminal state (`done`/`failed`). Throws on a transport error or
  /// a non-object body.
  Future<CloneStatus> cloneStatus();

  /// Absolute URL to download [snapshot], for opening in a browser / save dialog.
  String absoluteDownloadUrl(BackupSnapshot snapshot);

  void close();
}

/// Dio wrapper over `/api/v1/backup/*`. Create and restore are both
/// 202-Accepted with the work on a daemon-side background worker — poll
/// [createStatus] / [cloneStatus] for the `running`→`done`/`failed` outcome.
class BackupApi implements BackupClient {
  final Dio _dio;
  final String _baseUrl;

  /// [dio] is injectable so tests can supply a mock `HttpClientAdapter`; production
  /// passes nothing and a server-bound Dio is built.
  BackupApi(AraServer server, {Dio? dio})
      : _baseUrl = server.baseUrl,
        _dio = dio ??
            Dio(BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              sendTimeout: const Duration(seconds: 5),
              receiveTimeout: const Duration(seconds: 30),
            ));

  @override
  Future<List<BackupSnapshot>> listSnapshots() async {
    final res = await _dio.get<dynamic>('/api/v1/backup/snapshots');
    final data = res.data;
    if (data is! List) {
      // A 2xx with a non-array body means the wire contract changed (or an error
      // envelope slipped through). Throw rather than return [] so the AsyncNotifier
      // surfaces an error state instead of a silently-empty list.
      throw FormatException('backup/snapshots returned a non-array body (${data.runtimeType})');
    }
    final snapshots = data
        .whereType<Map<String, dynamic>>()
        .map(BackupSnapshot.fromJson)
        // Drop a malformed entry — id keys the row, and downloadUrl is required to
        // download or restore it, so an entry missing either is unusable.
        .where((s) => s.backupId.isNotEmpty && s.downloadUrl.isNotEmpty)
        .toList(growable: false);
    // A silently-dropped row would otherwise be invisible; log when the wire list
    // had entries we couldn't use so a malformed snapshot is diagnosable.
    final dropped = data.length - snapshots.length;
    if (dropped > 0) {
      debugPrint('[backup] listSnapshots dropped $dropped malformed/unusable '
          'snapshot entr${dropped == 1 ? 'y' : 'ies'} of ${data.length}');
    }
    return snapshots;
  }

  @override
  Future<String> createBackup() async {
    // §43-2: the 202 returns as soon as the daemon claims the create slot — the catalog-sized
    // packaging happens on its worker, so the default read timeout is plenty (the old 120s
    // in-request override is gone with the in-request packaging).
    final res = await _dio.post<dynamic>('/api/v1/backup/create-zip');
    return _operationId(res.data, 'create-zip');
  }

  @override
  Future<CloneStatus> createStatus() async {
    final res = await _dio.get<dynamic>('/api/v1/backup/create-status');
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw FormatException('backup/create-status returned a non-object body (${data.runtimeType})');
    }
    return CloneStatus.fromJson(data);
  }

  @override
  Future<String> restore({
    required String sourceUrl,
    required bool profiles,
    required bool sequences,
    required bool frameMetadata,
  }) async {
    // A real guard (not a release-stripped assert): an empty source would otherwise
    // reach the daemon as a confusing 422 that doesn't reflect the client mistake.
    if (sourceUrl.isEmpty) {
      throw ArgumentError.value(sourceUrl, 'sourceUrl', 'must not be empty');
    }
    final res = await _dio.post<dynamic>(
      '/api/v1/backup/restore-zip',
      data: <String, dynamic>{
        'backup_source_url': sourceUrl,
        'restore_profiles': profiles,
        'restore_sequences': sequences,
        'restore_frame_metadata': frameMetadata,
        // restore_logs stays reserved: logs are not a §43.4 zip area (§43.8 only prunes them).
        'restore_logs': false,
      },
      // Restore does the atomic swap + rollback over the sequences/ tree — give it
      // more headroom than the default read timeout for a larger tree on Pi hardware.
      options: Options(receiveTimeout: const Duration(seconds: 60)),
    );
    return _operationId(res.data, 'restore-zip');
  }

  @override
  Future<CloneStatus> cloneStatus() async {
    final res = await _dio.get<dynamic>('/api/v1/backup/clone-status');
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw FormatException('backup/clone-status returned a non-object body (${data.runtimeType})');
    }
    return CloneStatus.fromJson(data);
  }

  @override
  String absoluteDownloadUrl(BackupSnapshot snapshot) =>
      // downloadUrl is a server-relative path ("/api/v1/backup/snapshot/{id}/download"); resolve it against the
      // base URL via Uri so an origin/port (and any base path) is handled correctly rather than string-spliced.
      Uri.parse(_baseUrl).resolve(snapshot.downloadUrl).toString();

  static String _operationId(dynamic data, String op) {
    final id = data is Map<String, dynamic> ? data['operation_id'] : null;
    if (id is! String) {
      throw FormatException('$op accepted but no operation_id was returned');
    }
    return id;
  }

  @override
  void close() => _dio.close(force: true);
}
