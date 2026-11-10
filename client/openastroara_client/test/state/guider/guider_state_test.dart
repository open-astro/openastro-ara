import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/guider_api.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';

class _FakeSavedServerService implements SavedServerService {
  _FakeSavedServerService(this._stored);
  final List<AraServer> _stored;
  @override
  Future<List<AraServer>> loadAll() async => List.unmodifiable(_stored);
  @override
  Future<void> saveAll(List<AraServer> servers) async {}
  @override
  Future<void> add(AraServer server) async {}
}

/// Pure [GuiderClient] fake — no Dio / sockets. Records calls / serves scripted
/// statuses.
class _FakeGuiderApi implements GuiderClient {
  GuiderStatus? status;
  int connectCalls = 0;
  int disconnectCalls = 0;
  int closeCalls = 0;
  String? lastHost;
  int? lastPort;
  bool throwOnConnect = false;
  bool throwOnDisconnect = false;
  Completer<void>? connectGate;

  @override
  Future<GuiderStatus?> getStatus() async => status;

  @override
  void close() => closeCalls++;

  @override
  Future<void> connect({String host = kDefaultGuiderHost, int port = kDefaultGuiderPort}) async {
    connectCalls++;
    lastHost = host;
    lastPort = port;
    if (connectGate != null) await connectGate!.future;
    if (throwOnConnect) {
      throw StateError('connect failed');
    }
    status = const GuiderStatus(
      deviceId: 'phd2',
      name: 'OpenAstro Guider',
      connectionState: GuiderConnectionState.connecting,
      runtimeState: GuiderRuntimeState.stopped,
    );
  }

  @override
  Future<void> disconnect() async {
    disconnectCalls++;
    if (throwOnDisconnect) {
      throw StateError('disconnect failed');
    }
    status = const GuiderStatus(
      deviceId: 'phd2',
      name: 'OpenAstro Guider',
      connectionState: GuiderConnectionState.disconnected,
      runtimeState: GuiderRuntimeState.stopped,
    );
  }
  final guideCalls = <String>[];
  Object? throwOnGuide;
  String? startOperationId;

  @override
  Future<String?> startGuiding() async {
    guideCalls.add('start');
    if (throwOnGuide != null) throw throwOnGuide!;
    return startOperationId;
  }

  @override
  Future<void> stopGuiding() async {
    guideCalls.add('stop');
    if (throwOnGuide != null) throw throwOnGuide!;
  }
}

/// Serves the profile's phd2 section — the remote-OpenAstro Guider case.
class _Phd2ProfileApi extends ProfileApi {
  _Phd2ProfileApi() : super(const AraServer(hostname: 'test', port: 1));
  @override
  Future<Phd2Settings> getPhd2Settings() async =>
      const Phd2Settings(host: 'sbc.local', port: 8080);
}

ProviderContainer _container(List<AraServer> servers, GuiderClient api, {Stream<WsEvent>? ws}) {
  final c = ProviderContainer(overrides: [
    savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(servers)),
    guiderApiFactoryProvider.overrideWithValue((_) => api),
    wsEventsProvider.overrideWith((ref) => ws ?? const Stream<WsEvent>.empty()),
  ]);
  addTearDown(c.dispose);
  return c;
}

/// Reports connecting for two reads after the connect, then connected.
class _SettlingGuiderApi extends _FakeGuiderApi {
  int _reads = 0;
  @override
  Future<GuiderStatus?> getStatus() async {
    if (status?.connectionState == GuiderConnectionState.connecting && ++_reads > 2) {
      status = const GuiderStatus(
        deviceId: 'phd2',
        name: 'OpenAstro Guider',
        connectionState: GuiderConnectionState.connected,
        runtimeState: GuiderRuntimeState.stopped,
      );
    }
    return status;
  }
}

