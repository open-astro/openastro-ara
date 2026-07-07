import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/server_api.dart';
import 'package:openastroara/state/ws/client_session_state.dart';

/// Scripted §27 claim/release double. Each [connectClient] call pops the next
/// scripted outcome; a null script entry throws (network fault / older daemon).
class _FakeServerApi implements ServerApi {
  @override
  Future<EmergencyStopResult> emergencyStop() => throw UnimplementedError();
  final List<SessionClaim?> script;
  final List<({String hostname, String? sessionId})> claims = [];
  final List<String> released = [];

  _FakeServerApi(this.script);

  @override
  Future<SessionClaim> connectClient({
    required String hostname,
    String? sessionId,
  }) async {
    claims.add((hostname: hostname, sessionId: sessionId));
    final next = script.removeAt(0);
    if (next == null) {
      throw DioException(
        requestOptions: RequestOptions(path: '/api/v1/server/connect'),
        response: Response(
          requestOptions: RequestOptions(path: '/api/v1/server/connect'),
          statusCode: 404,
        ),
      );
    }
    return next;
  }

  @override
  Future<bool> disconnectClient(String sessionId) async {
    released.add(sessionId);
    return true;
  }

  @override
  Future<ServerInfo> getInfo() async =>
      const ServerInfo(name: 'fake', version: 'x', apiVersion: 'x');
}

void main() {
  const server = AraServer(hostname: 'host-a', port: 5555);

  (ProviderContainer, _FakeServerApi) harness(List<SessionClaim?> script) {
    final api = _FakeServerApi(script);
    final container = ProviderContainer(
      overrides: [serverApiFactoryProvider.overrideWithValue((_) => api)],
    );
    addTearDown(container.dispose);
    return (container, api);
  }

  group('ClientSessionNotifier', () {
    test(
      'a granted claim stores the session id and reports holdsSlot',
      () async {
        final (container, api) = harness([const SessionClaim.granted('sid-1')]);
        final notifier = container.read(clientSessionProvider.notifier);

        final sid = await notifier.claim(server);

        expect(sid, 'sid-1');
        final state = container.read(clientSessionProvider);
        expect(state.holdsSlot, isTrue);
        expect(state.sessionId, 'sid-1');
        expect(state.deniedReason, isNull);
        expect(api.claims.single.hostname, isNotEmpty);
        expect(
          api.claims.single.sessionId,
          isNull,
          reason: 'first claim has no cached session id',
        );
      },
    );

    test(
      'a re-claim passes the cached session id (idempotent re-grant path)',
      () async {
        final (container, api) = harness([
          const SessionClaim.granted('sid-1'),
          const SessionClaim.granted('sid-1'),
        ]);
        final notifier = container.read(clientSessionProvider.notifier);

        await notifier.claim(server);
        await notifier.claim(server); // e.g. the WS reconnect after a blip

        expect(api.claims[1].sessionId, 'sid-1');
      },
    );

    test(
      'a denial clears the stale session id and records the reason',
      () async {
        final (container, _) = harness([
          const SessionClaim.granted('sid-1'),
          const SessionClaim.denied('Server in use by mac.local.'),
        ]);
        final notifier = container.read(clientSessionProvider.notifier);

        await notifier.claim(server);
        final sid = await notifier.claim(server);

        expect(sid, isNull);
        final state = container.read(clientSessionProvider);
        expect(state.holdsSlot, isFalse);
        expect(state.deniedReason, 'Server in use by mac.local.');
      },
    );

    test(
      'a thrown claim (older daemon / network fault) keeps the cached state',
      () async {
        final (container, _) = harness([
          const SessionClaim.granted('sid-1'),
          null, // throws
        ]);
        final notifier = container.read(clientSessionProvider.notifier);

        await notifier.claim(server);
        final sid = await notifier.claim(server);

        expect(sid, isNull, reason: 'connect unbound this time');
        expect(
          container.read(clientSessionProvider).sessionId,
          'sid-1',
          reason: 'a transient fault must not discard a possibly-valid session',
        );
      },
    );

    test('release posts the disconnect and clears the slot', () async {
      final (container, api) = harness([const SessionClaim.granted('sid-1')]);
      final notifier = container.read(clientSessionProvider.notifier);

      await notifier.claim(server);
      await notifier.release(server);

      expect(api.released, ['sid-1']);
      expect(container.read(clientSessionProvider).holdsSlot, isFalse);
    });

    test('release without a session is a no-op', () async {
      final (container, api) = harness([]);

      await container.read(clientSessionProvider.notifier).release(server);

      expect(api.released, isEmpty);
    });

    test(
      'a release during an in-flight claim hands the late grant back',
      () async {
        final api = _BlockingServerApi();
        final container = ProviderContainer(
          overrides: [serverApiFactoryProvider.overrideWithValue((_) => api)],
        );
        addTearDown(container.dispose);
        final notifier = container.read(clientSessionProvider.notifier);

        // The claim goes out; the server is "deciding" (can block up to ~30s).
        final claimFuture = notifier.claim(server);
        await pumpEventQueue();

        // Teardown runs release() while the claim is still pending.
        await notifier.release(server);

        // The server then grants — too late. The grant must not be adopted...
        api.pending.complete(const SessionClaim.granted('sid-late'));
        final sid = await claimFuture;
        await pumpEventQueue();

        expect(sid, isNull);
        expect(
          container.read(clientSessionProvider).holdsSlot,
          isFalse,
          reason: 'nothing may resurrect a session after release',
        );
        // ...and must be handed straight back, not left for the 60s dead sweep.
        expect(api.released, ['sid-late']);
      },
    );
  });
}

/// A claim that blocks until the test completes [pending] — models the server
/// holding the connect open while the current holder answers the modal.
class _BlockingServerApi implements ServerApi {
  @override
  Future<EmergencyStopResult> emergencyStop() => throw UnimplementedError();
  final Completer<SessionClaim> pending = Completer<SessionClaim>();
  final List<String> released = [];

  @override
  Future<SessionClaim> connectClient({
    required String hostname,
    String? sessionId,
  }) => pending.future;

  @override
  Future<bool> disconnectClient(String sessionId) async {
    released.add(sessionId);
    return true;
  }

  @override
  Future<ServerInfo> getInfo() async =>
      const ServerInfo(name: 'fake', version: 'x', apiVersion: 'x');
}
