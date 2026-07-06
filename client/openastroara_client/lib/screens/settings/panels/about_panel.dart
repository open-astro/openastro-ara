import 'dart:developer' as developer;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:package_info_plus/package_info_plus.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../../theme/ara_colors.dart';

const String _kRepoUrl = 'https://github.com/open-astro/openastro-ara';

/// App version read at runtime from `package_info_plus` so pubspec.yaml is the
/// single source of truth (same pattern as the help dialog's provider — kept
/// separate because that one is private to its library).
final aboutAppVersionProvider = FutureProvider<String>((ref) async {
  final info = await PackageInfo.fromPlatform();
  return info.buildNumber.isEmpty
      ? info.version
      : '${info.version}+${info.buildNumber}';
});

/// Settings → System → About: what this app is, its licence, the source repo,
/// and the **open-source licences** of every bundled Dart/Flutter package —
/// served by Flutter's own [LicenseRegistry] via [showLicensePage], so the
/// notices ship with the binary and stay correct as dependencies change (no
/// generated file to forget; the daemon's `3rd-party-licenses.txt` sibling is
/// generated at build time because .NET has no equivalent registry).
class AboutPanel extends ConsumerWidget {
  const AboutPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final version = ref.watch(aboutAppVersionProvider);
    final dim = Theme.of(context)
        .textTheme
        .bodySmall
        ?.copyWith(color: AraColors.textSecondary);

    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        const Text('About',
            style: TextStyle(
                color: AraColors.textPrimary,
                fontSize: 16,
                fontWeight: FontWeight.w600)),
        const SizedBox(height: 12),
        Text(
          'OpenAstro Ara — WILMA (the desktop client)',
          style: Theme.of(context)
              .textTheme
              .bodyMedium
              ?.copyWith(color: AraColors.textPrimary),
        ),
        const SizedBox(height: 4),
        Text(
          'Version ${version.when(data: (v) => v, loading: () => '…', error: (_, _) => '(unknown)')}',
          style: dim,
        ),
        const SizedBox(height: 12),
        Text(
          'Forked from N.I.N.A. — Nighttime Imaging \'N\' Astronomy. '
          'The client is licensed under the AGPL-3.0; the daemon under the MPL-2.0. '
          'Source, issues and discussions live on GitHub.',
          style: dim,
        ),
        const SizedBox(height: 16),
        Wrap(spacing: 8, runSpacing: 8, children: [
          OutlinedButton.icon(
            icon: const Icon(Icons.description_outlined, size: 16),
            label: const Text('Open-source licenses'),
            onPressed: () => _showLicenses(context, version.value),
          ),
          OutlinedButton.icon(
            icon: const Icon(Icons.open_in_new, size: 16),
            label: const Text('Source on GitHub'),
            onPressed: () => _openRepo(context),
          ),
        ]),
        const SizedBox(height: 16),
        Text(
          'The licenses page lists every Dart/Flutter package bundled into this '
          'build, with its full license text — collected automatically by the '
          'Flutter tooling at compile time.',
          style: dim,
        ),
      ],
    );
  }

  void _showLicenses(BuildContext context, String? version) {
    // Flutter's built-in licenses page over LicenseRegistry — every bundled
    // package's LICENSE is registered by the build tooling automatically.
    showLicensePage(
      context: context,
      applicationName: 'OpenAstro Ara (WILMA)',
      applicationVersion: version,
      applicationLegalese:
          'Client: AGPL-3.0 · Daemon: MPL-2.0\nForked from N.I.N.A. (MPL-2.0)',
    );
  }

  Future<void> _openRepo(BuildContext context) async {
    final messenger = ScaffoldMessenger.of(context);
    final uri = Uri.parse(_kRepoUrl);
    try {
      final ok = await launchUrl(uri, mode: LaunchMode.externalApplication);
      if (!ok) {
        messenger.showSnackBar(
            const SnackBar(content: Text('Could not open the browser — $_kRepoUrl')));
      }
    } catch (e, st) {
      developer.log('launchUrl failed', error: e, stackTrace: st);
      messenger.showSnackBar(
          const SnackBar(content: Text('Could not open the browser — $_kRepoUrl')));
    }
  }
}
