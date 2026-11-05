/// Sky Album cards (docs/specs/sky-album-v1.md, "Layout"): the front shown in
/// the binder grid and the shelf, the back with the Card's figures, and the
/// in-place flip that scales a card up over its neighbours.
library;

import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../../l10n/l10n.dart';
import '../../services/dso_catalog_service.dart';
import '../../state/album/album_matching.dart';
import '../../theme/ara_colors.dart';

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

/// A grey stand-in per object type for a card with no frame to show.
IconData albumSilhouette(String type) => switch (type) {
      'G' || 'GPair' || 'GTrpl' || 'GGroup' => Icons.blur_circular,
      'OCl' || '*' || '**' => Icons.grain,
      'GCl' => Icons.blur_on,
      'PN' => Icons.radio_button_checked,
      'SNR' => Icons.flare,
      'DrkN' => Icons.cloud_outlined,
      _ => Icons.cloud,
    };

/// What one card shows: its object, catalog label, figures (null when
/// missing) and the best frame's thumbnail.
class AlbumCardData {
  final PlanningDso dso;
  final String label;
  final CardStats? stats;
  final String? thumbnailUrl;

  /// The best frame's gear profile name, blank when the profile is gone.
  final String profileName;

  const AlbumCardData({
    required this.dso,
    required this.label,
    this.stats,
    this.thumbnailUrl,
    this.profileName = '',
  });

  AlbumTier? get tier => stats?.tier;
}

/// The card front: the best frame (or a silhouette), the label and the name,
/// bordered in the tier's colour with a soft glow on gold.
class AlbumCardFront extends StatelessWidget {
  const AlbumCardFront(this.card, {super.key});
  final AlbumCardData card;

  @override
  Widget build(BuildContext context) {
    final tier = card.tier;
    final missing = tier == null;
    // A missing card is still tappable, so its text keeps 4.5:1; the grey border and
    // silhouette say "missing".
    final textColor = missing ? AraColors.textSecondary : AraColors.textPrimary;
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
            child: Stack(fit: StackFit.expand, children: [
              ColoredBox(
                color: AraColors.bgPanelAlt,
                child: Icon(albumSilhouette(card.dso.type),
                    size: 32, color: missing ? AraColors.textDisabled : AraColors.textSecondary),
              ),
              if (card.thumbnailUrl != null)
                Image.network(card.thumbnailUrl!,
                    fit: BoxFit.cover,
                    // Keep the silhouette when the thumbnail is missing or the rig is away.
                    errorBuilder: (_, _, _) => const SizedBox.shrink()),
            ]),
          ),
        ),
        const SizedBox(height: 4),
        Text(card.label,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: TextStyle(color: textColor, fontWeight: FontWeight.w600, fontSize: 12)),
        Text(card.dso.name,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: const TextStyle(color: AraColors.textSecondary, fontSize: 11)),
      ]),
    );
  }
}

/// The card back: the Card's figures and its one action.
class AlbumCardBack extends StatelessWidget {
  const AlbumCardBack(this.card, {super.key, required this.onAction});
  final AlbumCardData card;
  final VoidCallback onAction;

  @override
  Widget build(BuildContext context) {
    final l = context.l10n;
    final s = card.stats;
    final tier = card.tier;
    Widget row(String k, String v) => Padding(
          padding: const EdgeInsets.symmetric(vertical: 2),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(k, style: const TextStyle(color: AraColors.textSecondary, fontSize: 11)),
            Text(v, style: const TextStyle(color: AraColors.textPrimary, fontSize: 13)),
          ]),
        );
    return Container(
      decoration: BoxDecoration(
        color: AraColors.bgPanel,
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: albumTierColor(tier), width: 2),
      ),
      padding: const EdgeInsets.all(12),
      child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        Text('${card.label} · ${card.dso.name}',
            maxLines: 2,
            overflow: TextOverflow.ellipsis,
            style: const TextStyle(color: AraColors.textPrimary, fontWeight: FontWeight.w600)),
        if (tier != null)
          Text(albumTierName(context, tier), style: TextStyle(color: albumTierColor(tier), fontSize: 12)),
        const SizedBox(height: 6),
        Expanded(
          child: SingleChildScrollView(
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              if (s == null)
                Text(l.albumNotCollected, style: const TextStyle(color: AraColors.textSecondary))
              else ...[
                row(l.albumFirstCapture, switch (s.firstCaptureUtc) {
                  null => _unknown,
                  final first => MaterialLocalizations.of(context).formatMediumDate(first.toLocal()),
                }),
                row(l.albumIntegration, l.albumIntegrationValue(s.integrationHours.toStringAsFixed(1), s.frameCount)),
                row(l.albumFilters, s.filters.isEmpty ? _unknown : s.filters.join(', ')),
                row(l.albumGearProfile, card.profileName.isEmpty ? _unknown : card.profileName),
                row(l.albumBestScore, s.bestScore?.toStringAsFixed(2) ?? _unknown),
                row(l.albumNextTier, _nextTier(context, s)),
              ],
            ]),
          ),
        ),
        const SizedBox(height: 8),
        FilledButton(
          onPressed: onAction,
          child: Text(s == null ? l.albumImageThis : l.albumAddTime, textAlign: TextAlign.center),
        ),
      ]),
    );
  }

  /// Shown for a field the frames do not tell (no filter, profile or score).
  static const _unknown = '—';

  static String _nextTier(BuildContext context, CardStats s) {
    final (next, hoursAt) = switch (s.tier) {
      AlbumTier.gold => (null, 0.0),
      AlbumTier.silver => (AlbumTier.gold, albumGoldHours),
      _ => (AlbumTier.silver, albumSilverHours),
    };
    if (next == null) return context.l10n.albumGoldReached;
    final left = math.max(0.0, hoursAt - s.integrationHours);
    return context.l10n.albumNextTierValue(left.toStringAsFixed(1), albumTierName(context, next).toLowerCase());
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
      transitionDuration: MediaQuery.disableAnimationsOf(context) ? Duration.zero : const Duration(milliseconds: 320),
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
