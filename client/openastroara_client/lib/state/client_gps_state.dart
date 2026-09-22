import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../services/client_gps_prefs_service.dart';
import '../services/serial_gps_source.dart';
import '../util/nmea_parser.dart';
import 'time_sync_state.dart';

/// Injection seams (tests override with a canned source / temp-dir prefs).
final serialGpsSourceProvider = Provider<SerialGpsSource>((_) => const LibSerialPortGpsSource());
final clientGpsPrefsServiceProvider = Provider<ClientGpsPrefsService>((_) => ClientGpsPrefsService());

/// How long one read listens for a fix (tests override with milliseconds).
final clientGpsListenWindowProvider = Provider<Duration>((_) => kClientGpsListenWindow);

/// How long one acquisition listens for a usable RMC (+ GGA for altitude).
/// A warm receiver emits RMC every second; a cold one needs a minute or more
/// under open sky, which the periodic loop covers by retrying.
const Duration kClientGpsListenWindow = Duration(seconds: 15);

/// Re-sync cadence, mirroring the daemon's own USB-GPS worker: keep trying
/// every couple of minutes until a fix has been pushed, then hourly-ish.
const Duration kClientGpsUnsyncedInterval = Duration(minutes: 2);
const Duration kClientGpsSyncedInterval = Duration(minutes: 50);

/// What the settings panel shows and what "Fill from GPS" reads.
@immutable
class ClientGpsStatus {
  const ClientGpsStatus({
    required this.prefs,
    required this.supported,
    this.lastFix,
    this.lastFixAt,
    this.lastPushAt,
    this.lastError,
    this.busy = false,
  });

  final ClientGpsPrefs prefs;
  final bool supported;
  final NmeaFix? lastFix;
  final DateTime? lastFixAt;
  final DateTime? lastPushAt;
  final String? lastError;
  final bool busy;

  bool get enabled => supported && prefs.enabled;

  /// A fix is usable for a site fill when it carries a position and is
  /// recent (the same ten-minute rule the device-location fallback applies).
  bool freshFix(DateTime now) =>
      lastFix != null && lastFix!.hasPosition && lastFixAt != null && now.difference(lastFixAt!) < const Duration(minutes: 10);

  ClientGpsStatus copyWith({
    ClientGpsPrefs? prefs,
    NmeaFix? lastFix,
    DateTime? lastFixAt,
    DateTime? lastPushAt,
    String? lastError,
    bool clearError = false,
    bool? busy,
  }) =>
      ClientGpsStatus(
        prefs: prefs ?? this.prefs,
        supported: supported,
        lastFix: lastFix ?? this.lastFix,
        lastFixAt: lastFixAt ?? this.lastFixAt,
        lastPushAt: lastPushAt ?? this.lastPushAt,
        lastError: clearError ? null : (lastError ?? this.lastError),
        busy: busy ?? this.busy,
      );
}

final clientGpsProvider = AsyncNotifierProvider<ClientGpsNotifier, ClientGpsStatus>(ClientGpsNotifier.new);

/// Owns the "USB GPS on this computer" loop: when enabled, periodically read
/// one fix from the chosen serial port and relay it to the daemon as a
/// high-trust external GPS sync. Best-effort throughout — a missing dongle or
/// an unreachable daemon is reported in [ClientGpsStatus.lastError], never
/// thrown, and the next tick retries.
class ClientGpsNotifier extends AsyncNotifier<ClientGpsStatus> {
  Timer? _timer;
  int _gen = 0;

  /// Injectable clock for tests.
  DateTime Function() now = () => DateTime.now().toUtc();

  @override
  Future<ClientGpsStatus> build() async {
    ref.onDispose(() => _timer?.cancel());
    final source = ref.watch(serialGpsSourceProvider);
    final prefs = await ref.watch(clientGpsPrefsServiceProvider).load();
    final status = ClientGpsStatus(prefs: prefs, supported: source.supported);
    if (status.enabled) {
      // Not immediate: state is still loading inside build(), and a read that finished before
      // build() returned would have its status overwritten by the return value. A zero-length
      // timer runs after the notifier has its first state.
      _schedule(Duration.zero);
    }
    return status;
  }

  ClientGpsStatus get _current =>
      state.value ?? ClientGpsStatus(prefs: const ClientGpsPrefs(), supported: ref.read(serialGpsSourceProvider).supported);

  List<String> availablePorts() => ref.read(serialGpsSourceProvider).availablePorts();

