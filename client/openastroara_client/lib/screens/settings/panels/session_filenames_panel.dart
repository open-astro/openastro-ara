import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/filenames_settings_state.dart';
import '../../../state/settings/storage_settings_state.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §29.2 File saving + naming. Phase 12h.2-filenames makes the
/// naming-specific options editable (date separator + dark/bias
/// compression) and surfaces the storage-panel-owned template / format /
/// compression as read-only refs (edit them in Storage to keep one source
/// of truth).
class SessionFilenamesPanel extends ConsumerWidget {
  const SessionFilenamesPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final fs = ref.watch(filenamesSettingsProvider);
    final fn = ref.read(filenamesSettingsProvider.notifier);
    final ss = ref.watch(storageSettingsProvider);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Format'),
        SettingsRow(
          label: 'File format',
          value: _formatLabel(ss.fileFormat),
          hint: 'Edit in Settings → Session → Storage',
        ),
        SettingsRow(
          label: 'Compression',
          value: _compressionLabel(ss.compression),
          hint: 'Edit in Settings → Session → Storage',
        ),
        SettingsSwitchRow(
          label: 'Compress bias/dark frames',
          helpKey: 'session.filenames.compress_darks_and_bias',
          value: fs.compressDarksAndBias,
          onChanged: fn.setCompressDarksAndBias,
          hint: 'RICE losslessly compresses dark frames very well',
        ),
        const SettingsSectionHeader('Naming template'),
        SettingsRow(
          label: 'Template',
          value: ss.filenameTemplate,
          hint: 'Edit in Settings → Session → Storage',
        ),
        SettingsDropdownRow<DateSeparator>(
          label: 'Date separator',
          helpKey: 'session.filenames.date_separator',
          value: fs.dateSeparator,
          items: const {
            DateSeparator.forwardSlash: '/  (forward slash — real directories)',
            DateSeparator.underscore: '_  (underscore — flat filenames)',
            DateSeparator.dash: '-  (dash — flat filenames, Windows-safe)',
          },
          onChanged: (v) {
            if (v != null) fn.setDateSeparator(v);
          },
        ),
        const SettingsSectionHeader('Available tokens'),
        const SettingsRow(
          label: 'Date',
          value: r'$$DATE$$  $$TIME$$  $$DATETIME$$  $$DATEMINUS12$$',
        ),
        const SettingsRow(
          label: 'Target',
          value: r'$$TARGETNAME$$  $$SEQUENCETITLE$$',
        ),
        const SettingsRow(
          label: 'Frame',
          value: r'$$IMAGETYPE$$  $$FILTER$$  $$EXPOSURETIME$$  '
              r'$$GAIN$$  $$OFFSET$$  $$BINNING$$  $$FRAMENR$$',
        ),
        const SettingsRow(
          label: 'Sensor',
          value: r'$$CAMERA$$  $$SENSORTEMP$$  $$FOCUSPOSITION$$',
        ),
        const SizedBox(height: 24),
        Row(mainAxisAlignment: MainAxisAlignment.end, children: [
          FilledButton.icon(
            onPressed: () {
              ScaffoldMessenger.of(context).showSnackBar(
                const SnackBar(
                  content: Text(
                    'File naming options saved (in memory). Daemon round-trip lands in 12h.2b.',
                  ),
                ),
              );
            },
            icon: const Icon(Icons.save, size: 16),
            label: const Text('Save'),
          ),
        ]),
      ],
    );
  }

  String _formatLabel(StorageFileFormat f) => switch (f) {
        StorageFileFormat.fits => 'FITS',
        StorageFileFormat.xisf => 'XISF',
        StorageFileFormat.fitsRice => 'FITS + RICE',
        StorageFileFormat.fitsGzip => 'FITS + gzip',
      };

  String _compressionLabel(StorageCompression c) => switch (c) {
        StorageCompression.off => 'Off',
        StorageCompression.rice => 'RICE',
        StorageCompression.gzip => 'gzip',
      };
}
