import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/version/ara_version.dart';

void main() {
  group('AraVersion.tryParse', () {
    test('reads a stable version', () {
      expect(AraVersion.tryParse('1.2.3'), const AraVersion(1, 2, 3));
    });

    test('reads a beta in the SemVer and the VERSION spelling', () {
      expect(
        AraVersion.tryParse('1.0.0-beta.2'),
        const AraVersion(1, 0, 0, beta: 2),
      );
      expect(
        AraVersion.tryParse('1.0.0~beta2'),
        const AraVersion(1, 0, 0, beta: 2),
      );
    });

    test('ignores build metadata', () {
      expect(AraVersion.tryParse('0.0.1+37'), const AraVersion(0, 0, 1));
      expect(
        AraVersion.tryParse('1.0.0-beta.1+5f3e2a1'),
        const AraVersion(1, 0, 0, beta: 1),
      );
    });

    test('refuses what is not an Ara version', () {
      for (final bad in [
        null,
        '',
        'unknown',
        '1.0.0.0',
        '1.0',
        'v1.0.0',
        '1.0.0-beta.0',
        '1.0.0-rc.1',
        '01.0.0',
        '1.0.0-beta',
        'dev',
      ]) {
        expect(AraVersion.tryParse(bad), isNull, reason: '$bad');
      }
    });

    test('display is the SemVer spelling', () {
      expect(AraVersion.tryParse('1.0.0~beta1')!.display, '1.0.0-beta.1');
      expect(AraVersion.tryParse('2.1.0+9')!.display, '2.1.0');
    });
  });

  group('release stage', () {
    test('a beta is beta, major 0 is alpha, the rest is stable', () {
      expect(releaseStageOf('1.0.0-beta.1'), ReleaseStage.beta);
      expect(releaseStageOf('0.3.0-beta.1'), ReleaseStage.beta);
      expect(releaseStageOf('0.0.1'), ReleaseStage.alpha);
      expect(releaseStageOf('1.0.0'), ReleaseStage.stable);
      expect(releaseStageOf('2.4.1'), ReleaseStage.stable);
    });

    test('only an alpha gets a letter after the version', () {
      expect(displayVersion('0.0.1'), '0.0.1a');
      expect(displayVersion('1.0.0-beta.1'), '1.0.0-beta.1');
      expect(displayVersion('1.0.0'), '1.0.0');
    });

    test('a version that does not parse is shown as it is', () {
      expect(displayVersion('weird'), 'weird');
    });
  });

  group('versionsMismatch', () {
    test('a different minor or major differs', () {
      expect(versionsMismatch('1.0.0', '1.1.0'), isTrue);
      expect(versionsMismatch('1.0.0', '2.0.0'), isTrue);
      expect(versionsMismatch('0.0.1', '0.1.0'), isTrue);
    });

    test('the same X.Y line matches, betas and patches included', () {
      expect(versionsMismatch('1.0.0', '1.0.3'), isFalse);
      expect(versionsMismatch('1.0.0-beta.1', '1.0.0'), isFalse);
      expect(versionsMismatch('1.0.0-beta.1', '1.0.0-beta.4+abc'), isFalse);
    });

    test('a server version that does not parse never warns', () {
      expect(versionsMismatch('0.0.1', '1.0.0.0'), isFalse);
      expect(versionsMismatch('0.0.1', 'unknown'), isFalse);
      expect(versionsMismatch('0.0.1', null), isFalse);
      expect(versionsMismatch(null, '1.0.0'), isFalse);
    });
  });
}
