import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/storage_settings_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/settings/editable_field.dart';

/// Storage panel per §29 — save directory + format + compression + filename
/// template. Phase 12h.2-storage made the form editable; 12h.2-display-sync
/// swaps the panel's local _TextField for the shared `EditableTextRow` so
/// rejected input (empty save dir, empty filename template) snaps back to
/// the canonical state.
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
            color: AraColors.accentBusy.withOpacity(0.12),
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
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Row(
            children: [
              SizedBox(
                width: 280,
                child: Text('File format',
                    style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                          color: AraColors.textSecondary,
                        )),
              ),
              Expanded(
                child: DropdownButtonFormField<StorageFileFormat>(
                  value: s.fileFormat,
                  isDense: true,
                  items: const [
                    DropdownMenuItem(value: StorageFileFormat.fits, child: Text('FITS')),
                    DropdownMenuItem(value: StorageFileFormat.xisf, child: Text('XISF')),
                    DropdownMenuItem(value: StorageFileFormat.fitsRice, child: Text('FITS + RICE compression')),
                    DropdownMenuItem(value: StorageFileFormat.fitsGzip, child: Text('FITS + gzip')),
                  ],
                  onChanged: (v) {
                    if (v != null) n.setFileFormat(v);
                  },
                ),
              ),
            ],
          ),
        ),
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Row(
            children: [
              SizedBox(
                width: 280,
                child: Text('Compression',
                    style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                          color: AraColors.textSecondary,
                        )),
              ),
              Expanded(
                child: DropdownButtonFormField<StorageCompression>(
                  value: s.compression,
                  isDense: true,
                  items: const [
                    DropdownMenuItem(value: StorageCompression.off, child: Text('Off')),
                    DropdownMenuItem(value: StorageCompression.rice, child: Text('RICE')),
                    DropdownMenuItem(value: StorageCompression.gzip, child: Text('gzip')),
                  ],
                  onChanged: (v) {
                    if (v != null) n.setCompression(v);
                  },
                ),
              ),
            ],
          ),
        ),
        EditableTextRow(
          label: 'Filename template',
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
