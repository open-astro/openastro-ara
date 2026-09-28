import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/client_error_log.dart';
import '../../util/friendly_error.dart';
import '../../util/stream_save_location.dart';

/// §54 "Save client log" — exports this app's own error log (#1111). Sits
/// above the Support tab's connect gate on purpose: a client-side crash is
/// exactly the case where there may be no rig to talk to, and the log is on
/// this machine, not the daemon.
class ClientLogCard extends ConsumerStatefulWidget {
  /// Test seam for the OS save-location dialog (no platform channel under
  /// widget tests). Returns the destination path, or null on cancel.
  /// Production leaves it null → [pickStreamSavePath].
  final Future<String?> Function(String dialogTitle, String suggestedName)?
  savePathPicker;

  const ClientLogCard({super.key, this.savePathPicker});

  static const suggestedFileName = 'openastroara-client.log';

  @override
  ConsumerState<ClientLogCard> createState() => _ClientLogCardState();
}

class _ClientLogCardState extends ConsumerState<ClientLogCard> {
  bool _busy = false;

  Future<void> _save() async {
    final log = ref.read(clientErrorLogProvider);
    setState(() => _busy = true);
    try {
      final pick = widget.savePathPicker ?? pickStreamSavePath;
      final savePath = await pick(
        'Choose where to save the client log',
        ClientLogCard.suggestedFileName,
      );
      if (!mounted || savePath == null) return;
      await log.exportTo(savePath);
      if (!mounted) return;
      ScaffoldMessenger.of(
        context,
      ).showSnackBar(SnackBar(content: Text('Saved $savePath')));
    } catch (e) {
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(friendlyError(e, action: 'save the log'))),
      );
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final log = ref.watch(clientErrorLogProvider);
    return Card(
      margin: const EdgeInsets.all(8),
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Row(
          children: [
            Icon(Icons.laptop_outlined, color: theme.colorScheme.primary),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text('This app\'s error log', style: theme.textTheme.titleSmall),
                  const SizedBox(height: 2),
                  ValueListenableBuilder<ClientErrorLogStatus>(
                    valueListenable: log.status,
                    builder: (context, status, _) => Text(
                      _describe(status),
                      style: theme.textTheme.bodySmall,
                    ),
                  ),
                ],
              ),
            ),
            const SizedBox(width: 12),
            FilledButton.icon(
              onPressed: _busy ? null : _save,
              icon: _busy
                  ? const SizedBox(
                      width: 16,
                      height: 16,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : const Icon(Icons.save_alt, size: 18),
              label: const Text('Save client log'),
            ),
          ],
        ),
      ),
    );
  }

  static String _describe(ClientErrorLogStatus s) {
    const tail =
        'Errors this app hits on this computer — the rig\'s logs are '
        'separate. Attach it to a bug report.';
    if (!s.available) return 'The log could not be written. $tail';
    if (s.entries == 0) return 'No errors recorded. $tail';
    final noun = s.entries == 1 ? 'error' : 'errors';
    final session = s.sessionEntries > 0
        ? ' (${s.sessionEntries} since launch)'
        : '';
    return '${s.entries} $noun recorded$session. $tail';
  }
}
