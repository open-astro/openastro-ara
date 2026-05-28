import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/settings/plate_solve_settings_state.dart';

void main() {
  group('PlateSolveSettingsNotifier', () {
    late ProviderContainer container;
    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('defaults match playbook §37.10', () {
      final s = container.read(plateSolveSettingsProvider);
      expect(s.engine, PlateSolveEngine.astap);
      expect(s.pathOrEndpoint, '/usr/bin/astap');
      expect(s.indexDownloadPath, '/var/lib/astap');
      expect(s.searchRadiusDeg, 30);
      expect(s.downsampleFactor, 2);
      expect(s.timeoutSeconds, 60);
      expect(s.useBlindFallback, isTrue);
      expect(s.centerAfterSlew, isTrue);
      expect(s.syncToCoordinates, isTrue);
      expect(s.maxIterations, 5);
      expect(s.convergenceToleranceArcsec, 60);
    });

    test('setEngine cycles all three solvers', () {
      final n = container.read(plateSolveSettingsProvider.notifier);
      n.setEngine(PlateSolveEngine.astrometryNet);
      expect(container.read(plateSolveSettingsProvider).engine,
          PlateSolveEngine.astrometryNet);
      n.setEngine(PlateSolveEngine.platesolve2);
      expect(container.read(plateSolveSettingsProvider).engine,
          PlateSolveEngine.platesolve2);
      n.setEngine(PlateSolveEngine.astap);
      expect(container.read(plateSolveSettingsProvider).engine,
          PlateSolveEngine.astap);
    });

    test('string setters reject empty', () {
      final n = container.read(plateSolveSettingsProvider.notifier);
      n.setPathOrEndpoint('');
      n.setIndexDownloadPath('');
      final s = container.read(plateSolveSettingsProvider);
      expect(s.pathOrEndpoint, '/usr/bin/astap');
      expect(s.indexDownloadPath, '/var/lib/astap');
      n.setPathOrEndpoint('http://nova.astrometry.net');
      expect(container.read(plateSolveSettingsProvider).pathOrEndpoint,
          'http://nova.astrometry.net');
    });

    test('searchRadiusDeg clamps to (0, 180]', () {
      final n = container.read(plateSolveSettingsProvider.notifier);
      n.setSearchRadiusDeg(0);
      n.setSearchRadiusDeg(-5);
      n.setSearchRadiusDeg(181);
      expect(container.read(plateSolveSettingsProvider).searchRadiusDeg, 30);
      n.setSearchRadiusDeg(90);
      expect(container.read(plateSolveSettingsProvider).searchRadiusDeg, 90);
      n.setSearchRadiusDeg(180);
      expect(container.read(plateSolveSettingsProvider).searchRadiusDeg,
          180);
    });

    test('downsampleFactor clamps to [1, 8]', () {
      final n = container.read(plateSolveSettingsProvider.notifier);
      n.setDownsampleFactor(0);
      n.setDownsampleFactor(9);
      expect(container.read(plateSolveSettingsProvider).downsampleFactor, 2);
      n.setDownsampleFactor(4);
      expect(container.read(plateSolveSettingsProvider).downsampleFactor, 4);
    });

    test('timeoutSeconds + convergenceTolerance reject non-positive', () {
      final n = container.read(plateSolveSettingsProvider.notifier);
      n.setTimeoutSeconds(0);
      n.setTimeoutSeconds(-1);
      n.setConvergenceToleranceArcsec(0);
      n.setConvergenceToleranceArcsec(-5);
      final s = container.read(plateSolveSettingsProvider);
      expect(s.timeoutSeconds, 60);
      expect(s.convergenceToleranceArcsec, 60);
    });

    test('maxIterations rejects below 1', () {
      final n = container.read(plateSolveSettingsProvider.notifier);
      n.setMaxIterations(0);
      n.setMaxIterations(-1);
      expect(container.read(plateSolveSettingsProvider).maxIterations, 5);
      n.setMaxIterations(3);
      expect(container.read(plateSolveSettingsProvider).maxIterations, 3);
    });

    test('boolean toggles assign directly', () {
      final n = container.read(plateSolveSettingsProvider.notifier);
      n.setUseBlindFallback(false);
      n.setCenterAfterSlew(false);
      n.setSyncToCoordinates(false);
      final s = container.read(plateSolveSettingsProvider);
      expect(s.useBlindFallback, isFalse);
      expect(s.centerAfterSlew, isFalse);
      expect(s.syncToCoordinates, isFalse);
    });
  });
}
