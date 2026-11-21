import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../services/dso_catalog_service.dart' show objectTitle;
import '../../services/tonight_sky_api.dart';
import '../../state/sequencer/sequence_list_state.dart'
    show sequenceRunStateProvider;
import '../../state/simple_messages_state.dart';
import '../../state/simple_run_state.dart';
import '../../state/sky_atlas/planetarium_selection_state.dart';
import '../../state/sky_atlas/tonight_sky_state.dart';
import '../../state/sky_atlas/tonights_best_list_state.dart';
import '../../state/ws/ws_providers.dart' show serverLinkUpProvider;
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../widgets/emergency_stop_button.dart';
import '../../state/settings/site_settings_state.dart';
import 'simple_plan_panel.dart' show simpleSelectionOf;
import 'simple_shell.dart'
    show SimpleSegments, kSimpleStopSize, simpleRailScale;

String _minHhmm(int m) =>
    '${(m ~/ 60).toString().padLeft(2, '0')}:${(m % 60).toString().padLeft(2, '0')}';

String _hhmm(DateTime t) {
  final l = t.toLocal();
  return '${l.hour.toString().padLeft(2, '0')}:${l.minute.toString().padLeft(2, '0')}';
}

/// Tonight's Best (#1900): the ranker's list with sort, filters, markers and
/// Image this, pushed over the shell like Monitor. It renders
/// [tonightsBestListProvider] and holds no sort, filter or marker logic.
class TonightsBestScreen extends ConsumerWidget {
  const TonightsBestScreen({
    super.key,
    required this.onImage,
    required this.onBack,
    this.onSearch,
    this.onSetSite,
  });

  /// Image this with no plan running: the shell closes this page and opens
  /// the plan sheet on [TonightSkyObject].
  final ValueChanged<TonightSkyObject> onImage;
  final VoidCallback onBack;

