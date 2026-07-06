import 'dart:async';
import 'dart:io';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../services/server_api.dart';

/// Builds the [ServerApi] for a server — overridable in tests so the §27
/// claim/release flows can run against a fake instead of a live daemon.
final serverApiFactoryProvider = Provider<ServerApi Function(AraServer)>(
  (ref) =>
      (server) => ServerApi(server),
);

/// §27 single-client control-session state. [sessionId] set = this WILMA holds
/// the control slot; [deniedReason] set = the last claim was refused (another
/// client holds it / holder unresponsive). Both null = no claim attempted yet,
/// or the daemon predates §27 / was unreachable (the event stream then runs
/// unbound, which is fine — the slot is about control, not about watching).
class ClientSessionState {
  final String? sessionId;
  final String? deniedReason;

  const ClientSessionState({this.sessionId, this.deniedReason});

  bool get holdsSlot => sessionId != null;
}

/// Owns claiming/releasing the §27 control slot. [claim] is invoked by the WS
/// stream before every dial (see `WsEventStream.claimSession`), so a reconnect
/// automatically re-claims with the cached session id — the server treats a
/// matching id as an idempotent re-grant, no takeover modal at anyone.
class ClientSessionNotifier extends Notifier<ClientSessionState> {
  // Plain-field mirrors of what release() needs. release() runs from provider
  // onDispose callbacks, which during a full container teardown can fire AFTER
  // this notifier is disposed — at that point `ref` and `state` both throw, but
  // the fields (and the captured api factory) stay readable, so the best-effort
  // disconnect POST still goes out.
  ServerApi Function(AraServer)? _apiFactory;
  String? _sessionId;

  /// Bumped by [release]. A claim that was in flight when a release ran must
  /// not resurrect the session it comes back with — see [claim].
  int _releaseEpoch = 0;

  @override
  ClientSessionState build() {
    _apiFactory = ref.read(serverApiFactoryProvider);
    return const ClientSessionState();
  }

  /// Claim (or re-claim) the slot on [server]. Returns the session id to bind
  /// the WS upgrade with, or null when the claim was denied or errored — the
  /// caller connects unbound either way.
  Future<String?> claim(AraServer server) async {
    final api = ref.read(serverApiFactoryProvider)(server);
    final epochBefore = _releaseEpoch;
    try {
      final result = await api.connectClient(
        hostname: _localHostname(),
        sessionId: _sessionId,
      );
      if (_releaseEpoch != epochBefore) {
        // release() ran while the server was deciding (stream teardown / server
        // switch mid-claim). Adopting this grant would orphan it — no socket
        // will bind it and nothing would release it again (only the daemon's
        // 60 s sweep). Hand it straight back instead.
        if (result.granted) {
          unawaited(_bestEffortDisconnect(api, result.sessionId!));
        }
        return null;
      }
      if (result.granted) {
        _sessionId = result.sessionId;
        _setState(ClientSessionState(sessionId: result.sessionId));
        return result.sessionId;
      }
      // Denied: our cached id (if any) is stale — someone else holds the slot.
      _sessionId = null;
      _setState(ClientSessionState(deniedReason: result.deniedReason));
      return null;
    } on Exception {
      // Older daemon without §27 (404), network fault mid-reconnect, etc. —
      // connect unbound and KEEP the cached session id: a transient fault must
      // not discard a session that may still re-claim on the next attempt.
      return null;
    }
  }

  /// Gracefully release the slot (stream teardown / server switch / app exit).
  /// Best-effort, and safe to call while the container is disposing. Also
  /// fences any claim still in flight (see [claim]'s epoch check).
  Future<void> release(AraServer server) async {
    _releaseEpoch++;
    final id = _sessionId;
    final apiFactory = _apiFactory;
    if (id == null || apiFactory == null) return;
    _sessionId = null;
    _setState(const ClientSessionState());
    await _bestEffortDisconnect(apiFactory(server), id);
  }

  Future<void> _bestEffortDisconnect(ServerApi api, String sessionId) async {
    try {
      await api.disconnectClient(sessionId);
    } on Exception {
      // Already taken over / daemon gone — either way the slot isn't ours.
    }
  }

  /// State assignment that tolerates the disposed-mid-teardown case: provider
  /// onDispose callbacks (which drive [release], and can resolve an in-flight
  /// [claim]) may run after this notifier is disposed, when `state` throws.
  /// The plain-field mirrors above are the source of truth by then.
  void _setState(ClientSessionState next) {
    try {
      state = next;
    } on StateError {
      // Container teardown: nobody observes state anymore.
    }
  }

  static String _localHostname() {
    try {
      final name = Platform.localHostname.trim();
      return name.isEmpty ? 'WILMA client' : name;
    } on Object {
      return 'WILMA client';
    }
  }
}

final clientSessionProvider =
    NotifierProvider<ClientSessionNotifier, ClientSessionState>(
      ClientSessionNotifier.new,
    );
