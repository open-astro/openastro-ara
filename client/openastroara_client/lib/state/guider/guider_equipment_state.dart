import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/guider_equipment_choices.dart';
import '../../models/server.dart';
import '../../services/guider_equipment_api.dart';
import '../saved_server_state.dart';

/// Builds a [GuiderEquipmentClient] for a server. Overridable in tests.
final guiderEquipmentApiFactoryProvider =
    Provider<GuiderEquipmentClient Function(AraServer)>(
  (ref) => GuiderEquipmentApi.new,
);

/// [GuiderEquipmentClient] bound to the active server ([activeServerProvider]),
/// or `null` when no server is saved. Closes the old Dio on a server change.
final guiderEquipmentApiProvider = Provider<GuiderEquipmentClient?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(guiderEquipmentApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// The active server's §63.17 guider equipment-choice lists. `null` data means
/// no server is saved. Discovery and the profile push are exposed as actions
/// that surface their own results/errors to the caller (they're button-driven,
/// with panel-local feedback) rather than replacing the choices state.
class GuiderEquipmentNotifier
    extends AsyncNotifier<GuiderEquipmentChoicesResponse?> {
  // Bumped on every build() (active-server change). Refreshes capture it and
  // only write state if it still matches, so a server switch mid-read can't
  // land a stale result (or a spurious error from the old, now-closed Dio).
  int _generation = 0;

  @override
  Future<GuiderEquipmentChoicesResponse?> build() async {
    _generation++;
    final api = ref.watch(guiderEquipmentApiProvider);
    if (api == null) return null;
    return api.getChoices();
  }

  bool _refreshing = false;

  /// Public manual refresh. Skips when the initial load or another manual
  /// refresh is already running so a Refresh tap can't stack duplicate reads.
  Future<void> refresh() async {
    if (state.isLoading || _refreshing) return;
    _refreshing = true;
    try {
      if (!ref.mounted) return;
      final gen = _generation;
      final api = ref.read(guiderEquipmentApiProvider);
      final next = await AsyncValue.guard<GuiderEquipmentChoicesResponse?>(
          () async {
        if (api == null) return null;
        return api.getChoices();
      });
      // Skip the write if disposed or rebuilt for a new server mid-flight.
      if (ref.mounted && gen == _generation) state = next;
    } finally {
      _refreshing = false;
    }
  }

  /// Daemon-side Alpaca discovery sweep. Returns the `host:port` strings the
  /// guider saw on its network; throws (Dio 400/409/422) for the caller's
  /// feedback. Doesn't touch the choices state — discovery results are
  /// transient picker input, not equipment status.
  Future<List<String>> discoverAlpaca({
    int? numQueries,
    int? timeoutSeconds,
  }) async {
    final api = ref.read(guiderEquipmentApiProvider);
    if (api == null) {
      throw StateError('Not connected — connect to your rig to save this.');
    }
    return api.discoverAlpaca(
        numQueries: numQueries, timeoutSeconds: timeoutSeconds);
  }

  /// On-demand §63.17 profile push (202-Accepted; the daemon reports the
  /// attempted methods on the `guider.profile_pushed` WS event). Throws
  /// (Dio 409/422) for the caller's feedback.
  Future<void> pushProfile() async {
    final api = ref.read(guiderEquipmentApiProvider);
    if (api == null) {
      throw StateError('Not connected — connect to your rig to save this.');
    }
    await api.pushProfile();
  }
}

final guiderEquipmentProvider = AsyncNotifierProvider<GuiderEquipmentNotifier,
    GuiderEquipmentChoicesResponse?>(GuiderEquipmentNotifier.new);
