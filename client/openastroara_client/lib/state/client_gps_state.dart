import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../services/client_gps_prefs_service.dart';
import '../services/serial_gps_source.dart';
import '../services/time_sync_api.dart';
import '../util/nmea_parser.dart';
import 'time_sync_state.dart';

/// Injection seams (tests override with a canned source / temp-dir prefs).
final serialGpsSourceProvider = Provider<SerialGpsSource>((_) => const LibSerialPortGpsSource());
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
    final status = ClientGpsStatus(prefs: prefs, supported: source.supported, ports: _ports(source));
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

  static List<String> _ports(SerialGpsSource source) => source.supported ? source.availablePorts() : const [];

  /// Re-enumerate serial ports (the pane's refresh); cached in the status.
  void refreshPorts() => state = AsyncData(_current.copyWith(ports: _ports(ref.read(serialGpsSourceProvider))));

  Future<void> setEnabled(bool enabled) async {
    final prefs = _current.prefs.copyWith(enabled: enabled);
    await ref.read(clientGpsPrefsServiceProvider).save(prefs);
    state = AsyncData(_current.copyWith(prefs: prefs, clearError: true, ports: _ports(ref.read(serialGpsSourceProvider))));
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
    state = AsyncData(_current.copyWith(prefs: prefs, clearError: true, ports: _ports(ref.read(serialGpsSourceProvider))));
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
    final port = s.prefs.port;
    if (port == null || port.isEmpty) {
      state = AsyncData(s.copyWith(lastError: 'Choose the serial port the GPS dongle is on.'));
      return null;
    }
    state = AsyncData(s.copyWith(busy: true, clearError: true));
    NmeaFix? fix;
    String? error;
    var pushed = false;
    var alreadySynced = false;
    try {
      fix = await acquireFix(ref.read(serialGpsSourceProvider), port, ref.read(clientGpsListenWindowProvider));
      if (fix == null) {
        error = 'No GPS fix on $port yet — the receiver needs a clear view of the sky.';
      } else if (gen != null && gen != _gen) {
        // Disabled or re-pointed while reading: keep the fix locally, never relay it.
      } else {
        final api = ref.read(timeSyncApiProvider);
        if (api == null) {
          error = 'Fix read; not pushed — connect to your rig first.';
        } else {
          // Don't step a rig that already has a sync as good as ours (its own
          // dongle) — a relayed fix is a fallback, not an override.
          alreadySynced = await _daemonAlreadyHigh(api);
          if (!alreadySynced) {
            await api.pushGpsFix(timeUtc: fix.timeUtc!, lat: fix.latitudeDeg, lng: fix.longitudeDeg, alt: fix.altitudeM);
            pushed = true;
            ref.invalidate(timeSyncStatusProvider);
          }
        }
      }
    } catch (e) {
      error = 'GPS read failed on $port: $e';
      debugPrint('[client-gps] $error');
    }
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
      _schedule(pushed || alreadySynced ? kClientGpsSyncedInterval : kClientGpsUnsyncedInterval);
    }
    return fix;
  }

  /// True when the daemon already holds a fresh high-trust sync from its own
  /// receiver; a state read failure counts as "not synced" so the push proceeds.
  static Future<bool> _daemonAlreadyHigh(TimeSyncClient api) async {
    try {
      final st = await api.getState();
      return st.synced && st.trust == 'high' && st.source == 'gps-internal';
    } catch (_) {
      return false;
    }
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
