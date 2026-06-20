import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/bug_report_preparation.dart';
import '../../state/support/bug_report_state.dart';

/// §54 "Send me a bug report" action — prepares a diagnostic bundle on the
/// daemon, shows the user exactly what it contains (a PII disclosure, required
/// before the server will serve it via `?acknowledge=pii`), then downloads the
/// ZIP to a chosen path.
class BugReportCard extends ConsumerStatefulWidget {
  const BugReportCard({super.key});

  @override
  ConsumerState<BugReportCard> createState() => _BugReportCardState();
}

class _BugReportCardState extends ConsumerState<BugReportCard> {
  bool _busy = false;

  Future<void> _run() async {
    final api = ref.read(bugReportApiProvider);
    if (api == null) return;
    setState(() => _busy = true);
    try {
      final prep = await api.prepare();
      if (!mounted) return;

      // PII gate: the user must see what's in the bundle before it's downloaded.
      final proceed = await _showDisclosure(prep);
      if (!mounted || proceed != true) return;

      final dl = await api.download(prep.preparationId);
      if (!mounted) return;
      final saved = await FilePicker.saveFile(
        dialogTitle: 'Save bug report',
        fileName: dl.fileName,
        bytes: dl.bytes,
      );
      if (!mounted) return;
      if (saved != null) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text('Saved ${dl.fileName}')),
        );
      }
    } catch (e) {
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text('Bug report failed: $e')),
      );
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<bool?> _showDisclosure(BugReportPreparation prep) {
    return showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Before you share this bug report'),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const Text('The bundle (≈ a few hundred KB) contains:'),
            const SizedBox(height: 8),
            const Text('• the daemon\'s recent log files'),
            const Text(
                '• your full profile — which may include equipment credentials, '
                'API / notification tokens, precise observatory coordinates, and '
                'network endpoints'),
            const Text('• system info, including this device\'s filesystem path'),
            const SizedBox(height: 12),
            Text('Approximate size: ${_humanSize(prep.estimatedSizeBytes)}.'),
            const SizedBox(height: 8),
            const Text(
              'Only share it with people you trust (e.g. the developers) — '
              'treat it like a password.',
            ),
          ],
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(context).pop(false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(context).pop(true),
            child: const Text('Download anyway'),
          ),
        ],
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card(
      margin: const EdgeInsets.all(8),
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Row(
          children: [
            Icon(Icons.bug_report_outlined, color: theme.colorScheme.primary),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text('Send me a bug report',
                      style: theme.textTheme.titleSmall),
                  const SizedBox(height: 2),
                  Text(
                    'Bundles the daemon logs + your profile + system info into a '
                    'ZIP you can attach to a report.',
                    style: theme.textTheme.bodySmall,
                  ),
                ],
              ),
            ),
            const SizedBox(width: 12),
            FilledButton.icon(
              onPressed: _busy ? null : _run,
              icon: _busy
                  ? const SizedBox(
                      width: 16,
                      height: 16,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : const Icon(Icons.bug_report, size: 18),
              label: const Text('Prepare & download'),
            ),
          ],
        ),
      ),
    );
  }

  static String _humanSize(int bytes) {
    if (bytes < 1024) return '$bytes B';
    final kb = bytes / 1024;
    if (kb < 1024) return '${kb.toStringAsFixed(0)} KB';
    return '${(kb / 1024).toStringAsFixed(1)} MB';
  }
}
