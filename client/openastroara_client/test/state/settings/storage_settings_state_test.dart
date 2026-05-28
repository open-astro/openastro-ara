import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/settings/storage_settings_state.dart';

void main() {
  group('StorageSettingsNotifier', () {
    late ProviderContainer container;
    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('defaults match playbook §29', () {
      final s = container.read(storageSettingsProvider);
      expect(s.saveDirectory, '/media/openastroara');
      expect(s.fileFormat, StorageFileFormat.fits);
      expect(s.compression, StorageCompression.rice);
      expect(s.filenameTemplate.isNotEmpty, isTrue);
    });

    test('setSaveDirectory rejects empty', () {
      final n = container.read(storageSettingsProvider.notifier);
      n.setSaveDirectory('');
      expect(container.read(storageSettingsProvider).saveDirectory,
          '/media/openastroara');
      n.setSaveDirectory('/mnt/usb');
      expect(container.read(storageSettingsProvider).saveDirectory, '/mnt/usb');
    });

    test('setFileFormat + setCompression assign directly', () {
      final n = container.read(storageSettingsProvider.notifier);
      n.setFileFormat(StorageFileFormat.xisf);
      n.setCompression(StorageCompression.off);
      expect(container.read(storageSettingsProvider).fileFormat,
          StorageFileFormat.xisf);
      expect(container.read(storageSettingsProvider).compression,
          StorageCompression.off);
    });

    test('setFilenameTemplate rejects empty', () {
      final n = container.read(storageSettingsProvider.notifier);
      final original = container.read(storageSettingsProvider).filenameTemplate;
      n.setFilenameTemplate('');
      expect(container.read(storageSettingsProvider).filenameTemplate,
          original);
      n.setFilenameTemplate(r'$$TARGET$$/$$FILTER$$');
      expect(container.read(storageSettingsProvider).filenameTemplate,
          r'$$TARGET$$/$$FILTER$$');
    });
  });
}
