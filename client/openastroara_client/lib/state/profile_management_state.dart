import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/profile_list.dart';
import '../services/profile_api.dart';
import 'saved_server_state.dart';

/// §37/§30 multi-profile management — loads the known-profiles list from the
/// active daemon and exposes select / rename / delete actions. Each mutating
/// action calls the daemon then refreshes the list, so the active-profile badge
/// and membership stay in sync. Errors (e.g. the daemon's 409 when deleting the
/// active or last-remaining profile) propagate to the caller to surface.
class ProfileManagementNotifier extends AsyncNotifier<ProfileList> {
  /// The ProfileApi for the active server, built once per [build] (i.e. whenever
  /// the active server changes) rather than re-instantiated on every call — so
  /// it stays current with the active daemon yet shares one Dio/connection pool.
  late ProfileApi _api;

  /// Guards against overlapping mutations: a slow network + a rapid second
  /// action would otherwise race two refreshes, and the later HTTP response
  /// could clobber the newer list.
  bool _busy = false;

  @override
  Future<ProfileList> build() {
    // watch (not read) so a server switch rebuilds the api + re-fetches. A null
    // server throws here → the AsyncError surfaces "connect first" in the UI
    // (rather than escaping a later guard and spinning on AsyncLoading forever).
    final server = ref.watch(activeServerProvider);
    if (server == null) {
      throw StateError('No active server — connect to a daemon to manage profiles.');
    }
    _api = ProfileApi(server);
    return _api.listProfiles();
  }

  /// Re-fetch the list, surfacing transport errors through the AsyncValue. Keeps
  /// the current list visible during the fetch (no AsyncLoading flash after each
  /// mutation) — the initial load already shows a spinner via build()'s pending
  /// state. The fetch is wrapped in a closure so any throw is caught by guard
  /// rather than escaping and stranding state on AsyncLoading.
  Future<void> refresh() async {
    state = await AsyncValue.guard(() => _api.listProfiles());
  }

  /// Run a mutation then refresh, under the [_busy] guard. Errors (notably the
  /// daemon's 409 on deleting the active/last profile) propagate to the caller.
  Future<void> _mutate(Future<void> Function() op) async {
    // Reject (don't silently drop) an overlapping action so the UI can tell the
    // user, rather than discarding their tap with no feedback. Throwing before
    // setting _busy leaves the in-flight action untouched.
    if (_busy) {
      throw StateError('Another profile action is still in progress.');
    }
    _busy = true;
    try {
      await op();
    } finally {
      _busy = false;
      // Reconcile with server truth whether op succeeded OR threw — so an
      // unexpected failure can't leave the list showing pre-mutation data.
      // refresh() routes its own errors into `state`, so it can't throw here and
      // mask op's original exception (which still propagates to the UI snackbar).
      await refresh();
    }
  }

  /// Make [id] the active profile, then refresh. Throws on transport failure.
  Future<void> select(String id) => _mutate(() => _api.selectProfile(id));

  /// Rename [id], then refresh. Throws on transport failure.
  Future<void> rename(String id, String name) => _mutate(() => _api.renameProfile(id, name));

  /// Delete [id], then refresh. Throws on transport failure — notably the
  /// daemon's 409 when [id] is the active or last-remaining profile.
  Future<void> delete(String id) => _mutate(() => _api.deleteProfile(id));
}

final profileManagementProvider =
    AsyncNotifierProvider<ProfileManagementNotifier, ProfileList>(
        ProfileManagementNotifier.new);
