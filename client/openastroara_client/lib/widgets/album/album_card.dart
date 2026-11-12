/// Sky Album cards (docs/specs/sky-album-v1.md, "Layout"): the front shown in
/// the binder grid and the shelf, the baseball-card back, and the in-place
/// flip that scales a card up over its neighbours.
library;

import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:intl/intl.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../l10n/l10n.dart';
import '../../services/dso_catalog_service.dart';
import '../../state/album/album_art.dart';
import '../../state/album/album_matching.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../util/reduce_motion.dart';
import '../platform_context_menu.dart';

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

/// Turns the Art grey and 45 % opaque while the Card is missing: the dimming is in
/// the matrix, so a missing Card costs one compositing layer, not two.
const ColorFilter _greyscaleDim = ColorFilter.matrix(<double>[
  0.2126, 0.7152, 0.0722, 0, 0, //
  0.2126, 0.7152, 0.0722, 0, 0,
  0.2126, 0.7152, 0.0722, 0, 0,
  0, 0, 0, 0.45, 0,
]);

/// How long a new or tiered-up Card takes to turn to its colours, the grey hold included.
const Duration albumRevealDuration = Duration(milliseconds: 1800);

/// Grey for the first 35 % (about 630 ms): the rig answers in tens of milliseconds, so
/// without the hold the reveal is spent while the page is still coming in.
const Curve _revealCurve = Interval(0.35, 1, curve: Curves.easeInOut);

/// The Card's corner, Simple Mode's 10 px, front and back alike so the flip
/// keeps one shape.
const double albumCardRadius = 10;

/// Card back text: Simple Mode's 14 px body, secondary by default.
final TextStyle _backText = kSimpleText.copyWith(color: AraColors.textSecondary);

/// The object type in the user's language.
String albumTypeLabel(AppLocalizations l, String t) => switch (t) {
      'galaxy' || 'G' => l.dsoTypeGalaxy,
      'GPair' => l.dsoTypeGalaxyPair,
      'GTrpl' => l.dsoTypeGalaxyTriplet,
      'GGroup' => l.dsoTypeGalaxyGroup,
      'nebula' || 'Neb' => l.dsoTypeNebula,
      'HII' || 'EmN' => l.dsoTypeEmissionNebula,
      'RfN' => l.dsoTypeReflectionNebula,
      'PN' => l.dsoTypePlanetaryNebula,
      'SNR' => l.dsoTypeSupernovaRemnant,
      'cluster' => l.dsoTypeCluster,
      'OCl' => l.dsoTypeOpenCluster,
      'GCl' => l.dsoTypeGlobularCluster,
      'Cl+N' => l.dsoTypeClusterNebula,
      '' => l.dsoTypeObject,
      _ => t[0].toUpperCase() + t.substring(1),
    };

/// The card front: the Art filling the picture (dimmed to grey while missing) or
/// the Placeholder, the label and the name, bordered in the tier's colour
/// with a soft glow on gold. With [reveal] it plays once from [revealFrom] (null:
/// missing) to its tier: grey Art turns to colour, the border to the new tier's.
class AlbumCardFront extends StatelessWidget {
  const AlbumCardFront(this.card, {super.key, this.reveal = false, this.revealFrom, this.onRevealed, this.decodeWidth});
  final AlbumCardData card;

  /// Logical width to decode the Art at; null: the slot's. The flip pins its end size, or every
  /// frame of the scale-up would be a new image provider.
  final double? decodeWidth;
  final bool reveal;
  final AlbumTier? revealFrom;
  final VoidCallback? onRevealed;

  @override
  Widget build(BuildContext context) {
    // Under Reduce Motion the Card is simply in its colours.
    if (!reveal || reduceMotionOf(context)) return _at(context, 1);
    return TweenAnimationBuilder<double>(
      key: const Key('album-card-reveal'),
      tween: Tween(begin: 0, end: 1),
      duration: albumRevealDuration,
      curve: _revealCurve,
      onEnd: onRevealed,
      builder: (context, t, _) => _at(context, t),
    );
  }

