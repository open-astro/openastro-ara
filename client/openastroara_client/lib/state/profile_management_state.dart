import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/profile_list.dart';
import '../services/profile_api.dart';
import 'saved_server_state.dart';

/// The [ProfileApi] for the active server, or null if none is connected. A
/// provider (not built inline) so it's a single seam: it rebuilds when the
/// active server changes, shares one Dio/connection pool, and can be overridden
/// with a fake in tests.
final profileApiProvider = Provider<ProfileApi?>((ref) {
  final server = ref.watch(activeServerProvider);
  return server == null ? null : ProfileApi(server);
});

/// §37/§30 multi-profile management — loads the known-profiles list from the
/// active daemon and exposes select / rename / delete actions. Each mutating
/// action calls the daemon then refreshes the list, so the active-profile badge
/// and membership stay in sync. Errors (e.g. the daemon's 409 when deleting the
/// active or last-remaining profile) propagate to the caller to surface.
class ProfileManagementNotifier extends AsyncNotifier<ProfileList> {
  /// The active server's ProfileApi, captured each [build], or null when no
  /// server is connected (build() then surfaces AsyncError). Nullable so
  /// refresh()/mutations can't hit a LateInitializationError if invoked from the
  /// no-server error state (e.g. the Retry button). Mutations snapshot this
  /// locally so a server switch mid-operation can't redirect their refresh.
  ProfileApi? _api;

  /// Guards against overlapping mutations: a slow network + a rapid second
  /// action would otherwise race two refreshes, and the later HTTP response
  /// could clobber the newer list.
  bool _busy = false;

  @override
  Future<ProfileList> build() {
    // Reset the guard on every (re)build — a server switch while an action was
    // in flight must not leave new actions wedged behind a stale _busy.
    _busy = false;
    // watch (via profileApiProvider) so a server switch rebuilds + re-fetches. A
    // null server throws here → the AsyncError surfaces "connect first" in the UI
    // (rather than escaping a later guard and spinning on AsyncLoading forever).
    final api = ref.watch(profileApiProvider);
    if (api == null) {
      _api = null; // don't leave a stale api from a previously-connected server
      throw StateError('No active server — connect to a daemon to manage profiles.');
    }
    _api = api;
    return api.listProfiles();
  }

  /// Re-fetch the list, surfacing transport errors through the AsyncValue. Keeps
  /// the current list visible during the fetch (no AsyncLoading flash) — the
  /// initial load already shows a spinner via build()'s pending state.
  Future<void> refresh() async {
    final api = _api;
    if (api == null) return; // no server (e.g. Retry from the error state) — no-op
    state = await AsyncValue.guard(() => api.listProfiles());
  }

  /// Run a mutation then refresh, under the [_busy] guard. The ProfileApi is
  /// snapshotted up front so the post-mutation refresh always targets the same
  /// instance even if a server switch reassigns [_api] mid-flight. Errors
  /// (notably the daemon's 409) propagate to the caller.
  Future<void> _mutate(Future<void> Function(ProfileApi api) op) async {
    // Reject (don't silently drop) an overlapping action so the UI can tell the
    // user, rather than discarding their tap with no feedback.
    if (_busy) {
      throw StateError('Another profile action is still in progress.');
    }
    final api = _api; // snapshot — the finally refreshes against this instance
    if (api == null) return; // no active server; the UI is in the error state
    _busy = true;
    try {
      await op(api);
    } finally {
      // Reconcile with server truth on success OR failure (guard routes its own
      // errors into `state`, so it can't throw here and mask op's exception).
      // Hold _busy until the refresh settles so a second action can't race it.
      try {
        state = await AsyncValue.guard(() => api.listProfiles());
      } finally {
        _busy = false;
      }
    }
  }

  /// Make [id] the active profile, then refresh. Throws on transport failure.
  Future<void> select(String id) => _mutate((api) => api.selectProfile(id));

  /// Rename [id], then refresh. Throws on transport failure.
  Future<void> rename(String id, String name) => _mutate((api) => api.renameProfile(id, name));

  /// Delete [id], then refresh. Throws on transport failure — notably the
  /// daemon's 409 when [id] is the active or last-remaining profile.
  Future<void> delete(String id) => _mutate((api) => api.deleteProfile(id));
}

final profileManagementProvider =
    AsyncNotifierProvider<ProfileManagementNotifier, ProfileList>(
        ProfileManagementNotifier.new);