void main() {
  // connect() re-reads while connecting; no real waits in tests.
  setUp(() => GuiderStatusNotifier.settleInterval = Duration.zero);

  group('guiderStatusProvider', () {
    const server = AraServer(hostname: 'h', port: 5555);

    test('no saved server → null status (and no API built)', () async {
      final c = _container(const [], _FakeGuiderApi());
      await c.read(savedServersProvider.future);
      expect(c.read(guiderApiProvider), isNull);
      final status = await c.read(guiderStatusProvider.future);
      expect(status, isNull);
    });

    test('active server → exposes the daemon status', () async {
      final api = _FakeGuiderApi()
        ..status = const GuiderStatus(
          deviceId: 'phd2',
          name: 'OpenAstro Guider',
          connectionState: GuiderConnectionState.connected,
          runtimeState: GuiderRuntimeState.guiding,
          rmsTotal: 0.4,
        );
      final c = _container(const [server], api);
      await c.read(savedServersProvider.future);
      final status = await c.read(guiderStatusProvider.future);
      expect(status!.isConnected, isTrue);
      expect(status.rmsTotal, 0.4);
    });

    test('connect() forwards host/port and refreshes to the new state', () async {
      final api = _FakeGuiderApi()
        ..status = const GuiderStatus(
          deviceId: 'phd2',
          name: 'OpenAstro Guider',
          connectionState: GuiderConnectionState.disconnected,
          runtimeState: GuiderRuntimeState.stopped,
        );
      final c = _container(const [server], api);
      await c.read(savedServersProvider.future);
      await c.read(guiderStatusProvider.future);

      await c.read(guiderStatusProvider.notifier).connect(host: '10.0.0.5', port: 4401);

      expect(api.connectCalls, 1);
      expect(api.lastHost, '10.0.0.5');
      expect(api.lastPort, 4401);
      expect(c.read(guiderStatusProvider).value!.connectionState, GuiderConnectionState.connecting);
    });

    test('connect() keeps reading until the guider settles (nothing else polls it)', () async {
      final api = _SettlingGuiderApi()
        ..status = const GuiderStatus(
          deviceId: 'phd2',
          name: 'OpenAstro Guider',
          connectionState: GuiderConnectionState.disconnected,
          runtimeState: GuiderRuntimeState.stopped,
        );
      final c = _container(const [server], api);
      await c.read(savedServersProvider.future);
      await c.read(guiderStatusProvider.future);

      await c.read(guiderStatusProvider.notifier).connect(host: '10.0.0.5', port: 4401);

      expect(c.read(guiderStatusProvider).value!.connectionState, GuiderConnectionState.connected,
          reason: 'not left on Connecting…');
    });

    test('a no-args connect() targets the PROFILE phd2 host/port, not localhost',
        () async {
      // The equipment-strip guider dialog calls connect() bare. Pre-fix that
      // sent localhost:4400 and bypassed a remote OpenAstro Guider (the SBC at :8080) the
      // user configured in Settings → Guider.
      final api = _FakeGuiderApi()
        ..status = const GuiderStatus(
          deviceId: 'phd2',
          name: 'OpenAstro Guider',
          connectionState: GuiderConnectionState.disconnected,
          runtimeState: GuiderRuntimeState.stopped,
        );
      final c = ProviderContainer(overrides: [
        savedServerServiceProvider
            .overrideWithValue(_FakeSavedServerService(const [server])),
        guiderApiFactoryProvider.overrideWithValue((_) => api),
        profileApiProvider.overrideWithValue(_Phd2ProfileApi()),
      ]);
      addTearDown(c.dispose);
      await c.read(savedServersProvider.future);
      await c.read(guiderStatusProvider.future);

      await c.read(guiderStatusProvider.notifier).connect();

      expect(api.lastHost, 'sbc.local');
      expect(api.lastPort, 8080);
    });

    test('a failed connect surfaces as AsyncError (not a stale value)', () async {
      final api = _FakeGuiderApi()
        ..status = const GuiderStatus(
          deviceId: 'phd2',
          name: 'OpenAstro Guider',
          connectionState: GuiderConnectionState.disconnected,
          runtimeState: GuiderRuntimeState.stopped,
        )
        ..throwOnConnect = true;
      final c = _container(const [server], api);
      await c.read(savedServersProvider.future);
      await c.read(guiderStatusProvider.future);

      await c.read(guiderStatusProvider.notifier).connect();

      expect(c.read(guiderStatusProvider).hasError, isTrue);
    });

    test('disconnect() drives the status back to disconnected', () async {
      final api = _FakeGuiderApi()
        ..status = const GuiderStatus(
          deviceId: 'phd2',
          name: 'OpenAstro Guider',
          connectionState: GuiderConnectionState.connected,
          runtimeState: GuiderRuntimeState.guiding,
        );
      final c = _container(const [server], api);
      await c.read(savedServersProvider.future);
      await c.read(guiderStatusProvider.future);

      await c.read(guiderStatusProvider.notifier).disconnect();

      expect(api.disconnectCalls, 1);
      expect(c.read(guiderStatusProvider).value!.connectionState, GuiderConnectionState.disconnected);
    });

    test('a second connect while one is in flight is ignored (re-entrancy guard)', () async {
      final api = _FakeGuiderApi()
        ..status = const GuiderStatus(
          deviceId: 'phd2',
          name: 'OpenAstro Guider',
          connectionState: GuiderConnectionState.disconnected,
          runtimeState: GuiderRuntimeState.stopped,
        )
        ..connectGate = Completer<void>();
      final c = _container(const [server], api);
      await c.read(savedServersProvider.future);
      await c.read(guiderStatusProvider.future);

      final notifier = c.read(guiderStatusProvider.notifier);
      final first = notifier.connect();
      await pumpEventQueue(); // let the first call set loading + reach the gate
      final second = notifier.connect(); // state.isLoading → early return
      api.connectGate!.complete();
      await Future.wait<void>([first, second]);

      expect(api.connectCalls, 1, reason: 'the second call was guarded out');
    });

    test('a failed disconnect surfaces as AsyncError', () async {
      final api = _FakeGuiderApi()
        ..status = const GuiderStatus(
          deviceId: 'phd2',
          name: 'OpenAstro Guider',
          connectionState: GuiderConnectionState.connected,
          runtimeState: GuiderRuntimeState.guiding,
        )
        ..throwOnDisconnect = true;
      final c = _container(const [server], api);
      await c.read(savedServersProvider.future);
      await c.read(guiderStatusProvider.future);

      await c.read(guiderStatusProvider.notifier).disconnect();

      expect(c.read(guiderStatusProvider).hasError, isTrue);
    });

    test('start and stop guiding reach the daemon and keep the status on screen',
        () async {
      final api = _FakeGuiderApi()
        ..status = const GuiderStatus(
          deviceId: 'phd2',
          name: 'OpenAstro Guider',
          connectionState: GuiderConnectionState.connected,
          runtimeState: GuiderRuntimeState.stopped,
        );
      final c = _container(const [server], api);
      await c.read(savedServersProvider.future);
      await c.read(guiderStatusProvider.future);
      final notifier = c.read(guiderStatusProvider.notifier);

      final start = notifier.startGuiding();
      expect(c.read(guiderStatusProvider).isLoading, isFalse,
          reason: 'the strip must not blank while a start settles');
      await start;
      await notifier.stopGuiding();

      expect(api.guideCalls, ['start', 'stop']);
    });

    test('a refused start throws to the caller and leaves the status alone',
        () async {
      final api = _FakeGuiderApi()
        ..status = const GuiderStatus(
          deviceId: 'phd2',
          name: 'OpenAstro Guider',
          connectionState: GuiderConnectionState.connected,
          runtimeState: GuiderRuntimeState.stopped,
        )
        ..throwOnGuide = StateError('guider is not connected');
      final c = _container(const [server], api);
      await c.read(savedServersProvider.future);
      await c.read(guiderStatusProvider.future);

      await expectLater(
          c.read(guiderStatusProvider.notifier).startGuiding(), throwsStateError);
      expect(c.read(guiderStatusProvider).hasValue, isTrue);
    });
  });

  // #1664 — the daemon answers a start at once and runs it in the background; its
  // guider.operation_complete carries the outcome and, when it failed, the guider's reason.
  group('a start that fails says why', () {
    const server = AraServer(hostname: 'h', port: 5555);
    const stopped = GuiderStatus(
      deviceId: 'phd2',
      name: 'OpenAstro Guider',
      connectionState: GuiderConnectionState.connected,
      runtimeState: GuiderRuntimeState.stopped,
    );
    WsEvent complete(String id, {required bool ok, String? error}) => WsEvent(
          type: 'guider.operation_complete',
          ts: DateTime.utc(2026, 11, 8),
          seq: 1,
          payload: {'operation': 'guider.start', 'operation_id': id, 'ok': ok, 'error': error},
        );

    Future<(GuiderStatusNotifier, StreamController<WsEvent>)> started(_FakeGuiderApi api) async {
      final ws = StreamController<WsEvent>.broadcast();
      addTearDown(ws.close);
      final c = _container(const [server], api, ws: ws.stream);
      await c.read(savedServersProvider.future);
      await c.read(guiderStatusProvider.future);
      return (c.read(guiderStatusProvider.notifier), ws);
    }

    test("the guider's reason reaches the caller", () async {
      final api = _FakeGuiderApi()..status = stopped..startOperationId = 'op-1';
      final (notifier, ws) = await started(api);
      final start = notifier.startGuiding();
      await Future<void>.delayed(Duration.zero);
      ws.add(complete('op-1', ok: false, error: 'Stopped capturing'));
      await expectLater(
          start, throwsA(isA<GuideStartFailed>().having((e) => e.reason, 'reason', 'Stopped capturing')));
    });

    test('a failure that names no reason still throws, with none', () async {
      final api = _FakeGuiderApi()..status = stopped..startOperationId = 'op-1';
      final (notifier, ws) = await started(api);
      final start = notifier.startGuiding();
      await Future<void>.delayed(Duration.zero);
      ws.add(complete('op-1', ok: false, error: 'refused'));
      await expectLater(start, throwsA(isA<GuideStartFailed>().having((e) => e.reason, 'reason', isNull)));
    });

    test('a start that worked returns', () async {
      final api = _FakeGuiderApi()..status = stopped..startOperationId = 'op-1';
      final (notifier, ws) = await started(api);
      final start = notifier.startGuiding();
      await Future<void>.delayed(Duration.zero);
      ws.add(complete('op-1', ok: true));
      await start;
    });

    test("another operation's failure is not this start's", () async {
      final api = _FakeGuiderApi()..status = stopped..startOperationId = 'op-1';
      final (notifier, ws) = await started(api);
      final start = notifier.startGuiding();
      await Future<void>.delayed(Duration.zero);
      ws.add(complete('op-2', ok: false, error: 'Stopped capturing'));
      await start;
    });

    test('no event: the start returns as before, no false failure', () async {
      final api = _FakeGuiderApi()..status = stopped..startOperationId = 'op-1';
      final (notifier, _) = await started(api);
      await notifier.startGuiding();
    });
  });

  // #1756 — a start that fails after the guider reached calibrating: the start has
  // returned by then, so the outcome is kept as the last start's late failure.
  group('a start that fails during calibration is still heard', () {
    const server = AraServer(hostname: 'h', port: 5555);
    const calibrating = GuiderStatus(
      deviceId: 'phd2',
      name: 'OpenAstro Guider',
      connectionState: GuiderConnectionState.connected,
      runtimeState: GuiderRuntimeState.calibrating,
    );
    WsEvent complete(String id, {required bool ok, String? error}) => WsEvent(
          type: 'guider.operation_complete',
          ts: DateTime.utc(2026, 11, 10),
          seq: 1,
          payload: {'operation': 'guider.start', 'operation_id': id, 'ok': ok, 'error': error},
        );

    /// The guider shows calibrating at once, so each start returns before its outcome.
    Future<(ProviderContainer, _FakeGuiderApi, StreamController<WsEvent>)> calibratingRig() async {
      final api = _FakeGuiderApi()..status = calibrating..startOperationId = 'op-1';
      final ws = StreamController<WsEvent>.broadcast();
      addTearDown(ws.close);
      final c = _container(const [server], api, ws: ws.stream);
      await c.read(savedServersProvider.future);
      await c.read(guiderStatusProvider.future);
      return (c, api, ws);
    }

    Future<void> settle() => Future<void>.delayed(Duration.zero);

    test("a failure after calibration began is kept with the guider's reason", () async {
      final (c, _, ws) = await calibratingRig();
      await c.read(guiderStatusProvider.notifier).startGuiding();
      expect(c.read(guideStartLateFailureProvider), isNull, reason: 'calibrating: not failed yet');
      ws.add(complete('op-1', ok: false, error: 'Calibration failed: star did not move enough'));
      await settle();
      expect(c.read(guideStartLateFailureProvider)?.reason, 'Calibration failed: star did not move enough');
    });

    test('a late success records no failure', () async {
      final (c, _, ws) = await calibratingRig();
      await c.read(guiderStatusProvider.notifier).startGuiding();
      ws.add(complete('op-1', ok: true));
      await settle();
      expect(c.read(guideStartLateFailureProvider), isNull);
    });

    test('a Stop during calibration is not reported as a failed start', () async {
      final (c, _, ws) = await calibratingRig();
      final notifier = c.read(guiderStatusProvider.notifier);
      await notifier.startGuiding();
      await notifier.stopGuiding();
      ws.add(complete('op-1', ok: false, error: 'Stopped by the user'));
      await settle();
      expect(c.read(guideStartLateFailureProvider), isNull);
    });

    test("only the newest start's outcome counts", () async {
      final (c, api, ws) = await calibratingRig();
      final notifier = c.read(guiderStatusProvider.notifier);
      await notifier.startGuiding();
      api.startOperationId = 'op-2';
      await notifier.startGuiding();
      ws.add(complete('op-1', ok: false, error: 'old failure'));
      await settle();
      expect(c.read(guideStartLateFailureProvider), isNull, reason: "the first start's outcome no longer counts");
      ws.add(complete('op-2', ok: false, error: 'new failure'));
      await settle();
      expect(c.read(guideStartLateFailureProvider)?.reason, 'new failure');
    });

    test('a late "refused" is a failure with no reason, as a quick one is', () async {
      final (c, _, ws) = await calibratingRig();
      await c.read(guiderStatusProvider.notifier).startGuiding();
      ws.add(complete('op-1', ok: false, error: 'refused'));
      await settle();
      expect(c.read(guideStartLateFailureProvider), isNotNull);
      expect(c.read(guideStartLateFailureProvider)!.reason, isNull);
    });
  });
}
