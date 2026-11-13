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

/// [ClientSettingsClient] bound to the active server ([activeServerProvider]), or
/// `null` when no server is saved. Closes the old Dio on a server change.
final clientSettingsApiProvider = Provider<ClientSettingsClient?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(clientSettingsApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// The active server's synced UI preferences (§55.1). `null` data means no
/// server is saved. [build] reads them on connect; [save] replaces the blob and
/// lands the server's stored view (last-write-wins). [merge] (#1644) asks the
/// daemon to replace only the given top-level keys, so two devices saving
/// different keys don't overwrite each other. Both actions THROW on failure so
/// the caller can surface it.
class ClientSettingsNotifier extends AsyncNotifier<ClientSettings?> {
  // Bumped on each build() (active-server change); a save captures it and only
  // writes state if it still matches, so a server switch mid-save can't land a
  // stale result from the old, now-closed Dio.
  int _generation = 0;

  // The `updated_utc` the daemon stamped on this device's own last write: the
  // `client_settings.updated` event for it carries the same stamp and needs no
  // refetch (the device that saved must not reset its own screen).
  DateTime? _lastSaveUtc;

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
    _landWrite(stored, gen);
    return stored;
  }

  /// Update just the [patch] keys on the rig (a `null` value removes one),
  /// leaving every other key untouched. Returns the stored view, or null when
  /// no server is bound. Throws on failure.
  Future<ClientSettings?> merge(Map<String, dynamic> patch) async {
    final api = ref.read(clientSettingsApiProvider);
    if (api == null) return null;
    final gen = _generation;
    final stored = await api.merge(patch);
    _landWrite(stored, gen);
    return stored;
  }

  void _landWrite(ClientSettings stored, int gen) {
    _lastSaveUtc = stored.updatedUtc;
    if (ref.mounted && gen == _generation) state = AsyncData(stored);
  }

  /// A `client_settings.updated` event arrived: read the rig's copy again,
  /// unless [updatedUtc] is the stamp of this device's own last write.
  Future<void> onUpdatedEvent(DateTime? updatedUtc) async {
    if (updatedUtc != null && updatedUtc == _lastSaveUtc) return;
    await refetch();
  }

  /// Read the rig's copy again (a missed event, a reconnect). A failed read
  /// keeps what is already held: preferences are never worth an error screen.
  Future<void> refetch() async {
    final api = ref.read(clientSettingsApiProvider);
    if (api == null) return;
    final gen = _generation;
    try {
      final fresh = await api.fetch();
      if (ref.mounted && gen == _generation) state = AsyncData(fresh);
    } catch (_) {
      // keep the held value
    }
  }
}

final clientSettingsProvider =
    AsyncNotifierProvider<ClientSettingsNotifier, ClientSettings?>(
        ClientSettingsNotifier.new);
