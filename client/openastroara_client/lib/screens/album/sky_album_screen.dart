/// The Sky Album (docs/specs/sky-album-v1.md, "Layout" and "Tonight's Sky
/// tie-in"): one Book at a time as a binder of cards, with a shelf of missing
/// cards well placed tonight. Shell-agnostic: the only shell behaviour is
/// [SkyAlbumScreen.onImageTarget].
library;

import 'package:flutter/gestures.dart';
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
import '../../theme/ara_metrics.dart';
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

  /// Cards new or up a tier since the last open (only an open refreshes the
  /// cache, so the rows held before it are what the user last saw), by object id
  /// to the tier they had (null: missing). They sit on their own shelf for this open.
  final Map<String, AlbumTier?> _revealFrom = {};

  /// The [_revealFrom] cards whose reveal has played; they stay on the shelf, plain.
  final Set<String> _revealed = {};

  /// The cache was empty before this open's fetch: the whole collection is new.
  bool _firstOpen = false;

  @override
  void initState() {
    super.initState();
    // Every open fetches what the rig changed since the newest row it served.
    WidgetsBinding.instance.addPostFrameCallback((_) => _refresh());
  }

  Future<void> _refresh() async {
    final server = ref.read(activeServerProvider);
    if (!mounted || server == null) return;
    Future<Map<String, AlbumTier>> tiers() async => {
          for (final b in AlbumBook.values)
            for (final MapEntry(:key, :value) in (await ref.read(albumBookProvider(b).future)).stats.entries)
              key: ?value.tier,
        };
    try {
      final firstOpen = (await ref.read(albumFramesProvider.future)).frames.isEmpty;
      final before = await tiers();
      await ref.read(albumFramesProvider.notifier).refresh(server.baseUrl);
      final after = await tiers();
      if (mounted) {
        setState(() {
          _refreshError = null;
          _synced = true;
          _firstOpen = firstOpen;
          for (final MapEntry(:key, :value) in after.entries) {
            final was = before[key];
            if (was == null || value.index > was.index) _revealFrom[key] = was;
          }
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
    // Before a Book is matched its cards still show, all grey. An object in two Books
    // shows once, as its first Book's Card.
    final bookOf = <String, AlbumBook>{};
    final bookSize = <AlbumBook, int>{};
    final objects = <PlanningDso>[];
    for (final b in books) {
      final list = views[b]?.objects ?? bookObjects(catalog, b);
      bookSize[b] = list.length;
      for (final d in list) {
        if (bookOf.containsKey(d.id)) continue;
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
      final n = bookNumberOf(d, book)!;
      final label = bookLabel(d, book);
      return AlbumCardData(
        dso: d,
        label: label,
        number: n,
        bookSize: bookSize[book]!,
        stats: stats[d.id],
        // Its own Book's Art, else the picture another Book has of the same object.
        art: [book, ...AlbumBook.values]
            .where((b) => bookNumberOf(d, b) != null)
            .map((b) => manifest[albumArtId(bookLabel(d, b))])
            .nonNulls
            .firstOrNull,
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
    // The new-cards shelf: the catalogs shown, in Book order, and only cards that are
    // collected, so it hides under Missing. Its cards play their reveal once.
    final fresh = _filter == AlbumFilter.missing
        ? const <PlanningDso>[]
        : [
            for (final d in objects)
              if (_revealFrom.containsKey(d.id)) d,
          ];
    final newCards = fresh.where((d) => _revealFrom[d.id] == null).length;
    final changes = [
      if (newCards > 0) l.albumNewCards(newCards),
      if (fresh.length > newCards) l.albumTiersUp(fresh.length - newCards),
    ].join(' · ');
    Widget freshCard(PlanningDso d) => AlbumCard(
          cardFor(d),
          onAction: () => widget.onImageTarget(d),
          reveal: !_revealed.contains(d.id),
          revealFrom: _revealFrom[d.id],
          onRevealed: () => setState(() => _revealed.add(d.id)),
          news: _revealFrom[d.id] == null ? l.albumCardNew : l.albumCardTierUp,
        );
    // A titled row of cards above the grid: the new cards, then tonight's missing ones.
    List<Widget> shelfRow(Key key, String title, List<PlanningDso> cards, Widget Function(PlanningDso) build) => [
          SliverPadding(
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 6),
            sliver: SliverToBoxAdapter(child: Text(title, style: Theme.of(context).textTheme.titleSmall)),
          ),
          SliverToBoxAdapter(
            child: SizedBox(
              height: 130 / albumCardAspect,
              // A plain wheel scrolls the page, so a mouse drags the shelf sideways.
              child: ScrollConfiguration(
                behavior: ScrollConfiguration.of(context).copyWith(dragDevices: PointerDeviceKind.values.toSet()),
                child: ListView.separated(
                  key: key,
                  padding: const EdgeInsets.symmetric(horizontal: 16),
                  scrollDirection: Axis.horizontal,
                  itemCount: cards.length,
                  separatorBuilder: (_, _) => const SizedBox(width: 10),
                  itemBuilder: (_, i) => SizedBox(width: 130, child: build(cards[i])),
                ),
              ),
            ),
          ),
        ];

    // Simple Mode density (DESIGN.md, Two Densities Rule): 44 px controls, 14 px text.
    const headerText = kSimpleText;
    // Each catalog's count on its chip, so a card in an unselected catalog still shows.
    final catalogChips = Wrap(
      spacing: 8,
      runSpacing: 8,
      crossAxisAlignment: WrapCrossAlignment.center,
      children: [
        Text(l.albumCatalogs, style: headerText.copyWith(color: AraColors.textSecondary)),
        for (final b in AlbumBook.values)
          // 44 px drawn, not only hit: the chip is the target the eye aims at.
          SizedBox(
            height: 44,
            child: FilterChip(
              label: Text(l.albumBookCount(
                  switch (b) {
                    AlbumBook.messier => l.albumBookMessier,
                    AlbumBook.caldwell => l.albumBookCaldwell,
                    AlbumBook.rcw => l.albumBookRcw,
                  },
                  views[b]?.collected ?? 0)),
              selected: _catalogs.contains(b),
              labelStyle: headerText,
              materialTapTargetSize: MaterialTapTargetSize.shrinkWrap,
              padding: const EdgeInsets.symmetric(horizontal: 8),
              shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(22)),
              onSelected: (on) => setState(() => _catalogs = on ? {..._catalogs, b} : ({..._catalogs}..remove(b))),
            ),
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
                    style: headerText.copyWith(fontWeight: FontWeight.w600),
                  );
                  final tierCounts = Wrap(
                    spacing: 16,
                    alignment: WrapAlignment.end,
                    children: [
                      for (final t in AlbumTier.values.reversed)
                        Text(
                          l.albumTierCount(albumTierName(context, t), tiers[t]!),
                          style: headerText.copyWith(color: albumTierColor(t)),
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
                    style: const ButtonStyle(
                      // 40 px by default; one density step up draws it at 44.
                      visualDensity: VisualDensity(vertical: 1),
                      tapTargetSize: MaterialTapTargetSize.shrinkWrap,
                      textStyle: WidgetStatePropertyAll(TextStyle(fontSize: 14)),
                    ),
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
                    crossAxisAlignment: CrossAxisAlignment.start,
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
          if (fresh.isNotEmpty)
            ...shelfRow(const Key('album-shelf-new'), _firstOpen ? changes : l.albumSinceLastVisit(changes), fresh,
                freshCard),
          if (shelf.isNotEmpty)
            ...shelfRow(const Key('album-shelf-tonight'), l.albumShelfTitle(shelf.length), shelf, card),
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