  /// The empty states' actions (Search, Set site); the shell wires both.
  final VoidCallback? onSearch;
  final VoidCallback? onSetSite;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = context.l10n;
    final list = ref.watch(tonightsBestListProvider);
    final ranked = ref.watch(tonightsBestProvider);
    final choices = ref.watch(tonightsBestChoicesProvider);
    final site = ref.watch(siteSettingsProvider).value ?? const SiteSettings();
    final noSite = site.latitudeDeg == 0 && site.longitudeDeg == 0;
    final linkUp = ref.watch(serverLinkUpProvider);
    final rows = list.value;
    // Hide the controls when there is nothing to sort: no site, or nothing
    // well placed (the list after the never-rises cut, not the ranker's).
    final showControls =
        !noSite && (rows == null || rows.isNotEmpty || choices.filtered);
    final empty = switch ((noSite, list)) {
      (true, _) => _Empty(
        key: const Key('best-empty-site'),
        title: l10n.bestNoSiteTitle,
        body: l10n.bestNoSiteBody,
        action: l10n.bestSetSite,
        actionKey: 'best-set-site',
        onAction: onSetSite,
        amber: true,
      ),
      (_, AsyncData(:final value)) when value.isEmpty && !choices.filtered =>
        _Empty(
          key: const Key('best-empty-none'),
          title: l10n.bestNoneTitle,
          body: l10n.bestNoneBody,
          action: l10n.bestSearch,
          actionKey: 'best-search',
          onAction: onSearch,
        ),
      (_, AsyncData(:final value)) when value.isEmpty => _Empty(
        key: const Key('best-empty-match'),
        title: l10n.bestNoMatchTitle,
        body: l10n.bestNoMatchBody,
        action: l10n.bestClearFilters,
        actionKey: 'best-clear-filters',
        onAction: ref.read(tonightsBestChoicesProvider.notifier).clearFilters,
      ),
      (_, AsyncError()) => _Empty(
        key: const Key('best-error'),
        title: l10n.bestLoadFailed,
        action: l10n.bestRetry,
        actionKey: 'best-retry',
        onAction: () => ref.invalidate(tonightsBestProvider),
      ),
      (_, AsyncData()) => null,
      _ => const _Skeleton(),
    };
    final stop = kSimpleStopSize * simpleRailScale(context);
    // One scrolling page: on a phone the controls would leave the rows no room.
    return Scaffold(
      key: const Key('best-screen'),
      backgroundColor: AraColors.bgPrimary,
      body: SafeArea(
        child: Padding(
          padding: const EdgeInsets.all(AraSpace.s12),
          child: Stack(
            children: [
              Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  _Header(onBack: onBack),
                  Expanded(
                    child: ListView(
                      key: const Key('best-list'),
                      padding: EdgeInsets.only(bottom: stop + AraSpace.s12),
                      children: [
                        // Blocks sit s8 apart, as the rows do.
                        if (!linkUp)
                          const Padding(
                            padding: EdgeInsets.only(bottom: AraSpace.s8),
                            child: _OfflineNote(),
                          ),
                        if (showControls)
                          Padding(
                            padding: const EdgeInsets.only(bottom: AraSpace.s8),
                            child: _Controls(
                              choices: choices,
                              fieldKnown: fieldKnown(ranked.value ?? const []),
                              shown: rows?.length,
                              total: ranked.value
                                  ?.where((o) => o.maxAltitudeDeg > 0)
                                  .length,
                            ),
                          ),
                        ?empty,
                        if (!noSite && empty == null)
                          for (final r in rows!)
                            Padding(
                              padding: const EdgeInsets.only(
                                bottom: AraSpace.s8,
                              ),
                              child: _Row(row: r, onImage: onImage),
                            ),
                      ],
                    ),
                  ),
                ],
              ),
              // The deck keeps STOP in reach on every page of Simple Mode.
              Positioned(
                right: 0,
                bottom: 0,
                width: stop,
                height: stop,
                child: const EmergencyStopButton(compact: true),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _Header extends ConsumerWidget {
  const _Header({required this.onBack});

  final VoidCallback onBack;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = context.l10n;
    // The build instant, as the list's own clock: "updated" is when it was
    // ranked (a tick or Refresh rebuilds this).
    ref.watch(tonightsBestProvider);
    final updated = _hhmm(ref.read(clockProvider)());
    return Row(
      spacing: AraSpace.s8,
      children: [
        TextButton.icon(
          key: const Key('best-back'),
          onPressed: onBack,
          icon: const Icon(Icons.arrow_back, size: 20),
          label: Text(l10n.bestBack),
          style: TextButton.styleFrom(minimumSize: const Size(44, 44)),
        ),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                l10n.bestTitle,
                style: kSimpleText.copyWith(
                  fontSize: AraFontSize.simpleHeading,
                ),
              ),
              Text(
                l10n.bestSubtitle(updated),
                key: const Key('best-updated'),
                style: kSimpleCaption,
              ),
            ],
          ),
        ),
        IconButton(
          key: const Key('best-refresh'),
          tooltip: l10n.bestRefresh,
          constraints: const BoxConstraints(minWidth: 44, minHeight: 44),
          onPressed: () => ref.invalidate(tonightsBestProvider),
          icon: const Icon(Icons.refresh),
        ),
      ],
    );
  }
}

/// No rig: the list shows in full and planning works offline (ADR 0004).
class _OfflineNote extends StatelessWidget {
  const _OfflineNote();

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    return Column(
      key: const Key('best-offline'),
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          spacing: AraSpace.s8,
          children: [
            const Icon(
              Icons.link_off,
              size: 16,
              color: AraColors.accentWarning,
            ),
            // Wraps at large text instead of running off a phone.
            Flexible(
              child: Text(
                l10n.bestRigOff,
                style: kSimpleText.copyWith(color: AraColors.accentWarning),
              ),
            ),
          ],
        ),
        Text(l10n.bestOffline, style: kSimpleCaption),
      ],
    );
  }
}

class _Controls extends ConsumerWidget {
  const _Controls({
    required this.choices,
    required this.fieldKnown,
    required this.shown,
    required this.total,
  });

