import 'dart:io';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/alarm/safety_alarm_state.dart';
import 'package:openastroara/state/settings/notifications_settings_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';

import '../../support/section_load.dart';

class _Player implements SafetyAlarmPlayer {
  final played = <String>[];
  @override
  Future<void> playLoop(String assetPath) async => played.add(assetPath);
  @override
  Future<void> stop() async {}
  @override
  void dispose() {}
}

class _Haptics implements SafetyAlarmHaptics {
  int starts = 0;
  int stops = 0;
  @override
  void start() => starts++;
  @override
  void stop() => stops++;
}

void main() {
  late Directory dir;
  setUp(
    () async => dir = await Directory.systemTemp.createTemp('oara-haptics'),
  );
  tearDown(() => dir.delete(recursive: true));

  Future<(ProviderContainer, _Player, _Haptics)> ring({
    required bool companion,
    bool soundAlert = true,
  }) async {
    final player = _Player();
    final haptics = _Haptics();
    final c = ProviderContainer(
      overrides: [
        companionModeProvider.overrideWithValue(companion),
        safetyAlarmHapticsProvider.overrideWithValue(haptics),
        safetyAlarmPrefsProvider.overrideWithValue(
          SafetyAlarmPrefsService(supportDir: () async => dir),
        ),
      ],
    );
    addTearDown(c.dispose);
    await loadSection(c, notificationsSettingsProvider, 'notifications');
    c.read(notificationsSettingsProvider.notifier).setSoundAlert(soundAlert);
    c.listen(safetyAlarmProvider, (_, _) {});
    final alarm = c.read(safetyAlarmProvider.notifier);
    alarm.playerFactory = () => player;
    alarm.setDelaySec(0);
    alarm.trigger('wind over the limit');
    await Future<void>.delayed(const Duration(milliseconds: 100));
    // The ring waits on a real prefs file read, which a loaded host can
    // stretch past any fixed wait: poll for it when a ring is expected.
    final deadline = DateTime.now().add(const Duration(seconds: 5));
    while (soundAlert &&
        !c.read(safetyAlarmProvider).ringing &&
        DateTime.now().isBefore(deadline)) {
      await Future<void>.delayed(const Duration(milliseconds: 20));
    }
    return (c, player, haptics);
  }

  test(
    'on a phone the alarm rings and vibrates, and silence stops both',
    () async {
      final (c, player, haptics) = await ring(companion: true);
      expect(player.played, isNotEmpty);
      expect(haptics.starts, 1);
      c.read(safetyAlarmProvider.notifier).silence();
      expect(haptics.stops, 1);
    },
  );

  test('off a phone the alarm rings and never vibrates', () async {
    final (_, player, haptics) = await ring(companion: false);
    expect(player.played, isNotEmpty);
    expect(haptics.starts, 0);
  });

  test('with Sound alert off a phone neither rings nor vibrates', () async {
    final (c, player, haptics) = await ring(companion: true, soundAlert: false);
    expect(c.read(safetyAlarmProvider).pending, isTrue);
    expect(player.played, isEmpty);
    expect(haptics.starts, 0);
  });
}
