import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../services/client_gps_prefs_service.dart';
import '../services/serial_gps_source.dart';
import '../util/friendly_error.dart';
import '../util/nmea_parser.dart';
import 'time_sync_state.dart';

/// Injection seams (tests override with a canned source / temp-dir prefs).
final serialGpsSourceProvider = Provider<SerialGpsSource>((_) => const OsSerialGpsSource());
final clientGpsPrefsServiceProvider = Provider<ClientGpsPrefsService>((_) => ClientGpsPrefsService());

/// How long one read listens for a fix (tests override with milliseconds).
final clientGpsListenWindowProvider = Provider<Duration>((_) => kClientGpsListenWindow);

/// When a timer tick lands while a manual read (Read now / Fill from GPS) holds
/// the port, the tick re-arms itself after this delay instead of dying.
final clientGpsBusyRetryProvider = Provider<Duration>((_) => const Duration(seconds: 20));

/// How long one acquisition listens for a usable RMC (+ GGA for altitude).
/// A warm receiver emits RMC every second; a cold one needs a minute or more
/// under open sky, which the periodic loop covers by retrying.
const Duration kClientGpsListenWindow = Duration(seconds: 15);

/// Re-sync cadence: keep trying
/// every couple of minutes until a fix has been pushed, then hourly-ish.
const Duration kClientGpsUnsyncedInterval = Duration(minutes: 2);
const Duration kClientGpsSyncedInterval = Duration(minutes: 50);

/// What the settings panel shows and what "Fill from GPS" reads.
@immutable
class ClientGpsStatus {
  const ClientGpsStatus({
    required this.prefs,
    required this.supported,
    this.ports = const [],
    this.lastFix,
    this.lastFixAt,
    this.lastPushAt,
    this.lastError,
    this.busy = false,
  });

  final ClientGpsPrefs prefs;
  final bool supported;