  final BestChoices choices;
  final bool fieldKnown;
  final int? shown;
  final int? total;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = context.l10n;
    final n = ref.read(tonightsBestChoicesProvider.notifier);
    final types = [
      (null, l10n.bestTypeAll, 'all'),
      (BestType.galaxy, l10n.bestTypeGalaxy, 'galaxy'),
      (BestType.nebula, l10n.bestTypeNebula, 'nebula'),
      (BestType.cluster, l10n.bestTypeCluster, 'cluster'),
      (BestType.planetary, l10n.bestTypePlanetary, 'planetary'),
      (BestType.other, l10n.bestTypeOther, 'other'),
    ];
    // Each group only as wide as its segments need (the full width when they
    // need more), so the Wrap can put groups side by side on a wide window.
    Widget group(String label, Widget child) => Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      spacing: 2,
      children: [
        Text(label, style: kSimpleCaption),
        IntrinsicWidth(child: child),
      ],
    );
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      spacing: AraSpace.s8,
      children: [
        Wrap(
          spacing: AraSpace.s12,
          runSpacing: AraSpace.s8,
          children: [
            group(
              l10n.bestSort,
              SimpleSegments<BestSort>(
                keyPrefix: 'best-sort',
                expand: false,
                group: l10n.bestSort,
                selected: choices.sort,
                onPick: n.setSort,
                options: [
                  (BestSort.score, l10n.bestSortScore, 'score'),
                  (BestSort.transit, l10n.bestSortTransit, 'transit'),
                  (BestSort.altitudeNow, l10n.bestSortAltitude, 'altitude'),
                ],
              ),
            ),
            group(
              l10n.bestShow,
              SimpleSegments<BestShow>(
                keyPrefix: 'best-show',
                expand: false,
                group: l10n.bestShow,
                selected: choices.show,
                onPick: n.setShow,
                options: [
                  (BestShow.allNight, l10n.bestShowAll, 'all'),
                  (BestShow.upNow, l10n.bestShowUp, 'up'),
                ],
              ),
            ),
            group(
              l10n.bestField,
              // A pair of named segments, never a bare Switch (DESIGN.md).
              // Locked when no row knows its framing.
              SimpleSegments<bool>(
                keyPrefix: 'best-fits',
                expand: false,
                group: l10n.bestField,
                selected: choices.fitsField && fieldKnown,
                onPick: fieldKnown ? n.setFitsField : null,
                options: [
                  (true, l10n.bestFitsOn, 'on'),
                  (false, l10n.bestFitsOff, 'off'),
                ],
              ),
            ),
          ],
        ),
        if (!fieldKnown)
          Text(
            l10n.bestFieldUnknown,
            key: const Key('best-field-unknown'),
            style: kSimpleCaption,
          ),
        group(
          l10n.bestPeak,
          SimpleSegments<int>(
            keyPrefix: 'best-peak',
            expand: false,
            group: l10n.bestPeak,
            selected: choices.minPeakAltDeg,
            onPick: n.setMinPeakAlt,
            options: [
              for (final d in kBestPeakAlts)
                (
                  d,
                  d == 0 ? l10n.bestPeakAny : l10n.bestPeakDeg(d),
                  d == 0 ? 'any' : '$d',
                ),
            ],
          ),
        ),
        group(
          l10n.bestHours,
          SingleChildScrollView(
            scrollDirection: Axis.horizontal,
            child: Row(
              spacing: AraSpace.s8,
              children: [
                _Chip(
                  key: const Key('best-window-any'),
                  label: l10n.bestHoursAny,
                  selected: choices.windowFromMin == null,
                  onTap: n.clearWindow,
                ),
                _Chip(
                  key: const Key('best-window-pick'),
                  label: choices.windowFromMin == null
                      ? l10n.bestHoursPick
                      : l10n.bestHoursRange(
                          _minHhmm(choices.windowFromMin!),
                          _minHhmm(choices.windowToMin!),
                        ),
                  selected: choices.windowFromMin != null,
                  onTap: () => _pickWindow(context, choices, n),
                ),
              ],
            ),
          ),
        ),
        group(
          l10n.bestType,
          SingleChildScrollView(
            scrollDirection: Axis.horizontal,
            child: Row(
              spacing: AraSpace.s8,
              children: [
                for (final (t, label, id) in types)
                  _Chip(
                    key: Key('best-type-$id'),
                    label: label,
                    selected: t == null
                        ? choices.types.isEmpty
                        : choices.types.contains(t),
                    onTap: t == null ? n.clearTypes : () => n.toggleType(t),
                  ),
              ],
            ),
          ),
        ),
        Text(
          [
            if (shown != null && total != null) l10n.bestCount(shown!, total!),
            l10n.bestKept,
          ].join(' · '),
          key: const Key('best-count'),
          style: kSimpleCaption,
        ),
      ],
    );
  }
}

