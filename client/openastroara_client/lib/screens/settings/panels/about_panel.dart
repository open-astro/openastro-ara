import 'dart:developer' as developer;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:package_info_plus/package_info_plus.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../../app_version.dart';
import '../../../services/server_maintenance_api.dart';
import '../../../state/night_mode_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/settings/editable_field.dart';

const String _kRepoUrl = 'https://github.com/open-astro/openastro-ara';

/// App version read at runtime from `package_info_plus` so pubspec.yaml is the
/// single source of truth (same pattern as the help dialog's provider — kept
/// separate because that one is private to its library).
final aboutAppVersionProvider = FutureProvider<String>((ref) async {
  return formatFullVersion(await PackageInfo.fromPlatform());
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
    final dim = Theme.of(
      context,
    ).textTheme.bodySmall?.copyWith(color: AraColors.textSecondary);

    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        const Text(
          'About',
          style: TextStyle(
            color: AraColors.textPrimary,
            fontSize: 16,
            fontWeight: FontWeight.w600,
          ),
        ),
        const SizedBox(height: 12),
        Text(
          'OpenAstro Ara — the desktop client',
          style: Theme.of(
            context,
          ).textTheme.bodyMedium?.copyWith(color: AraColors.textPrimary),
        ),
        const SizedBox(height: 4),
        Text(
          'Version ${version.when(data: (v) => v, loading: () => '…', error: (_, _) => '(unknown)')}',
          style: dim,
        ),
        const SizedBox(height: 12),
        // Observing-session night display: red overlay/theme, toggleable via
        // the switch or the N hotkey; persisted across launches.
        _NightModeRow(),
        const SizedBox(height: 12),
        // The daemon is the half that actually runs the rig — its build is what
        // matters in a bug report, and it was previously invisible in the app.
        const _DaemonIdentity(),
        const SizedBox(height: 12),
        Text(
          'Forked from N.I.N.A. — Nighttime Imaging \'N\' Astronomy. '
          'Ara\'s app is licensed under the AGPL-3.0; the server under the MPL-2.0. '
          'Source, issues and discussions live on GitHub.',
          style: dim,
        ),
        const SizedBox(height: 16),
        Wrap(
          spacing: 8,
          runSpacing: 8,
          children: [
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
          ],
        ),
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
      applicationName: 'OpenAstro Ara',
      applicationVersion: version,
      applicationLegalese:
          'Client: AGPL-3.0 · Ara: MPL-2.0\nForked from N.I.N.A. (MPL-2.0)',
    );
  }

  Future<void> _openRepo(BuildContext context) async {
    final messenger = ScaffoldMessenger.of(context);
    final uri = Uri.parse(_kRepoUrl);
    try {
      final ok = await launchUrl(uri, mode: LaunchMode.externalApplication);
      if (!ok) {
        messenger.showSnackBar(
          const SnackBar(
            content: Text('Could not open the browser — $_kRepoUrl'),
          ),
        );
      }
    } catch (e, st) {
      developer.log('launchUrl failed', error: e, stackTrace: st);
      messenger.showSnackBar(
        const SnackBar(
          content: Text('Could not open the browser — $_kRepoUrl'),
        ),
      );
    }
  }
}

/// Settings → System → About — night mode toggle. Also togglable via the moon
/// button in the top bar and the `N` hotkey from anywhere in the app;
/// persisted across launches.
class _NightModeRow extends ConsumerWidget {
  const _NightModeRow();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final night = switch (ref.watch(nightModeProvider)) {
      AsyncData(:final value) => value,
      _ => false,
    };
    return SettingsSwitchRow(
      label: 'Night mode',
      hint: 'Red display for dark-site observing (N hotkey)',
      helpKey: 'app.night_mode',
      value: night,
      onChanged: (v) => ref.read(nightModeProvider.notifier).set(v),
    );
  }
}

/// Settings → System → About — the daemon half of "what am I running?".
class _DaemonIdentity extends ConsumerWidget {
  const _DaemonIdentity();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final dim = Theme.of(
      context,
    ).textTheme.bodySmall?.copyWith(color: AraColors.textSecondary);
    final async = ref.watch(daemonVersionsProvider);
    final text = async.when(
      loading: () => 'Server: reading…',
      error: (_, _) => 'Server: unavailable (not connected?)',
      data: (v) {
        if (v == null) {
          return 'Server: not connected';
        }
        final sha = v.daemonGitSha.isEmpty
            ? ''
            : ' (${v.daemonGitSha.substring(0, v.daemonGitSha.length.clamp(0, 7))})';
        final platform = [
          v.osRelease,
          v.osArch,
        ].where((p) => p.isNotEmpty).join(' · ');
        return 'Server: ${v.daemonVersion}$sha'
            '${platform.isEmpty ? '' : '\n$platform'}';
      },
    );
    return Text(text, style: dim);
  }
}
