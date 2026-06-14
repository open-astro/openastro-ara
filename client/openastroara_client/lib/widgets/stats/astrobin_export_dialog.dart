import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../models/stats/stats_target.dart';
import '../../state/stats/stats_targets_state.dart';
import '../../theme/ara_colors.dart';

/// §50.19 AstroBin acquisition export: pick a target, then open its AstroBin
/// acquisition CSV (`GET /api/v1/stats/export/astrobin?target=…`) in the browser
/// / save dialog. One row per imaged target, busiest first.
class AstrobinExportDialog extends ConsumerStatefulWidget {
  const AstrobinExportDialog({super.key});

  @override
  ConsumerState<AstrobinExportDialog> createState() => _AstrobinExportDialogState();
}

class _AstrobinExportDialogState extends ConsumerState<AstrobinExportDialog> {
  // Guards a second tap while a launch is in flight, and names the target whose
  // row is busy so only that row shows the spinner.
  String? _launching;

  Future<void> _export(String target) async {
    if (_launching != null) return;
    final api = ref.read(statsExportApiProvider);
    if (api == null) {
      _snack('No server connected.');
      return;
    }
    setState(() => _launching = target);
    try {
      final uri = Uri.tryParse(api.astrobinExportUrl(target));
      var ok = false;
      // Only ever launch http(s) — the URL is built from the server base, so an
      // allowlist stops a rogue server steering us into a javascript:/data: scheme.
      if (uri != null && (uri.isScheme('http') || uri.isScheme('https'))) {
        try {
          ok = await launchUrl(uri, mode: LaunchMode.externalApplication);
        } catch (_) {
          ok = false;
        }
      }
      if (!ok) _snack('Could not open the export.');
    } finally {
      if (mounted) setState(() => _launching = null);
    }
  }

  void _snack(String text) {
    if (!mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(text)));
  }

  @override
  Widget build(BuildContext context) {
    final async = ref.watch(statsTargetsProvider);
    final targets = async.asData?.value;

    return AlertDialog(
      title: const Text('Export to AstroBin'),
      content: SizedBox(width: 420, child: _content(context, async, targets)),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Close'),
        ),
      ],
    );
  }

  Widget _content(
    BuildContext context,
    AsyncValue<List<StatsTarget>?> async,
    List<StatsTarget>? targets,
  ) {
    if (async.isLoading && targets == null) {
      return const SizedBox(
          height: 120, child: Center(child: CircularProgressIndicator()));
    }
    if (async.hasError && targets == null) {
      return _hintWithRetry('Could not load targets.');
    }
    if (targets == null) {
      return const _Hint('Connect to a server to export acquisition data.');
    }
    if (targets.isEmpty) {
      return const _Hint('No imaged targets yet.');
    }
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        const Padding(
          padding: EdgeInsets.only(bottom: 8),
          child: Text(
            'Pick a target — its acquisition CSV opens for download.',
            style: TextStyle(fontSize: 12, color: AraColors.textSecondary),
          ),
        ),
        Flexible(
          child: ListView(
            shrinkWrap: true,
            children: [for (final t in targets) _row(context, t)],
          ),
        ),
      ],
    );
  }

  Widget _row(BuildContext context, StatsTarget t) {
    final busy = _launching == t.targetName;
    return ListTile(
      dense: true,
      contentPadding: EdgeInsets.zero,
      leading: const Icon(Icons.gps_fixed, color: AraColors.textSecondary, size: 18),
      title: Text(t.targetName),
      subtitle: Text(
        '${t.frameCount} frame${t.frameCount == 1 ? '' : 's'} · '
        '${t.integrationHours.toStringAsFixed(1)} h',
      ),
      trailing: busy
          ? const SizedBox(
              width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2))
          : const Icon(Icons.file_download, size: 18),
      enabled: _launching == null,
      onTap: () => unawaited(_export(t.targetName)),
    );
  }

  Widget _hintWithRetry(String message) {
    return Row(
      children: [
        Expanded(child: _Hint(message)),
        TextButton(
          onPressed: () =>
              unawaited(ref.read(statsTargetsProvider.notifier).refresh()),
          child: const Text('Retry'),
        ),
      ],
    );
  }
}

class _Hint extends StatelessWidget {
  const _Hint(this.message);

  final String message;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 12),
      child: Text(message, style: const TextStyle(color: AraColors.textSecondary)),
    );
  }
}
