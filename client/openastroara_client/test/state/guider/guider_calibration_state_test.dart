import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/calibration_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/guider_calibration_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/guider/guider_calibration_state.dart';
import 'package:openastroara/state/saved_server_state.dart';

class _FakeSavedServerService implements SavedServerService {
  _FakeSavedServerService(List<AraServer> stored) : _stored = [...stored];
  final List<AraServer> _stored;
  @override
  Future<List<AraServer>> loadAll() async => List.unmodifiable(_stored);
  @override
  Future<void> saveAll(List<AraServer> servers) async => _stored
    ..clear()
    ..addAll(servers);
  @override
  Future<void> add(AraServer server) async => _stored.add(server);
}

class _FakeCalibrationClient implements GuiderCalibrationClient {
  _FakeCalibrationClient(this.response);
  CalibrationStatusResponse response;
  int darkBuilds = 0;
  int defectBuilds = 0;
  bool? darkEnabled;
  bool? defectEnabled;
  bool throwOnBuild = false;
  Completer<void>? buildGate;

  @override
  Future<CalibrationStatusResponse> getStatus() async => response;
  @override
  Future<void> buildDarkLibrary({
    int frameCount = 5,
    int? minExposureMs,
    int? maxExposureMs,
    bool clearExisting = false,
    String? notes,
    bool loadAfter = true,
  }) async {
    darkBuilds++;
    if (buildGate != null) await buildGate!.future;
    if (throwOnBuild) throw StateError('build failed');
  }

  @override
  Future<void> buildDefectMap({
    int exposureMs = 3000,
    int frameCount = 10,
    String? notes,
    bool loadAfter = true,
  }) async {
    defectBuilds++;
    if (throwOnBuild) throw StateError('build failed');
  }
  @override
  Future<void> setDarkLibraryEnabled(bool enabled) async => darkEnabled = enabled;
  @override
  Future<void> setDefectMapEnabled(bool enabled) async => defectEnabled = enabled;
  @override
  void close() {}
}

ProviderContainer _container(List<AraServer> servers, GuiderCalibrationClient api) {
  final c = ProviderContainer(overrides: [
    savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(servers)),
    guiderCalibrationApiFactoryProvider.overrideWithValue((_) => api),
  ]);
  addTearDown(c.dispose);
  return c;
}

CalibrationStatusResponse _resp({bool darkExists = false}) => CalibrationStatusResponse(
      connected: true,
      status: CalibrationStatus(profileId: 1, darkLibraryExists: darkExists),
    );

void main() {
  const server = AraServer(hostname: 'h', port: 5555);

  group('guiderCalibrationProvider', () {
    test('no saved server → null', () async {
      final c = _container(const [], _FakeCalibrationClient(_resp()));
      await c.read(savedServersProvider.future);
      expect(c.read(guiderCalibrationApiProvider), isNull);
      expect(await c.read(guiderCalibrationProvider.future), isNull);
    });

    test('active server → exposes the calibration status', () async {
      final c = _container(const [server], _FakeCalibrationClient(_resp(darkExists: true)));
      await c.read(savedServersProvider.future);
      final r = await c.read(guiderCalibrationProvider.future);
      expect(r!.connected, isTrue);
      expect(r.status!.darkLibraryExists, isTrue);
    });

    test('buildDarkLibrary forwards to the client then refreshes', () async {
      final api = _FakeCalibrationClient(_resp());
      final c = _container(const [server], api);
      await c.read(savedServersProvider.future);
      await c.read(guiderCalibrationProvider.future);

      api.response = _resp(darkExists: true);
      await c.read(guiderCalibrationProvider.notifier).buildDarkLibrary(frameCount: 10);

      expect(api.darkBuilds, 1);
      expect(c.read(guiderCalibrationProvider).value!.status!.darkLibraryExists, isTrue);
    });

    test('buildDefectMap forwards to the client then refreshes', () async {
      final api = _FakeCalibrationClient(_resp());
      final c = _container(const [server], api);
      await c.read(savedServersProvider.future);
      await c.read(guiderCalibrationProvider.future);

      await c.read(guiderCalibrationProvider.notifier).buildDefectMap(exposureMs: 4000);

      expect(api.defectBuilds, 1);
      expect(c.read(guiderCalibrationProvider).hasValue, isTrue);
    });

    test('setDarkLibraryEnabled / setDefectMapEnabled forward the flag', () async {
      final api = _FakeCalibrationClient(_resp());
      final c = _container(const [server], api);
      await c.read(savedServersProvider.future);
      await c.read(guiderCalibrationProvider.future);

      await c.read(guiderCalibrationProvider.notifier).setDarkLibraryEnabled(false);
      await c.read(guiderCalibrationProvider.notifier).setDefectMapEnabled(true);

      expect(api.darkEnabled, isFalse);
      expect(api.defectEnabled, isTrue);
    });

    test('a second action while one is in flight is ignored (state.isLoading guard)', () async {
      final api = _FakeCalibrationClient(_resp())..buildGate = Completer<void>();
      final c = _container(const [server], api);
      await c.read(savedServersProvider.future);
      await c.read(guiderCalibrationProvider.future);

      final notifier = c.read(guiderCalibrationProvider.notifier);
      final first = notifier.buildDarkLibrary();
      await pumpEventQueue(); // let the first action set loading
      final second = notifier.buildDarkLibrary(); // state.isLoading → no-op
      api.buildGate!.complete();
      await Future.wait<void>([first, second]);

      expect(api.darkBuilds, 1, reason: 'the second action was guarded out');
    });

    test('a server switch mid-action ends on the new server status (generation guard)', () async {
      final fakeA = _FakeCalibrationClient(_resp())..buildGate = Completer<void>();
      final fakeB = _FakeCalibrationClient(_resp(darkExists: true));
      const serverA = AraServer(hostname: 'a', port: 5555);
      const serverB = AraServer(hostname: 'b', port: 5555);
      final c = ProviderContainer(overrides: [
        savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(const [serverA])),
        guiderCalibrationApiFactoryProvider
            .overrideWithValue((s) => s.hostname == 'a' ? fakeA : fakeB),
      ]);
      addTearDown(c.dispose);
      await c.read(savedServersProvider.future);
      final sub = c.listen(guiderCalibrationProvider, (_, _) {});
      addTearDown(sub.close);
      await c.read(guiderCalibrationProvider.future);

      final f = c.read(guiderCalibrationProvider.notifier).buildDarkLibrary(); // gated on fakeA
      await pumpEventQueue();
      // Switch the active server mid-action → build() re-runs against fakeB.
      await c.read(savedServersProvider.notifier).add(serverB);
      await pumpEventQueue();
      fakeA.buildGate!.complete();
      await f;
      await pumpEventQueue();

      final r = c.read(guiderCalibrationProvider).value;
      expect(r!.status!.darkLibraryExists, isTrue,
          reason: 'ends on server B status — the stale action refresh is dropped by the gen guard');
    });

    test('a failed build surfaces as AsyncError', () async {
      final api = _FakeCalibrationClient(_resp())..throwOnBuild = true;
      final c = _container(const [server], api);
      await c.read(savedServersProvider.future);
      await c.read(guiderCalibrationProvider.future);

      await c.read(guiderCalibrationProvider.notifier).buildDarkLibrary();

      expect(c.read(guiderCalibrationProvider).hasError, isTrue);
    });
  });
}
