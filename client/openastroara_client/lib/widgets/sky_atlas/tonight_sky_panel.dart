import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/tonight_sky_api.dart';
import '../../state/sky_atlas/sky_atlas_state.dart';
import '../../state/sky_atlas/tonight_sky_state.dart';
import '../../theme/ara_colors.dart';

/// §36/§25.5 Tonight's Sky — a ranked side list of the best-placed curated
/// objects for the active profile's site right now. Tapping one recentres the
/// embedded Aladin atlas on it (via [skyAtlasSearchProvider], which the
/// `AladinView` listens to). Shown in the Planning tab when the view mode is
/// Tonight's Sky.
class TonightSkyPanel extends ConsumerWidget {
  const TonightSkyPanel({super.key});

  static const double width = 300;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(tonightSkyProvider);
    final theme = Theme.of(context);

    return Container(
      width: width,
      color: AraColors.bgPanel,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 16, 8, 8),
            child: Row(
              children: [
                Expanded(
                  child: Text("Tonight's Sky", style: theme.textTheme.titleSmall),
                ),
                IconButton(
                  tooltip: 'Refresh',
                  icon: const Icon(Icons.refresh, size: 18),
                  onPressed: () => ref.invalidate(tonightSkyProvider),
                ),
              ],
            ),
          ),
          Expanded(
            child: async.when(
              loading: () => const Center(child: CircularProgressIndicator()),
              error: (e, _) => _Message(
                message: 'Could not load Tonight\'s Sky.',
                onRetry: () => ref.invalidate(tonightSkyProvider),
              ),
              data: (objects) {
                if (objects.isEmpty) {
                  return const _Message(
                    message: 'Nothing to show. Connect to a server and set your site location in '
                        'Settings → Safety → Site, then refresh.',
                  );
                }
                return ListView.builder(
                  padding: const EdgeInsets.only(bottom: 12),
                  itemCount: objects.length,
                  itemBuilder: (_, i) => _ObjectRow(object: objects[i]),
                );
              },
            ),
          ),
        ],
      ),
    );
  }
}

class _ObjectRow extends ConsumerWidget {
  final TonightSkyObject object;
  const _ObjectRow({required this.object});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final subtitle = '${_typeLabel(object.type)} · mag ${object.magnitude.toStringAsFixed(1)}';
    return ListTile(
      dense: true,
      title: Text(object.name, style: theme.textTheme.bodyMedium),
      subtitle: Text(subtitle,
          style: theme.textTheme.bodySmall?.copyWith(color: AraColors.textSecondary)),
      trailing: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        crossAxisAlignment: CrossAxisAlignment.end,
        children: [
          Text('${object.altitudeDeg.toStringAsFixed(0)}°',
              style: theme.textTheme.bodyMedium),
          Text('max ${object.maxAltitudeDeg.toStringAsFixed(0)}°',
              style: theme.textTheme.labelSmall?.copyWith(color: AraColors.textDisabled)),
        ],
      ),
      // Recentre the atlas on this object. Its catalog id (e.g. "M31") resolves
      // via Aladin's Sesame lookup just like a typed search.
      onTap: () => ref.read(skyAtlasSearchProvider.notifier).set(object.id),
    );
  }

  static String _typeLabel(String t) => switch (t) {
        'galaxy' => 'Galaxy',
        'nebula' => 'Nebula',
        'cluster' => 'Cluster',
        '' => 'Object',
        _ => t[0].toUpperCase() + t.substring(1),
      };
}

class _Message extends StatelessWidget {
  final String message;
  final VoidCallback? onRetry;
  const _Message({required this.message, this.onRetry});

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(message,
                textAlign: TextAlign.center,
                style: Theme.of(context).textTheme.bodySmall),
            if (onRetry != null) ...[
              const SizedBox(height: 12),
              TextButton(onPressed: onRetry, child: const Text('Retry')),
            ],
          ],
        ),
      ),
    );
  }
}
