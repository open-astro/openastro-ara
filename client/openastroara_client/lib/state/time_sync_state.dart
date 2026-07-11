import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/server.dart';
import '../services/time_sync_api.dart';
import 'saved_server_state.dart';

/// Builds a [TimeSyncClient] for a server. Overridable in tests.
final timeSyncApiFactoryProvider = Provider<TimeSyncClient Function(AraServer)>(
  (ref) => TimeSyncApi.new,
);

/// [TimeSyncClient] bound to the active server, or null when none is saved.
final timeSyncApiProvider = Provider<TimeSyncClient?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(timeSyncApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// The daemon's current §31 sync state for the settings status UI, or null
/// when no server is active. Refresh with `ref.invalidate`.
final timeSyncStatusProvider = FutureProvider.autoDispose<TimeSyncState?>((
  ref,
) async {
  final api = ref.watch(timeSyncApiProvider);
  if (api == null) return null;
  return api.getState();
});

/// The §31.1 on-connect push: ask the daemon whether it holds a fresh,
/// trustworthy sync and, when it doesn't, push this device's clock (waterfall
/// step 1 — an NTP-synced laptop/phone clock at medium trust covers ~95% of
/// sessions per the playbook). Best-effort by design: a failure is logged and
/// swallowed — a Pi with no time sync degrades exactly as before this existed,
/// and the next connect retries.
final timeSyncOnConnectProvider = Provider<TimeSyncOnConnect>(
  (ref) => TimeSyncOnConnect(ref),
);

class TimeSyncOnConnect {
  final Ref _ref;

  /// Injectable clock for tests.
  DateTime Function() now = () => DateTime.now().toUtc();

  TimeSyncOnConnect(this._ref);

  Future<void> syncIfNeeded() async {
    final api = _ref.read(timeSyncApiProvider);
    if (api == null) return;
    try {
      final state = await api.getState();
      if (state.synced) {
        return; // fresh + trustworthy — nothing to do (§31.1 top branch)
      }
      await api.pushClientTime(now());
      debugPrint(
        '[time-sync] pushed device clock (server was ${state.source}/${state.trust})',
      );
    } catch (e) {
      // Best-effort: never surface an error for an opportunistic sync.
      debugPrint('[time-sync] on-connect push failed: $e');
    }
  }
}
