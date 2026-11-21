import 'dart:async';

import 'package:fake_async/fake_async.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/session_live_stack_api.dart';
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

/// While [slow], each status() answers what the daemon held when asked, but
/// only once its own gate opens: a slow read can land after a later one.
class _SlowDaemon extends FakeSessionLiveStackClient {
  bool slow = false;
  final gates = <Completer<void>>[];

  @override
  Future<SessionLiveStackStatus> status() async {
    final answer = super.status();
    if (slow) {
      final gate = Completer<void>();
      gates.add(gate);
      await gate.future;
    }
    return answer;
  }
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

  test('a slow poll tick that lands after a stop does not show the stack on '
      'again', () {
    fakeAsync((async) {
      final daemon = _SlowDaemon()
        ..enabled = true
        ..stackOne();
      final c = _container(daemon);
      c.listen(simpleLiveStackProvider, (_, _) {});
      async.flushMicrotasks();
      expect(c.read(simpleLiveStackProvider).on, isTrue);
      daemon.slow = true;
      async.elapse(kSimpleLiveStackPoll);
      expect(daemon.gates, hasLength(1));
      daemon.slow = false;
      c.read(simpleLiveStackProvider.notifier).setOn(false);
      async.flushMicrotasks();
      expect(c.read(simpleLiveStackProvider).on, isFalse);
      daemon.gates.single.complete();
      async.flushMicrotasks();
      expect(c.read(simpleLiveStackProvider).on, isFalse);
    });
  });
}
