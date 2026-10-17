import 'dart:async';
import 'dart:io';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/client_gps_prefs_service.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/services/serial_gps_source.dart';
import 'package:openastroara/services/time_sync_api.dart';
import 'package:openastroara/state/client_gps_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/time_sync_state.dart';

String _cs(String body) {
  var c = 0;
  for (final u in body.codeUnits) {
    c ^= u;
  }
  return '\$$body*${c.toRadixString(16).toUpperCase().padLeft(2, '0')}';
}

final _rmc = _cs('GPRMC,041926.000,A,3851.2384,N,07702.6101,W,0.09,318.63,220926,,,A');
final _gga = _cs('GPGGA,041926.000,3851.2384,N,07702.6101,W,1,08,0.9,120.5,M,-33.0,M,,');
final _void = _cs('GPRMC,041920.000,V,,,,,,,220926,,,N');

class _FakeSource implements SerialGpsSource {
  _FakeSource(this.script, {this.supported = true, this.ports = const ['/dev/cu.usbserial-1'], this.unopenable = const {}});
  final List<String> script;
  @override
  final bool supported;
  final List<String> ports;
  /// Ports whose open fails, like a dongle that was unplugged from them.
  final Set<String> unopenable;
  int opens = 0;
  String? lastPort;
  /// When set, every enumeration after the first (build's) waits on it, like a slow `mode`.
  Completer<void>? slowPorts;
  int _portCalls = 0;

  @override
  Future<List<String>> availablePorts() async {
    final gate = slowPorts;
    if (gate != null && ++_portCalls > 1) await gate.future;
    return ports;
  }

  @override
  Stream<String> lines(String port) {
    opens++;
    lastPort = port;
    if (unopenable.contains(port)) return Stream<String>.error(StateError('could not open $port'));
    // Like the real port: emits its lines and then stays open until cancelled.
    final c = StreamController<String>();
    Future<void>.microtask(() {
      for (final l in script) {
        if (!c.isClosed) c.add(l);
      }
    });
    return c.stream;
  }
}

class _FakeApi implements TimeSyncClient {
  final pushes = <Map<String, Object?>>[];
  TimeSyncState? state;
  @override
  Future<TimeSyncState> getState() async => state ?? (throw StateError('no state'));
  @override
  Future<void> pushClientTime(DateTime utcNow) async {}
  @override
  Future<TimeSyncPushResult> pushGpsFix({required DateTime timeUtc, double? lat, double? lng, double? alt}) async {
    pushes.add({'t': timeUtc, 'lat': lat, 'lng': lng, 'alt': alt});
    return const TimeSyncPushResult(locationUpdated: true, clockSet: true);
  }
  @override
  Future<TimeSyncPushResult> pushManual({required DateTime timeUtc, double? lat, double? lng, double? alt}) async =>
      const TimeSyncPushResult(locationUpdated: false, clockSet: false);
  @override
  Future<void> close() async {}
}

class _FakeServers implements SavedServerService {
  @override
  Future<List<AraServer>> loadAll() async => const [AraServer(hostname: 'rig', port: 5555)];
  @override
  Future<void> saveAll(List<AraServer> s) async {}
  @override
  Future<void> add(AraServer server) async {}
}

