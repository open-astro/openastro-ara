import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/storage_settings_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/settings/editable_field.dart';

/// Storage panel per §29 — save directory + format + compression + filename
/// template. Phase 12h.3c registered all 4 fields in `settings/registry.dart`
/// and wired `helpKey`s on the non-obvious controls (format, compression,
/// filename template). The ⌘K command palette now finds them; ⓘ icons
/// explain them in-place.
class StoragePanel extends ConsumerWidget {
  const StoragePanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(storageSettingsProvider);
    final n = ref.read(storageSettingsProvider.notifier);
    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        Container(
          padding: const EdgeInsets.all(12),
          decoration: BoxDecoration(
            color: AraColors.accentBusy.withValues(alpha: 0.12),
            border: Border.all(color: AraColors.accentBusy),
            borderRadius: BorderRadius.circular(4),
          ),
          child: Row(children: const [
            Icon(Icons.warning_amber, size: 18, color: AraColors.accentBusy),
            SizedBox(width: 8),
            Expanded(
              child: Text(
                'Storage is currently on the SD card. Capture > 1 night will '
                'wear the SD card out — connect a USB drive and click '
                '"Reformat as ext4" to migrate. (§29 + §29.1.3 wizard)',
              ),
            ),
          ]),
        ),
        const SizedBox(height: 16),
        EditableTextRow(
          label: 'Save directory',
          helpKey: 'session.storage.save_directory',
          currentValue: s.saveDirectory,
          getCanonical: () => ref.read(storageSettingsProvider).saveDirectory,
          parse: n.setSaveDirectory,
        ),
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Row(
            children: [
              SizedBox(
                width: 280,
                child: Text('Free space',
                    style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                          color: AraColors.textSecondary,
                        )),
              ),
              const Expanded(
                child: Text('12.3 GB / 32 GB  (real probe in 12h.2b)'),
              ),
            ],
          ),
        ),
        SettingsDropdownRow<StorageFileFormat>(
          label: 'File format',
          helpKey: 'session.storage.file_format',
          value: s.fileFormat,
          items: const {
            StorageFileFormat.fits: 'FITS',
            StorageFileFormat.xisf: 'XISF',
            StorageFileFormat.fitsRice: 'FITS + RICE compression',
            StorageFileFormat.fitsGzip: 'FITS + gzip',
          },
          onChanged: (v) {
            if (v != null) n.setFileFormat(v);
          },
        ),
        SettingsDropdownRow<StorageCompression>(
          label: 'Compression',
          helpKey: 'session.storage.compression',
          value: s.compression,
          items: const {
            StorageCompression.off: 'Off',
            StorageCompression.rice: 'RICE',
            StorageCompression.gzip: 'gzip',
          },
          onChanged: (v) {
            if (v != null) n.setCompression(v);
          },
        ),
        EditableTextRow(
          label: 'Filename template',
          helpKey: 'session.storage.filename_template',
          currentValue: s.filenameTemplate,
          getCanonical: () =>
              ref.read(storageSettingsProvider).filenameTemplate,
          parse: n.setFilenameTemplate,
          maxLines: 2,
        ),
        const SizedBox(height: 24),
        Row(
          mainAxisAlignment: MainAxisAlignment.end,
          children: [
            FilledButton.icon(
              onPressed: () {
                ScaffoldMessenger.of(context).showSnackBar(
                  const SnackBar(
                    content: Text(
                      'Storage settings saved (in memory). Daemon round-trip lands in 12h.2b.',
                    ),
                  ),
                );
              },
              icon: const Icon(Icons.save, size: 16),
              label: const Text('Save'),
            ),
          ],
        ),
      ],
    );
  }
}
