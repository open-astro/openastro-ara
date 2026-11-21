import 'package:fake_async/fake_async.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/simple_live_stack_state.dart';

import '../helpers/fake_session_live_stack_client.dart';

class _NoPrefs extends SimpleModePrefsService {
  @override
  Future<bool> load() async => false;
  @override
  Future<void> save(bool enabled) async {}
}

ProviderContainer _container(FakeSessionLiveStackClient daemon) {
  final c = ProviderContainer(
    overrides: [
      activeServerProvider.overrideWithValue(
        const AraServer(hostname: '192.0.2.10', port: 5000),
      ),
      sessionLiveStackApiFactoryProvider.overrideWithValue((_) => daemon),
      simpleLiveStackViewPrefsProvider.overrideWithValue(_NoPrefs()),
    ],
  );
  addTearDown(c.dispose);
  return c;
}

void main() {
  test('the container disposed under a watcher leaves no timer running', () {
    fakeAsync((async) {
      final daemon = FakeSessionLiveStackClient()
        ..enabled = true
        ..stackOne();
      final c = _container(daemon);
      c.listen(simpleLiveStackProvider, (_, _) {});
      async.flushMicrotasks();
      expect(async.periodicTimerCount, 1);
      c.dispose();
      expect(async.periodicTimerCount, 0);
    });
  });

  test('a daemon that reports the stack off but still counts lights holds '
      'no frame', () {
    fakeAsync((async) {
      final daemon = FakeSessionLiveStackClient()
        ..enabled = true
        ..stackOne();
      final c = _container(daemon);
      c.listen(simpleLiveStackProvider, (_, _) {});
      async.flushMicrotasks();
      expect(c.read(simpleLiveStackProvider).frame?.seq, 1);
      daemon.enabled = false;
      async.elapse(kSimpleLiveStackPoll);
      async.flushMicrotasks();
      expect(c.read(simpleLiveStackProvider).frame, isNull);
    });
  });

  test('a camera the daemon calls not_colour is not polled, even with the '
      'daemon flag on', () {
    fakeAsync((async) {
      final daemon = FakeSessionLiveStackClient()
        ..enabled = true
        ..unsupported = 'not_colour';
      final c = _container(daemon);
      c.listen(simpleLiveStackProvider, (_, _) {});
      async.flushMicrotasks();
      final before = daemon.count('status');
      async.elapse(kSimpleLiveStackPoll * 5);
      async.flushMicrotasks();
      expect(daemon.count('status'), before);
    });
  });
}
