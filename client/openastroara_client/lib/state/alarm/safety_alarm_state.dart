import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:audioplayers/audioplayers.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:path_provider/path_provider.dart';

import '../settings/notifications_settings_state.dart';
import '../ws/ws_providers.dart';

/// §35.5 bundled tones (generated sine-synth loops shipped in assets/audio/).
const safetyAlarmTones = <String, String>{
  'siren': 'audio/alarm_siren.wav',
  'beeps': 'audio/alarm_beeps.wav',
  'chime': 'audio/alarm_chime.wav',
};

/// The audio surface the controller drives — a seam so tests assert plays
/// without real audio output.
abstract interface class SafetyAlarmPlayer {
  Future<void> playLoop(String assetPath);
  Future<void> stop();
  void dispose();
}

/// Production player: audioplayers at forced-max volume, looping until
/// stopped — a safety alarm that respects a muted mixer defeats its purpose.
class AudioplayersAlarmPlayer implements SafetyAlarmPlayer {
  final AudioPlayer _player = AudioPlayer();

  @override
  Future<void> playLoop(String assetPath) async {
    await _player.setReleaseMode(ReleaseMode.loop);
    await _player.setVolume(1.0);
    await _player.play(AssetSource(assetPath));
  }

  @override
  Future<void> stop() => _player.stop();

  @override
  void dispose() => _player.dispose();
}

/// Device-local §35.5 knobs (each desktop picks its own tone/delay — the
/// machine that rings is the one being configured). Same JSON-in-app-support
/// pattern as the backup-stream prefs.
class SafetyAlarmPrefsService {
  SafetyAlarmPrefsService({Future<Directory> Function()? supportDir})
      : _supportDir = supportDir ?? getApplicationSupportDirectory;

  final Future<Directory> Function() _supportDir;
  static const _fileName = 'safety_alarm.json';
  Future<void> _chain = Future<void>.value();

  Future<File> _file() async {
    final dir = await _supportDir();
    return File('${dir.path}/$_fileName');
  }

  Future<({int delaySec, String tone})> load() async {
    try {
      final f = await _file();
      if (!await f.exists()) return (delaySec: 5, tone: 'siren');
      final decoded = jsonDecode(await f.readAsString());
      if (decoded is! Map) return (delaySec: 5, tone: 'siren');
      final tone = decoded['tone'] is String ? decoded['tone'] as String : 'siren';
      return (
        delaySec: decoded['delay_sec'] is int ? decoded['delay_sec'] as int : 5,
        tone: safetyAlarmTones.containsKey(tone) ? tone : 'siren',
      );
    } catch (_) {
      return (delaySec: 5, tone: 'siren');
    }
  }

  Future<void> save({required int delaySec, required String tone}) {
    final task = _chain.then((_) async {
      try {
        final f = await _file();
        await f.writeAsString(
            jsonEncode({'delay_sec': delaySec, 'tone': tone}), flush: true);
      } catch (_) {/* best effort — prefs must not break the alarm */}
    });
    _chain = task;
    return task;
  }
}

final safetyAlarmPrefsProvider =
    Provider<SafetyAlarmPrefsService>((ref) => SafetyAlarmPrefsService());

@immutable
class SafetyAlarmState {
  /// A safety event fired and the silent-popup window is counting down.
  final bool pending;

  /// The tone is looping right now.
  final bool ringing;

  /// What tripped — shown on the alarm modal.
  final String reason;

  final int delaySec;
  final String tone;

  const SafetyAlarmState({
    this.pending = false,
    this.ringing = false,
    this.reason = '',
    this.delaySec = 5,
    this.tone = 'siren',
  });

  SafetyAlarmState copyWith({
    bool? pending,
    bool? ringing,
    String? reason,
    int? delaySec,
    String? tone,
  }) =>
      SafetyAlarmState(
        pending: pending ?? this.pending,
        ringing: ringing ?? this.ringing,
        reason: reason ?? this.reason,
        delaySec: delaySec ?? this.delaySec,
        tone: tone ?? this.tone,
      );
}

