import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/backup_snapshot.dart';
import '../../models/clone_status.dart';
import '../../models/server.dart';
import '../../services/backup_api.dart';
import '../saved_server_state.dart';

/// Builds a [BackupClient] for a server. Overridable in tests.
final backupApiFactoryProvider = Provider<BackupClient Function(AraServer)>(
  (ref) => BackupApi.new,
);

/// [BackupClient] bound to the active server (`savedServers.last`), or `null`
/// when no server is saved. Closes the old Dio on a server change.
final backupApiProvider = Provider<BackupClient?>((ref) {
  final server = ref.watch(savedServersProvider.select((async) => async.maybeWhen(
        data: (list) => list.isEmpty ? null : list.last,
        orElse: () => null,
      )));
  if (server == null) return null;
  final api = ref.watch(backupApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// The active server's backup snapshots (newest first). `null` data means no
/// server is saved. Exposes create/restore actions; create re-reads the list so
/// the new snapshot appears, restore does not (it changes live config, not the
/// snapshot set). Both THROW on failure so the caller (the UI) can surface the
/// daemon's message (422 nothing-to-archive / 404 unknown / 422 corrupt) —
/// failures do NOT land in this provider's state.
class BackupSnapshotsNotifier extends AsyncNotifier<List<BackupSnapshot>?> {
  // Bumped on every build() (active-server change); refreshes capture it and only
  // write state if it still matches, so a server switch mid-action can't land a
  // stale read from the old, now-closed Dio.
  int _generation = 0;

  @override
  Future<List<BackupSnapshot>?> build() async {
    _generation++;
    final api = ref.watch(backupApiProvider);
    if (api == null) return null;
    return api.listSnapshots();
  }

  /// Create a backup, then refresh so the new snapshot appears. Returns the
  /// operation id, or null when no server is bound. Throws on failure.
  Future<String?> createBackup() async {
    final api = ref.read(backupApiProvider);
    if (api == null) return null;
    final id = await api.createBackup();
    await refresh();
    return id;
  }

  /// Restore the selected areas from [snapshot]. Returns the operation id, or
  /// null when no server is bound. Throws on failure. Does not refresh — restore
  /// changes live config, not the snapshot list.
  Future<String?> restore(
    BackupSnapshot snapshot, {
    required bool profiles,
    required bool sequences,
  }) async {
    final api = ref.read(backupApiProvider);
    if (api == null) return null;
    // Pass the snapshot's own (relative) download URL straight through — the daemon's restore-source parser
    // round-trips exactly this value (`/api/v1/backup/snapshot/{id}/download`); verified by the §43-2a server tests.
    return api.restore(
      sourceUrl: snapshot.downloadUrl,
      profiles: profiles,
      sequences: sequences,
    );
  }

  /// §43-2b: poll clone-status until the restore worker reaches a terminal state
  /// (`done`/`failed`), returning that status — or `null` if [isCancelled] starts
  /// returning true (e.g. the caller's widget was disposed), so the poll stops
  /// making requests instead of running to the deadline. Call right after
  /// [restore] (the daemon sets `running` synchronously on the POST, so there's
  /// no stale-`done` race against a prior restore). Throws [TimeoutException] if
  /// it doesn't finish within [timeout], [StateError] if no server is bound, or a
  /// transport error from the poll. [interval] is injectable so tests don't
  /// wall-clock wait.
  Future<CloneStatus?> awaitRestoreTerminal({
    Duration interval = const Duration(milliseconds: 500),
    Duration timeout = const Duration(minutes: 5),
    bool Function()? isCancelled,
  }) async {
    final api = ref.read(backupApiProvider);
    if (api == null) {
      throw StateError('No server connected.');
    }
    final deadline = DateTime.now().add(timeout);
    while (true) {
      if (isCancelled?.call() ?? false) {
        return null;
      }
      final status = await api.cloneStatus();
      if (status.isTerminal) {
        return status;
      }
      if (!DateTime.now().isBefore(deadline)) {
        throw TimeoutException('Restore did not finish within ${timeout.inSeconds}s.');
      }
      await Future<void>.delayed(interval);
    }
  }

  bool _refreshing = false;
  bool _refreshPending = false;

  /// Re-read the snapshot list. Coalesces concurrent calls: a refresh triggered
  /// while one is running runs once more rather than being dropped.
  Future<void> refresh() async {
    if (_refreshing) {
      _refreshPending = true;
      return;
    }
    _refreshing = true;
    try {
      do {
        _refreshPending = false;
        if (!ref.mounted) return;
        final gen = _generation;
        final api = ref.read(backupApiProvider);
        final next = await AsyncValue.guard<List<BackupSnapshot>?>(() async {
          if (api == null) return null;
          return api.listSnapshots();
        });
        if (ref.mounted && gen == _generation) state = next;
      } while (_refreshPending);
    } finally {
      _refreshing = false;
    }
  }
}

final backupSnapshotsProvider =
    AsyncNotifierProvider<BackupSnapshotsNotifier, List<BackupSnapshot>?>(
        BackupSnapshotsNotifier.new);
