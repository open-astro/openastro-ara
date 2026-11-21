import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../app_version.dart';
import '../l10n/l10n.dart';
import '../theme/ara_colors.dart';
import '../version/ara_version.dart';

/// A one-line warning when the server and this app come from different X.Y
/// release lines (#2093): "Server 1.2.0 and app 1.1.0 differ; update both
/// from the same release." It never blocks anything. Nothing shows when
/// [serverVersion] is missing or not an Ara version (an old daemon reports
/// `1.0.0.0`). The app's side is [appVersionProvider] ([kAraAppVersion]).
class VersionMismatchWarning extends ConsumerWidget {
  const VersionMismatchWarning({super.key, required this.serverVersion});

  /// The server's version as it reports it, e.g. `1.0.0-beta.1`.
  final String? serverVersion;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final pair = mismatchedVersions(
      ref.watch(appVersionProvider),
      serverVersion,
    );
    if (pair == null) {
      return const SizedBox.shrink();
    }
    return _Warning(
      text: context.l10n.versionMismatchWarning(
        pair.server.display,
        pair.app.display,
      ),
    );
  }
}

class _Warning extends StatelessWidget {
  const _Warning({required this.text});

  final String text;

  @override
  Widget build(BuildContext context) {
    return Padding(
      key: const Key('version-mismatch-warning'),
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Icon(
            Icons.warning_amber_rounded,
            size: 16,
            color: AraColors.accentWarning,
          ),
          const SizedBox(width: 6),
          Expanded(
            child: Text(
              text,
              style: Theme.of(context).textTheme.bodySmall
                  ?.copyWith(color: AraColors.accentWarning),
            ),
          ),
        ],
      ),
    );
  }
}
