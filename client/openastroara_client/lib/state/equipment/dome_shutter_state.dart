import 'dart:convert';
import 'dart:io';
import 'dart:math' as math;

import 'package:clock/clock.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:path_provider/path_provider.dart';

import '../../models/dome_status.dart';
import 'dome_state.dart';

/// How long each dome's shutter took on its last full move, by device id,
/// so the Simple Mode Dome window can slide its drawing over about as long
/// as the real one takes (#1662). Same small-JSON-file pattern as
/// LastRigService.
class DomeShutterTimesService {
  /// [supportDir] overrides the app-support directory lookup (tests).
  DomeShutterTimesService({Future<Directory> Function()? supportDir})
    : _supportDir = supportDir ?? getApplicationSupportDirectory;

  final Future<Directory> Function() _supportDir;

  Future<File> _file() async =>
      File('${(await _supportDir()).path}/dome_shutter_times.json');

  /// Seconds by device id; empty when nothing is stored or on any read error.
  Future<Map<String, double>> load() async {
    try {
      final f = await _file();
      if (!await f.exists()) return {};
      final decoded = jsonDecode(await f.readAsString());
      if (decoded is! Map) return {};
      return {
        for (final e in decoded.entries)
          if (e.key is String && e.value is num)
            e.key as String: (e.value as num).toDouble(),
      };
    } catch (_) {
      return {};
    }
  }

  Future<void> save(Map<String, double> times) async {
    try {
      await (await _file()).writeAsString(jsonEncode(times), flush: true);
    } catch (_) {
      /* best effort: the next move times it again */
    }
  }
}

final domeShutterTimesServiceProvider = Provider<DomeShutterTimesService>(
  (ref) => DomeShutterTimesService(),
);

/// A shutter move under way: which way (null when Ara didn't see where it
/// started), since when, and how long this dome took last time.
class DomeShutterMove {
  const DomeShutterMove({
    required this.opening,
    required this.startedAt,
    required this.expected,
  });

  final bool? opening;
  final DateTime startedAt;
  final Duration expected;

  /// How far along, 0 to 1: at the expected pace to 80 %, then slowing
  /// toward the end without reaching it, so a dome slower than last time
  /// never freezes at a cap and the landing has no big jump. The curve meets
  /// the steady slide at the same pace, with no kink.
  double progressAt(DateTime now) {
    final ms = expected.inMilliseconds;
    if (ms <= 0) return 0.95;
    final p = math.max(0.0, now.difference(startedAt).inMilliseconds / ms);
    const knee = 0.8, room = 0.19;
    return p <= knee ? p : knee + room * (1 - math.exp(-(p - knee) / room));
  }
}

/// Watches the dome: a shutter move starting is timed (its direction from
/// the shutter's state before it), and a move seen from its start to its end
/// teaches the expected time for that dome, kept on this device.
class DomeShutterNotifier extends Notifier<DomeShutterMove?> {
  /// Until a full move has been seen.
  static const fallback = Duration(seconds: 60);

  final _times = <String, double>{};
  bool? _wasOpen;
  String? _device;

  /// When this app last asked the shutter to move: only a move seen from
  /// that ask is timed. One a plan or the safety policy started is seen up
  /// to a poll late, and would teach a time too short.
  DateTime? _askedAt;

  /// The current move started from an ask, so its time is worth keeping.
  bool _timed = false;

  /// The Dome window is about to ask the shutter to open or close.
  void asked() => _askedAt = clock.now();

  @override
  DomeShutterMove? build() {
    ref.read(domeShutterTimesServiceProvider).load().then((t) {
      if (ref.mounted) _times.addAll(t);
    });
    ref.listen<DomeStatus?>(
      domeProvider.select((s) => s.value),
      (_, s) => _seen(s),
    );
    // Opened mid-move: which way is unknown, so is how far.
    final s = ref.read(domeProvider).value;
    if (s == null || !s.isConnected) return null;
    _device = s.deviceId;
    if (s.runtimeState != 'shutter_moving') {
      _wasOpen = s.shutterOpen;
      return null;
    }
    return DomeShutterMove(
      opening: null,
      startedAt: clock.now(),
      expected: fallback,
    );
  }

  void _seen(DomeStatus? s) {
    final moving =
        s != null && s.isConnected && s.runtimeState == 'shutter_moving';
    if (s != null && s.deviceId != _device) {
      _device = s.deviceId;
      _wasOpen = null;
    }
    final move = state;
    if (moving && move == null) {
      final seconds = _times[s.deviceId];
      final asked = _askedAt;
      _askedAt = null;
      _timed = asked != null && clock.now().difference(asked).inSeconds < 5;
      state = DomeShutterMove(
        opening: _wasOpen == null ? null : !_wasOpen!,
        startedAt: _timed ? asked! : clock.now(),
        expected: seconds == null
            ? fallback
            : Duration(milliseconds: (seconds * 1000).round()),
      );
    } else if (!moving && move != null) {
      state = null;
      // Only a move seen from its start is a time worth keeping; a stop, a
      // fault or a dome that went away cuts it short.
      if (_timed &&
          s != null &&
          s.isConnected &&
          move.opening != null &&
          s.shutterOpen == move.opening) {
        final took = clock.now().difference(move.startedAt).inMilliseconds;
        if (took >= 1000 && took <= 600000) {
          _times[s.deviceId] = took / 1000;
          ref.read(domeShutterTimesServiceProvider).save(Map.of(_times));
        }
      }
    }
    if (s != null && s.isConnected && !moving) _wasOpen = s.shutterOpen;
  }
}

final domeShutterProvider =
    NotifierProvider<DomeShutterNotifier, DomeShutterMove?>(
      DomeShutterNotifier.new,
    );
