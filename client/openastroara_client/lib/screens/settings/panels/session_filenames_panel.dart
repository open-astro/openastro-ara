import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/filenames_settings_state.dart';
import '../../../state/settings/storage_settings_state.dart';
import '../../../theme/ara_colors.dart';
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
        _SwitchRow(
          label: 'Compress bias/dark frames',
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
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Row(children: [
            SizedBox(
              width: 280,
              child: Text('Date separator',
                  style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                        color: AraColors.textSecondary,
                      )),
            ),
            Expanded(
              child: DropdownButtonFormField<DateSeparator>(
                initialValue: fs.dateSeparator,
                isDense: true,
                items: const [
                  DropdownMenuItem(
                    value: DateSeparator.forwardSlash,
                    child: Text('/  (forward slash — real directories)'),
                  ),
                  DropdownMenuItem(
                    value: DateSeparator.underscore,
                    child: Text('_  (underscore — flat filenames)'),
                  ),
                  DropdownMenuItem(
                    value: DateSeparator.dash,
                    child: Text('-  (dash — flat filenames, Windows-safe)'),
                  ),
                ],
                onChanged: (v) {
                  if (v != null) fn.setDateSeparator(v);
                },
              ),
            ),
          ]),
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

class _SwitchRow extends StatelessWidget {
  final String label;
  final bool value;
  final ValueChanged<bool> onChanged;
  final String? hint;
  const _SwitchRow({
    required this.label,
    required this.value,
    required this.onChanged,
    this.hint,
  });

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Row(children: [
        SizedBox(
          width: 280,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(label,
                  style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                        color: AraColors.textSecondary,
                      )),
              if (hint != null)
                Text(hint!,
                    style: Theme.of(context).textTheme.labelSmall?.copyWith(
                          color: AraColors.textDisabled,
                        )),
            ],
          ),
        ),
        Switch(value: value, onChanged: onChanged),
      ]),
    );
  }
}
