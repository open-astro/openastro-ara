import 'package:flutter/material.dart';
import '../../util/friendly_error.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/server_maintenance_api.dart';
import '../../state/saved_server_state.dart';
import '../../theme/ara_colors.dart';

/// §33/§34 — restart the daemon from the app. Without this, recovering a wedged
/// server means finding a laptop and an SSH session, in the dark, at the rig.
///
/// Two verbs, deliberately distinct: **Restart now** interrupts whatever is
/// running (confirmed first), **Restart when idle** waits for the daemon to be
/// doing nothing, so it is safe to press mid-evening.
class DaemonRestartCard extends ConsumerStatefulWidget {
  const DaemonRestartCard({super.key});

  @override
  ConsumerState<DaemonRestartCard> createState() => _DaemonRestartCardState();
}

class _DaemonRestartCardState extends ConsumerState<DaemonRestartCard> {
  bool _busy = false;

  Future<void> _run(Future<void> Function(ServerMaintenanceApi) action,
      String successMessage) async {
    final server = ref.read(activeServerProvider);
    if (server == null) {
      return;
    }
    setState(() => _busy = true);
    final api = ServerMaintenanceApi(server);
    try {
      await action(api);
      if (mounted) {
        ScaffoldMessenger.of(context)
            .showSnackBar(SnackBar(content: Text(successMessage)));
      }
    } on Exception catch (e) {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(content: Text(friendlyError(e, action: 'restart your rig'))));
      }
    } finally {
      api.close();
      if (mounted) {
        setState(() => _busy = false);
      }
    }
  }

  Future<void> _restartNow() async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Restart the server now?'),
        content: const Text(
          'Any running sequence, capture or guiding session stops immediately. '
          'Ara reconnects on its own once the server is back (usually a '
          'few seconds).',
        ),
        actions: [
          TextButton(
              onPressed: () => Navigator.of(ctx).pop(false),
              child: const Text('Cancel')),
          FilledButton(
            style: FilledButton.styleFrom(backgroundColor: AraColors.accentBusy),
            onPressed: () => Navigator.of(ctx).pop(true),
            child: const Text('Restart now'),
          ),
        ],
      ),
    );
    if (confirmed ?? false) {
      await _run((api) => api.restart(), 'Restart requested — reconnecting…');
    }
  }

  @override
  Widget build(BuildContext context) {
    final connected = ref.watch(activeServerProvider) != null;
    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 12, 16, 12),
      child: Row(
        children: [
          const Icon(Icons.restart_alt, size: 18, color: AraColors.textSecondary),
          const SizedBox(width: 8),
          Expanded(
            child: Text(
              'Server maintenance',
              style: Theme.of(context).textTheme.bodyMedium,
            ),
          ),
          OutlinedButton(
            onPressed: !connected || _busy
                ? null
                : () => _run((api) => api.restartOnIdle(),
                    'Queued — the server restarts once it is idle.'),
            child: const Text('Restart when idle'),
          ),
          const SizedBox(width: 8),
          OutlinedButton(
            onPressed: !connected || _busy ? null : _restartNow,
            child: const Text('Restart now…'),
          ),
        ],
      ),
    );
  }
}
