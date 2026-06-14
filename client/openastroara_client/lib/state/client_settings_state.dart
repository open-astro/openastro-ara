import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/client_settings.dart';
import '../models/server.dart';
import '../services/client_settings_api.dart';
import 'saved_server_state.dart';

/// Builds a [ClientSettingsClient] for a server. Overridable in tests.
final clientSettingsApiFactoryProvider =
    Provider<ClientSettingsClient Function(AraServer)>(
  (ref) => ClientSettingsApi.new,
);

/// [ClientSettingsClient] bound to the active server (`savedServers.last`), or
/// `null` when no server is saved. Closes the old Dio on a server change.
final clientSettingsApiProvider = Provider<ClientSettingsClient?>((ref) {
  final server = ref.watch(savedServersProvider.select((async) => async.maybeWhen(
        data: (list) => list.isEmpty ? null : list.last,
        orElse: () => null,
      )));
  if (server == null) return null;
  final api = ref.watch(clientSettingsApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// The active server's synced UI preferences (§55.1). `null` data means no
/// server is saved. [build] reads them on connect (read-once); [save] replaces
/// the blob and lands the server's stored view (last-write-wins). [merge] is a
/// convenience for a feature to update just its own keys without clobbering the
/// rest. Both actions THROW on failure so the caller can surface it.
class ClientSettingsNotifier extends AsyncNotifier<ClientSettings?> {
  // Bumped on each build() (active-server change); a save captures it and only
  // writes state if it still matches, so a server switch mid-save can't land a
  // stale result from the old, now-closed Dio.
  int _generation = 0;

  @override
  Future<ClientSettings?> build() async {
    _generation++;
    final api = ref.watch(clientSettingsApiProvider);
    if (api == null) return null;
    return api.fetch();
  }

  /// Replace the whole preferences blob. Returns the stored view, or null when
  /// no server is bound. Throws on failure.
  Future<ClientSettings?> save(Map<String, dynamic> settings) async {
    final api = ref.read(clientSettingsApiProvider);
    if (api == null) return null;
    final gen = _generation;
    final stored = await api.replace(settings);
    if (ref.mounted && gen == _generation) state = AsyncData(stored);
    return stored;
  }

  /// Update [patch] keys over the current blob (last-write-wins per key),
  /// leaving the rest untouched. Returns the stored view, or null when no server
  /// is bound. Throws on failure — and, critically, throws rather than merging
  /// onto an empty baseline when the current settings aren't loaded (the initial
  /// fetch errored or is still in flight): merging then would send only [patch]
  /// and wipe every other key the server has stored.
  Future<ClientSettings?> merge(Map<String, dynamic> patch) async {
    if (ref.read(clientSettingsApiProvider) == null) return null;
    final current = state.asData?.value;
    if (current == null) {
      throw StateError('Cannot merge client settings before they have loaded — '
          'merging now would overwrite the server-stored blob with only the patch.');
    }
    return save(<String, dynamic>{...current.settings, ...patch});
  }
}

final clientSettingsProvider =
    AsyncNotifierProvider<ClientSettingsNotifier, ClientSettings?>(
        ClientSettingsNotifier.new);
