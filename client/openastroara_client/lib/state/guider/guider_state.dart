import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/guider_status.dart';
import '../../models/server.dart';
import '../../services/guider_api.dart';
import '../saved_server_state.dart';

/// Builds a [GuiderClient] for a server. Overridable in tests so a pure fake
/// can be injected (the default constructs a real Dio-backed [GuiderApi]).
final guiderApiFactoryProvider = Provider<GuiderClient Function(AraServer)>(
  (ref) => GuiderApi.new,
);

/// [GuiderClient] bound to the **active** server (`savedServers.last`), or
/// `null` when no server is saved.
final guiderApiProvider = Provider<GuiderClient?>((ref) {
  // Select only the active server (deduped by AraServer's value equality), so a
  // same-content re-emit of savedServers doesn't rebuild this provider and
  // force-close a Dio mid-request.
  final server = ref.watch(savedServersProvider.select((async) => async.maybeWhen(
        data: (list) => list.isEmpty ? null : list.last,
        orElse: () => null,
      )));
  if (server == null) return null;
  final api = ref.watch(guiderApiFactoryProvider)(server);
  // Close the old Dio when the active server changes (provider recompute) or
  // the provider is disposed, so connection pools don't leak.
  ref.onDispose(api.close);
  return api;
});

/// Live guider status for the active server. `null` data means either no server
/// is saved or the daemon has no guider configured (`404`). Exposes
/// connect/disconnect/refresh actions that re-read status after the daemon
/// accepts the request (connect/disconnect are 202-Accepted, so the post-action
/// refresh may still show `connecting`).
class GuiderStatusNotifier extends AsyncNotifier<GuiderStatus?> {
  @override
  Future<GuiderStatus?> build() async {
    final api = ref.watch(guiderApiProvider);
    if (api == null) return null;
    return api.getStatus();
  }

  Future<void> connect({String host = kDefaultGuiderHost, int port = kDefaultGuiderPort}) async {
    // Re-entrancy guard: ignore a second call while one is in flight (loading),
    // so a double-tap doesn't fire two daemon requests with a racing refresh.
    if (state.isLoading) return;
    final api = ref.read(guiderApiProvider);
    if (api == null) return;
    state = const AsyncValue<GuiderStatus?>.loading();
    try {
      await api.connect(host: host, port: port);
    } catch (e, st) {
      // Surface a failed connect as an error state so the UI can show it; a
      // bare throw here would leave the notifier on its stale prior value.
      if (ref.mounted) state = AsyncValue<GuiderStatus?>.error(e, st);
      return;
    }
    // The active server may have changed during the await, disposing this
    // notifier; don't write to a disposed notifier.
    if (!ref.mounted) return;
    // Refresh against the SAME client we just acted on — re-reading the provider
    // could pick up a different server if the active one changed in between.
    await refresh(api);
  }

  Future<void> disconnect() async {
    if (state.isLoading) return;
    final api = ref.read(guiderApiProvider);
    if (api == null) return;
    state = const AsyncValue<GuiderStatus?>.loading();
    try {
      await api.disconnect();
    } catch (e, st) {
      if (ref.mounted) state = AsyncValue<GuiderStatus?>.error(e, st);
      return;
    }
    if (!ref.mounted) return;
    await refresh(api);
  }

  /// Re-read status. [client] pins the read to a specific [GuiderClient] (used
  /// by connect/disconnect so a mid-action server switch can't redirect the
  /// follow-up read); when omitted it reads the current active client.
  Future<void> refresh([GuiderClient? client]) async {
    final api = client ?? ref.read(guiderApiProvider);
    state = const AsyncValue<GuiderStatus?>.loading();
    final next = await AsyncValue.guard<GuiderStatus?>(() async {
      if (api == null) return null;
      return api.getStatus();
    });
    // getStatus() can outlive the active server; don't write to a disposed notifier.
    if (ref.mounted) state = next;
  }
}

final guiderStatusProvider =
    AsyncNotifierProvider<GuiderStatusNotifier, GuiderStatus?>(
        GuiderStatusNotifier.new);
