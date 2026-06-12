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

    test('§29 threshold setters validate each field independently (>= 1)', () {
      final n = container.read(storageSettingsProvider.notifier);
      // Non-positive is rejected per-field...
      n.setMinFreeDiskWarnGb(0);
      n.setMinFreeDiskWarnGb(-3);
      expect(container.read(storageSettingsProvider).minFreeDiskWarnGb, 10);
      // ...but a value that crosses the other threshold is accepted (validated at save, not on edit).
      n.setMinFreeDiskWarnGb(1); // below critical default (2) — still accepted as an intermediate edit
      expect(container.read(storageSettingsProvider).minFreeDiskWarnGb, 1);
      n.setMinFreeDiskCriticalGb(0);
      expect(container.read(storageSettingsProvider).minFreeDiskCriticalGb, 2);
      n.setMinFreeDiskCriticalGb(8);
      expect(container.read(storageSettingsProvider).minFreeDiskCriticalGb, 8);
    });

    test('§29 thresholdsValid flags an inverted pair (the save-time check)', () {
      final n = container.read(storageSettingsProvider.notifier);
      expect(n.thresholdsValid, isTrue); // defaults 10 > 2
      n.setMinFreeDiskCriticalGb(8); // 8 >= warn? no, warn=10 → 8 < 10 still valid
      expect(n.thresholdsValid, isTrue);
      n.setMinFreeDiskWarnGb(5); // now critical(8) >= warn(5) → invalid
      expect(n.thresholdsValid, isFalse);
    });
  });
}
