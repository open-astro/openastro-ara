import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/server.dart';
import '../services/saved_server_service.dart';

final savedServerServiceProvider = Provider<SavedServerService>(
  (ref) => SavedServerService(),
);

/// Servers the user has confirmed (handshake OK at least once). Stays loaded
/// for the app lifetime so the AppShell can route on startup based on whether
/// the list is empty (→ FirstRunScreen) or not (→ home).
class SavedServersNotifier extends AsyncNotifier<List<AraServer>> {
  @override
  Future<List<AraServer>> build() async {
    return ref.read(savedServerServiceProvider).loadAll();
  }

  Future<void> add(AraServer server) async {
    final svc = ref.read(savedServerServiceProvider);
    // Persist first, but on failure still update in-memory state so the
    // user isn't blocked on first-run by a transient keyring/storage
    // error. The next loadAll() will reconcile if/when persistence works.
    try {
      await svc.add(server);
    } catch (_) {
      final current = state.value ?? const <AraServer>[];
      state = AsyncValue.data([...current, server]);
      return;
    }
    // AsyncValue.guard captures any throw from loadAll() into AsyncError
    // so the _RootRouter can render the error branch instead of letting
    // the exception unhandled-future through the FirstRunScreen handler.
    state = await AsyncValue.guard(() => svc.loadAll());
  }
}

final savedServersProvider =
    AsyncNotifierProvider<SavedServersNotifier, List<AraServer>>(
        SavedServersNotifier.new);

/// The de-facto **active** server, or null if none is saved yet.
///
/// ARA talks to one server at a time (§30.8); the most-recently-saved/connected
/// entry is treated as active — the convention used across the app where call
/// sites historically reached for `savedServersProvider…last` directly. This
/// names that rule in one place so it's explicit and can evolve (e.g. into an
/// explicit "default-on-launch" selection) without hunting down `.last` usages.
final activeServerProvider = Provider<AraServer?>((ref) {
  // asData?.value is null while the saved-servers list is still loading (initial
  // async storage read) or on error, and the list once it's data — so a null
  // here means "no active server yet", whether not-yet-loaded or genuinely empty.
  // Both collapse to the same safe, retryable outcome at the call site.
  final servers = ref.watch(savedServersProvider).asData?.value;
  return (servers == null || servers.isEmpty) ? null : servers.last;
});
