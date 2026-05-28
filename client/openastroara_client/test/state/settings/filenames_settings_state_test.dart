import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/settings/filenames_settings_state.dart';

void main() {
  group('FilenamesSettingsNotifier', () {
    late ProviderContainer container;
    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('defaults', () {
      final s = container.read(filenamesSettingsProvider);
      expect(s.dateSeparator, DateSeparator.forwardSlash);
      expect(s.compressDarksAndBias, isTrue);
    });

    test('setDateSeparator cycles all 3 options', () {
      final n = container.read(filenamesSettingsProvider.notifier);
      n.setDateSeparator(DateSeparator.underscore);
      expect(container.read(filenamesSettingsProvider).dateSeparator,
          DateSeparator.underscore);
      n.setDateSeparator(DateSeparator.dash);
      expect(container.read(filenamesSettingsProvider).dateSeparator,
          DateSeparator.dash);
      n.setDateSeparator(DateSeparator.forwardSlash);
      expect(container.read(filenamesSettingsProvider).dateSeparator,
          DateSeparator.forwardSlash);
    });

    test('setCompressDarksAndBias toggles independently', () {
      final n = container.read(filenamesSettingsProvider.notifier);
      n.setCompressDarksAndBias(false);
      expect(container.read(filenamesSettingsProvider).compressDarksAndBias,
          isFalse);
      // dateSeparator unchanged.
      expect(container.read(filenamesSettingsProvider).dateSeparator,
          DateSeparator.forwardSlash);
    });
  });
}
