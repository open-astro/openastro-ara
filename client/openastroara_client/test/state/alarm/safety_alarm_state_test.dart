import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/ws_event_stream.dart';
import 'package:openastroara/state/alarm/safety_alarm_state.dart';
import 'package:openastroara/state/settings/notifications_settings_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';

class _RecordingPlayer implements SafetyAlarmPlayer {
  final List<String> played = [];
  int stops = 0;
  @override
  Future<void> playLoop(String assetPath) async => played.add(assetPath);
  @override
  Future<void> stop() async => stops++;
  @override
  void dispose() {}
}

class _FakeSocket {
  final StreamController<dynamic> incoming = StreamController<dynamic>();
  late final WsSocket socket = WsSocket(
    stream: incoming.stream,
    send: (_) {},
    close: () async {
      if (!incoming.isClosed) await incoming.close();
    },
    closeCode: () => null,
  );
}

void main() {
  late Directory prefsDir;
  late _FakeSocket socket;
  late _RecordingPlayer player;
  late ProviderContainer container;
  late WsEventStream stream;

  setUp(() async {
    prefsDir = await Directory.systemTemp.createTemp('oara-alarm-prefs');
    socket = _FakeSocket();
    player = _RecordingPlayer();
    stream = WsEventStream(const AraServer(hostname: 'pi-test', port: 5555),
        connect: (url, headers) => socket.socket);
    container = ProviderContainer(overrides: [
      wsEventStreamProvider.overrideWith((ref) {
        stream.connect();
        return stream;
      }),
      safetyAlarmPrefsProvider.overrideWithValue(
          SafetyAlarmPrefsService(supportDir: () async => prefsDir)),
    ]);
    // Sound alert defaults on in NotificationsSettings? Ensure it's on.
    container.read(notificationsSettingsProvider.notifier).setSoundAlert(true);
    // Riverpod 3 pauses providers without active listeners — in production
    // the shell's SafetyAlarmListener watches; tests need an explicit one or
    // the controller's ws subscription never delivers.
    container.listen(safetyAlarmProvider, (prev, next) {});
    final c = container.read(safetyAlarmProvider.notifier);
    c.playerFactory = () => player;
    c.setDelaySec(0); // compress the silent window for tests
    // connect() runs the §27 claim (a real Dio dial that fails) BEFORE the
    // fake socket is wired — wait until the stream is actually listening or
    // the first emits race into a buffered-but-unread controller.
    for (var i = 0; i < 400 && !socket.incoming.hasListener; i++) {
      await Future<void>.delayed(const Duration(milliseconds: 25));
    }
    expect(socket.incoming.hasListener, isTrue,
        reason: 'the WS stream must be listening before tests emit');
  });

  tearDown(() async {
    container.dispose();
    stream.dispose();
    try {
      await prefsDir.delete(recursive: true);
    } on FileSystemException {
      // best effort
    }
  });

  void emit(String type, [Map<String, dynamic> payload = const {}]) {
    socket.incoming.add(jsonEncode({
      'type': type,
      'ts': '2026-01-01T00:00:00Z',
      'seq': 1,
      'payload': payload,
    }));
  }

  Future<void> settle() async {
    for (var i = 0; i < 20; i++) {
      await Future<void>.delayed(const Duration(milliseconds: 10));
    }
  }

  test('safety.unsafe rings the selected tone with the reasons surfaced', () async {
    container.read(safetyAlarmProvider.notifier).setTone('beeps');
    emit('safety.unsafe', {
      'reasons': ['wind 54 km/h over the 36 km/h limit'],
    });
    await settle();

    final state = container.read(safetyAlarmProvider);
    expect(state.ringing, isTrue);
    expect(state.reason, contains('wind'));
    expect(player.played, ['audio/alarm_beeps.wav']);
  });

  test('safety.safe auto-silences a ringing alarm', () async {
    emit('safety.unsafe');
    await settle();
    expect(container.read(safetyAlarmProvider).ringing, isTrue);

    emit('safety.safe');
    await settle();

    expect(container.read(safetyAlarmProvider).ringing, isFalse);
    expect(player.stops, 1);
  });

  test('silence during the delay window means the tone never plays', () async {
    final c = container.read(safetyAlarmProvider.notifier);
    c.setDelaySec(300); // long silent window
    emit('safety.emergency_stop');
    await settle();
    expect(container.read(safetyAlarmProvider).pending, isTrue);

    c.silence();

    expect(container.read(safetyAlarmProvider).pending, isFalse);
    expect(player.played, isEmpty,
        reason: 'silencing within the delay is the whole point of the delay');
  });

  test('the master Sound alert toggle off means no alarm at all', () async {
    container.read(notificationsSettingsProvider.notifier).setSoundAlert(false);
    emit('safety.unsafe');
    await settle();

    final state = container.read(safetyAlarmProvider);
    expect(state.pending, isFalse);
    expect(state.ringing, isFalse);
    expect(player.played, isEmpty);
  });

  test('a flapping monitor does not stack alarms', () async {
    final c = container.read(safetyAlarmProvider.notifier);
    emit('safety.unsafe');
    await settle();
    c.trigger('again');
    c.trigger('and again');
    await settle();

    expect(player.played, hasLength(1), reason: 'one alarm per episode');
  });
}
