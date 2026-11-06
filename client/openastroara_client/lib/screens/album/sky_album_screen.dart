/// The Sky Album (docs/specs/sky-album-v1.md, "Layout" and "Tonight's Sky
/// tie-in"): one Book at a time as a binder of cards, with a shelf of missing
/// cards well placed tonight. Shell-agnostic: the only shell behaviour is
/// [SkyAlbumScreen.onImageTarget].
library;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../services/dso_catalog_service.dart';
import '../../state/album/album_art.dart';
import '../../state/album/album_matching.dart';
import '../../state/album/album_state.dart';
import '../../state/saved_server_state.dart';
import '../../state/sky_atlas/dso_catalog_state.dart';
import '../../state/sky_atlas/tonight_sky_state.dart';
import '../../theme/ara_colors.dart';
import '../../util/friendly_error.dart';
import '../../widgets/album/album_card.dart';

/// The shelf shows at most this many missing cards.
const int albumShelfCap = 8;

/// The album's icon, shared by its title and its menu entry.
const IconData albumIcon = Icons.style_outlined;

/// Which of the Book's cards the grid shows.
enum AlbumFilter { all, collected, missing }

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
  /// The catalogs shown; none selected shows every catalog.
  Set<AlbumBook> _catalogs = const {};
  AlbumFilter _filter = AlbumFilter.all;
  Object? _refreshError;

  /// Whether this open's fetch has come back; until then an empty collection
  /// may only mean nothing has loaded.
  bool _synced = false;

  @override
  void initState() {
    super.initState();
    // Every open fetches what the rig changed since the newest row it served.
    WidgetsBinding.instance.addPostFrameCallback((_) => _refresh());
  }

  Future<void> _refresh() async {
    final server = ref.read(activeServerProvider);
    if (!mounted || server == null) return;
    try {
      await ref.read(albumFramesProvider.notifier).refresh(server.baseUrl);
      if (mounted) {
        setState(() {
          _refreshError = null;
          _synced = true;
        });
      }
    } catch (e) {
      if (mounted) setState(() => _refreshError = e);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l10n;
    final server = ref.watch(activeServerProvider);
    final catalog = ref.watch(bundledCatalogProvider).value ?? const <PlanningDso>[];
    final collection = ref.watch(albumFramesProvider).value;
    // Every catalog with none selected, else the selected ones; Messier first.
    final books = [
      for (final b in AlbumBook.values)
        if (_catalogs.isEmpty || _catalogs.contains(b)) b,
    ];
    final views = {for (final b in AlbumBook.values) b: ref.watch(albumBookProvider(b)).value};
    // Before a Book is matched its cards still show, all grey.
    final bookOf = <String, AlbumBook>{};
    final bookSize = <AlbumBook, int>{};
    final objects = <PlanningDso>[];
    for (final b in books) {
      final list = views[b]?.objects ?? bookObjects(catalog, b);
      bookSize[b] = list.length;
      for (final d in list) {
        bookOf[d.id] = b;
        objects.add(d);
      }
    }
    final stats = <String, CardStats>{
      for (final b in books) ...?views[b]?.stats,
    };
    final view = books.every((b) => views[b] != null) ? views : null;
    final manifest = ref.watch(albumManifestProvider).value ?? const <String, AlbumArt>{};

    AlbumCardData cardFor(PlanningDso d) {
      final book = bookOf[d.id]!;
      final n = (book == AlbumBook.messier ? d.messierNum : d.caldwellNum)!;
      final label = book == AlbumBook.messier ? 'M $n' : 'C $n';
      return AlbumCardData(
        dso: d,
        label: label,
        number: n,
        bookSize: bookSize[book]!,
        stats: stats[d.id],
        art: manifest[albumArtId(label)],
      );
    }

    final error = server == null
        ? l.albumNoServer
        : switch (_refreshError) {
            null => null,
            final e => l.albumLoadFailed(friendlyError(l, e)),
          };
    final noLights = _synced && (collection?.frames.isEmpty ?? false);
    // Without rows, or before the Book is matched against them, every card looks missing,
    // so a shelf would offer collected cards.
    final loaded = _synced || (collection?.frames.isNotEmpty ?? false);
    final shelf = loaded && view != null && _filter != AlbumFilter.collected
        ? _shelf(objects, stats)
        : const <PlanningDso>[];
    final shown = switch (_filter) {
      AlbumFilter.all => objects,
      AlbumFilter.collected => [
        for (final d in objects)
          if (stats.containsKey(d.id)) d,
      ],
      AlbumFilter.missing => [
        for (final d in objects)
          if (!stats.containsKey(d.id)) d,
      ],
    };
    final tiers = {for (final t in AlbumTier.values) t: stats.values.where((s) => s.tier == t).length};

    Widget card(PlanningDso d) => AlbumCard(cardFor(d), onAction: () => widget.onImageTarget(d));

    // Each catalog's count on its chip, so a card in an unselected catalog still shows.
    final catalogChips = Wrap(
      spacing: 8,
      runSpacing: 8,
      crossAxisAlignment: WrapCrossAlignment.center,
      children: [
        Text(l.albumCatalogs, style: const TextStyle(color: AraColors.textSecondary)),
        for (final b in AlbumBook.values)
          FilterChip(
            label: Text(l.albumBookCount(
                b == AlbumBook.messier ? l.albumBookMessier : l.albumBookCaldwell, views[b]?.collected ?? 0)),
            selected: _catalogs.contains(b),
            onSelected: (on) => setState(() => _catalogs = on ? {..._catalogs, b} : ({..._catalogs}..remove(b))),
          ),
      ],
    );

    return Scaffold(
      backgroundColor: AraColors.bgPrimary,
      appBar: AppBar(
        title: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            const Icon(albumIcon),
            const SizedBox(width: 8),
            Flexible(child: Text(l.albumTitle, overflow: TextOverflow.ellipsis)),
          ],
        ),
      ),
      body: CustomScrollView(
        slivers: [
          SliverPadding(
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 4),
            sliver: SliverToBoxAdapter(
              child: LayoutBuilder(
                builder: (context, box) {
                  final count = Text(
                    l.albumCollected(stats.length, objects.length),
                    style: Theme.of(context).textTheme.titleMedium,
                  );
                  final tierCounts = Wrap(
                    spacing: 16,
                    alignment: WrapAlignment.end,
                    children: [
                      for (final t in AlbumTier.values.reversed)
                        Text(
                          l.albumTierCount(albumTierName(context, t), tiers[t]!),
                          style: TextStyle(color: albumTierColor(t)),
                        ),
                    ],
                  );
                  final filter = SegmentedButton<AlbumFilter>(
                    segments: [
                      ButtonSegment(value: AlbumFilter.all, label: Text(l.albumFilterAll)),
                      ButtonSegment(value: AlbumFilter.collected, label: Text(l.albumFilterCollected)),
                      ButtonSegment(value: AlbumFilter.missing, label: Text(l.albumFilterMissing)),
                    ],
                    selected: {_filter},
                    showSelectedIcon: false,
                    onSelectionChanged: (s) => setState(() => _filter = s.single),
                  );
                  // Catalogs left and the filter right while they fit, the count and tier counts
                  // below; a narrow window or large text wraps each line instead of overflowing.
                  Widget line(List<Widget> children) => SizedBox(
                        width: double.infinity,
                        child: Wrap(
                          alignment: WrapAlignment.spaceBetween,
                          crossAxisAlignment: WrapCrossAlignment.center,
                          spacing: 16,
                          runSpacing: 8,
                          children: children,
                        ),
                      );
                  return Column(
                    children: [
                      line([catalogChips, filter]),
                      const SizedBox(height: 8),
                      line([count, tierCounts]),
                    ],
                  );
                },
              ),
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
                child: Text(
                  l.albumEmpty,
                  textAlign: TextAlign.center,
                  style: const TextStyle(color: AraColors.textSecondary),
                ),
              ),
            ),
          SliverPadding(
            // The last row clears the home indicator.
            padding: EdgeInsets.fromLTRB(16, 16, 16, 16 + MediaQuery.paddingOf(context).bottom),
            sliver: SliverGrid(
              gridDelegate: const SliverGridDelegateWithMaxCrossAxisExtent(
                maxCrossAxisExtent: 130,
                childAspectRatio: albumCardAspect,
                mainAxisSpacing: 10,
                crossAxisSpacing: 10,
              ),
              delegate: SliverChildBuilderDelegate((_, i) => card(shown[i]), childCount: shown.length),
            ),
          ),
        ],
      ),
    );
  }

  /// Tonight's Sky ranking filtered to this Book's missing cards, in ranker
  /// order, capped. Empty (so hidden) when the ranker has nothing.
  List<PlanningDso> _shelf(List<PlanningDso> objects, Map<String, CardStats> stats) {
    final ranked = ref.watch(tonightSkyProvider).value ?? const [];
    // By any designation: the ranker's row may carry the object under another id.
    final missing = <String, PlanningDso>{
      for (final d in objects)
        if (!stats.containsKey(d.id))
          for (final name in [d.id, ...d.aliases]) designationKey(name): d,
    };
    final out = <PlanningDso>[];
    for (final t in ranked) {
      final d = [t.id, ...t.aliases].map((n) => missing[designationKey(n)]).nonNulls.firstOrNull;
      if (d != null && !out.contains(d)) out.add(d);
      if (out.length == albumShelfCap) break;
    }
    return out;
  }
}
