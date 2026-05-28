import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/storage_settings_state.dart';
import '../../../theme/ara_colors.dart';

/// Storage panel per §29 — save directory + format + compression + filename
/// template. Phase 12h.2-storage makes the form editable, wired to
/// `storageSettingsProvider`. The §29.1.3 SD-card wear warning stays at the
/// top. Free-space probing + §63 directory picker land in 12h.2b alongside
/// daemon round-trip.
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
        _TextField(
          label: 'Save directory',
          initialValue: s.saveDirectory,
          parse: n.setSaveDirectory,
        ),
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Row(
            children: [
              SizedBox(
                width: 200,
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
                width: 200,
                child: Text('File format',
                    style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                          color: AraColors.textSecondary,
                        )),
              ),
              Expanded(
                child: DropdownButtonFormField<StorageFileFormat>(
                  initialValue: s.fileFormat,
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
                width: 200,
                child: Text('Compression',
                    style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                          color: AraColors.textSecondary,
                        )),
              ),
              Expanded(
                child: DropdownButtonFormField<StorageCompression>(
                  initialValue: s.compression,
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
        _TextField(
          label: 'Filename template',
          initialValue: s.filenameTemplate,
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

/// Owns its own TextEditingController + FocusNode per the PR #63 contract
/// — never allocate a controller inline in build(). Commits on focus-out
/// + onSubmitted.
class _TextField extends StatefulWidget {
  final String label;
  final String initialValue;
  final void Function(String) parse;
  final int maxLines;
  const _TextField({
    required this.label,
    required this.initialValue,
    required this.parse,
    this.maxLines = 1,
  });

  @override
  State<_TextField> createState() => _TextFieldState();
}

class _TextFieldState extends State<_TextField> {
  late final TextEditingController _controller;
  late final FocusNode _focusNode;

  @override
  void initState() {
    super.initState();
    _controller = TextEditingController(text: widget.initialValue);
    _focusNode = FocusNode();
    _focusNode.addListener(() {
      if (!_focusNode.hasFocus) {
        widget.parse(_controller.text);
      }
    });
  }

  @override
  void dispose() {
    _controller.dispose();
    _focusNode.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(
            width: 200,
            child: Padding(
              padding: const EdgeInsets.only(top: 12),
              child: Text(widget.label,
                  style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                        color: AraColors.textSecondary,
                      )),
            ),
          ),
          Expanded(
            child: TextField(
              controller: _controller,
              focusNode: _focusNode,
              maxLines: widget.maxLines,
              decoration: const InputDecoration(isDense: true),
              onSubmitted: widget.parse,
            ),
          ),
        ],
      ),
    );
  }
}