  /// The front [t] of the way through its reveal; 1 is the plain card.
  Widget _at(BuildContext context, double t) {
    final tier = card.tier;
    final missing = tier == null;
    final fromMissing = reveal && revealFrom == null && t < 1;
    // A missing card is still tappable, so its text keeps 4.5:1; the grey border and
    // dimmed Art say "missing".
    final textColor = missing
        ? AraColors.textSecondary
        : fromMissing
            ? Color.lerp(AraColors.textSecondary, AraColors.textPrimary, t)!
            : AraColors.textPrimary;
    final borderColor = reveal ? Color.lerp(albumTierColor(revealFrom), albumTierColor(tier), t)! : albumTierColor(tier);
    // The gold glow arrives last, once the border is gold.
    final glow = reveal ? ((t - 0.5) * 2).clamp(0.0, 1.0) : 1.0;
    final placeholder = ColoredBox(
      key: const Key('album-placeholder'),
      color: AraColors.bgPanelAlt,
      child: Icon(albumSilhouette(card.dso.type),
          size: 32,
          color: missing
              ? AraColors.albumPlaceholderMissing
              : Color.lerp(AraColors.albumPlaceholderMissing, AraColors.textSecondary, fromMissing ? t : 1)),
    );
    final asset = card.art?.asset;
    return Container(
      decoration: BoxDecoration(
        color: AraColors.bgPanel,
        borderRadius: BorderRadius.circular(albumCardRadius),
        // 2 px from the reveal's first frame: a border that thickens shrinks the picture, and every
        // new width decodes the Art again (black frames mid-fade).
        border: Border.all(color: borderColor, width: missing ? 1 : 2),
        boxShadow: [
          if (tier == AlbumTier.gold)
            BoxShadow(color: albumTierColor(tier).withValues(alpha: 0.35 * glow), blurRadius: 8),
        ],
      ),
      padding: const EdgeInsets.all(4),
      child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        Expanded(
          child: ClipRRect(
            // The card's corner less its 4 px inset.
            borderRadius: BorderRadius.circular(albumCardRadius - 4),
            child: asset == null
                ? placeholder
                : ColoredBox(
                    color: AraColors.albumArtBackdrop,
                    child: LayoutBuilder(builder: (context, box) {
                      Image image({Key? key, Animation<double>? opacity}) => Image.asset(asset,
                          key: key,
                          opacity: opacity,
                          // The Art is cut to the slot's shape already; cover trims the last few
                          // pixels so no black bar shows.
                          fit: BoxFit.cover,
                          // Decode at the slot's width: a Book holds 110 cards.
                          cacheWidth: ((decodeWidth ?? box.maxWidth) * MediaQuery.devicePixelRatioOf(context)).round(),
                          errorBuilder: (_, _, _) => placeholder);
                      final key = Key(missing ? 'album-art-dimmed' : 'album-art');
                      if (missing) return ColorFiltered(colorFilter: _greyscaleDim, child: image(key: key));
                      if (!fromMissing) return image(key: key);
                      // Collected since the last open: the colour Art fades in over its grey self, by
                      // the Image's own opacity (an Opacity widget adds a layer per fading card).
                      return Stack(fit: StackFit.expand, children: [
                        ColorFiltered(colorFilter: _greyscaleDim, child: image()),
                        image(key: key, opacity: AlwaysStoppedAnimation(t)),
                      ]);
                    }),
                  ),
          ),
        ),
        const SizedBox(height: 4),
        Row(children: [
          Flexible(
            child: Text(card.label,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: AraText.body.copyWith(color: textColor, fontWeight: FontWeight.w600)),
          ),
          // The border says the tier by colour; the word says it for everyone else.
          if (tier != null) ...[
            const SizedBox(width: 6),
            Flexible(
              child: Text(albumTierName(context, tier),
                  maxLines: 1, overflow: TextOverflow.ellipsis, style: AraText.caption.copyWith(color: borderColor.withValues(alpha: t))),
            ),
          ],
        ]),
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
/// Opens the licence deed; a browser that cannot be reached (false or thrown) says so.
Future<void> _openLicence(BuildContext context, String url) async {
  final messenger = ScaffoldMessenger.maybeOf(context);
  final failed = context.l10n.albumArtCreditOpenFailed;
  final opened = await launchUrl(Uri.parse(url), mode: LaunchMode.externalApplication).catchError((_) => false);
  if (!opened) messenger?.showSnackBar(SnackBar(content: Text(failed)));
}

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
    final onBanner = tier == null ? AraColors.textPrimary : AraColors.onTier;
    final locale = Localizations.localeOf(context);
    final lang = locale.languageCode;
    final oneDecimal = NumberFormat('0.0', locale.toString());
    final bio = [
      albumTypeLabel(l, d.type),
      ?d.constellation,
      if (d.magnitude case final m?) 'V ${oneDecimal.format(m)}',
      if (d.sizeMajArcmin case final a?) '${a < 10 ? oneDecimal.format(a) : a.round()}′',
      ?art?.distanceIn(lang),
    ].join(' · ');
    final cell = _backText.copyWith(color: AraColors.textPrimary);
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
    String hours(double h) => oneDecimal.format(h);
    String sub(double sec) => '${sec.round()} s';

    final top = Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
      ColoredBox(
        color: banner,
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 6),
          child: Row(children: [
            DecoratedBox(
              decoration: BoxDecoration(color: AraColors.bgPanel, borderRadius: BorderRadius.circular(AraRadius.field)),
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
                    style: AraText.title.copyWith(color: onBanner, fontWeight: FontWeight.w700)),
                Wrap(spacing: 8, children: [
                  Text(l.albumSetNumber(card.number, card.bookSize), style: _backText.copyWith(color: onBanner)),
                  if (tier != null)
                    Text(albumTierName(context, tier), style: _backText.copyWith(color: onBanner, fontWeight: FontWeight.w700)),
                ]),
              ]),
            ),
          ]),
        ),
      ),
      Padding(
        padding: const EdgeInsets.fromLTRB(12, 8, 12, 0),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text(bio, style: _backText),
          const SizedBox(height: 8),
          if (s == null)
            Text(l.albumNotCollected, style: _backText)
          else
            Table(
              // The figures take what they need; the Night column the rest.
              defaultColumnWidth: const IntrinsicColumnWidth(),
              columnWidths: const {0: FlexColumnWidth()},
              children: [
                tableRow([l.albumNight, l.albumLights, l.albumSub, l.albumHours], _backText),
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
                  // The total carries its unit, like the Sub column's seconds.
                  '${hours(s.integrationHours)} h',
                ], cell.copyWith(fontWeight: FontWeight.w700)),
              ],
            ),
          if (art?.triviaIn(lang) case final trivia?) ...[
            const SizedBox(height: 8),
            Text(l.albumDidYouKnow, style: _backText.copyWith(fontWeight: FontWeight.w700)),
            Text(trivia, style: _backText.copyWith(color: AraColors.textPrimary)),
          ],
        ]),
      ),
    ]);
    final bottom = Padding(
      padding: const EdgeInsets.fromLTRB(12, 8, 12, 8),
      child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        FilledButton(
          onPressed: onAction,
          // Desktop's compact density would take the theme's minimum height under 44.
          style: FilledButton.styleFrom(minimumSize: const Size(0, 44), visualDensity: VisualDensity.standard),
          child: Text(s == null ? l.albumImageThis : l.albumAddTime, textAlign: TextAlign.center),
        ),
        if (art != null && art.creditLine != null) ...[
          // The licence name links its deed, as CC BY asks; the whole 44 px row is the tap target.
          Semantics(
            link: art.licenceUrl != null,
            child: InkWell(
            key: const Key('album-art-credit'),
            onTap: switch (art.licenceUrl) {
              null => null,
              final url => () => unawaited(_openLicence(context, url)),
            },
            child: ConstrainedBox(
              constraints: const BoxConstraints(minHeight: 44),
              child: Align(
                alignment: Alignment.centerLeft,
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
                  style: _backText,
                ),
              ),
            ),
            ),
          ),
        ],
      ]),
    );
    return Container(
      decoration: BoxDecoration(
        color: AraColors.bgPanel,
        borderRadius: BorderRadius.circular(albumCardRadius),
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
  const AlbumCard(this.card,
      {super.key, required this.onAction, this.reveal = false, this.revealFrom, this.onRevealed, this.news});
  final AlbumCardData card;

  /// What changed since the last open ("new", "up a tier"), said after the tier: on screen
  /// only the new shelf's title says it.
  final String? news;

  /// Plays the front's reveal from [revealFrom]; see [AlbumCardFront].
  final bool reveal;
  final AlbumTier? revealFrom;
  final VoidCallback? onRevealed;

  /// The back's action; the flip closes before it runs.
  final VoidCallback onAction;

  @override
  Widget build(BuildContext context) => PlatformContextMenu(
        // The back's action from a long press on iOS and iPadOS; a tap still flips.
        items: [
          ContextMenuItem(
            card.stats == null ? context.l10n.albumImageThis : context.l10n.albumAddTime,
            card.stats == null ? Icons.photo_camera_outlined : Icons.add_circle_outline,
            onAction,
          ),
        ],
        child: _flipTarget(context),
      );

  Widget _flipTarget(BuildContext context) => Semantics(
        button: true,
        // The tier is a border colour on screen; say it too. The front's own texts stay
        // out, or the label and name are read twice.
        label: context.l10n.albumCardSemantics(card.label, card.dso.name,
            card.tier == null ? context.l10n.albumNotCollected : albumTierName(context, card.tier!)) +
            (news == null ? '' : ', $news'),
        child: InkWell(
          borderRadius: BorderRadius.circular(albumCardRadius),
          onTap: () => _flip(context),
          child: ExcludeSemantics(
              child: AlbumCardFront(card, reveal: reveal, revealFrom: revealFrom, onRevealed: onRevealed)),
        ),
      );

  void _flip(BuildContext context) {
    // The flip draws the plain front, so a reveal still running ends here, not mid-colour.
    onRevealed?.call();
    final box = context.findRenderObject()! as RenderBox;
    final from = box.localToGlobal(Offset.zero) & box.size;
    showGeneralDialog<void>(
      context: context,
      barrierDismissible: true,
      barrierLabel: MaterialLocalizations.of(context).modalBarrierDismissLabel,
      barrierColor: AraColors.barrierSheet,
      transitionDuration: reduceMotionOf(context) ? Duration.zero : const Duration(milliseconds: 320),
      pageBuilder: (dialogContext, _, _) => const SizedBox.shrink(),
      transitionBuilder: (dialogContext, animation, _, _) {
        final screen = MediaQuery.sizeOf(dialogContext);
        final insets = MediaQuery.paddingOf(dialogContext);
        // Scale up over the neighbours, centred on the card, kept inside the safe area: a
        // landscape phone limits the card by its height, not its width.
        final left = insets.left + 16, right = screen.width - insets.right - 16;
        final top = insets.top + 16, bottom = screen.height - insets.bottom - 16;
        // 320 px holds the back's 14 px text; a smaller screen limits it.
        final width = math.min(320.0, math.min(right - left, (bottom - top) * albumCardAspect));
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
                  : AlbumCardFront(card, decodeWidth: size.width),
            ),
          ),
        ]);
      },
    );
  }
}
