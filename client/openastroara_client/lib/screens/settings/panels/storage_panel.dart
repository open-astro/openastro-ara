import 'package:flutter/material.dart';

import '../../../theme/ara_colors.dart';

/// Storage panel per §29 — save directory + format + compression. Phase
/// 12h.1 stubs the form; 12h.2 wires §29.1.3 ext4 reformat UX + free-space
/// warnings + the §63 directory picker.
class StoragePanel extends StatelessWidget {
  const StoragePanel({super.key});

  @override
  Widget build(BuildContext context) {
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
          child: Row(children: [
            Icon(Icons.warning_amber, size: 18, color: AraColors.accentBusy),
            const SizedBox(width: 8),
            const Expanded(
              child: Text(
                'Storage is currently on the SD card. Capture > 1 night will '
                'wear the SD card out — connect a USB drive and click '
                '"Reformat as ext4" to migrate. (§29 + §29.1.3 wizard)',
              ),
            ),
          ]),
        ),
        const SizedBox(height: 16),
        _Row(label: 'Save directory', value: '/media/openastroara (SD)'),
        _Row(label: 'Free space', value: '12.3 GB / 32 GB'),
        _Row(label: 'File format', value: 'FITS'),
        _Row(label: 'Compression', value: 'On (RICE)'),
        _Row(label: 'Filename template',
            value: r'$$DATEMINUS12$$\\$$IMAGETYPE$$\\$$DATETIME$$_…'),
      ],
    );
  }
}

class _Row extends StatelessWidget {
  final String label;
  final String value;
  const _Row({required this.label, required this.value});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Row(children: [
        SizedBox(
          width: 200,
          child: Text(label,
              style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                    color: AraColors.textSecondary,
                  )),
        ),
        Expanded(
            child: Text(value, style: Theme.of(context).textTheme.bodyMedium)),
      ]),
    );
  }
}