  Future<void> setEnabled(bool enabled) async {
    final prefs = _current.prefs.copyWith(enabled: enabled);
    await ref.read(clientGpsPrefsServiceProvider).save(prefs);
    state = AsyncData(_current.copyWith(prefs: prefs, clearError: true));
    if (enabled) {
      _schedule(kClientGpsUnsyncedInterval, immediate: true);
    } else {
      _timer?.cancel();
      _gen++;
    }
  }

  Future<void> setPort(String? port) async {
    final prefs = _current.prefs.copyWith(port: port, clearPort: port == null);
    await ref.read(clientGpsPrefsServiceProvider).save(prefs);
    state = AsyncData(_current.copyWith(prefs: prefs, clearError: true));
    if (_current.enabled) _schedule(kClientGpsUnsyncedInterval, immediate: true);
  }

  void _schedule(Duration after, {bool immediate = false, ClientGpsStatus? initial}) {
    _timer?.cancel();
    final gen = ++_gen;
    if (immediate) {
      unawaited(syncNow(gen: gen, initial: initial));
    } else {
      _timer = Timer(after, () => unawaited(syncNow(gen: gen)));
    }
  }

  /// Read one fix and push it. Returns the fix (position may be null when the
  /// receiver has time but no position yet). Public so "Fill from GPS" and the
  /// panel's "Read now" share the one code path.
  Future<NmeaFix?> syncNow({int? gen, ClientGpsStatus? initial}) async {
    final s = initial ?? _current;
    if (!s.enabled) return null;
    final port = s.prefs.port;
    if (port == null || port.isEmpty) {
      state = AsyncData(s.copyWith(lastError: 'Choose the serial port the GPS dongle is on.'));
      return null;
    }
    state = AsyncData(s.copyWith(busy: true, clearError: true));
    NmeaFix? fix;
    String? error;
    var pushed = false;
    try {
      fix = await acquireFix(ref.read(serialGpsSourceProvider), port, ref.read(clientGpsListenWindowProvider));
      if (fix == null) {
        error = 'No GPS fix on $port yet — the receiver needs a clear view of the sky.';
      } else {
        final api = ref.read(timeSyncApiProvider);
        if (api == null) {
          error = 'Fix read; not pushed — connect to your rig first.';
        } else {
          await api.pushGpsFix(timeUtc: fix.timeUtc!, lat: fix.latitudeDeg, lng: fix.longitudeDeg, alt: fix.altitudeM);
          pushed = true;
          ref.invalidate(timeSyncStatusProvider);
        }
      }
    } catch (e) {
      error = 'GPS read failed on $port: $e';
      debugPrint('[client-gps] $error');
    }
    if (gen != null && gen != _gen) return fix; // superseded by a toggle/port change
    final t = now();
    state = AsyncData((state.value ?? s).copyWith(
      busy: false,
      lastFix: fix,
      lastFixAt: fix != null ? t : null,
      lastPushAt: pushed ? t : null,
      lastError: error,
      clearError: error == null,
    ));
    if ((state.value ?? s).enabled && gen != null) {
      _schedule(pushed ? kClientGpsSyncedInterval : kClientGpsUnsyncedInterval);
    }
    return fix;
  }

  /// Listen on [port] for up to [window] and combine sentences into one fix:
  /// RMC supplies UTC + position (required), GGA adds altitude when it arrives
  /// within the same window. Pure over the source stream — unit-testable.
  static Future<NmeaFix?> acquireFix(SerialGpsSource source, String port, Duration window) async {
    NmeaFix? rmc;
    double? alt;
    final done = Completer<void>();
    late final StreamSubscription<String> sub;
    void finish() {
      if (!done.isCompleted) done.complete();
    }
    sub = source.lines(port).listen(
      (line) {
        final fix = parseNmeaSentence(line);
        if (fix == null) return;
        if (fix.timeUtc != null) {
          rmc = fix;
        } else if (fix.altitudeM != null) {
          alt = fix.altitudeM;
        }
        if (rmc != null && rmc!.hasPosition && alt != null) finish();
      },
      onError: (Object e) {
        if (!done.isCompleted) done.completeError(e);
      },
      onDone: finish,
      cancelOnError: true,
    );
    final timer = Timer(window, finish);
    try {
      await done.future;
    } finally {
      timer.cancel();
      // Not awaited: a source that is slow to release the port must not hold up the fix.
      unawaited(sub.cancel());
    }
    if (rmc == null) return null;
    return NmeaFix(timeUtc: rmc!.timeUtc, latitudeDeg: rmc!.latitudeDeg, longitudeDeg: rmc!.longitudeDeg, altitudeM: alt);
  }
}
