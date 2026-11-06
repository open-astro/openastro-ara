/// Sky Album cards (docs/specs/sky-album-v1.md, "Layout"): the front shown in
/// the binder grid and the shelf, the baseball-card back, and the in-place
/// flip that scales a card up over its neighbours.
library;

import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../l10n/l10n.dart';
import '../../services/dso_catalog_service.dart';
import '../../state/album/album_art.dart';
import '../../state/album/album_matching.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../util/reduce_motion.dart';
import '../sky_atlas/tonight_sky_panel.dart' show dsoTypeLabel;

/// Cards are trading-card shaped: 2.5 wide by 3.5 tall.
const double albumCardAspect = 2.5 / 3.5;

Color albumTierColor(AlbumTier? tier) => switch (tier) {
      null => AraColors.border,
      AlbumTier.bronze => AraColors.tierBronze,
      AlbumTier.silver => AraColors.tierSilver,
      AlbumTier.gold => AraColors.tierGold,
    };

String albumTierName(BuildContext context, AlbumTier tier) => switch (tier) {
      AlbumTier.bronze => context.l10n.albumTierBronze,
      AlbumTier.silver => context.l10n.albumTierSilver,
      AlbumTier.gold => context.l10n.albumTierGold,
    };

/// The Placeholder: a grey stand-in per object type for a card with no Art.
IconData albumSilhouette(String type) => switch (type) {
      'G' || 'GPair' || 'GTrpl' || 'GGroup' => Icons.blur_circular,
      'OCl' || '*' || '**' => Icons.grain,
      'GCl' => Icons.blur_on,
      'PN' => Icons.radio_button_checked,
      'SNR' => Icons.flare,
      'DrkN' => Icons.cloud_outlined,
      _ => Icons.cloud,
    };

/// What one card shows: its object, catalog label and number in the Book,
/// figures (null when missing) and its manifest row (null when it has none).
class AlbumCardData {
  final PlanningDso dso;
  final String label;
  final int number;
  final int bookSize;
  final CardStats? stats;
  final AlbumArt? art;

  const AlbumCardData({
    required this.dso,
    required this.label,
    required this.number,
    required this.bookSize,
    this.stats,
    this.art,
  });

  AlbumTier? get tier => stats?.tier;
}

/// Turns the Art grey while the Card is missing.
const ColorFilter _greyscale = ColorFilter.matrix(<double>[
  0.2126, 0.7152, 0.0722, 0, 0, //
  0.2126, 0.7152, 0.0722, 0, 0,
  0.2126, 0.7152, 0.0722, 0, 0,
  0, 0, 0, 1, 0,
]);

/// The card front: the Art filling the picture (dimmed to grey while missing) or
/// the Placeholder, the label and the name, bordered in the tier's colour
/// with a soft glow on gold.
class AlbumCardFront extends StatelessWidget {
  const AlbumCardFront(this.card, {super.key});
  final AlbumCardData card;

  @override
  Widget build(BuildContext context) {
    final tier = card.tier;
    final missing = tier == null;
    // A missing card is still tappable, so its text keeps 4.5:1; the grey border and
    // dimmed Art say "missing".
    final textColor = missing ? AraColors.textSecondary : AraColors.textPrimary;
    final placeholder = ColoredBox(
      key: const Key('album-placeholder'),
      color: AraColors.bgPanelAlt,
      child: Icon(albumSilhouette(card.dso.type),
          size: 32, color: missing ? AraColors.textDisabled : AraColors.textSecondary),
    );
    final asset = card.art?.asset;
    return Container(
      decoration: BoxDecoration(
        color: AraColors.bgPanel,
        borderRadius: BorderRadius.circular(8),
        border: Border.all(color: albumTierColor(tier), width: missing ? 1 : 2),
        boxShadow: [
          if (tier == AlbumTier.gold)
            BoxShadow(color: albumTierColor(tier).withValues(alpha: 0.35), blurRadius: 8),
        ],
      ),
      padding: const EdgeInsets.all(4),
      child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        Expanded(
          child: ClipRRect(
            borderRadius: BorderRadius.circular(5),
            child: asset == null
                ? placeholder
                : ColoredBox(
                    color: Colors.black,
                    child: LayoutBuilder(builder: (context, box) {
                      final image = Image.asset(asset,
                          key: Key(missing ? 'album-art-dimmed' : 'album-art'),
                          // The Art is cut to the slot's shape already; cover trims the last few
                          // pixels so no black bar shows.
                          fit: BoxFit.cover,
                          // Decode at the slot's width: a Book holds 110 cards.
                          cacheWidth: (box.maxWidth * MediaQuery.devicePixelRatioOf(context)).round(),
                          errorBuilder: (_, _, _) => placeholder);
                      return missing
                          ? Opacity(opacity: 0.45, child: ColorFiltered(colorFilter: _greyscale, child: image))
                          : image;
                    }),
                  ),
          ),
        ),
        const SizedBox(height: 4),
        Text(card.label,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: AraText.body.copyWith(color: textColor, fontWeight: FontWeight.w600)),
        Text(card.dso.name,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: AraText.caption),
      ]),
    );
  }
}