void main() {
  late Directory tmp;
  setUp(() async => tmp = await Directory.systemTemp.createTemp('ara-gps-'));
  tearDown(() async => tmp.delete(recursive: true));

  Future<ProviderContainer> container(_FakeSource source, _FakeApi api, ClientGpsPrefs prefs) async {
    final prefsSvc = ClientGpsPrefsService(supportDir: () async => tmp);
    await prefsSvc.save(prefs);
    final c = ProviderContainer(overrides: [
      serialGpsSourceProvider.overrideWithValue(source),
      clientGpsPrefsServiceProvider.overrideWithValue(prefsSvc),
      timeSyncApiFactoryProvider.overrideWithValue((_) => api),
      savedServerServiceProvider.overrideWithValue(_FakeServers()),
      clientGpsListenWindowProvider.overrideWithValue(const Duration(milliseconds: 150)),
      clientGpsBusyRetryProvider.overrideWithValue(const Duration(milliseconds: 50)),
    ]);
    addTearDown(c.dispose);
    return c;
  }

  group('acquireFix', () {
    test('combines RMC time+position with GGA altitude', () async {
      final fix = await ClientGpsNotifier.acquireFix(_FakeSource([_void, _rmc, _gga]), 'p', const Duration(seconds: 2));
      expect(fix, isNotNull);
      expect(fix!.timeUtc, DateTime.utc(2026, 9, 22, 4, 19, 26));
      expect(fix.latitudeDeg, closeTo(38.854, 1e-3));
      expect(fix.longitudeDeg, closeTo(-77.0435, 1e-3));
      expect(fix.altitudeM, closeTo(120.5, 1e-9));
    });

    test('RMC alone is a fix without altitude once the window ends', () async {
      final fix = await ClientGpsNotifier.acquireFix(_FakeSource([_rmc]), 'p', const Duration(milliseconds: 200));
      expect(fix?.altitudeM, isNull);
      expect(fix?.hasPosition, isTrue);
    });

    test('only void sentences within the window is no fix', () async {
      final fix = await ClientGpsNotifier.acquireFix(_FakeSource([_void, 'garbage', _gga]), 'p', const Duration(milliseconds: 200));
      expect(fix, isNull, reason: 'GGA has no date, so it cannot be a sync on its own');
    });
  });

  group('ClientGpsNotifier', () {
    test('disabled: no port is ever opened and nothing is pushed', () async {
      final source = _FakeSource([_rmc]);
      final api = _FakeApi();
      final c = await container(source, api, const ClientGpsPrefs());
      final status = await c.read(clientGpsProvider.future);
      expect(status.enabled, isFalse);
      await Future<void>.delayed(const Duration(milliseconds: 50));
      expect(source.opens, 0);
      expect(api.pushes, isEmpty);
    });

    test('enabled with a port: pushes the receiver time and fix as gps-client', () async {
      final source = _FakeSource([_rmc, _gga]);
      final api = _FakeApi();
      final c = await container(source, api, const ClientGpsPrefs(enabled: true, port: '/dev/cu.usbserial-1'));
      await c.read(clientGpsProvider.future);
      await Future<void>.delayed(const Duration(milliseconds: 400));
      final fix = await c.read(clientGpsProvider.notifier).syncNow();
      expect(fix, isNotNull);
      expect(api.pushes, isNotEmpty);
      final p = api.pushes.last;
      expect(p['t'], DateTime.utc(2026, 9, 22, 4, 19, 26));
      expect(p['lat'], closeTo(38.854, 1e-3));
      expect(p['alt'], closeTo(120.5, 1e-9));
      final status = c.read(clientGpsProvider).value!;
      expect(status.lastPushAt, isNotNull);
      expect(status.lastError, isNull);
      expect(status.freshFix(DateTime.now().toUtc()), isTrue);
    });

    test('a rig that already holds a high-trust sync is still pushed to (no daemon-side dongle exists)', () async {
      final source = _FakeSource([_rmc, _gga]);
      final api = _FakeApi()
        ..state = const TimeSyncState(
            synced: true, source: 'gps-external', trust: 'high', systemTimeOffsetSeconds: 0,
            location: null, internetAvailableOnPi: false, syncedAtUtc: null);
      final c = await container(source, api, const ClientGpsPrefs(enabled: true, port: '/dev/cu.usbserial-1'));
      await c.read(clientGpsProvider.future);
      await Future<void>.delayed(const Duration(milliseconds: 400));
      final fix = await c.read(clientGpsProvider.notifier).syncNow();
      expect(fix, isNotNull);
      expect(api.pushes, hasLength(1), reason: 'the relay is the rig\'s only receiver, so it always refreshes');
    });

    test('a second read while one is in flight returns the last fix without opening the port again', () async {
      final source = _FakeSource([]); // never emits: the read stays busy for the whole window
      final api = _FakeApi();
      final c = await container(source, api, const ClientGpsPrefs(enabled: true, port: '/dev/cu.usbserial-1'));
      await c.read(clientGpsProvider.future);
      final n = c.read(clientGpsProvider.notifier);
      final first = n.syncNow();
      await Future<void>.delayed(const Duration(milliseconds: 10));
      expect(c.read(clientGpsProvider).value!.busy, isTrue);
      final opensDuring = source.opens;
      await n.syncNow();
      expect(source.opens, opensDuring);
      await first;
    });

    test('changing the port during an in-flight read re-arms the loop and reads the new port', () async {
      // no fix: each read holds busy for the whole window
      final source = _FakeSource([], ports: const ['/dev/old', '/dev/new']);
      final api = _FakeApi();
      final c = await container(source, api, const ClientGpsPrefs(enabled: true, port: '/dev/old'));
      await c.read(clientGpsProvider.future);
      await Future<void>.delayed(const Duration(milliseconds: 20));
      expect(source.lastPort, '/dev/old');
      final n = c.read(clientGpsProvider.notifier);
      await n.setPort('/dev/new'); // its immediate read bails on busy
      // The in-flight read finishes (150 ms window) and must re-arm for the new port.
      await Future<void>.delayed(const Duration(milliseconds: 400));
      expect(source.lastPort, '/dev/new', reason: 'the superseded read re-armed the loop');
      expect(source.opens, greaterThanOrEqualTo(2));
    });

    test('a timer tick landing on a manual read re-arms instead of ending the loop', () async {
      final source = _FakeSource([]); // no fix: every read holds busy for the whole window
      final api = _FakeApi();
      final c = await container(source, api, const ClientGpsPrefs(enabled: true, port: '/dev/cu.usbserial-1'));
      await c.read(clientGpsProvider.future);
      await Future<void>.delayed(const Duration(milliseconds: 250)); // first background read done
      final n = c.read(clientGpsProvider.notifier);
      final manual = n.syncNow(); // holds the port for 150 ms
      await Future<void>.delayed(const Duration(milliseconds: 10));
      final tick = await n.syncNow(gen: n.generation); // the armed tick lands on the manual read
      expect(tick, isNull);
      await manual;
      final opensAfterManual = source.opens;
      // The tick re-armed itself (50 ms retry in tests): another background read follows.
      await Future<void>.delayed(const Duration(milliseconds: 300));
      expect(source.opens, greaterThan(opensAfterManual), reason: 'the loop is still alive');
    });

    test('a dongle moved to another USB socket is followed and the new port remembered', () async {
      // 2026-10-16: saved /dev/cu.usbmodem1101, the dongle came back as /dev/cu.usbmodem101.
      final source = _FakeSource([_rmc], ports: const ['/dev/cu.RW403B', '/dev/cu.usbmodem101']);
      final api = _FakeApi();
      final c = await container(source, api, const ClientGpsPrefs(enabled: true, port: '/dev/cu.usbmodem1101'));
      await c.read(clientGpsProvider.future);
      // The loop's own first read follows the dongle and pushes.
      await Future<void>.delayed(const Duration(milliseconds: 400));
      final fix = await c.read(clientGpsProvider.notifier).syncNow();
      expect(fix, isNotNull);
      expect(source.lastPort, '/dev/cu.usbmodem101');
      expect(c.read(clientGpsProvider).value!.prefs.port, '/dev/cu.usbmodem101');
      final saved = await ClientGpsPrefsService(supportDir: () async => tmp).load();
      expect(saved.port, '/dev/cu.usbmodem101', reason: 'remembered for the next launch');
      expect(api.pushes, isNotEmpty);
    });

    test('with two candidate dongles the saved port is kept and the failure names them', () async {
      final source = _FakeSource([_rmc],
          ports: const ['/dev/cu.usbmodem101', '/dev/cu.usbserial-9'], unopenable: const {'/dev/cu.usbmodem1101'});
      final c = await container(source, _FakeApi(), const ClientGpsPrefs(enabled: true, port: '/dev/cu.usbmodem1101'));
      await c.read(clientGpsProvider.future);
      final fix = await c.read(clientGpsProvider.notifier).syncNow();
      expect(fix, isNull);
      final status = c.read(clientGpsProvider).value!;
      expect(status.prefs.port, '/dev/cu.usbmodem1101', reason: 'never guess between two dongles');
      expect(status.lastError, contains('/dev/cu.usbmodem101 or /dev/cu.usbserial-9'));
    });

    test('a followed port that gives no fix is never remembered', () async {
      // The lone usbmodem port may be an Arduino focuser or an OnStep mount, not the GPS.
      final source = _FakeSource([], ports: const ['/dev/cu.usbmodem101']);
      final c = await container(source, _FakeApi(), const ClientGpsPrefs(enabled: true, port: '/dev/cu.usbmodem1101'));
      await c.read(clientGpsProvider.future);
      await Future<void>.delayed(const Duration(milliseconds: 400)); // let the loop's own read finish
      final fix = await c.read(clientGpsProvider.notifier).syncNow();
      expect(fix, isNull);
      expect(source.lastPort, '/dev/cu.usbmodem101', reason: 'it was tried');
      expect(c.read(clientGpsProvider).value!.prefs.port, '/dev/cu.usbmodem1101');
      final saved = await ClientGpsPrefsService(supportDir: () async => tmp).load();
      expect(saved.port, '/dev/cu.usbmodem1101');
    });

    test('a read while the first is still enumerating ports never opens the port twice', () async {
      final source = _FakeSource([_rmc])..slowPorts = Completer<void>();
      final c = await container(source, _FakeApi(), const ClientGpsPrefs(enabled: true, port: '/dev/cu.usbserial-1'));
      await c.read(clientGpsProvider.future);
      await Future<void>.delayed(const Duration(milliseconds: 20)); // the loop's first read is enumerating
      final manual = c.read(clientGpsProvider.notifier).syncNow(); // "Read now" in that gap
      source.slowPorts!.complete();
      await manual;
      await Future<void>.delayed(const Duration(milliseconds: 300));
      expect(source.opens, 1);
    });

    test('a port chosen while the read is enumerating is never overwritten by the follow', () async {
      final source = _FakeSource([_rmc], ports: const ['/dev/cu.usbmodem101', '/dev/ttyS0'])..slowPorts = Completer<void>();
      final c = await container(source, _FakeApi(), const ClientGpsPrefs(enabled: true, port: '/dev/cu.usbmodem1101'));
      await c.read(clientGpsProvider.future);
      await Future<void>.delayed(const Duration(milliseconds: 20)); // the loop's first read is enumerating
      final picked = c.read(clientGpsProvider.notifier).setPort('/dev/ttyS0');
      source.slowPorts!.complete();
      await picked;
      await Future<void>.delayed(const Duration(milliseconds: 600));
      expect(c.read(clientGpsProvider).value!.prefs.port, '/dev/ttyS0');
      final saved = await ClientGpsPrefsService(supportDir: () async => tmp).load();
      expect(saved.port, '/dev/ttyS0');
      expect(source.lastPort, '/dev/ttyS0', reason: 'the chosen port is read once the first read ends');
    });

    test('a listed port that fails gets no "the GPS may be on" hint', () async {
      final source = _FakeSource([_rmc], ports: const ['/dev/cu.usbmodem1101', '/dev/cu.usbmodem101'], unopenable: const {'/dev/cu.usbmodem1101'});
      final c = await container(source, _FakeApi(), const ClientGpsPrefs(enabled: true, port: '/dev/cu.usbmodem1101'));
      await c.read(clientGpsProvider.future);
      await Future<void>.delayed(const Duration(milliseconds: 400)); // let the loop's own read finish
      await c.read(clientGpsProvider.notifier).syncNow();
      final error = c.read(clientGpsProvider).value!.lastError;
      expect(error, contains('GPS read failed on /dev/cu.usbmodem1101'));
      expect(error, isNot(contains('may be on')));
    });

    test('replacementPort follows only an unambiguous GPS-like port', () {
      const saved = '/dev/cu.usbmodem1101';
      expect(ClientGpsNotifier.replacementPort(saved, const [saved, '/dev/cu.usbmodem101']), saved);
      expect(ClientGpsNotifier.replacementPort(saved, const ['/dev/cu.usbmodem101']), '/dev/cu.usbmodem101');
      expect(ClientGpsNotifier.replacementPort(saved, const ['/dev/cu.RW403B']), isNull, reason: 'not a GPS-like name');
      expect(ClientGpsNotifier.replacementPort(saved, const ['/dev/cu.usbmodem101', '/dev/cu.usbserial-2']), isNull);
      expect(ClientGpsNotifier.replacementPort('COM3', const ['COM4']), isNull, reason: 'COM names are never guessed');
    });

    test('a failed read clears the stale fix from the status', () async {
      final source = _FakeSource([_rmc, _gga]);
      final api = _FakeApi();
      final c = await container(source, api, const ClientGpsPrefs(enabled: true, port: '/dev/cu.usbserial-1'));
      await c.read(clientGpsProvider.future);
      await Future<void>.delayed(const Duration(milliseconds: 400));
      expect(c.read(clientGpsProvider).value!.lastFix, isNotNull);
      source.script.clear();
      await c.read(clientGpsProvider.notifier).syncNow();
      final st = c.read(clientGpsProvider).value!;
      expect(st.lastFix, isNull);
      expect(st.lastError, contains('No GPS fix'));
    });

    test('enabled without a port reports what to do instead of opening anything', () async {
      final source = _FakeSource([_rmc]);
      final api = _FakeApi();
      final c = await container(source, api, const ClientGpsPrefs(enabled: true));
      await c.read(clientGpsProvider.future);
      await Future<void>.delayed(const Duration(milliseconds: 50));
      expect(source.opens, 0);
      expect(c.read(clientGpsProvider).value!.lastError, contains('Choose the serial port'));
    });

    test('unsupported platform: never enabled even when the pref says so', () async {
      final source = _FakeSource([_rmc], supported: false);
      final c = await container(source, _FakeApi(), const ClientGpsPrefs(enabled: true, port: 'x'));
      final status = await c.read(clientGpsProvider.future);
      expect(status.enabled, isFalse);
      expect(source.opens, 0);
    });

    test('setEnabled persists and starts a read; setEnabled(false) stops', () async {
      final source = _FakeSource([_rmc, _gga]);
      final api = _FakeApi();
      final c = await container(source, api, const ClientGpsPrefs(port: '/dev/cu.usbserial-1'));
      await c.read(clientGpsProvider.future);
      await c.read(clientGpsProvider.notifier).setEnabled(true);
      await Future<void>.delayed(const Duration(milliseconds: 400));
      expect(source.opens, greaterThanOrEqualTo(1));
      expect(await ClientGpsPrefsService(supportDir: () async => tmp).load().then((p) => p.enabled), isTrue);
      await c.read(clientGpsProvider.notifier).setEnabled(false);
      expect(c.read(clientGpsProvider).value!.enabled, isFalse);
    });
  });
}
