import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/slew_target_body.dart';
import '../../services/tonight_sky_api.dart';
import '../../state/saved_server_state.dart';
import '../../state/sequencer/sequence_list_state.dart';
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
    // Read once, before the async.when: if tonightSkyProvider settles (empty)
    // while savedServersProvider is still loading, reading it inside the data
    // callback would briefly mis-report "no server". Watching it here makes the
    // empty-state advice deterministic.
    final hasServer = ref
        .watch(savedServersProvider)
        .maybeWhen(data: (list) => list.isNotEmpty, orElse: () => false);
    final theme = Theme.of(context);

    // Material (not a bare Container colour) so the rows' ListTile ink splashes
    // have a Material ancestor to paint on — a ColoredBox between ListTile and
    // Material would hide them.
    return Material(
      color: AraColors.bgPanel,
      child: SizedBox(
        width: width,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 16, 8, 8),
              child: Row(
                children: [
                  Expanded(
                    child: Text(
                      "Tonight's Sky",
                      style: theme.textTheme.titleSmall,
                    ),
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
                    // Distinguish "no server" (the provider returns [] immediately) from
                    // "connected, but nothing's up / no site set" — the advice differs.
                    return _Message(
                      message: hasServer
                          ? 'Nothing well-placed right now. Set your site location in '
                                'Settings → Safety → Site, then refresh.'
                          : 'Connect to a server to see Tonight\'s Sky.',
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
      ),
    );
  }
}

class _ObjectRow extends ConsumerStatefulWidget {
  final TonightSkyObject object;
  const _ObjectRow({required this.object});

  @override
  ConsumerState<_ObjectRow> createState() => _ObjectRowState();
}

class _ObjectRowState extends ConsumerState<_ObjectRow> {
  bool _busy = false;

  TonightSkyObject get _object => widget.object;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final mag = _object.magnitude;
    final magText = mag == null ? 'mag —' : 'mag ${mag.toStringAsFixed(1)}';
    // Max altitude moves into the subtitle so the trailing column has room for
    // the "add to sequence" action alongside the current-altitude readout.
    final subtitle =
        '${_typeLabel(_object.type)} · $magText · '
        'max ${_object.maxAltitudeDeg.toStringAsFixed(0)}°';
    // Watch (not read) so the autoDispose sequence API stays alive while the
    // panel is shown — a bare read would let it dispose (closing its Dio) before
    // an in-flight create() resolves.
    final canAdd = ref.watch(sequenceApiProvider) != null;
    return ListTile(
      dense: true,
      title: Text(_object.name, style: theme.textTheme.bodyMedium),
      subtitle: Text(
        subtitle,
        style: theme.textTheme.bodySmall?.copyWith(
          color: AraColors.textSecondary,
        ),
      ),
      trailing: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(
            '${_object.altitudeDeg.toStringAsFixed(0)}°',
            style: theme.textTheme.bodyMedium,
          ),
          const SizedBox(width: 4),
          _busy
              ? const SizedBox(
                  width: 18,
                  height: 18,
                  child: Padding(
                    padding: EdgeInsets.all(2),
                    child: CircularProgressIndicator(strokeWidth: 2),
                  ),
                )
              : IconButton(
                  iconSize: 18,
                  visualDensity: VisualDensity.compact,
                  tooltip: 'Add to a new sequence',
                  icon: const Icon(Icons.playlist_add),
                  onPressed: canAdd ? _addToSequence : null,
                ),
        ],
      ),
      // Recentre the atlas on this object. Its catalog id (e.g. "M31") resolves
      // via Aladin's Sesame lookup just like a typed search.
      onTap: () => ref.read(skyAtlasSearchProvider.notifier).set(_object.id),
    );
  }

  /// Create a new sequence named after this object containing a single slew to
  /// its coordinates, then surface the outcome via a SnackBar. Mirrors the
  /// `createSequenceFromTemplate` mounted/ref ordering.
  Future<void> _addToSequence() async {
    if (_busy) return;
    final api = ref.read(sequenceApiProvider);
    if (api == null) return;
    final messenger = ScaffoldMessenger.of(context);
    final name = _object.name;
    setState(() => _busy = true);
    try {
      final body = buildSlewTargetBody(
        raDeg: _object.raDeg,
        decDeg: _object.decDeg,
        targetName: name,
      );
      await api.create(name, body);
    } catch (e, st) {
      debugPrint('[planning] add-to-sequence failed: $e\n$st');
      if (mounted) {
        messenger.showSnackBar(
          const SnackBar(
            content: Text(
              "Couldn't add to a sequence. Check the connection and try again.",
            ),
            backgroundColor: AraColors.accentError,
          ),
        );
      }
      return;
    } finally {
      if (mounted) setState(() => _busy = false);
    }
    // Gate on mounted before touching ref: the row can scroll out of the
    // ListView (disposing it) during the create await, leaving ref defunct.
    if (!mounted) return;
    // Refresh the library so the new sequence shows up in the Sequencer tab.
    ref.invalidate(sequenceListProvider);
    messenger.showSnackBar(
      SnackBar(content: Text('Added "$name" to a new sequence.')),
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
            Text(
              message,
              textAlign: TextAlign.center,
              style: Theme.of(context).textTheme.bodySmall,
            ),
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
