/// The Sky Album (docs/specs/sky-album-v1.md, "Layout" and "Tonight's Sky
/// tie-in"): one Book at a time as a binder of cards, with a shelf of missing
/// cards well placed tonight. Shell-agnostic: the only shell behaviour is
/// [SkyAlbumScreen.onImageTarget].
library;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../services/dso_catalog_service.dart';
import '../../state/album/album_matching.dart';
import '../../state/album/album_state.dart';
import '../../state/profile_cache_state.dart';
import '../../state/saved_server_state.dart';
import '../../state/sky_atlas/dso_catalog_state.dart';
import '../../state/sky_atlas/tonight_sky_state.dart';
import '../../theme/ara_colors.dart';
import '../../util/friendly_error.dart';
import '../../widgets/album/album_card.dart';

/// The shelf shows at most this many missing cards.
const int albumShelfCap = 8;

class SkyAlbumScreen extends ConsumerStatefulWidget {
  const SkyAlbumScreen({super.key, required this.onImageTarget});

  /// Fired from a card back's action, grid and shelf alike, with the card's
  /// object. The shell decides what imaging it means (Simple Mode opens the
  /// plan sheet with the target picked).
  final void Function(PlanningDso target) onImageTarget;

  @override
  ConsumerState<SkyAlbumScreen> createState() => _SkyAlbumScreenState();
}

class _SkyAlbumScreenState extends ConsumerState<SkyAlbumScreen> {
  AlbumBook _book = AlbumBook.messier;
  Object? _refreshError;

  @override
  void initState() {
    super.initState();
    // Reopening the album fetches what changed; the first open's build fetches everything.
    WidgetsBinding.instance.addPostFrameCallback((_) => _refreshIfCached());
  }

