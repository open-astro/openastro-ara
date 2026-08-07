import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../services/storage_space_api.dart';
import '../../../state/saved_server_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../util/friendly_error.dart';
import '../../../widgets/settings/settings_row.dart';

/// §50 Stats maintenance. The Stats views (and the image library) are fed by
/// the rig's frame catalog; in the take-home-drive world the connected disk
/// changes, and the catalog can describe a drive that left. This panel owns
/// the honest reset: wipe the catalog and re-ingest from whatever store is
/// mounted right now.
class SessionStatsPanel extends ConsumerStatefulWidget {
  const SessionStatsPanel({super.key});

  @override
  ConsumerState<SessionStatsPanel> createState() => _SessionStatsPanelState();
}

class _SessionStatsPanelState extends ConsumerState<SessionStatsPanel> {
  bool _busy = false;
  String? _lastOutcome;

  Future<void> _rebuild() async {
    final server = ref.read(activeServerProvider);
    final messenger = ScaffoldMessenger.of(context);
    if (server == null) {
      messenger.showSnackBar(
          const SnackBar(content: Text('Not connected to a server.')));
      return;
    }
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => _ConfirmRebuildDialog(),
    );
    if (confirmed != true || !mounted) return;
    setState(() {
      _busy = true;
      _lastOutcome = null;
    });
    final dio = Dio(BaseOptions(
      baseUrl: server.baseUrl,
      connectTimeout: const Duration(seconds: 3),
      // Re-ingesting a large drive takes real time.
      receiveTimeout: const Duration(minutes: 10),
    ));
    try {
      final res = await dio
          .post<Map<String, dynamic>>('/api/v1/stats/rebuild-catalog');
      final data = res.data ?? const <String, dynamic>{};
      final cleared = (data['frames_cleared'] as num?)?.toInt() ?? 0;
      final recovered = (data['frames_recovered'] as num?)?.toInt() ?? 0;
      final rescanned = data['rescanned'] == true;
      if (!mounted) return;
      ref.invalidate(storageSpaceProvider);
      setState(() => _lastOutcome = rescanned
          ? 'Cleared $cleared cataloged frames; found $recovered on the '
              'connected drive. Stats now describe this disk.'
          : 'Cleared $cleared cataloged frames, but the drive couldn\'t be '
              'scanned (${data['skip_reason'] ?? 'unknown'}) — connect a '
              'store and use "Find frames on disk".');
    } on DioException catch (e) {
      if (!mounted) return;
      final body = e.response?.data;
      setState(() => _lastOutcome = body is Map && body['detail'] != null
          ? 'Couldn\'t rebuild: ${body['detail']}'
          : friendlyError(e, action: 'rebuild the stats catalog'));
    } finally {
      dio.close();
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Statistics'),
        Text(
          'Every chart in the Stats tab — and the image library — is built '
          'from the frame catalog on your rig. The catalog normally follows '
          'your imaging automatically; these tools are for when it and '
          'reality disagree.',
          style: theme.textTheme.bodySmall
              ?.copyWith(color: AraColors.textSecondary),
        ),
        const SizedBox(height: 20),
        const SettingsSectionHeader('Rebuild from the connected drive'),
        Text(
          'Clears every cataloged frame and session, then re-reads whatever '
          'store drive is mounted right now — afterwards, stats describe '
          'exactly what\'s on this disk. Use it after swapping drives, or '
          'whenever the numbers feel stale. Your FITS files are never '
          'touched; ratings and tags on cleared entries are lost.',
          style: theme.textTheme.bodySmall
              ?.copyWith(color: AraColors.textSecondary),
        ),
        const SizedBox(height: 12),
        Align(
          alignment: Alignment.centerLeft,
          child: FilledButton.icon(
            onPressed: _busy ? null : _rebuild,
            icon: _busy
                ? const SizedBox(
                    width: 14,
                    height: 14,
                    child: CircularProgressIndicator(strokeWidth: 2))
                : const Icon(Icons.restart_alt, size: 16),
            label: const Text('Clear & rebuild stats…'),
          ),
        ),
        if (_lastOutcome != null) ...[
          const SizedBox(height: 12),
          Text(_lastOutcome!, style: theme.textTheme.bodySmall),
        ],
      ],
    );
  }
}

/// The destructive confirm — spells out exactly what is and isn't lost.
class _ConfirmRebuildDialog extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: const Text('Clear and rebuild the catalog?'),
      content: const Text(
          'Every cataloged frame and session is deleted, then the connected '
          'store drive is re-read from scratch.\n\n'
          'Your FITS files are NOT touched. Ratings, tags, and history for '
          'frames not on the connected drive are permanently lost, and the '
          'backup mirror will treat re-found frames as new.'),
      actions: [
        TextButton(
            onPressed: () => Navigator.of(context).pop(false),
            child: const Text('Cancel')),
        FilledButton(
          style:
              FilledButton.styleFrom(backgroundColor: AraColors.accentBusy),
          onPressed: () => Navigator.of(context).pop(true),
          child: const Text('Clear & rebuild'),
        ),
      ],
    );
  }
}
