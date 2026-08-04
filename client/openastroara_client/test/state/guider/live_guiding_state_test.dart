import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/guider_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/state/guider/live_guiding_state.dart';
import 'package:openastroara/state/saved_server_state.dart';

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

class _FakeGuiderApi implements GuiderClient {
  GuiderStatus? status;
  @override
  Future<GuiderStatus?> getStatus() async => status;
  @override
  void close() {}
  @override
  Future<void> connect(
      {String host = kDefaultGuiderHost, int port = kDefaultGuiderPort}) async {}
  @override
  Future<void> disconnect() async {}
}

const _guiding = GuiderStatus(
  deviceId: 'phd2',
  name: 'OpenAstro Guider',
  connectionState: GuiderConnectionState.connected,
  runtimeState: GuiderRuntimeState.guiding,
  rmsTotal: 0.5,
  rmsRa: 0.3,
  rmsDec: 0.4,
);

RmsSample _s(int sec, double total) =>
    RmsSample(time: DateTime.utc(2026, 1, 1, 0, 0, sec), total: total);

void main() {
  group('RmsRingBuffer', () {
    test('appends in order and exposes oldest-first', () {
      final b = RmsRingBuffer();
      b.add(_s(0, 0.5));
      b.add(_s(2, 0.6));
      expect(b.samples, [_s(0, 0.5), _s(2, 0.6)]);
    });

    test('ignores out-of-order and duplicate-timestamp samples', () {
      // A re-poll of an unchanged status must not duplicate points.
      final b = RmsRingBuffer();
      b.add(_s(10, 0.5));
      b.add(_s(10, 0.5));
      b.add(_s(5, 0.9));
      expect(b.samples, [_s(10, 0.5)]);
    });

    test('evicts samples older than the window relative to the newest', () {
      final b = RmsRingBuffer(window: const Duration(seconds: 10));
      b.add(_s(0, 0.1));
      b.add(_s(5, 0.2));
      b.add(_s(16, 0.3)); // 0s and 5s now fall outside [6s, 16s]
      expect(b.samples, [_s(16, 0.3)]);
    });

    test('a sample exactly at the window edge is retained', () {
      final b = RmsRingBuffer(window: const Duration(seconds: 10));
      b.add(_s(0, 0.1));
      b.add(_s(10, 0.2));
      expect(b.samples, [_s(0, 0.1), _s(10, 0.2)]);
    });

    test('hard-caps the sample count regardless of window', () {
      final b = RmsRingBuffer(
          window: const Duration(days: 1), maxSamples: 3);
      for (var i = 0; i < 5; i++) {
        b.add(_s(i, i.toDouble()));
      }
      expect(b.samples.length, 3);
      expect(b.samples.first, _s(2, 2));
    });

    test('a constant RMS at advancing timestamps keeps appending — the '
        'duplicate rejection keys on timestamp, not value', () {
      final b = RmsRingBuffer(window: const Duration(seconds: 10));
      b.add(_s(0, 0.5));
      b.add(_s(2, 0.5));
      b.add(_s(4, 0.5));
      expect(b.samples.length, 3,
          reason: 'steady guiding must not freeze the trace');
      // ...and eviction still slides the window as those samples age.
      b.add(_s(15, 0.5));
      expect(b.samples, [_s(15, 0.5)]);
    });

    test('clear() empties the buffer', () {
      final b = RmsRingBuffer();
      b.add(_s(0, 0.5));
      b.clear();
      expect(b.isEmpty, isTrue);
    });
  });

  group('guiderArcsecPerPixel', () {
    test('computes 206.265 * µm / mm', () {
      expect(guiderArcsecPerPixel(200, 3.75), closeTo(3.867, 0.001));
    });

    test('unset focal length or pixel size → null', () {
      expect(guiderArcsecPerPixel(0, 3.75), isNull);
      expect(guiderArcsecPerPixel(200, 0), isNull);
      expect(guiderArcsecPerPixel(-1, 3.75), isNull);
    });
  });

  group('liveGuidingRmsProvider', () {
    const server = AraServer(hostname: 'h', port: 5555);

    ProviderContainer container(GuiderClient api) {
      final c = ProviderContainer(overrides: [
        savedServerServiceProvider
            .overrideWithValue(_FakeSavedServerService(const [server])),
        guiderApiFactoryProvider.overrideWithValue((_) => api),
      ]);
      addTearDown(c.dispose);
      return c;
    }

    test('folds guiding statuses into the rolling buffer', () async {
      final api = _FakeGuiderApi()..status = _guiding;
      final c = container(api);
      await c.read(savedServersProvider.future);
      await c.read(guiderStatusProvider.future);

      // Keep the autoDispose provider alive like a mounted panel would.
      final sub = c.listen(liveGuidingRmsProvider, (_, _) {});
      addTearDown(sub.close);
      expect(sub.read().length, 1,
          reason: 'seeds from the already-loaded status');
      expect(sub.read().single.total, 0.5);
      expect(sub.read().single.ra, 0.3);
      expect(sub.read().single.dec, 0.4);

      // A later refresh with a new RMS appends a second point.
      api.status = const GuiderStatus(
        deviceId: 'phd2',
        name: 'OpenAstro Guider',
        connectionState: GuiderConnectionState.connected,
        runtimeState: GuiderRuntimeState.guiding,
        rmsTotal: 0.7,
      );
      await c.read(liveGuidingRmsProvider.notifier).pollTick();
      expect(sub.read().length, 2);
      expect(sub.read().last.total, 0.7);
    });

    test('an UNCHANGED RMS still appends a fresh point on every poll tick',
        () async {
      // GuiderStatus has value equality, so a listener-driven fold would see
      // nothing during steady guiding — the poll tick must append regardless.
      final api = _FakeGuiderApi()..status = _guiding;
      final c = container(api);
      await c.read(savedServersProvider.future);
      await c.read(guiderStatusProvider.future);
      final sub = c.listen(liveGuidingRmsProvider, (_, _) {});
      addTearDown(sub.close);
      expect(sub.read().length, 1);

      await c.read(liveGuidingRmsProvider.notifier).pollTick();
      await c.read(liveGuidingRmsProvider.notifier).pollTick();
      expect(sub.read().length, 3,
          reason: 'constant-RMS guiding must keep the trace sliding');
      expect(sub.read().map((s) => s.total), everyElement(0.5));
    });

    test('non-guiding statuses (stopped, star lost) are not folded', () async {
      final api = _FakeGuiderApi()
        ..status = const GuiderStatus(
          deviceId: 'phd2',
          name: 'OpenAstro Guider',
          connectionState: GuiderConnectionState.connected,
          runtimeState: GuiderRuntimeState.stopped,
          rmsTotal: 0.5,
        );
      final c = container(api);
      await c.read(savedServersProvider.future);
      await c.read(guiderStatusProvider.future);
      final sub = c.listen(liveGuidingRmsProvider, (_, _) {});
      addTearDown(sub.close);
      expect(sub.read(), isEmpty);

      api.status = const GuiderStatus(
        deviceId: 'phd2',
        name: 'OpenAstro Guider',
        connectionState: GuiderConnectionState.connected,
        runtimeState: GuiderRuntimeState.starLost,
        rmsTotal: 2.5,
      );
      await c.read(liveGuidingRmsProvider.notifier).pollTick();
      expect(sub.read(), isEmpty,
          reason: 'star-lost RMS is stale — it must not pollute the trace');
    });

    test('dithering statuses ARE folded (guiding continues through a dither)',
        () async {
      final api = _FakeGuiderApi()
        ..status = const GuiderStatus(
          deviceId: 'phd2',
          name: 'OpenAstro Guider',
          connectionState: GuiderConnectionState.connected,
          runtimeState: GuiderRuntimeState.dithering,
          rmsTotal: 0.9,
        );
      final c = container(api);
      await c.read(savedServersProvider.future);
      await c.read(guiderStatusProvider.future);
      final sub = c.listen(liveGuidingRmsProvider, (_, _) {});
      addTearDown(sub.close);
      expect(sub.read().single.total, 0.9);
    });

    test('a guiding status without an RMS yet is skipped', () async {
      final api = _FakeGuiderApi()
        ..status = const GuiderStatus(
          deviceId: 'phd2',
          name: 'OpenAstro Guider',
          connectionState: GuiderConnectionState.connected,
          runtimeState: GuiderRuntimeState.guiding,
        );
      final c = container(api);
      await c.read(savedServersProvider.future);
      await c.read(guiderStatusProvider.future);
      final sub = c.listen(liveGuidingRmsProvider, (_, _) {});
      addTearDown(sub.close);
      expect(sub.read(), isEmpty);
    });
  });
}
