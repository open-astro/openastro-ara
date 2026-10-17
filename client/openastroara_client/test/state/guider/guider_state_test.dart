import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/guider_api.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';

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
  bool throwOnStatus = false;
  int connectCalls = 0;
  int disconnectCalls = 0;
  int closeCalls = 0;
  String? lastHost;
  int? lastPort;
  bool throwOnConnect = false;
  bool throwOnDisconnect = false;
  Completer<void>? connectGate;

  @override
  Future<GuiderStatus?> getStatus() async {
    if (throwOnStatus) throw StateError('status read failed');
    return status;
  }

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

  @override
  Future<void> startGuiding() async {
    guideCalls.add('start');
    if (throwOnGuide != null) throw throwOnGuide!;
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

ProviderContainer _container(List<AraServer> servers, GuiderClient api) {
  final c = ProviderContainer(overrides: [
    savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(servers)),
    guiderApiFactoryProvider.overrideWithValue((_) => api),
  ]);
  addTearDown(c.dispose);
  return c;
}

void main() {
  group('guiderStatusProvider', () {
    const server = AraServer(hostname: 'h', port: 5555);

    test('no saved server → null status (and no API built)', () async {
      final c = _container(const [], _FakeGuiderApi());
      await c.read(savedServersProvider.future);
      expect(c.read(guiderApiProvider), isNull);
      final status = await c.read(guiderStatusProvider.future);
      expect(status, isNull);
    });

    test('a guider that was reporting keeps its status through three failed reads, the fourth surfaces the error', () async {
      // The GUIDE chip used to go red on one dropped 2 s poll; the other device
      // chips wait four. A failure streak is what should turn the chip red.
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
      final notifier = c.read(guiderStatusProvider.notifier);

      api.throwOnStatus = true;
      for (var i = 1; i < GuiderStatusNotifier.staleAfterFailures; i++) {
        await notifier.refresh();
        expect(c.read(guiderStatusProvider).hasValue, isTrue, reason: 'read $i of a blip keeps the last status');
        expect(c.read(guiderStatusProvider).value!.isConnected, isTrue);
      }
      await notifier.refresh();
      expect(c.read(guiderStatusProvider).hasError, isTrue, reason: 'the fourth failure in a row is an outage');

      api.throwOnStatus = false;
      await notifier.refresh();
      expect(c.read(guiderStatusProvider).hasValue, isTrue, reason: 'one good read restores the status');
      api.throwOnStatus = true;
      await notifier.refresh();
      expect(c.read(guiderStatusProvider).hasValue, isTrue, reason: 'the streak restarted from zero');
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
}
