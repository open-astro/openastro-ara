import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guiding_autotune.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/guiding_autotune_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/guider/guiding_autotune_state.dart';
import 'package:openastroara/state/saved_server_state.dart';

class _FakeSavedServerService implements SavedServerService {
  @override
  Future<List<AraServer>> loadAll() async => const [AraServer(hostname: 'host', port: 5555)];

  @override
  Future<void> saveAll(List<AraServer> servers) async {}

  @override
  Future<void> add(AraServer server) async {}
}

class _FakeAutoTuneApi implements GuidingAutoTuneClient {
  _FakeAutoTuneApi(this.status);

  GuidingAutoTuneStatus status;
  int startCalls = 0;

  @override
  Future<GuidingAutoTuneCapabilities> getCapabilities() async => const GuidingAutoTuneCapabilities(
        enabled: true,
        connected: true,
        hasTelemetry: true,
        canAnalyze: true,
        canApply: false,
        guideRateChangesSupported: false,
        lockedReasons: [],
      );

  @override
  Future<GuidingAutoTuneStatus> getLatest() async => status;

  @override
  Future<GuidingAutoTuneStatus> getSession(String sessionId) async => status;

  @override
  Future<GuidingAutoTuneReport> getReport() async => const GuidingAutoTuneReport(sessionId: 's', markdown: '# report');

  @override
  Future<GuidingAutoTuneReport> getSessionReport(String sessionId) async => getReport();

  @override
  Future<GuidingAutoTuneStatus> start({String depth = 'standard', bool dryRun = true, bool useMainCameraValidation = false}) async {
    startCalls++;
    status = GuidingAutoTuneStatus(
      sessionId: 's',
      state: dryRun ? 'Proposed' : 'CharacterizingUnguided',
      progress: .2,
      currentStep: 'started',
      behaviorClass: null,
      behaviorConfidence: null,
      telemetrySamples: 8,
      baselineScore: 1,
      bestScore: null,
      canApply: dryRun,
      canRollback: true,
      warnings: const [],
      plan: null,
      bestCandidate: null,
      startedAtUtc: DateTime.utc(2026, 7, 31),
      updatedAtUtc: DateTime.utc(2026, 7, 31),
    );
    return status;
  }

  @override
  Future<GuidingAutoTuneStatus> cancel() async => status;
  @override
  Future<GuidingAutoTuneStatus> cancelSession(String sessionId) async => status;
  @override
  Future<GuidingAutoTuneStatus> apply() async => status;
  @override
  Future<GuidingAutoTuneStatus> applySession(String sessionId) async => status;
  @override
  Future<GuidingAutoTuneStatus> rollback() async => status;
  @override
  Future<GuidingAutoTuneStatus> rollbackSession(String sessionId) async => status;
  @override
  void close() {}
}

GuidingAutoTuneStatus _status(String state) => GuidingAutoTuneStatus(
      sessionId: 's',
      state: state,
      progress: .5,
      currentStep: state,
      behaviorClass: null,
      behaviorConfidence: null,
      telemetrySamples: 8,
      baselineScore: 1,
      bestScore: null,
      canApply: false,
      canRollback: state != 'Completed',
      warnings: const [],
      plan: null,
      bestCandidate: null,
      startedAtUtc: DateTime.utc(2026, 7, 31),
      updatedAtUtc: DateTime.utc(2026, 7, 31),
    );

void main() {
  test('build loads latest session and resumes active polling state', () async {
    final api = _FakeAutoTuneApi(_status('EvaluatingCandidate'));
    final container = ProviderContainer(overrides: [
      savedServerServiceProvider.overrideWithValue(_FakeSavedServerService()),
      guidingAutoTuneApiFactoryProvider.overrideWithValue((_) => api),
    ]);
    addTearDown(container.dispose);

    await container.read(savedServersProvider.future);
    final result = await container.read(guidingAutoTuneProvider.future);
    expect(result!.state, 'EvaluatingCandidate');
  });

  test('start sends dry-run request through injected client', () async {
    final api = _FakeAutoTuneApi(_status('idle'));
    final container = ProviderContainer(overrides: [
      savedServerServiceProvider.overrideWithValue(_FakeSavedServerService()),
      guidingAutoTuneApiFactoryProvider.overrideWithValue((_) => api),
    ]);
    addTearDown(container.dispose);

    await container.read(savedServersProvider.future);
    final notifier = container.read(guidingAutoTuneProvider.notifier);
    await notifier.start(depth: 'deep', dryRun: true);
    expect(api.startCalls, 1);
    expect(container.read(guidingAutoTuneProvider).value!.state, 'Proposed');
  });
}
