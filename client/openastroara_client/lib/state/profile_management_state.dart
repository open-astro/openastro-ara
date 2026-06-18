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
  /// Builds a ProfileApi for the active server, or throws if none is connected
  /// (so the UI can show a "connect first" state rather than a silent no-op).
  ProfileApi _api() {
    final server = ref.read(activeServerProvider);
    if (server == null) {
      throw StateError('No active server — connect to a daemon to manage profiles.');
    }
    return ProfileApi(server);
  }

  @override
  Future<ProfileList> build() => _api().listProfiles();

  /// Re-fetch the list, surfacing transport errors through the AsyncValue.
  Future<void> refresh() async {
    state = const AsyncLoading();
    state = await AsyncValue.guard(_api().listProfiles);
  }

  /// Make [id] the active profile, then refresh. Throws on transport failure.
  Future<void> select(String id) async {
    await _api().selectProfile(id);
    await refresh();
  }

  /// Rename [id], then refresh. Throws on transport failure.
  Future<void> rename(String id, String name) async {
    await _api().renameProfile(id, name);
    await refresh();
  }

  /// Delete [id], then refresh. Throws on transport failure — notably the
  /// daemon's 409 when [id] is the active or last-remaining profile.
  Future<void> delete(String id) async {
    await _api().deleteProfile(id);
    await refresh();
  }
}

final profileManagementProvider =
    AsyncNotifierProvider<ProfileManagementNotifier, ProfileList>(
        ProfileManagementNotifier.new);
