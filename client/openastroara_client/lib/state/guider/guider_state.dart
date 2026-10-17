import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/guider_status.dart';
import '../../models/server.dart';
import '../../services/guider_api.dart';
import '../profile_management_state.dart';
import '../saved_server_state.dart';

/// Builds a [GuiderClient] for a server. Overridable in tests so a pure fake
/// can be injected (the default constructs a real Dio-backed [GuiderApi]).
final guiderApiFactoryProvider = Provider<GuiderClient Function(AraServer)>(
  (ref) => GuiderApi.new,
);

/// [GuiderClient] bound to the **active** server ([activeServerProvider]), or
/// `null` when no server is saved.
final guiderApiProvider = Provider<GuiderClient?>((ref) {
  // Select only the active server (deduped by AraServer's value equality), so a
  // same-content re-emit of savedServers doesn't rebuild this provider and
  // force-close a Dio mid-request.
  final server = ref.watch(activeServerProvider);
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
  // Serializes refresh() without using state.isLoading — refresh deliberately
  // doesn't emit a bare loading state (to avoid a blank flash), and connect/
  // disconnect call refresh() *after* setting loading, so a state.isLoading
  // guard would wrongly skip their follow-up read.
  bool _refreshing = false;
  // Bumped on every build() (i.e. active-server change). A refresh captures the
  // generation at its start and only writes state if it still matches, so a
  // refresh in flight when the server switches can't land stale data over the
  // new server's result.
  int _generation = 0;

  @override
  Future<GuiderStatus?> build() async {
    _refreshing = false;
    _generation++;
    final api = ref.watch(guiderApiProvider);
    if (api == null) return null;
    return api.getStatus();
  }

  Future<void> connect({String? host, int? port}) async {
    // Re-entrancy guard: ignore a second call while one is in flight (loading),
    // so a double-tap doesn't fire two daemon requests with a racing refresh.
    if (state.isLoading) return;
    final api = ref.read(guiderApiProvider);
    if (api == null) return;
    state = const AsyncValue<GuiderStatus?>.loading();
    // No explicit target → the PROFILE's phd2 host/port, fetched fresh from
    // the daemon (the authoritative copy). Hardcoding kDefaultGuiderHost here
    // sent every chip-dialog connect to localhost:4400 and bypassed a remote
    // OpenAstro Guider (e.g. the SBC at :8080) the user had configured in Settings.
    if (host == null || port == null) {
      try {
        final phd2 = await ref.read(profileApiProvider)?.getPhd2Settings();
        host ??= phd2?.host ?? kDefaultGuiderHost;
        port ??= phd2?.port ?? kDefaultGuiderPort;
      } catch (_) {
        // Profile fetch failed — fall back to defaults rather than blocking
        // the connect entirely (the daemon-side request still validates).
        host ??= kDefaultGuiderHost;
        port ??= kDefaultGuiderPort;
      }
    }
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

  bool _guideActionInFlight = false;

  /// Start guiding by hand — the way back after guiding was stopped, with or
  /// without a run. Throws when the daemon refuses, for the caller to show.
  Future<void> startGuiding() => _guideAction((api) => api.startGuiding());

  /// Stop guiding by hand. Throws when the daemon refuses.
  Future<void> stopGuiding() => _guideAction((api) => api.stopGuiding());

  // Unlike connect, keeps the current status on screen (no loading state):
  // the strip must not blank while a start settles.
  Future<void> _guideAction(Future<void> Function(GuiderClient api) op) async {
    if (_guideActionInFlight) return;
    final api = ref.read(guiderApiProvider);
    if (api == null) return;
    _guideActionInFlight = true;
    try {
      await op(api);
    } finally {
      _guideActionInFlight = false;
    }
    if (!ref.mounted) return;
    await refresh(api);
  }

  /// Re-read status. [client] pins the read to a specific [GuiderClient] (used
  /// by connect/disconnect so a mid-action server switch can't redirect the
  /// follow-up read); when omitted it reads the current active client.
  /// Consecutive failed status reads of a guider that was reporting; the
  /// error surfaces at [staleAfterFailures] (~8 s at the 2 s live poll, one
  /// minute at the slow poll), matching EquipmentDeviceNotifier.
  static const int staleAfterFailures = 4;
  int _consecutiveReadFailures = 0;

  Future<void> refresh([GuiderClient? client]) async {
    if (!ref.mounted) return;
    // A manual refresh (no pinned client) serializes so rapid taps don't stack
    // concurrent getStatus() calls. The internal post-action call from connect/
    // disconnect passes a pinned client and must ALWAYS proceed — otherwise its
    // status read would be silently dropped (and state left on AsyncLoading) if a
    // manual refresh happened to be in flight. So the flag gates only the manual
    // path; the notifier doesn't depend on a UI lock to stay self-consistent.
    final manual = client == null;
    if (manual) {
      if (_refreshing) return;
      _refreshing = true;
    }
    final gen = _generation;
    try {
      final api = client ?? ref.read(guiderApiProvider);
      // Don't emit a bare loading state here — keep the prior data visible while
      // the reload is in flight so a manual refresh / poll doesn't flash blank.
      // (Riverpod 3.x's copyWithPrevious is internal, so we just hold the value.)
      final next = await AsyncValue.guard<GuiderStatus?>(() async {
        if (api == null) return null;
        return api.getStatus();
      });
      // Skip the write if the notifier was disposed or rebuilt for a new server
      // mid-flight (gen changed) — otherwise this stale read could clobber the
      // new server's status.
      if (!ref.mounted || gen != _generation) return;
      // The same patience the other device chips have: one failed read of a
      // guider that WAS reporting keeps the last status (the 2 s live poll
      // retries); only [staleAfterFailures] in a row surface the error. The
      // GUIDE chip used to go red on a single dropped poll.
      if (next.hasError && state.hasValue) {
        _consecutiveReadFailures++;
        if (_consecutiveReadFailures < staleAfterFailures) return;
      } else if (!next.hasError) {
        _consecutiveReadFailures = 0;
      }
      state = next;
    } finally {
      // Only clear the flag if this was the manual path and no rebuild happened —
      // build() already reset it for the new generation, and a fresh manual
      // refresh may have set it again.
      if (manual && gen == _generation) _refreshing = false;
    }
  }
}

final guiderStatusProvider =
    AsyncNotifierProvider<GuiderStatusNotifier, GuiderStatus?>(
        GuiderStatusNotifier.new);