  Future<void> _refreshIfCached() async {
    final server = ref.read(activeServerProvider);
    if (!mounted || server == null) return;
    final provider = albumFramesProvider(server.baseUrl);
    if (!ref.read(provider).hasValue) return;
    try {
      await ref.read(provider.notifier).refresh();
    } catch (e) {
      if (mounted) setState(() => _refreshError = e);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l10n;
    final server = ref.watch(activeServerProvider);
    final catalog = ref.watch(bundledCatalogProvider).value ?? const <PlanningDso>[];
    final frames = server == null ? null : ref.watch(albumFramesProvider(server.baseUrl));
    final view = server == null ? null : ref.watch(albumBookProvider((server.baseUrl, _book))).value;
    // On a failed first fetch the Book still shows, all grey.
    final objects = view?.objects ?? bookObjects(catalog, _book);
    final stats = view?.stats ?? const <String, CardStats>{};
    final profiles = ref.watch(cachedProfilesProvider).value?.profiles ?? const [];

    AlbumCardData cardFor(PlanningDso d) {
      final s = stats[d.id];
      final n = _book == AlbumBook.messier ? d.messierNum : d.caldwellNum;
      return AlbumCardData(
        dso: d,
        label: _book == AlbumBook.messier ? 'M $n' : 'C $n',
        stats: s,
        thumbnailUrl: s == null || server == null ? null : '${server.baseUrl}/api/v1/frames/${s.bestFrameId}/thumbnail',
        profileName: s?.profileId == null ? '' : profiles.where((p) => p.id == s!.profileId).firstOrNull?.name ?? '',
      );
    }

    final error = server == null
        ? l.albumNoServer
        : switch (frames?.error ?? _refreshError) {
            null => null,
            final e => l.albumLoadFailed(friendlyError(l, e)),
          };
    final noLights = frames?.value?.isEmpty ?? false;
    final shelf = _shelf(objects, stats);
    final tiers = {for (final t in AlbumTier.values) t: stats.values.where((s) => s.tier == t).length};

    Widget card(PlanningDso d) => AlbumCard(cardFor(d), onAction: () => widget.onImageTarget(d));

    return Scaffold(
      backgroundColor: AraColors.bgPrimary,
      appBar: AppBar(
        title: Text(l.albumTitle),
        actions: [
          Padding(
            padding: const EdgeInsets.only(right: 12),
            child: SegmentedButton<AlbumBook>(
              segments: [
                ButtonSegment(value: AlbumBook.messier, label: Text(l.albumBookMessier)),
                ButtonSegment(value: AlbumBook.caldwell, label: Text(l.albumBookCaldwell)),
              ],
              selected: {_book},
              showSelectedIcon: false,
              onSelectionChanged: (s) => setState(() => _book = s.single),
            ),
          ),
        ],
      ),
      body: CustomScrollView(slivers: [
        SliverPadding(
          padding: const EdgeInsets.fromLTRB(16, 12, 16, 4),
          sliver: SliverToBoxAdapter(
            child: Wrap(spacing: 16, runSpacing: 4, crossAxisAlignment: WrapCrossAlignment.center, children: [
              Text(l.albumCollected(stats.length, objects.length), style: Theme.of(context).textTheme.titleMedium),
              for (final t in AlbumTier.values.reversed)
                Text(l.albumTierCount(albumTierName(context, t), tiers[t]!),
                    style: TextStyle(color: albumTierColor(t))),
            ]),
          ),
        ),
        if (error != null)
          SliverToBoxAdapter(
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 4),
              child: Align(
                alignment: Alignment.centerLeft,
                child: DecoratedBox(
                  decoration: BoxDecoration(
                    color: AraColors.bgPanel,
                    borderRadius: BorderRadius.circular(16),
                    border: Border.all(color: AraColors.accentError),
                  ),
                  child: Padding(
                    padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
                    child: Text(error, style: const TextStyle(color: AraColors.errorText)),
                  ),
                ),
              ),
            ),
          ),
        if (shelf.isNotEmpty) ...[
          SliverPadding(
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 6),
            sliver: SliverToBoxAdapter(
              child: Text(l.albumShelfTitle(shelf.length), style: Theme.of(context).textTheme.titleSmall),
            ),
          ),
          SliverToBoxAdapter(
            child: SizedBox(
              height: 130 / albumCardAspect,
              child: ListView.separated(
                padding: const EdgeInsets.symmetric(horizontal: 16),
                scrollDirection: Axis.horizontal,
                itemCount: shelf.length,
                separatorBuilder: (_, _) => const SizedBox(width: 10),
                itemBuilder: (_, i) => SizedBox(width: 130, child: card(shelf[i])),
              ),
            ),
          ),
        ],
        if (noLights)
          SliverPadding(
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 0),
            sliver: SliverToBoxAdapter(
              child: Text(l.albumEmpty,
                  textAlign: TextAlign.center, style: const TextStyle(color: AraColors.textSecondary)),
            ),
          ),
        SliverPadding(
          padding: const EdgeInsets.all(16),
          sliver: SliverGrid(
            gridDelegate: const SliverGridDelegateWithMaxCrossAxisExtent(
              maxCrossAxisExtent: 130,
              childAspectRatio: albumCardAspect,
              mainAxisSpacing: 10,
              crossAxisSpacing: 10,
            ),
            delegate: SliverChildBuilderDelegate((_, i) => card(objects[i]), childCount: objects.length),
          ),
        ),
      ]),
    );
  }

  /// Tonight's Sky ranking filtered to this Book's missing cards, in ranker
  /// order, capped. Empty (so hidden) when the ranker has nothing.
  List<PlanningDso> _shelf(List<PlanningDso> objects, Map<String, CardStats> stats) {
    final ranked = ref.watch(tonightSkyProvider).value ?? const [];
    final missing = {for (final d in objects) if (!stats.containsKey(d.id)) d.id: d};
    final out = <PlanningDso>[];
    for (final t in ranked) {
      final d = missing.remove(t.id);
      if (d != null) out.add(d);
      if (out.length == albumShelfCap) break;
    }
    return out;
  }
}