/// The card back, a baseball-card back: name banner in the Tier colour, bio
/// line, the latest Nights and a totals row, the "Did you know?" line, the one
/// action and the Art credit.
class AlbumCardBack extends StatelessWidget {
  const AlbumCardBack(this.card, {super.key, required this.onAction});
  final AlbumCardData card;
  final VoidCallback onAction;

  /// The back lists at most this many Nights above the totals row.
  static const int nightRows = 4;

  @override
  Widget build(BuildContext context) {
    final l = context.l10n;
    final s = card.stats;
    final tier = card.tier;
    final art = card.art;
    final d = card.dso;
    final banner = tier == null ? AraColors.bgPanelAlt : albumTierColor(tier);
    // Tier colours are light: dark text on them, light text on the missing grey.
    final onBanner = tier == null ? AraColors.textPrimary : Colors.black;
    final bio = [
      dsoTypeLabel(d.type),
      ?d.constellation,
      if (d.magnitude case final m?) 'V ${m.toStringAsFixed(1)}',
      if (d.sizeMajArcmin case final a?) '${a < 10 ? a.toStringAsFixed(1) : a.round()}′',
      ?art?.distance,
    ].join(' · ');
    final cell = AraText.caption.copyWith(color: AraColors.textPrimary);
    TableRow tableRow(List<String> cells, TextStyle style) => TableRow(children: [
          for (final (i, c) in cells.indexed)
            Padding(
              padding: EdgeInsets.only(left: i == 0 ? 0 : 8, top: 1, bottom: 1),
              // Bounded lines: a narrow card or large text shortens a row, never stretches it.
              child: Text(c,
                  maxLines: i == 0 ? 2 : 1,
                  overflow: TextOverflow.ellipsis,
                  style: style,
                  textAlign: i == 0 ? TextAlign.start : TextAlign.end),
            ),
        ]);
    String hours(double h) => h.toStringAsFixed(1);
    String sub(double sec) => '${sec.round()} s';

    final top = Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
      ColoredBox(
        color: banner,
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 6),
          child: Row(children: [
            DecoratedBox(
              decoration: BoxDecoration(color: AraColors.bgPanel, borderRadius: BorderRadius.circular(4)),
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                child: Text(card.label, style: AraText.caption.copyWith(color: AraColors.textPrimary)),
              ),
            ),
            const SizedBox(width: 8),
            // The set number under the name, not beside it: large text keeps the width.
            Expanded(
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Text(d.name.toUpperCase(),
                    maxLines: 2,
                    overflow: TextOverflow.ellipsis,
                    style: AraText.body.copyWith(color: onBanner, fontWeight: FontWeight.w700)),
                Text(l.albumSetNumber(card.number, card.bookSize), style: AraText.caption.copyWith(color: onBanner)),
              ]),
            ),
          ]),
        ),
      ),
      Padding(
        padding: const EdgeInsets.fromLTRB(12, 8, 12, 0),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text(bio, style: AraText.caption),
          const SizedBox(height: 8),
          if (s == null)
            Text(l.albumNotCollected, style: const TextStyle(color: AraColors.textSecondary))
          else
            Table(
              // The figures take what they need; the Night column the rest.
              defaultColumnWidth: const IntrinsicColumnWidth(),
              columnWidths: const {0: FlexColumnWidth()},
              children: [
                tableRow([l.albumNight, l.albumLights, l.albumSub, l.albumHours], AraText.caption),
                for (final n in s.nights.take(nightRows))
                  tableRow([
                    MaterialLocalizations.of(context).formatShortMonthDay(DateTime.parse(n.key)),
                    '${n.lights}',
                    sub(n.subSeconds),
                    hours(n.hours),
                  ], cell),
                tableRow([
                  l.albumNightsTotal(s.nights.length),
                  '${s.frameCount}',
                  '',
                  hours(s.integrationHours),
                ], cell.copyWith(fontWeight: FontWeight.w700)),
              ],
            ),
          if (art?.trivia case final trivia?) ...[
            const SizedBox(height: 8),
            Text(l.albumDidYouKnow, style: AraText.caption.copyWith(fontWeight: FontWeight.w700)),
            Text(trivia, style: AraText.caption.copyWith(color: AraColors.textPrimary)),
          ],
        ]),
      ),
    ]);
    final bottom = Padding(
      padding: const EdgeInsets.fromLTRB(12, 8, 12, 8),
      child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        FilledButton(
          onPressed: onAction,
          child: Text(s == null ? l.albumImageThis : l.albumAddTime, textAlign: TextAlign.center),
        ),
        if (art != null && art.creditLine != null) ...[
          const SizedBox(height: 4),
          // The licence name links its deed, as CC BY asks; the whole line is the tap target.
          InkWell(
            key: const Key('album-art-credit'),
            onTap: switch (art.licenceUrl) {
              null => null,
              final url => () => unawaited(launchUrl(Uri.parse(url), mode: LaunchMode.externalApplication)
                  .catchError((_) => false)),
            },
            child: Text.rich(
              TextSpan(text: l.albumArtCredit(art.credit!), children: [
                if (art.licence case final licence?) ...[
                  const TextSpan(text: ' · '),
                  TextSpan(
                    text: licence,
                    style: art.licenceUrl == null ? null : const TextStyle(decoration: TextDecoration.underline),
                  ),
                ],
              ]),
              maxLines: 2,
              overflow: TextOverflow.ellipsis,
              style: AraText.caption,
            ),
          ),
        ],
      ]),
    );
    return Container(
      decoration: BoxDecoration(
        color: AraColors.bgPanel,
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: albumTierColor(tier), width: 2),
      ),
      clipBehavior: Clip.antiAlias,
      // The action sits at the foot of the card; when large text needs more than the card
      // has, the whole back scrolls instead of overflowing.
      child: LayoutBuilder(
        builder: (context, box) => SingleChildScrollView(
          child: ConstrainedBox(
            constraints: BoxConstraints(minHeight: box.maxHeight),
            child: Column(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [top, bottom],
            ),
          ),
        ),
      ),
    );
  }
}

