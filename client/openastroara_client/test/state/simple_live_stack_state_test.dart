import 'dart:async';
import 'dart:io';
import 'dart:typed_data';

import 'package:fake_async/fake_async.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/session_live_stack_api.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/simple_live_stack_state.dart';

import '../helpers/fake_session_live_stack_client.dart';

class _MemPrefs extends SimpleModePrefsService {
  _MemPrefs(this.value);
  bool value;
  @override
  Future<bool> load() async => value;
  @override
  Future<void> save(bool enabled) async => value = enabled;
}

const _rig = AraServer(hostname: '192.0.2.10', port: 5000);

ProviderContainer _container(
  FakeSessionLiveStackClient daemon, {
  _MemPrefs? prefs,
}) {
  final c = ProviderContainer(
    overrides: [
      activeServerProvider.overrideWithValue(_rig),
      sessionLiveStackApiFactoryProvider.overrideWithValue((_) => daemon),
      simpleLiveStackViewPrefsProvider.overrideWithValue(
        prefs ?? _MemPrefs(false),
      ),
    ],
  );
  addTearDown(c.dispose);
  return c;
}

/// Reads the provider (as a mounted widget does) and lets the first probe land.
SimpleLiveStack _open(FakeAsync async, ProviderContainer c) {
  c.listen(simpleLiveStackProvider, (_, _) {});
  async.flushMicrotasks();
  return c.read(simpleLiveStackProvider);
}

void _tick(FakeAsync async, [Duration d = kSimpleLiveStackPoll]) {
  async.elapse(d);
  async.flushMicrotasks();
}