/// §35.5 — the audible alarm. `safety.unsafe` / `safety.emergency_stop`
/// arrive BEFORE the daemon's reaction runs (the §35.4 contract exists for
/// exactly this), so the modal pops immediately and the tone starts after the
/// configured silent delay (default 5 s — a glance at the screen beats a
/// 3 a.m. siren for a passing cloud you were watching anyway). `safety.safe`
/// auto-silences; the modal's Silence button stops it manually. The master
/// on/off is the existing Notifications "Sound alert" toggle.
class SafetyAlarmController extends Notifier<SafetyAlarmState> {
  Timer? _delayTimer;
  SafetyAlarmPlayer? _player;
  bool _userTouched = false;

  // ─── test seams ───
  SafetyAlarmPlayer Function()? playerFactory;

  @override
  SafetyAlarmState build() {
    ref.onDispose(_teardown);
    ref.listen(wsEventsProvider, (prev, next) {
      final event = next.asData?.value;
      if (event == null) return;
      switch (event.type) {
        case 'safety.unsafe':
          final reasons = event.payload['reasons'];
          trigger(reasons is List && reasons.isNotEmpty
              ? reasons.whereType<String>().join('; ')
              : 'Conditions are UNSAFE');
        case 'safety.emergency_stop':
          trigger('EMERGENCY STOP triggered');
        case 'safety.safe':
          silence();
      }
    });
    unawaited(_restore());
    return const SafetyAlarmState();
  }

  Future<void> _restore() async {
    final saved = await ref.read(safetyAlarmPrefsProvider).load();
    if (_userTouched) return;
    state = state.copyWith(delaySec: saved.delaySec, tone: saved.tone);
  }

  void _persist() {
    unawaited(ref
        .read(safetyAlarmPrefsProvider)
        .save(delaySec: state.delaySec, tone: state.tone));
  }

  void setDelaySec(int v) {
    if (v < 0 || v > 300) return;
    _userTouched = true;
    state = state.copyWith(delaySec: v);
    _persist();
  }

  void setTone(String tone) {
    if (!safetyAlarmTones.containsKey(tone)) return;
    _userTouched = true;
    state = state.copyWith(tone: tone);
    _persist();
  }

  /// Fires the alarm flow for [reason]. No-op while one is already pending
  /// or ringing (a flapping monitor must not stack sirens), or when the
  /// Notifications "Sound alert" master toggle is off.
  @visibleForTesting
  void trigger(String reason) {
    if (state.pending || state.ringing) return;
    if (!ref.read(notificationsSettingsProvider).soundAlert) return;
    state = state.copyWith(pending: true, reason: reason);
    _delayTimer?.cancel();
    _delayTimer = Timer(Duration(seconds: state.delaySec), _startRinging);
  }

  void _startRinging() {
    if (!state.pending) return;
    state = state.copyWith(pending: false, ringing: true);
    _player ??= (playerFactory ?? AudioplayersAlarmPlayer.new)();
    unawaited(_player!
        .playLoop(safetyAlarmTones[state.tone] ?? safetyAlarmTones['siren']!)
        .catchError((Object e) => debugPrint('safety alarm play failed: $e')));
  }

  /// Stops the countdown and/or the tone — the modal's Silence button and
  /// the daemon's `safety.safe` both land here.
  void silence() {
    _delayTimer?.cancel();
    _delayTimer = null;
    if (state.ringing) {
      unawaited(_player?.stop());
    }
    if (state.pending || state.ringing) {
      state = state.copyWith(pending: false, ringing: false);
    }
  }

  void _teardown() {
    _delayTimer?.cancel();
    _player?.dispose();
    _player = null;
  }
}

final safetyAlarmProvider =
    NotifierProvider<SafetyAlarmController, SafetyAlarmState>(
        SafetyAlarmController.new);