/// From, then To, in the system time picker; backing out of either keeps
/// the range as it was. Hours are the device's local day, as the rows show,
/// and read 24-hour like the chip and the rows.
Future<void> _pickWindow(
  BuildContext context,
  BestChoices choices,
  TonightsBestChoicesNotifier n,
) async {
  final l10n = context.l10n;
  TimeOfDay of(int? m, int fallback) =>
      TimeOfDay(hour: (m ?? fallback) ~/ 60, minute: (m ?? fallback) % 60);
  Widget twentyFour(BuildContext context, Widget? child) => MediaQuery(
    data: MediaQuery.of(context).copyWith(alwaysUse24HourFormat: true),
    child: child!,
  );
  final from = await showTimePicker(
    context: context,
    initialTime: of(choices.windowFromMin, 21 * 60),
    helpText: l10n.bestHoursFromHelp,
    builder: twentyFour,
  );
  if (from == null || !context.mounted) return;
  final to = await showTimePicker(
    context: context,
    initialTime: of(choices.windowToMin, 60),
    helpText: l10n.bestHoursToHelp,
    builder: twentyFour,
  );
  if (to == null) return;
  n.setWindow(from.hour * 60 + from.minute, to.hour * 60 + to.minute);
}

class _Chip extends StatelessWidget {
  const _Chip({
    super.key,
    required this.label,
    required this.selected,
    required this.onTap,
  });

  final String label;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => Semantics(
    button: true,
    selected: selected,
    child: Material(
      color: selected ? AraColors.selectionBg : AraColors.tileFill,
      borderRadius: BorderRadius.circular(AraRadius.container),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(AraRadius.container),
        child: Container(
          constraints: const BoxConstraints(minHeight: 44, minWidth: 44),
          padding: const EdgeInsets.symmetric(horizontal: 14),
          alignment: Alignment.center,
          child: Text(
            label,
            maxLines: 1,
            style: kSimpleText.copyWith(
              color: selected ? AraColors.selectionFg : AraColors.textSecondary,
            ),
          ),
        ),
      ),
    ),
  );
}

class _Skeleton extends StatelessWidget {
  const _Skeleton();

  @override
  Widget build(BuildContext context) => Column(
    key: const Key('best-loading'),
    spacing: AraSpace.s8,
    children: [
      for (var i = 0; i < 4; i++)
        Container(
          height: 72,
          decoration: BoxDecoration(
            color: AraColors.tileFill,
            borderRadius: BorderRadius.circular(AraRadius.container),
          ),
        ),
    ],
  );
}

class _Empty extends StatelessWidget {
  const _Empty({
    super.key,
    required this.title,
    this.body,
    required this.action,
    required this.actionKey,
    required this.onAction,
    this.amber = false,
  });