  /// Serial ports as of the last enumeration (build, toggle, port change,
  /// read) — cached so the settings pane never enumerates on every rebuild.
  final List<String> ports;
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
    List<String>? ports,
    NmeaFix? lastFix,
    DateTime? lastFixAt,
    DateTime? lastPushAt,
    String? lastError,
    bool clearError = false,
    bool clearFix = false,
    bool? busy,
  }) =>
      ClientGpsStatus(
        prefs: prefs ?? this.prefs,
        supported: supported,
        ports: ports ?? this.ports,
        lastFix: clearFix ? null : (lastFix ?? this.lastFix),
        lastFixAt: clearFix ? null : (lastFixAt ?? this.lastFixAt),
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

  /// The current timer generation (tests simulate a tick with it).
  @visibleForTesting
  int get generation => _gen;

  @override
  Future<ClientGpsStatus> build() async {
    ref.onDispose(() => _timer?.cancel());
    final source = ref.watch(serialGpsSourceProvider);
    final prefs = await ref.watch(clientGpsPrefsServiceProvider).load();
    final status = ClientGpsStatus(prefs: prefs, supported: source.supported, ports: await _ports(source));
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

  static Future<List<String>> _ports(SerialGpsSource source) async =>
      source.supported ? await source.availablePorts() : const [];

  /// Re-enumerate serial ports (the pane's refresh); cached in the status.
  Future<void> refreshPorts() async {
    final ports = await _ports(ref.read(serialGpsSourceProvider));
    if (!ref.mounted) return;
    state = AsyncData(_current.copyWith(ports: ports));
  }

  Future<void> setEnabled(bool enabled) async {
    final prefs = _current.prefs.copyWith(enabled: enabled);
    await ref.read(clientGpsPrefsServiceProvider).save(prefs);
    state = AsyncData(_current.copyWith(prefs: prefs, clearError: true, ports: await _ports(ref.read(serialGpsSourceProvider))));
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
    state = AsyncData(_current.copyWith(prefs: prefs, clearError: true, ports: await _ports(ref.read(serialGpsSourceProvider))));
    if (_current.enabled) _schedule(kClientGpsUnsyncedInterval, immediate: true);
  }

  void _schedule(Duration after, {bool immediate = false}) {
    _timer?.cancel();
    final gen = ++_gen;
    if (immediate) {
      unawaited(syncNow(gen: gen));
    } else {
      _timer = Timer(after, () => unawaited(syncNow(gen: gen)));
    }
  }

  /// Read one fix and push it. Returns the fix (position may be null when the
  /// receiver has time but no position yet). Public so "Fill from GPS" and the
  /// panel's "Read now" share the one code path.
  Future<NmeaFix?> syncNow({int? gen}) async {
    final s = _current;
    if (!s.enabled) return null;
    // One reader per port: a second caller during the window would only get
    // "device busy"; hand it the last fix. A TIMER tick that lands on a manual read
    // must re-arm itself, though: the manual read never touches the timer, so the
    // tick would otherwise be the loop's last (#1095 r3).
    if (s.busy) {
      if (gen != null && gen == _gen) _schedule(ref.read(clientGpsBusyRetryProvider));
      return s.lastFix;
    }
    final saved = s.prefs.port;
    if (saved == null || saved.isEmpty) {
      state = AsyncData(s.copyWith(lastError: 'Choose the serial port the GPS dongle is on.'));
      return null;
    }
    // Busy before the first await: the port enumeration below can take a while (a `mode`
    // subprocess on Windows), and a second caller in that gap must still see the guard.
    state = AsyncData(s.copyWith(busy: true, clearError: true));
    // macOS names a USB serial port after the socket it is plugged into, so the
    // dongle moved to another socket comes back as a different /dev/cu.usbmodem…
    // and the saved one no longer exists (2026-10-16: saved usbmodem1101, dongle
    // on usbmodem101, every read failed). Follow it when the choice is obvious.
    final source = ref.read(serialGpsSourceProvider);
    final ports = await _ports(source);
    if (!ref.mounted) return null;
    state = AsyncData(_current.copyWith(ports: ports));
    final savedGone = ports.isNotEmpty && !ports.contains(saved);
    var port = saved;
    // Only while the saved port is still the user's choice: a port picked during the
    // enumeration wins, and the newer generation reads it once this read ends.
    if (savedGone && _current.prefs.port == saved) {
      final moved = replacementPort(saved, ports);
      if (moved != null) {
        debugPrint('[client-gps] $saved is gone; trying the dongle on $moved');
        port = moved;
      }
      // Otherwise read the saved port as before: a port the enumeration misses may
      // still open (a COM port `mode` does not list), and two candidates are the
      // user's choice — the failure message below names them.
    }
    NmeaFix? fix;
    String? error;
    var pushed = false;
    try {
      fix = await acquireFix(source, port, ref.read(clientGpsListenWindowProvider));
      if (fix != null && port != saved && ref.mounted && _current.prefs.port == saved) {
        // Remembered only once it spoke NMEA: a lone usbmodem/ttyACM port can just as
        // well be an Arduino focuser or an OnStep mount, which must never become the
        // saved GPS port (probeAndAdopt keeps the same rule).
        final prefs = _current.prefs.copyWith(port: port);
        await ref.read(clientGpsPrefsServiceProvider).save(prefs);
        if (ref.mounted) state = AsyncData(_current.copyWith(prefs: prefs));
      }
      if (fix == null) {
        error = 'No GPS fix on $port yet — the receiver needs a clear view of the sky.';
      } else if ((gen != null && gen != _gen) || !ref.mounted || !_current.enabled) {
        // Disabled or re-pointed while reading (timer read: generation moved; manual
        // read: the switch is now off): keep the fix locally, never relay it.
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
      // Only when the saved port is gone; a listed port that failed (busy) is not misplaced.
      final others = savedGone ? ports.where((p) => p != port && looksLikeGpsPort(p)).toList() : const <String>[];
      error = fix == null
          ? 'GPS read failed on $port: $e'
              '${others.isEmpty ? '' : ' — the GPS may be on ${others.join(' or ')}; choose it in GPS on this computer.'}'
          // The read worked; the daemon refused the push. A 422 here is a rig running a
          // daemon that predates client-side GPS (it does not know source gps-client).
          : 'Fix read, but the rig refused it: ${friendlyError(e, action: 'push the GPS fix')}'
              '${e is DioException && e.response?.statusCode == 422 ? ' — update Ara Server on the rig; this daemon predates GPS on this computer.' : ''}';
      debugPrint('[client-gps] $error');
    }
    if (!ref.mounted) return fix; // the container went away mid-read (tests, hot restart)
    final t = now();
    state = AsyncData(_current.copyWith(
      busy: false,
      lastFix: fix,
      lastFixAt: fix != null ? t : null,
      clearFix: fix == null,
      lastPushAt: pushed ? t : null,
      lastError: error,
      clearError: error == null,
    ));
    if (gen == null) return fix; // a manual read: the timer is untouched (a tick that hit `busy` re-armed itself)
    if (gen != _gen) {
      // Superseded mid-read (toggle or port change). The newer generation's own read bailed on
      // `busy` above without arming anything, so the loop would otherwise stop here for the rest
      // of the session: re-arm at once so the (possibly new) port is read right away.
      if (_current.enabled) _schedule(Duration.zero);
      return fix;
    }
    if (_current.enabled) {
      _schedule(pushed ? kClientGpsSyncedInterval : kClientGpsUnsyncedInterval);
    }
    return fix;
  }

  /// Fill-from-GPS on a machine where "GPS on this computer" was never turned
  /// on (the profile wizard is the usual case): try the ports a dongle appears
  /// on, briefly, and on a fix adopt that port — enable the setting and start
  /// the loop — so the user never has to find the switch first. Returns null
  /// when no port yields a fix in time.
  Future<NmeaFix?> probeAndAdopt({Duration perPort = const Duration(seconds: 8)}) async {
    final source = ref.read(serialGpsSourceProvider);
    if (!source.supported) return null;
    final s = _current;
    if (s.enabled || s.busy) return null;
    final ports = await _ports(source);
    if (!ref.mounted) return null;
    state = AsyncData(_current.copyWith(ports: ports, busy: true, clearError: true));
    try {
      for (final port in ports.where(looksLikeGpsPort)) {
        NmeaFix? fix;
        try {
          fix = await acquireFix(source, port, perPort);
        } catch (_) {
          continue; // busy/unopenable port: try the next
        }
        if (fix == null || !fix.hasPosition) continue;
        if (!ref.mounted) return fix;
        final prefs = _current.prefs.copyWith(enabled: true, port: port);
        await ref.read(clientGpsPrefsServiceProvider).save(prefs);
        if (!ref.mounted) return fix;
        state = AsyncData(_current.copyWith(prefs: prefs, busy: false, lastFix: fix, lastFixAt: now(), clearError: true));
        _schedule(Duration.zero); // push it and keep the loop alive from here on
        return fix;
      }
      return null;
    } finally {
      if (ref.mounted && _current.busy) state = AsyncData(_current.copyWith(busy: false));
    }
  }

  /// The port to follow when [saved] is no longer present: the one GPS-like
  /// port in [ports], or null when there is none or more than one (the user
  /// must choose — never guess between two dongles). The name alone cannot tell
  /// a GPS from a mount or focuser, so [syncNow] saves the follow only after it
  /// reads a fix there. Pure — tested.
  static String? replacementPort(String saved, List<String> ports) {
    if (ports.contains(saved)) return saved;
    final candidates = ports.where(looksLikeGpsPort).toList();
    return candidates.length == 1 ? candidates.single : null;
  }

  /// The names a USB GPS dongle presents under; anything else (Bluetooth ports,
  /// debug consoles, arbitrary COM ports) is skipped by the opportunistic probe
  /// so it never holds a mount's or focuser's serial line.
  static bool looksLikeGpsPort(String port) {
    final l = port.toLowerCase();
    return l.contains('usbserial') || l.contains('usbmodem') || l.contains('ttyusb') || l.contains('ttyacm');
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
      // Give the port a moment to be released (a Read now right after a read would
      // otherwise see "busy"), but never let a stuck source hold up the fix.
      await sub.cancel().timeout(const Duration(seconds: 1), onTimeout: () {});
    }
    if (rmc == null) return null;
    return NmeaFix(timeUtc: rmc!.timeUtc, latitudeDeg: rmc!.latitudeDeg, longitudeDeg: rmc!.longitudeDeg, altitudeM: alt);
  }
}
