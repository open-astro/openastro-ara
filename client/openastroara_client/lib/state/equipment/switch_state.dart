import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/discovered_device.dart';
import '../../models/server.dart';
import '../../models/switch_device.dart';
import '../../services/switch_api.dart';
import '../saved_server_state.dart';

/// Builds a [SwitchClient] for a server. Overridable in tests so a pure fake can
/// be injected (the default constructs a real Dio-backed [SwitchApi]).
final switchApiFactoryProvider = Provider<SwitchClient Function(AraServer)>(
  (ref) => SwitchApi.new,
);

/// [SwitchClient] bound to the **active** server (`savedServers.last`), or `null`
/// when no server is saved.
final switchApiProvider = Provider<SwitchClient?>((ref) {
  // Select only the active server (deduped by AraServer value equality) so a
  // same-content re-emit of savedServers doesn't rebuild this and force-close a
  // Dio mid-request.
  final server = ref.watch(savedServersProvider.select((async) => async.maybeWhen(
        data: (list) => list.isEmpty ? null : list.last,
        orElse: () => null,
      )));
  if (server == null) return null;
  final api = ref.watch(switchApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// Live list of connected/known switches for the active server. Empty when no
/// server is saved or no switch is connected. Exposes connect/disconnect/setValue
/// actions that re-read the list after the daemon accepts the request (all are
/// 202-Accepted, so the follow-up read may still show `connecting`).
class SwitchListNotifier extends AsyncNotifier<List<SwitchDevice>> {
  // Serializes manual refresh() so rapid taps don't stack concurrent getAll()s;
  // the post-action refresh from connect/etc. passes a pinned client and always
  // proceeds (see refresh()).
  bool _refreshing = false;
  // True while a connect/disconnect/setValue is in flight — a second action is
  // dropped (re-entrancy guard) without touching the list's AsyncValue, so a
  // one-off action failure never wipes the loaded device list.
  bool _acting = false;
  // Bumped on every build() (active-server change). A refresh captures the gen at
  // its start and only writes if it still matches, so an in-flight read can't land
  // stale data over a new server's result.
  int _generation = 0;

  @override
  Future<List<SwitchDevice>> build() async {
    // Reset BOTH in-flight guards on a server change: a stale _acting from an
    // action against the old server would otherwise lock out actions on the new
    // one until the abandoned (force-closed) Dio call unwinds.
    _refreshing = false;
    _acting = false;
    _generation++;
    final api = ref.watch(switchApiProvider);
    if (api == null) return const <SwitchDevice>[];
    return api.getAll();
  }

  // connect/disconnect/setValue run the 202-Accepted request then re-read the
  // list. They share a re-entrancy guard (one action at a time) and return whether
  // the call was PERFORMED: `false` means it was dropped (another action in flight,
  // or no active server) so the caller can distinguish "accepted" from "ignored".
  // A failure is RETHROWN rather than pushed into the list's state — for a
  // multi-device list a one-off failure (one port write) shouldn't wipe the view
  // of every other switch. The UI wraps each control to surface the error; the
  // list keeps showing the last-read devices. (List-READ failures still surface as
  // the provider's AsyncError, since a list we can't read can't be shown.)
  Future<bool> connect(DiscoveredDevice device) => _act((api) => api.connect(device));

  Future<bool> disconnect(int deviceNumber) =>
      _act((api) => api.disconnect(deviceNumber));

  Future<bool> setValue({
    required int deviceNumber,
    required int portId,
    required double value,
  }) =>
      _act((api) => api.setValue(
            deviceNumber: deviceNumber,
            portId: portId,
            value: value,
          ));

  // Run a 202-Accepted action then re-read the list against the SAME client (a
  // mid-action server switch must not redirect the follow-up read). A failed
  // action surfaces as an error state rather than a bare throw.
  // Returns true if the action ran, false if it was dropped (re-entrancy / no API).
  Future<bool> _act(Future<void> Function(SwitchClient api) action) async {
    if (_acting) return false; // another action in flight — dropped
    final api = ref.read(switchApiProvider);
    if (api == null) return false; // no active server — dropped
    _acting = true;
    try {
      // The action throws on failure — propagate to the caller (the list stays as
      // last read). On success, re-read against the SAME client (a mid-action
      // server switch must not redirect the follow-up read).
      await action(api);
      if (ref.mounted) await refresh(api);
      return true;
    } finally {
      _acting = false;
    }
  }

  /// Re-read the switch list. [client] pins the read to a specific [SwitchClient]
  /// (used by the actions so a mid-action server switch can't redirect the
  /// follow-up read); when omitted it reads the current active client.
  Future<void> refresh([SwitchClient? client]) async {
    if (!ref.mounted) return;
    final manual = client == null;
    if (manual) {
      if (_refreshing) return;
      _refreshing = true;
    }
    final gen = _generation;
    try {
      final api = client ?? ref.read(switchApiProvider);
      // Keep the prior data visible while reloading (don't flash blank).
      final next = await AsyncValue.guard<List<SwitchDevice>>(() async {
        if (api == null) return const <SwitchDevice>[];
        return api.getAll();
      });
      if (ref.mounted && gen == _generation) state = next;
    } finally {
      // Clear the manual-refresh flag ONLY when no rebuild happened mid-flight.
      // The guard is deliberate, NOT an unconditional reset: if the server changed
      // (gen bumped), build() already set _refreshing = false for the new
      // generation AND a fresh manual refresh may have set it true again — an
      // unconditional clear here would wipe that newer refresh's flag (a race).
      // Every generation bump comes from build(), which resets the flag, so this
      // stale branch can never strand _refreshing == true.
      if (manual && gen == _generation) _refreshing = false;
    }
  }
}

final switchListProvider =
    AsyncNotifierProvider<SwitchListNotifier, List<SwitchDevice>>(
        SwitchListNotifier.new);
