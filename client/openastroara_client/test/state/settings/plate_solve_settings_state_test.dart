import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../support/section_load.dart';

import 'package:openastroara/state/settings/plate_solve_settings_state.dart';

void main() {
  group('PlateSolveSettingsNotifier', () {
    late ProviderContainer container;
    setUp(() async {
      container = ProviderContainer();
      await loadSection(container, plateSolveSettingsProvider, 'plate-solve');
    });
    tearDown(() => container.dispose());

    test('defaults match playbook §37.10', () {
      final s = container.read(plateSolveSettingsProvider).value!;
      expect(s.engine, PlateSolveEngine.astap);
      expect(s.pathOrEndpoint, '/opt/openastroara/astap_cli');
      expect(s.indexDownloadPath, '/var/lib/astap');
      expect(s.searchRadiusDeg, 30);
      expect(s.downsampleFactor, 0); // 0 = automatic (#1136)
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
      expect(
        container.read(plateSolveSettingsProvider).value!.engine,
        PlateSolveEngine.astrometryNet,
      );
      n.setEngine(PlateSolveEngine.platesolve2);
      expect(
        container.read(plateSolveSettingsProvider).value!.engine,
        PlateSolveEngine.platesolve2,
      );
      n.setEngine(PlateSolveEngine.astap);
      expect(
        container.read(plateSolveSettingsProvider).value!.engine,
        PlateSolveEngine.astap,
      );
    });

    test('string setters trim + reject empty or whitespace-only', () {
      final n = container.read(plateSolveSettingsProvider.notifier);
      n.setPathOrEndpoint('');
      n.setPathOrEndpoint('   ');
      n.setIndexDownloadPath('');
      n.setIndexDownloadPath('\t\n');
      final s = container.read(plateSolveSettingsProvider).value!;
      expect(s.pathOrEndpoint, '/opt/openastroara/astap_cli');
      expect(s.indexDownloadPath, '/var/lib/astap');
      n.setPathOrEndpoint('  http://nova.astrometry.net  ');
      expect(
        container.read(plateSolveSettingsProvider).value!.pathOrEndpoint,
        'http://nova.astrometry.net',
      );
    });

    test('searchRadiusDeg rejects outside (0, 180]', () {
      final n = container.read(plateSolveSettingsProvider.notifier);
      n.setSearchRadiusDeg(0);
      n.setSearchRadiusDeg(-5);
      n.setSearchRadiusDeg(181);
      expect(
        container.read(plateSolveSettingsProvider).value!.searchRadiusDeg,
        30,
      );
      n.setSearchRadiusDeg(90);
      expect(
        container.read(plateSolveSettingsProvider).value!.searchRadiusDeg,
        90,
      );
      n.setSearchRadiusDeg(180);
      expect(
        container.read(plateSolveSettingsProvider).value!.searchRadiusDeg,
        180,
      );
    });

    test('downsampleFactor rejects outside [0, 8]; 0 = automatic', () {
      final n = container.read(plateSolveSettingsProvider.notifier);
      n.setDownsampleFactor(-1);
      n.setDownsampleFactor(9);
      expect(
        container.read(plateSolveSettingsProvider).value!.downsampleFactor,
        0,
      );
      n.setDownsampleFactor(4);
      expect(
        container.read(plateSolveSettingsProvider).value!.downsampleFactor,
        4,
      );
      n.setDownsampleFactor(0);
      expect(
        container.read(plateSolveSettingsProvider).value!.downsampleFactor,
        0,
      );
    });

    test('timeoutSeconds + convergenceTolerance reject non-positive', () {
      final n = container.read(plateSolveSettingsProvider.notifier);
      n.setTimeoutSeconds(0);
      n.setTimeoutSeconds(-1);
      n.setConvergenceToleranceArcsec(0);
      n.setConvergenceToleranceArcsec(-5);
      final s = container.read(plateSolveSettingsProvider).value!;
      expect(s.timeoutSeconds, 60);
      expect(s.convergenceToleranceArcsec, 60);
    });

    test('maxIterations rejects below 1', () {
      final n = container.read(plateSolveSettingsProvider.notifier);
      n.setMaxIterations(0);
      n.setMaxIterations(-1);
      expect(
        container.read(plateSolveSettingsProvider).value!.maxIterations,
        5,
      );
      n.setMaxIterations(3);
      expect(
        container.read(plateSolveSettingsProvider).value!.maxIterations,
        3,
      );
    });

    test('boolean toggles assign directly', () {
      final n = container.read(plateSolveSettingsProvider.notifier);
      n.setUseBlindFallback(false);
      n.setCenterAfterSlew(false);
      n.setSyncToCoordinates(false);
      final s = container.read(plateSolveSettingsProvider).value!;
      expect(s.useBlindFallback, isFalse);
      expect(s.centerAfterSlew, isFalse);
      expect(s.syncToCoordinates, isFalse);
    });
  });
}
