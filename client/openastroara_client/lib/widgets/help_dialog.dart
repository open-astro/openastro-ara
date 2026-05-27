import 'dart:developer' as developer;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:package_info_plus/package_info_plus.dart';
import 'package:url_launcher/url_launcher.dart';

import '../state/saved_server_state.dart';
import '../theme/ara_colors.dart';

/// §54 Help / Report-a-bug dialog. Surfaces the app version + active server
/// + a "Copy diagnostics" button for pasting into a GitHub Issue. Phase 12i
/// covers the minimal surface — later phases extend the diagnostic payload
/// with daemon health (`/api/v1/server/info`), recent log tail, and last
/// sequence state snapshot.
Future<void> showHelpDialog(BuildContext context) async {
  await showDialog<void>(
    context: context,
    builder: (_) => const _HelpDialog(),
  );
}

const String _kRepoUrl = 'https://github.com/open-astro/openastro-ara';

/// App version read at runtime from `package_info_plus` so pubspec.yaml is
/// the single source of truth. FutureProvider so the async fetch is cached
/// across rebuilds of the help dialog.
final _appVersionProvider = FutureProvider<String>((ref) async {
  final info = await PackageInfo.fromPlatform();
  // Format: `<version>+<build>` (e.g. `0.0.1+1`). Empty build number is
  // skipped so the trailing `+` doesn't dangle.
  return info.buildNumber.isEmpty
      ? info.version
      : '${info.version}+${info.buildNumber}';
});

class _HelpDialog extends ConsumerWidget {
  const _HelpDialog();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final servers = ref.watch(savedServersProvider);
    final appVersion = ref.watch(_appVersionProvider);
    final activeServer = servers.maybeWhen(
      // Most-recently-saved entry is the de-facto "active" server; the
      // saved-server list is append-ordered (oldest first), so .last picks
      // the latest handshake-confirmed server.
      data: (list) => list.isEmpty ? '— (none saved)' : list.last.toString(),
      orElse: () => '—',
    );

    return AlertDialog(
      title: const Text('Help / Report a bug'),
      content: SizedBox(
        width: 480,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const Text(
              'Found a bug or have a question? Open an issue on GitHub. '
              'The Copy diagnostics button below puts the build info on your '
              'clipboard — paste it into the issue body so we have context.',
            ),
            const SizedBox(height: 16),
            _DiagnosticRow(
              label: 'App version',
              value: appVersion.maybeWhen(
                data: (v) => v,
                orElse: () => '(loading...)',
              ),
            ),
            _DiagnosticRow(label: 'Active server', value: activeServer),
            _DiagnosticRow(
              label: 'Saved servers',
              value: '${servers.value?.length ?? 0}',
            ),
            const SizedBox(height: 16),
            const Text(
              'Useful links',
              style: TextStyle(fontWeight: FontWeight.w600),
            ),
            const SizedBox(height: 4),
            _LinkRow(
              url: '$_kRepoUrl/issues',
              label: '• $_kRepoUrl/issues  (file a new issue)',
            ),
            _LinkRow(
              url: '$_kRepoUrl/wiki',
              label: '• $_kRepoUrl/wiki  (docs + troubleshooting)',
            ),
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Close'),
        ),
        Consumer(builder: (context, ref, _) {
          return FilledButton.icon(
            onPressed: () => _copyDiagnostics(context, ref),
            icon: const Icon(Icons.copy, size: 16),
            label: const Text('Copy diagnostics'),
          );
        }),
      ],
    );
  }

  Future<void> _copyDiagnostics(BuildContext context, WidgetRef ref) async {
    final servers = ref.read(savedServersProvider);
    final activeServer = servers.maybeWhen(
      data: (list) => list.isEmpty ? '(none)' : list.last.toString(),
      orElse: () => '(unknown)',
    );
    // Re-fetch package info inline so the diagnostics text always has the
    // freshest version even if the dialog opened before the async resolved.
    final info = await PackageInfo.fromPlatform();
    final version = info.buildNumber.isEmpty
        ? info.version
        : '${info.version}+${info.buildNumber}';
    final payload = '''
OpenAstroAra diagnostics:
  app version: $version
  active server: $activeServer
  saved servers: ${servers.value?.length ?? 0}

Steps to reproduce:
  1.
  2.
  3.

Expected behavior:

Actual behavior:
''';

    try {
      await Clipboard.setData(ClipboardData(text: payload));
    } on PlatformException catch (e, st) {
      developer.log('Failed to copy diagnostics',
          name: 'openastroara.help_dialog', error: e, stackTrace: st);
      if (!context.mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(
          content: Text('Failed to copy diagnostics to clipboard.'),
        ),
      );
      return;
    }
    if (!context.mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(
      const SnackBar(content: Text('Diagnostics copied to clipboard.')),
    );
  }
}

class _LinkRow extends StatelessWidget {
  final String url;
  final String label;
  const _LinkRow({required this.url, required this.label});

  @override
  Widget build(BuildContext context) {
    return InkWell(
      onTap: () => _openUrl(context, url),
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: 2),
        child: Text(
          label,
          style: Theme.of(context).textTheme.bodySmall?.copyWith(
                color: AraColors.selectionBg,
                decoration: TextDecoration.underline,
              ),
        ),
      ),
    );
  }

  Future<void> _openUrl(BuildContext context, String url) async {
    final uri = Uri.parse(url);
    try {
      final ok = await launchUrl(uri, mode: LaunchMode.externalApplication);
      if (!ok && context.mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text('Could not open $url')),
        );
      }
    } catch (e, st) {
      developer.log('launchUrl failed',
          name: 'openastroara.help_dialog', error: e, stackTrace: st);
      if (!context.mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text('Could not open $url')),
      );
    }
  }
}

class _DiagnosticRow extends StatelessWidget {
  final String label;
  final String value;
  const _DiagnosticRow({required this.label, required this.value});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Row(
        children: [
          SizedBox(
            width: 140,
            child: Text(
              label,
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                    color: AraColors.textSecondary,
                  ),
            ),
          ),
          Expanded(
            child: Text(value,
                style: Theme.of(context).textTheme.bodyMedium),
          ),
        ],
      ),
    );
  }
}