  final String title;
  final String? body;
  final String action;
  final String actionKey;
  final VoidCallback? onAction;
  final bool amber;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: AraSpace.s24),
    child: Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 420),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          spacing: AraSpace.s8,
          children: [
            Text(
              title,
              textAlign: TextAlign.center,
              style: kSimpleText.copyWith(fontSize: AraFontSize.simpleHeading),
            ),
            if (body != null)
              Text(body!, textAlign: TextAlign.center, style: kSimpleCaption),
            OutlinedButton(
              key: Key(actionKey),
              onPressed: onAction,
              style: OutlinedButton.styleFrom(
                minimumSize: const Size(44, 44),
                foregroundColor: amber ? AraColors.accentWarning : null,
                side: amber
                    ? const BorderSide(color: AraColors.accentWarning)
                    : null,
              ),
              child: Text(action),
            ),
          ],
        ),
      ),
    ),
  );
}

class _Row extends ConsumerWidget {
  const _Row({required this.row, required this.onImage});

  final BestRow row;
  final ValueChanged<TonightSkyObject> onImage;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = context.l10n;
    final o = row.object;
    final selection = simpleSelectionOf(o);
    final running =
        ref.watch(sequenceRunStateProvider).value?.state?.isActive ?? false;
    final typeWord = switch (row.type) {
      BestType.galaxy => l10n.bestTypeGalaxy,
      BestType.nebula => l10n.bestTypeNebula,
      BestType.cluster => l10n.bestTypeCluster,
      BestType.planetary => l10n.bestTypePlanetary,
      BestType.other => l10n.bestTypeOther,
    };
    final (fitWord, fitGlyph, fitWarn) = switch (o.framing) {
      TonightFraming.tooSmall => (
        l10n.bestFitTooSmall,
        Icons.zoom_out_map,
        true,
      ),
      TonightFraming.goodFit ||
      TonightFraming.good => (l10n.bestFitFills, Icons.check, false),
      TonightFraming.tooBig => (l10n.bestFitMosaic, Icons.grid_view, true),
      TonightFraming.unknown => (null, Icons.check, false),
    };
    final start = o.windowStartUtc;
    final end = o.windowEndUtc;
    final transit = o.transitUtc;
    final image = _imageButton(context, ref, running);
    return Material(
      color: AraColors.tileFill,
      borderRadius: BorderRadius.circular(AraRadius.container),
      child: InkWell(
        key: Key('best-row-${o.id}'),
        borderRadius: BorderRadius.circular(AraRadius.container),
        // A row tap only shows the Target on the sky; it never plans or queues.
        onTap: () =>
            ref.read(planetariumSelectionProvider.notifier).set(selection),
        child: Padding(
          padding: const EdgeInsets.all(AraSpace.s12),
          child: LayoutBuilder(
            builder: (context, box) {
              final facts = Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                spacing: 4,
                children: [
                  Text(
                    objectTitle(o.id, o.aliases, o.name),
                    style: kSimpleText.copyWith(
                      fontSize: AraFontSize.simpleHeading,
                    ),
                  ),
                  Wrap(
                    spacing: AraSpace.s12,
                    runSpacing: 2,
                    children: [
                      Text(typeWord, style: kSimpleCaption),
                      if (fitWord != null)
                        _Tagged(
                          glyph: fitGlyph,
                          word: fitWord,
                          colour: fitWarn
                              ? AraColors.accentWarning
                              : AraColors.textSecondary,
                          style: kSimpleCaption,
                        ),
                    ],
                  ),
                  Wrap(
                    spacing: AraSpace.s12,
                    runSpacing: 2,
                    children: [
                      if (start != null && end != null)
                        Text(
                          l10n.bestWindow('${_hhmm(start)}-${_hhmm(end)}'),
                          style: kSimpleText,
                        ),
                      if (transit != null)
                        Text(
                          l10n.bestTransitAt(_hhmm(transit)),
                          style: kSimpleText,
                        ),
                      Text(
                        l10n.bestHighest(o.maxAltitudeDeg.round()),
                        style: kSimpleText,
                      ),
                      Text(
                        row.belowHorizon
                            ? l10n.bestNowBelow
                            : l10n.bestNow(o.altitudeDeg.round()),
                        key: Key('best-altitude-${o.id}'),
                        style: kSimpleText,
                      ),
                    ],
                  ),
                  if (row.moonSeparationDeg case final sep?)
                    _Tagged(
                      glyph: Icons.nightlight_round,
                      word: l10n.bestMoon(sep.round()),
                      colour: AraColors.accentWarning,
                      style: kSimpleText,
                    ),
                  if (row.inQueue || row.collected)
                    Wrap(
                      spacing: AraSpace.s8,
                      children: [
                        if (row.inQueue)
                          _Marker(
                            glyph: Icons.playlist_add_check,
                            word: l10n.bestInQueue,
                            colour: AraColors.linkText,
                          ),
                        if (row.collected)
                          _Marker(
                            glyph: Icons.check_circle_outline,
                            word: l10n.bestCollected,
                            colour: AraColors.accentConnected,
                          ),
                      ],
                    ),
                ],
              );
              return box.maxWidth < 560
                  ? Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      spacing: AraSpace.s8,
                      children: [facts, image],
                    )
                  : Row(
                      crossAxisAlignment: CrossAxisAlignment.center,
                      spacing: AraSpace.s12,
                      children: [
                        Expanded(child: facts),
                        image,
                      ],
                    );
            },
          ),
        ),
      ),
    );
  }

  Widget _imageButton(BuildContext context, WidgetRef ref, bool running) {
    final l10n = context.l10n;
    final o = row.object;
    final label = running
        ? (row.inQueue ? l10n.bestInQueue : l10n.bestAddToQueue)
        : l10n.bestImage;
    return FilledButton(
      key: Key('best-image-${o.id}'),
      // A queued Target stays in the Queue once (ADR 0010 rule 2).
      onPressed: running && row.inQueue
          ? null
          : () => _image(context, ref, running),
      style: FilledButton.styleFrom(minimumSize: const Size(44, 44)),
      child: Text(label),
    );
  }

  Future<void> _image(BuildContext context, WidgetRef ref, bool running) async {
    final o = row.object;
    final l10n = context.l10n;
    final selection = simpleSelectionOf(o);
    ref.read(planetariumSelectionProvider.notifier).set(selection);
    // No plan running (no rig included): the plan sheet plans it.
    if (!running) return onImage(o);
    final messages = ref.read(simpleMessagesProvider.notifier);
    final run = ref.read(simpleRunProvider.notifier);
    // Read before the await: Back may close this page before the rig answers.
    final container = ProviderScope.containerOf(context, listen: false);
    final added = await run.create();
    if (added) {
      // The person's own tap, so the pill is posted directly (ADR 0021 rule 5).
      messages.show(
        SimpleMessageKind.result,
        l10n.bestAdded(selection.name),
        origin: SimpleStatusOrigin.plan,
      );
    } else if (container.read(simpleRunProvider).error case final error?) {
      // A refused add says why, through the existing error text.
      messages.show(
        SimpleMessageKind.error,
        error,
        origin: SimpleStatusOrigin.plan,
      );
    }
  }
}

/// A glyph and a word: a marker never rests on colour alone.
class _Tagged extends StatelessWidget {
  const _Tagged({
    required this.glyph,
    required this.word,
    required this.colour,
    required this.style,
  });

  final IconData glyph;
  final String word;
  final Color colour;
  final TextStyle style;

  @override
  Widget build(BuildContext context) => Row(
    mainAxisSize: MainAxisSize.min,
    spacing: 4,
    children: [
      ExcludeSemantics(child: Icon(glyph, size: 16, color: colour)),
      Flexible(
        child: Text(word, style: style.copyWith(color: colour)),
      ),
    ],
  );
}

class _Marker extends StatelessWidget {
  const _Marker({
    required this.glyph,
    required this.word,
    required this.colour,
  });

  final IconData glyph;
  final String word;
  final Color colour;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
    decoration: BoxDecoration(
      border: Border.all(color: colour),
      borderRadius: BorderRadius.circular(AraRadius.container),
    ),
    child: _Tagged(
      glyph: glyph,
      word: word,
      colour: colour,
      style: kSimpleCaption,
    ),
  );
}
