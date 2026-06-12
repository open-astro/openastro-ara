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

    test('setSaveDirectory trims + rejects empty or whitespace-only', () {
      final n = container.read(storageSettingsProvider.notifier);
      n.setSaveDirectory('');
      n.setSaveDirectory('   ');
      expect(container.read(storageSettingsProvider).saveDirectory,
          '/media/openastroara');
      n.setSaveDirectory('  /mnt/usb  ');
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

    test('setFilenameTemplate trims + rejects empty or whitespace-only', () {
      final n = container.read(storageSettingsProvider.notifier);
      final original = container.read(storageSettingsProvider).filenameTemplate;
      n.setFilenameTemplate('');
      n.setFilenameTemplate('   ');
      expect(container.read(storageSettingsProvider).filenameTemplate,
          original);
      n.setFilenameTemplate(r'  $$TARGET$$/$$FILTER$$  ');
      expect(container.read(storageSettingsProvider).filenameTemplate,
          r'$$TARGET$$/$$FILTER$$');
    });

    test('§29 disk-space threshold defaults', () {
      final s = container.read(storageSettingsProvider);
      expect(s.minFreeDiskWarnGb, 10);
      expect(s.minFreeDiskCriticalGb, 2);
    });

    test('§29 threshold setters keep critical strictly below warn', () {
      final n = container.read(storageSettingsProvider.notifier);
      // Warn must stay above critical (default 2) and positive.
      n.setMinFreeDiskWarnGb(0);
      n.setMinFreeDiskWarnGb(2); // == critical, rejected
      expect(container.read(storageSettingsProvider).minFreeDiskWarnGb, 10);
      n.setMinFreeDiskWarnGb(25);
      expect(container.read(storageSettingsProvider).minFreeDiskWarnGb, 25);

      // Critical must stay below warn (now 25) and positive.
      n.setMinFreeDiskCriticalGb(0);
      n.setMinFreeDiskCriticalGb(25); // == warn, rejected
      n.setMinFreeDiskCriticalGb(30); // > warn, rejected
      expect(container.read(storageSettingsProvider).minFreeDiskCriticalGb, 2);
      n.setMinFreeDiskCriticalGb(5);
      expect(container.read(storageSettingsProvider).minFreeDiskCriticalGb, 5);
    });
  });
}
