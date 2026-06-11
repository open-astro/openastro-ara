import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/imaging/last_frame_state.dart';

void main() {
  group('LastCapturedFrameId', () {
    late ProviderContainer container;

    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('defaults to null (no frame captured yet)', () {
      expect(container.read(lastCapturedFrameIdProvider), isNull);
    });

    test('set updates the current frame id', () {
      final notifier = container.read(lastCapturedFrameIdProvider.notifier);
      notifier.set('abc-123');
      expect(container.read(lastCapturedFrameIdProvider), 'abc-123');
    });

    test('set replaces the previous frame id', () {
      final notifier = container.read(lastCapturedFrameIdProvider.notifier);
      notifier.set('first');
      notifier.set('second');
      expect(container.read(lastCapturedFrameIdProvider), 'second');
    });
  });

  group('CaptureInProgress', () {
    late ProviderContainer container;

    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('defaults to false', () {
      expect(container.read(captureInProgressProvider), isFalse);
    });

    test('set toggles in-progress state', () {
      final notifier = container.read(captureInProgressProvider.notifier);
      notifier.set(true);
      expect(container.read(captureInProgressProvider), isTrue);
      notifier.set(false);
      expect(container.read(captureInProgressProvider), isFalse);
    });
  });
}
