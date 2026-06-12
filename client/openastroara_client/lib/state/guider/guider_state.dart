import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/guider_status.dart';
import '../../models/server.dart';
import '../../services/guider_api.dart';
import '../saved_server_state.dart';

/// Builds a [GuiderApi] for a server. Overridable in tests so a fake can be
/// injected (the default constructs a real Dio-backed client).
final guiderApiFactoryProvider = Provider<GuiderApi Function(AraServer)>(
  (ref) => GuiderApi.new,
);

/// [GuiderApi] bound to the **active** server (`savedServers.last`), or `null`
/// when no server is saved.
final guiderApiProvider = Provider<GuiderApi?>((ref) {
  final servers = ref.watch(savedServersProvider).maybeWhen(
        data: (list) => list,
        orElse: () => const <AraServer>[],
      );
  if (servers.isEmpty) return null;
  final api = ref.watch(guiderApiFactoryProvider)(servers.last);
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

  Future<void> connect({String host = 'localhost', int port = 4400}) async {
    final api = ref.read(guiderApiProvider);
    if (api == null) return;
    try {
      await api.connect(host: host, port: port);
    } catch (e, st) {
      // Surface a failed connect as an error state so the UI can show it; a
      // bare throw here would leave the notifier on its stale prior value.
      state = AsyncValue<GuiderStatus?>.error(e, st);
      return;
    }
    await refresh();
  }

  Future<void> disconnect() async {
    final api = ref.read(guiderApiProvider);
    if (api == null) return;
    try {
      await api.disconnect();
    } catch (e, st) {
      state = AsyncValue<GuiderStatus?>.error(e, st);
      return;
    }
    await refresh();
  }

  Future<void> refresh() async {
    final api = ref.read(guiderApiProvider);
    state = const AsyncValue<GuiderStatus?>.loading();
    state = await AsyncValue.guard<GuiderStatus?>(() async {
      if (api == null) return null;
      return api.getStatus();
    });
  }
}

final guiderStatusProvider =
    AsyncNotifierProvider<GuiderStatusNotifier, GuiderStatus?>(
        GuiderStatusNotifier.new);
