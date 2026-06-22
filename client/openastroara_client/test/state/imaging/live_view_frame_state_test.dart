import 'dart:typed_data';

import 'package:fake_async/fake_async.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/live_view_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/imaging/live_view_frame_state.dart';
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

class _FakeLiveViewClient implements LiveViewClient {
  final List<String> calls = [];
  final List<LiveFrame?> frames;
  int _i = 0;
  bool startThrows = false;
  _FakeLiveViewClient(this.frames);
  @override
  Future<void> start({required double exposureSec, int? gain, int binX = 2, int binY = 2}) async {
    calls.add('start:$exposureSec:$binX');
    if (startThrows) throw StateError('boom');
  }
  @override
  Future<void> stop() async => calls.add('stop');
  @override
  Future<LiveFrame?> fetchFrame() async {
    final f = _i < frames.length ? frames[_i] : frames.isEmpty ? null : frames.last;
    _i++;
    return f;
  }
  @override
  void close() => calls.add('close');
}

ProviderContainer _container(SavedServerService servers, LiveViewClient client) =>
    ProviderContainer(overrides: [
      savedServerServiceProvider.overrideWithValue(servers),
      liveViewApiFactoryProvider.overrideWithValue((_) => client),
    ]);

void main() {
  final jpegA = Uint8List.fromList([1, 2, 3]);
  final jpegB = Uint8List.fromList([4, 5, 6]);

  test('start with no saved server reports an error, stays inactive', () async {
    final client = _FakeLiveViewClient(const []);
    final c = _container(_FakeSavedServerService(const []), client);
    addTearDown(c.dispose);
    await c.read(liveViewFrameProvider.notifier).start(exposureSec: 1.0);
    final s = c.read(liveViewFrameProvider);
    expect(s.active, isFalse);
    expect(s.error, isNotNull);
    expect(client.calls, isEmpty); // never built a client / called start
  });

  test('start activates and polls a frame; change-detect ignores a repeat', () {
    // fakeAsync drives the 250 ms poll timer deterministically (no wall clock).
    fakeAsync((async) {
      // Two fetches return the same frame (seq 1), the third a new one (seq 2).
      final f1 = LiveFrame(jpegA, 1, 7);
      final client = _FakeLiveViewClient([f1, f1, LiveFrame(jpegB, 2, 7)]);
      final c = _container(
          _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)]), client);
      addTearDown(c.dispose);

      c.read(savedServersProvider); // kick the async server-list load
      async.flushMicrotasks(); // resolve loadAll()

      c.read(liveViewFrameProvider.notifier).start(exposureSec: 2.0, binX: 2);
      async.flushMicrotasks(); // resolve start() → active + first poll scheduled
      expect(c.read(liveViewFrameProvider).active, isTrue);
      expect(client.calls.first, startsWith('start:2.0:2'));

      async.elapse(const Duration(milliseconds: 800)); // three 250 ms poll ticks
      final s = c.read(liveViewFrameProvider);
      expect(s.seq, 2); // advanced past the repeated seq-1 frame
      expect(s.session, 7);
      expect(s.jpeg, jpegB);

      c.read(liveViewFrameProvider.notifier).stop();
      async.flushMicrotasks();
      final stopped = c.read(liveViewFrameProvider);
      expect(stopped.active, isFalse);
      expect(stopped.jpeg, isNull);
      expect(client.calls, contains('stop'));
    });
  });

  test('a start failure surfaces the error and leaves the loop inactive', () async {
    final client = _FakeLiveViewClient(const [])..startThrows = true;
    final c = _container(
        _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)]), client);
    addTearDown(c.dispose);
    await c.read(savedServersProvider.future); // let the async server list load
    await c.read(liveViewFrameProvider.notifier).start(exposureSec: 1.0);
    final s = c.read(liveViewFrameProvider);
    expect(s.active, isFalse);
    expect(s.error, contains('boom'));
    expect(client.calls, contains('close'));
  });
}
