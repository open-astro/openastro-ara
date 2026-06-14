import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/data_package.dart';
import '../../models/server.dart';
import '../../services/data_manager_api.dart';
import '../saved_server_state.dart';
import '../ws/ws_providers.dart';

/// Builds a [DataManagerClient] for a server. Overridable in tests.
final dataManagerApiFactoryProvider =
    Provider<DataManagerClient Function(AraServer)>(
  (ref) => DataManagerApi.new,
);

/// [DataManagerClient] bound to the active server (`savedServers.last`), or
/// `null` when no server is saved. Closes the old Dio on a server change.
final dataManagerApiProvider = Provider<DataManagerClient?>((ref) {
  final server = ref.watch(savedServersProvider.select((async) => async.maybeWhen(
        data: (list) => list.isEmpty ? null : list.last,
        orElse: () => null,
      )));
  if (server == null) return null;
  final api = ref.watch(dataManagerApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// The active server's curated package catalog (with on-disk install state).
/// `null` data means no server is saved. Exposes download/cancel/delete actions
/// that re-read the catalog after the daemon accepts the request (downloads are
/// 202-Accepted; install state flips when the `data_manager.download.complete`
/// event lands, so the UI also refreshes on that — see the downloads provider).
class DataManagerPackagesNotifier extends AsyncNotifier<List<DataPackage>?> {
  // Bumped on every build() (active-server change). Refreshes capture it and only
  // write state if it still matches, so a server switch mid-action can't land a
  // stale read from the old, now-closed Dio.
  int _generation = 0;

  @override
  Future<List<DataPackage>?> build() async {
    _generation++;
    final api = ref.watch(dataManagerApiProvider);
    if (api == null) return null;
    return api.listPackages();
  }

  /// Start a download. Returns the download id, or null when no server is bound.
  Future<String?> download(String packageId, {bool forceReinstall = false}) async {
    final api = ref.read(dataManagerApiProvider);
    if (api == null) return null;
    final id = await api.download(packageId, forceReinstall: forceReinstall);
    // Don't refresh here — install state flips on the complete event, and a
    // refresh now would just re-read "not installed". The download starts; the
    // downloads provider drives the live UI.
    return id;
  }

  Future<void> cancel(String downloadId) async {
    final api = ref.read(dataManagerApiProvider);
    if (api == null) return;
    await api.cancel(downloadId);
  }

  Future<void> delete(String packageId) async {
    final api = ref.read(dataManagerApiProvider);
    if (api == null) return;
    try {
      await api.delete(packageId);
    } finally {
      // Always refresh so the catalog reflects reality even if delete errored — a
      // 404 (already gone) is swallowed in the API, and on any other failure the
      // list should still re-read rather than stay stale showing it installed.
      await refresh();
    }
  }

  bool _refreshing = false;
  bool _refreshPending = false;

  /// Manual / on-complete refresh. If one is already running, coalesce: flag a
  /// pending re-read so a refresh triggered mid-flight (e.g. a download.complete
  /// landing during a prior poll) isn't silently dropped — it runs once more.
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
        final api = ref.read(dataManagerApiProvider);
        final next = await AsyncValue.guard<List<DataPackage>?>(() async {
          if (api == null) return null;
          return api.listPackages();
        });
        if (ref.mounted && gen == _generation) state = next;
      } while (_refreshPending);
    } finally {
      _refreshing = false;
    }
  }
}

final dataManagerPackagesProvider =
    AsyncNotifierProvider<DataManagerPackagesNotifier, List<DataPackage>?>(
        DataManagerPackagesNotifier.new);

/// Live download progress for the active server, keyed by package id, folded
/// from the `data_manager.download.{progress,complete,failed}` WS stream. Reads
/// as empty when no server is connected. A `.complete` event also pokes the
/// packages provider to re-read install state.
///
/// Terminal entries (`complete` / `failed`) stay in the map until the server
/// reconnects (which rebuilds this notifier with a fresh empty map) — the slice-2
/// UI decides how to surface or dismiss a finished row.
class DataManagerDownloadsNotifier extends Notifier<Map<String, DownloadProgress>> {
  @override
  Map<String, DownloadProgress> build() {
    final stream = ref.watch(wsEventStreamProvider);
    if (stream == null) {
      return const <String, DownloadProgress>{};
    }
    // The listener mutates `acc` and assigns `state`. Safe: wsEventsProvider is a
    // StreamProvider — events arrive asynchronously (next microtask), never
    // synchronously inside this build(), so `state` is always assigned after the
    // initial empty map below is returned.
    final acc = <String, DownloadProgress>{};
    ref.listen(wsEventsProvider, (prev, next) {
      final event = next.asData?.value;
      if (event == null || !event.type.startsWith(_prefix)) {
        return;
      }
      final phase = switch (event.type) {
        '$_prefix.progress' => DownloadPhase.downloading,
        '$_prefix.complete' => DownloadPhase.complete,
        '$_prefix.failed' => DownloadPhase.failed,
        _ => null,
      };
      if (phase == null) return;
      final progress = DownloadProgress.fromPayload(event.payload, phase);
      if (progress == null) return;

      acc[progress.packageId] = progress;
      state = Map<String, DownloadProgress>.unmodifiable(acc);

      // A finished download flips the package's install state — re-read the
      // catalog so the row updates from "downloading" to installed/failed.
      if (phase == DownloadPhase.complete) {
        ref.read(dataManagerPackagesProvider.notifier).refresh();
      }
    });
    return const <String, DownloadProgress>{};
  }
}

const String _prefix = 'data_manager.download';

final dataManagerDownloadsProvider =
    NotifierProvider<DataManagerDownloadsNotifier, Map<String, DownloadProgress>>(
        DataManagerDownloadsNotifier.new);