void main() {
  test('a camera the daemon calls not_colour is unsupported: one probe, no '
      'polling', () {
    fakeAsync((async) {
      final daemon = FakeSessionLiveStackClient()..unsupported = 'not_colour';
      final c = _container(daemon);
      final s = _open(async, c);
      expect(s.supported, isFalse);
      _tick(async, kSimpleLiveStackPoll * 5);
      expect(daemon.count('status'), 1);
    });
  });

  test('a colour camera before its first light is supported and off: the '
      'answer comes from the status, one read, no polling', () {
    fakeAsync((async) {
      final daemon = FakeSessionLiveStackClient();
      final c = _container(daemon);
      final s = _open(async, c);
      expect((s.supported, s.on), (true, false));
      _tick(async, kSimpleLiveStackPoll * 5);
      expect(daemon.calls, ['status']);
    });
  });

  test('setOn(true) posts start once, then polls the status every poll '
      'period', () {
    fakeAsync((async) {
      final daemon = FakeSessionLiveStackClient();
      final c = _container(daemon);
      _open(async, c);
      c.read(simpleLiveStackProvider.notifier).setOn(true);
      async.flushMicrotasks();
      expect(daemon.count('start'), 1);
      expect(c.read(simpleLiveStackProvider).on, isTrue);
      final before = daemon.count('status');
      _tick(async, kSimpleLiveStackPoll * 3);
      expect(daemon.count('status') - before, 3);
      expect(daemon.count('start'), 1);
    });
  });

  test('the frame is fetched only when stack_seq moves, and kept while it '
      'stands still', () {
    fakeAsync((async) {
      final daemon = FakeSessionLiveStackClient();
      final c = _container(daemon);
      _open(async, c);
      c.read(simpleLiveStackProvider.notifier).setOn(true);
      async.flushMicrotasks();
      _tick(async);
      expect(daemon.count('frame'), 0, reason: 'seq 0: nothing stacked yet');
      daemon.stackOne();
      _tick(async);
      expect(daemon.count('frame'), 1);
      expect(c.read(simpleLiveStackProvider).frame!.seq, 1);
      _tick(async, kSimpleLiveStackPoll * 3);
      expect(daemon.count('frame'), 1, reason: 'seq unchanged: no refetch');
      daemon.stackOne();
      _tick(async);
      expect(daemon.count('frame'), 2);
      expect(c.read(simpleLiveStackProvider).frame!.seq, 2);
    });
  });

  test('a stack the daemon already runs (another client, a restart) is '
      'adopted on open: polling and its frame, no second start', () {
    fakeAsync((async) {
      final daemon = FakeSessionLiveStackClient()
        ..enabled = true
        ..stackOne();
      final c = _container(daemon);
      final s = _open(async, c);
      expect(s.on, isTrue);
      async.flushMicrotasks();
      expect(c.read(simpleLiveStackProvider).frame!.seq, 1);
      _tick(async);
      expect(daemon.count('start'), 0);
      expect(daemon.count('status'), greaterThan(1));
    });
  });

  test('a poll tick is skipped while the last status request is in flight', () {
    fakeAsync((async) {
      final daemon = FakeSessionLiveStackClient()..enabled = true;
      final c = _container(daemon);
      _open(async, c);
      daemon.statusGate = Completer<void>();
      _tick(async, kSimpleLiveStackPoll * 4);
      expect(
        daemon.count('status'),
        2,
        reason: 'the probe, then one stuck request; no pile-up behind it',
      );
      daemon.statusGate!.complete();
      async.flushMicrotasks();
      daemon.statusGate = null;
      _tick(async);
      expect(daemon.count('status'), 3);
    });
  });

  test('setOn(false) posts stop, ends the polling and drops the frame', () {
    fakeAsync((async) {
      final daemon = FakeSessionLiveStackClient();
      final c = _container(daemon);
      _open(async, c);
      c.read(simpleLiveStackProvider.notifier).setOn(true);
      daemon.stackOne();
      _tick(async);
      expect(c.read(simpleLiveStackProvider).frame, isNotNull);
      c.read(simpleLiveStackProvider.notifier).setOn(false);
      async.flushMicrotasks();
      expect(daemon.count('stop'), 1);
      expect(c.read(simpleLiveStackProvider).frame, isNull);
      final before = daemon.calls.length;
      _tick(async, kSimpleLiveStackPoll * 5);
      expect(daemon.calls.length, before, reason: 'no request after stop');
    });
  });

  test('reset posts reset, keeps the stack on and drops the frame; the next '
      'light starts it again from the new seq', () {
    fakeAsync((async) {
      final daemon = FakeSessionLiveStackClient();
      final c = _container(daemon);
      _open(async, c);
      c.read(simpleLiveStackProvider.notifier).setOn(true);
      daemon.stackOne();
      daemon.stackOne();
      _tick(async);
      expect(c.read(simpleLiveStackProvider).frame!.seq, 2);
      c.read(simpleLiveStackProvider.notifier).reset();
      async.flushMicrotasks();
      expect(daemon.count('reset'), 1);
      expect(c.read(simpleLiveStackProvider).on, isTrue);
      expect(c.read(simpleLiveStackProvider).frame, isNull);
      daemon.stackOne(); // seq 3: the daemon's seq never goes back on reset
      _tick(async);
      expect(c.read(simpleLiveStackProvider).frame!.seq, 3);
    });
  });

  test('a reset from another client (lights 0, stack_seq unchanged) drops '
      'the held frame: nothing stacked means nothing shown', () {
    fakeAsync((async) {
      final daemon = FakeSessionLiveStackClient()
        ..enabled = true
        ..stackOne()
        ..stackOne();
      final c = _container(daemon);
      _open(async, c);
      async.flushMicrotasks();
      expect(c.read(simpleLiveStackProvider).frame!.seq, 2);
      daemon
        ..lights = 0
        ..frameNow = null;
      _tick(async);
      expect(c.read(simpleLiveStackProvider).frame, isNull);
      expect(daemon.seq, 2, reason: 'the daemon keeps its seq on reset');
    });
  });

  test('a daemon restart (stack_seq falls, a new frame, no empty status '
      'between) replaces the held frame: refetch on any change, not on a '
      'rise', () {
    fakeAsync((async) {
      final daemon = FakeSessionLiveStackClient()
        ..enabled = true
        ..stackOne()
        ..stackOne();
      final c = _container(daemon);
      _open(async, c);
      async.flushMicrotasks();
      expect(c.read(simpleLiveStackProvider).frame!.seq, 2);
      daemon
        ..seq = 1
        ..lights = 1
        ..frameNow = StackFrame(Uint8List.fromList([9]), 1, 1);
      _tick(async);
      final f = c.read(simpleLiveStackProvider).frame!;
      expect((f.seq, f.bytes.first), (1, 9));
    });
  });

  test('the camera session ending (unsupported no_camera, the switch still '
      'on in the daemon) stops the polling and clears the frame', () {
    fakeAsync((async) {
      final daemon = FakeSessionLiveStackClient()
        ..enabled = true
        ..stackOne();
      final c = _container(daemon);
      _open(async, c);
      daemon.unsupported = 'no_camera';
      _tick(async);
      final s = c.read(simpleLiveStackProvider);
      expect((s.supported, s.frame), (false, null));
      final before = daemon.calls.length;
      _tick(async, kSimpleLiveStackPoll * 5);
      expect(daemon.calls.length, before);
      // The USB drop ends: the rig kept stacking, the client re-probes.
      daemon
        ..unsupported = null
        ..stackOne();
      _tick(async, kSimpleLiveStackReprobe);
      final back = c.read(simpleLiveStackProvider);
      expect((back.supported, back.on, back.frame?.seq), (true, true, 2));
    });
  });

  test('a failed first probe is retried on the re-probe timer: the row '
      'appears and an enabled stack is adopted once the rig answers', () {
    fakeAsync((async) {
      final daemon = FakeSessionLiveStackClient()
        ..statusError = StateError('rejoining the hotspot');
      final c = _container(daemon);
      final first = _open(async, c);
      expect((first.supported, first.error != null), (false, true));
      daemon
        ..statusError = null
        ..enabled = true
        ..stackOne();
      _tick(async, kSimpleLiveStackReprobe);
      final s = c.read(simpleLiveStackProvider);
      expect((s.supported, s.on, s.frame?.seq, s.error), (true, true, 1, null));
    });
  });

  test('the camera connects after open (no_camera first): the row appears '
      'and an enabled stack is adopted, without reopening the screen', () {
    fakeAsync((async) {
      final daemon = FakeSessionLiveStackClient()..unsupported = 'no_camera';
      final c = _container(daemon);
      expect(_open(async, c).supported, isFalse);
      daemon
        ..unsupported = null
        ..enabled = true
        ..stackOne();
      _tick(async, kSimpleLiveStackReprobe);
      final s = c.read(simpleLiveStackProvider);
      expect((s.supported, s.on, s.frame?.seq), (true, true, 1));
    });
  });

  test('a camera that is not_colour, too_large or not_available stays one '
      'probe: no re-probe', () {
    for (final token in ['not_colour', 'too_large', 'not_available']) {
      fakeAsync((async) {
        final daemon = FakeSessionLiveStackClient()..unsupported = token;
        final c = _container(daemon);
        _open(async, c);
        _tick(async, kSimpleLiveStackReprobe * 3);
        expect(daemon.count('status'), 1, reason: token);
      });
    }
  });

  test('a failed start leaves the stack as the daemon reports it (off): the '
      'error is recorded and nothing polls', () {
    fakeAsync((async) {
      final daemon = FakeSessionLiveStackClient()
        ..startError = StateError('refused');
      final c = _container(daemon);
      _open(async, c);
      c.read(simpleLiveStackProvider.notifier).setOn(true);
      async.flushMicrotasks();
      final s = c.read(simpleLiveStackProvider);
      expect((s.on, s.error != null), (false, true));
      final before = daemon.calls.length;
      _tick(async, kSimpleLiveStackPoll * 5);
      expect(daemon.calls.length, before);
    });
  });

  test('a failed stop leaves the stack on and its frame held, as the daemon '
      'still stacks: the error is recorded', () {
    fakeAsync((async) {
      final daemon = FakeSessionLiveStackClient()
        ..enabled = true
        ..stackOne()
        ..stopError = StateError('refused');
      final c = _container(daemon);
      _open(async, c);
      async.flushMicrotasks();
      c.read(simpleLiveStackProvider.notifier).setOn(false);
      async.flushMicrotasks();
      final s = c.read(simpleLiveStackProvider);
      expect((s.on, s.frame?.seq, s.error != null), (true, 1, true));
    });
  });

  test('a failed poll keeps the last stacked frame, records the error and '
      'the next tick retries', () {
    fakeAsync((async) {
      final daemon = FakeSessionLiveStackClient()
        ..enabled = true
        ..stackOne();
      final c = _container(daemon);
      _open(async, c);
      daemon.statusError = StateError('link down');
      _tick(async);
      expect(c.read(simpleLiveStackProvider).frame!.seq, 1);
      expect(c.read(simpleLiveStackProvider).error, isNotNull);
      daemon.statusError = null;
      daemon.stackOne();
      _tick(async);
      expect(c.read(simpleLiveStackProvider).frame!.seq, 2);
      expect(c.read(simpleLiveStackProvider).error, isNull);
    });
  });

  test('leaving the screen (the provider disposed) cancels the timer and '
      'closes the client', () {
    fakeAsync((async) {
      final daemon = FakeSessionLiveStackClient()..enabled = true;
      final c = _container(daemon);
      final sub = c.listen(simpleLiveStackProvider, (_, _) {});
      async.flushMicrotasks();
      sub.close();
      async.flushMicrotasks();
      final before = daemon.count('status');
      _tick(async, kSimpleLiveStackPoll * 5);
      expect(daemon.count('status'), before);
      expect(daemon.count('close'), 1);
    });
  });

  group('what shows: the stack or the last light', () {
    SimpleLiveStack state({
      required bool on,
      required SimpleLiveStackView view,
      required bool frame,
    }) => SimpleLiveStack(
      status: SessionLiveStackStatus(enabled: on, stackSeq: 1),
      frame: frame ? StackFrame(Uint8List.fromList([1]), 1, 1) : null,
      view: view,
    );

    test('the stack shows only when it is on, chosen and has a frame; '
        'otherwise the last light does', () {
      const stack = SimpleLiveStackView.stack;
      const last = SimpleLiveStackView.lastLight;
      expect(
        simpleStackShown(state(on: true, view: stack, frame: true)),
        isTrue,
      );
      expect(
        simpleStackShown(state(on: true, view: last, frame: true)),
        isFalse,
        reason: 'Last light chosen',
      );
      expect(
        simpleStackShown(state(on: true, view: stack, frame: false)),
        isFalse,
        reason: 'no stacked frame yet: the last light stays',
      );
      expect(
        simpleStackShown(state(on: false, view: stack, frame: true)),
        isFalse,
        reason: 'off: a stale frame never shows',
      );
    });
  });

  test('the view choice: Live Stack by default, saved per client in its own '
      'file, and it outlives the stack going off', () async {
    final dir = await Directory.systemTemp.createTemp('simple_stack_view');
    addTearDown(() => dir.delete(recursive: true));
    final prefs = SimpleModePrefsService(
      supportDir: () async => dir,
      fileName: 'simple_live_stack_last_light.json',
    );
    final daemon = FakeSessionLiveStackClient();
    final c = ProviderContainer(
      overrides: [
        activeServerProvider.overrideWithValue(_rig),
        sessionLiveStackApiFactoryProvider.overrideWithValue((_) => daemon),
        simpleLiveStackViewPrefsProvider.overrideWithValue(prefs),
      ],
    );
    addTearDown(c.dispose);
    c.listen(simpleLiveStackProvider, (_, _) {});
    await Future<void>.delayed(Duration.zero);
    expect(c.read(simpleLiveStackProvider).view, SimpleLiveStackView.stack);
    await c
        .read(simpleLiveStackProvider.notifier)
        .setView(SimpleLiveStackView.lastLight);
    expect(await prefs.load(), isTrue);
    await c.read(simpleLiveStackProvider.notifier).setOn(true);
    await c.read(simpleLiveStackProvider.notifier).setOn(false);
    expect(c.read(simpleLiveStackProvider).view, SimpleLiveStackView.lastLight);

    final reopened = ProviderContainer(
      overrides: [
        activeServerProvider.overrideWithValue(_rig),
        sessionLiveStackApiFactoryProvider.overrideWithValue((_) => daemon),
        simpleLiveStackViewPrefsProvider.overrideWithValue(prefs),
      ],
    );
    addTearDown(reopened.dispose);
    reopened.listen(simpleLiveStackProvider, (_, _) {});
    await Future<void>.delayed(Duration.zero);
    await Future<void>.delayed(Duration.zero);
    expect(
      reopened.read(simpleLiveStackProvider).view,
      SimpleLiveStackView.lastLight,
    );
  });
}
