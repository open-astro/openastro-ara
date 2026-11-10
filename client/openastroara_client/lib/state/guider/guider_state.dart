import 'package:flutter/foundation.dart' show visibleForTesting;
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/guider_status.dart';
import '../../models/server.dart';
import '../../models/ws_event.dart';
import '../../services/guider_api.dart';
import '../profile_management_state.dart';
import '../saved_server_state.dart';
import '../ws/ws_providers.dart';

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

  /// How often [connect] re-reads while the guider is connecting.
  @visibleForTesting
  static Duration settleInterval = const Duration(milliseconds: 250);

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
    // The connect is 202-Accepted and nothing else polls the guider, so read
    // until it settles (the rig takes ~50 ms; 15 s at most) rather than stay on Connecting….
    for (var i = 0; i < 60; i++) {
      if (!ref.mounted ||
          state.value?.connectionState != GuiderConnectionState.connecting) {
        return;
      }
      await Future<void>.delayed(settleInterval);
      if (!ref.mounted) return;
      await refresh(api);
    }
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

  /// #1756 — the running start's operation, heard after [startGuiding] has
  /// returned (the Guider calibrates for minutes). A newer start or a Stop
  /// drops it.
  ProviderSubscription<AsyncValue<WsEvent>>? _lateStart;

  void _dropLateStart() {
    _lateStart?.close();
    _lateStart = null;
  }

  /// The start's outcome from its `guider.operation_complete`, null on success.
  /// The daemon says "refused" when the guider named no reason.
  static GuideStartFailed? _failureOf(Map<String, dynamic> p) {
    if (p['ok'] != false) return null;
    final error = p['error'];
    return GuideStartFailed(error is String && error.isNotEmpty && error != 'refused' ? error : null);
  }

  /// Start guiding by hand — the way back after guiding was stopped, with or
  /// without a run. Throws when the daemon refuses, for the caller to show;
  /// throws [GuideStartFailed] when the guider gives up on the start (#1664).
  /// A failure after the Guider reached calibrating goes to
  /// [guideStartLateFailureProvider] instead (#1756).
  Future<void> startGuiding() => _guideAction((api) async {
        _dropLateStart();
        // Listening before the ask: a guider that refuses at once can answer
        // before the 202 does.
        final done = <Object?, Map<String, dynamic>>{};
        final sub = ref.listen(wsEventsProvider, (_, next) {
          final e = next.asData?.value;
          if (e != null && e.type == 'guider.operation_complete') {
            done[e.payload['operation_id']] = e.payload;
          }
        });
        try {
          final id = await api.startGuiding();
          if (id == null) return;
          // A start that works can run long (calibration): stop waiting once the
          // guider is past picking a star, or after 10 s, as before (no false failure).
          for (var i = 0; ref.mounted; i++) {
            final p = done[id];
            if (p != null) {
              final failed = _failureOf(p);
              if (failed != null) throw failed;
              return;
            }
            final runtime = state.value?.runtimeState;
            if (i >= 40 ||
                runtime == GuiderRuntimeState.calibrating ||
                runtime == GuiderRuntimeState.guiding ||
                runtime == GuiderRuntimeState.settling) {
              // Still running: keep listening for its outcome. No await since the
              // check above, so no event falls between the two listeners.
              final late = ref.read(guideStartLateFailureProvider.notifier);
              _lateStart = ref.listen(wsEventsProvider, (_, next) {
                final e = next.asData?.value;
                if (e == null || e.type != 'guider.operation_complete' || e.payload['operation_id'] != id) return;
                _dropLateStart();
                if (_failureOf(e.payload) case final failed?) late.report(failed);
              });
              return;
            }
            await Future<void>.delayed(settleInterval);
            await refresh(api);
          }
        } finally {
          sub.close();
        }
      });

  /// Stop guiding by hand. Throws when the daemon refuses. A start still
  /// calibrating ends here, and its outcome is not a failure to report.
  Future<void> stopGuiding() => _guideAction((api) {
        _dropLateStart();
        return api.stopGuiding();
      });

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
      if (ref.mounted && gen == _generation) state = next;
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

/// #1756 — the last Guider start's failure that came after the start returned
/// (the Guider had reached calibrating): null until one comes. Each failure is
/// a new value, so a listener hears every one.
class GuideStartLateFailureNotifier extends Notifier<GuideStartFailed?> {
  @override
  GuideStartFailed? build() => null;

  void report(GuideStartFailed failure) => state = failure;

  void clear() => state = null;
}

final guideStartLateFailureProvider =
    NotifierProvider<GuideStartLateFailureNotifier, GuideStartFailed?>(
        GuideStartLateFailureNotifier.new);