/// A tappable card front that flips in place to its back.
class AlbumCard extends StatelessWidget {
  const AlbumCard(this.card, {super.key, required this.onAction});
  final AlbumCardData card;

  /// The back's action; the flip closes before it runs.
  final VoidCallback onAction;

  @override
  Widget build(BuildContext context) => Semantics(
        button: true,
        // The tier is a border colour on screen; say it too. The front's own texts stay
        // out, or the label and name are read twice.
        label: context.l10n.albumCardSemantics(card.label, card.dso.name,
            card.tier == null ? context.l10n.albumNotCollected : albumTierName(context, card.tier!)),
        child: InkWell(
          borderRadius: BorderRadius.circular(8),
          onTap: () => _flip(context),
          child: ExcludeSemantics(child: AlbumCardFront(card)),
        ),
      );

  void _flip(BuildContext context) {
    final box = context.findRenderObject()! as RenderBox;
    final from = box.localToGlobal(Offset.zero) & box.size;
    showGeneralDialog<void>(
      context: context,
      barrierDismissible: true,
      barrierLabel: MaterialLocalizations.of(context).modalBarrierDismissLabel,
      barrierColor: Colors.black38,
      transitionDuration: reduceMotionOf(context) ? Duration.zero : const Duration(milliseconds: 320),
      pageBuilder: (dialogContext, _, _) => const SizedBox.shrink(),
      transitionBuilder: (dialogContext, animation, _, _) {
        final screen = MediaQuery.sizeOf(dialogContext);
        final insets = MediaQuery.paddingOf(dialogContext);
        // Scale up over the neighbours, centred on the card, kept inside the safe area: a
        // landscape phone limits the card by its height, not its width.
        final left = insets.left + 16, right = screen.width - insets.right - 16;
        final top = insets.top + 16, bottom = screen.height - insets.bottom - 16;
        final width = math.min(280.0, math.min(right - left, (bottom - top) * albumCardAspect));
        final size = Size(width, width / albumCardAspect);
        final centre = Offset(
          from.center.dx.clamp(left + size.width / 2, math.max(left + size.width / 2, right - size.width / 2)),
          from.center.dy.clamp(top + size.height / 2, math.max(top + size.height / 2, bottom - size.height / 2)),
        );
        final to = Rect.fromCenter(center: centre, width: size.width, height: size.height);
        final t = Curves.easeInOut.transform(animation.value);
        final rect = Rect.lerp(from, to, t)!;
        final angle = t * math.pi;
        final showBack = angle > math.pi / 2;
        return Stack(children: [
          Positioned.fromRect(
            rect: rect,
            child: Transform(
              alignment: Alignment.center,
              transform: Matrix4.identity()
                ..setEntry(3, 2, 0.001)
                ..rotateY(showBack ? angle - math.pi : angle),
              child: showBack
                  // Tapping the flipped card again flips it back; its button still acts.
                  ? GestureDetector(
                      // A pointer shortcut only: screen readers close through the labelled barrier.
                      excludeFromSemantics: true,
                      onTap: () => Navigator.of(dialogContext).pop(),
                      child: Material(
                        type: MaterialType.transparency,
                        child: AlbumCardBack(card, onAction: () {
                          Navigator.of(dialogContext).pop();
                          onAction();
                        }),
                      ),
                    )
                  : AlbumCardFront(card),
            ),
          ),
        ]);
      },
    );
  }
}
