import 'package:flutter/material.dart';

import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../fit_pane.dart';
import '../help_icon.dart';

/// One instrument on the Smart Focus pane: a flat, borderless group (rounded
/// panel fill, generous padding) with a headline row — title + ⓘ on the left,
/// the one primary action on the right — and a body. The headline carries the
/// state in words ("In focus · HFR 1.42"), so there is no separate chip.
/// The body fills the height the [FitPane] leaves under the headline.
class FocusSection extends StatelessWidget {
  final String title;
  final String helpKey;
  final String headline;
  final Color? headlineColor;
  final String? subhead;
  final Widget action;
  final Widget? secondaryAction;
  /// Drawn at the right end of the title row (the main telescope's method
  /// picker).
  final Widget? titleTrailing;
  final Widget child;

  const FocusSection({
    super.key,
    required this.title,
    required this.helpKey,
    required this.headline,
    required this.action,
    required this.child,
    this.headlineColor,
    this.subhead,
    this.secondaryAction,
    this.titleTrailing,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final short = AraBreakpoints.isShort(context);
    return Container(
      padding: short
          ? const EdgeInsets.fromLTRB(16, 12, 16, 16)
          : const EdgeInsets.fromLTRB(24, 20, 24, 24),
      decoration: BoxDecoration(
        color: AraColors.bgPanel,
        borderRadius: BorderRadius.circular(12),
      ),
      child: FitColumn(
        children: [
          // The trailing widget sits at the right end of the title row, or
          // wraps under the title when the card is too narrow for both. Full
          // width, or the Wrap shrinks to its children and nothing spreads.
          SizedBox(
            width: double.infinity,
            child: Wrap(
            alignment: WrapAlignment.spaceBetween,
            crossAxisAlignment: WrapCrossAlignment.center,
            runSpacing: 8,
            children: [
              Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Text(
                    title.toUpperCase(),
                    style: theme.textTheme.labelSmall?.copyWith(
                      color: AraColors.textSecondary,
                      letterSpacing: 1.1,
                    ),
                  ),
                  const SizedBox(width: 2),
                  HelpIcon(helpKey: helpKey),
                ],
              ),
              ?titleTrailing,
            ],
          ),
          ),
          const SizedBox(height: 6),
          LayoutBuilder(
            builder: (context, c) {
              final narrow = c.maxWidth < 560;
              final words = Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    headline,
                    style: (short ? theme.textTheme.titleLarge : theme.textTheme.headlineSmall)?.copyWith(
                      color: headlineColor ?? AraColors.textPrimary,
                      fontWeight: FontWeight.w600,
                    ),
                  ),
                  if (subhead != null) ...[
                    const SizedBox(height: 2),
                    Text(
                      subhead!,
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: AraColors.textSecondary,
                      ),
                    ),
                  ],
                ],
              );
              final actions = Wrap(
                spacing: 8,
                runSpacing: 8,
                crossAxisAlignment: WrapCrossAlignment.center,
                alignment: narrow ? WrapAlignment.start : WrapAlignment.end,
                children: [?secondaryAction, action],
              );
              if (narrow) {
                return Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [words, const SizedBox(height: 12), actions],
                );
              }
              // The actions take their natural width (up to 60 %) so they stay
              // on one line at the right; the words wrap in what is left.
              return Row(
                crossAxisAlignment: CrossAxisAlignment.center,
                children: [
                  Expanded(child: words),
                  const SizedBox(width: 16),
                  ConstrainedBox(
                    constraints: BoxConstraints(maxWidth: c.maxWidth * 0.6),
                    child: actions,
                  ),
                ],
              );
            },
          ),
          SizedBox(height: short ? 12 : 20),
          FitFill(child: child),
        ],
      ),
    );
  }
}

/// A large number with a small caption — the "stat tile".
class StatTile extends StatelessWidget {
  final String caption;
  final String value;
  final String? unit;
  final Color? color;
  const StatTile({
    super.key,
    required this.caption,
    required this.value,
    this.unit,
    this.color,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final short = AraBreakpoints.isShort(context);
    return Container(
      padding: short
          ? const EdgeInsets.fromLTRB(14, 8, 14, 8)
          : const EdgeInsets.fromLTRB(16, 12, 16, 12),
      decoration: BoxDecoration(
        color: AraColors.bgPanelAlt,
        borderRadius: BorderRadius.circular(10),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(
            caption,
            style: theme.textTheme.labelSmall?.copyWith(
              color: AraColors.textSecondary,
            ),
          ),
          const SizedBox(height: 4),
          Row(
            crossAxisAlignment: CrossAxisAlignment.baseline,
            textBaseline: TextBaseline.alphabetic,
            children: [
              Flexible(
                child: Text(
                  value,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: (short ? theme.textTheme.titleLarge : theme.textTheme.headlineSmall)?.copyWith(
                    color: color ?? AraColors.textPrimary,
                    fontWeight: FontWeight.w600,
                    fontFeatures: const [FontFeature.tabularFigures()],
                  ),
                ),
              ),
              if (unit != null && value != '—') ...[
                const SizedBox(width: 4),
                Text(
                  unit!,
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: AraColors.textSecondary,
                  ),
                ),
              ],
            ],
          ),
        ],
      ),
    );
  }
}

/// A row of stat tiles that wraps on narrow panes.
class StatRow extends StatelessWidget {
  final List<Widget> tiles;
  const StatRow({super.key, required this.tiles});

  @override
  Widget build(BuildContext context) {
    // No tiles: nothing to lay out (and no per-row count to divide by).
    if (tiles.isEmpty) return const SizedBox.shrink();
    return LayoutBuilder(
      builder: (context, c) {
        // One row while each tile still gets ~140 px (a 1100-wide window);
        // a second row costs the instruments their height.
        final perRow = c.maxWidth >= tiles.length * 140
            ? tiles.length
            : (c.maxWidth >= 420 ? 2 : 1);
        final width = (c.maxWidth - 12 * (perRow - 1)) / perRow;
        return Wrap(
          spacing: 12,
          runSpacing: 12,
          children: [for (final t in tiles) SizedBox(width: width, child: t)],
        );
      },
    );
  }
}

/// A tinted inline notice: icon + text (+ an optional action). Used for a
/// failure, a cancelled run, a blocker ("connect the guider first") and the
/// OAG gate — never colour alone.
class InlineNotice extends StatelessWidget {
  final IconData icon;
  final Color tint;
  final String text;
  final Widget? action;
  const InlineNotice({
    super.key,
    required this.icon,
    required this.tint,
    required this.text,
    this.action,
  });

  const InlineNotice.error(String text, {Key? key, Widget? action})
    : this(
        key: key,
        icon: Icons.error_outline,
        tint: AraColors.accentError,
        text: text,
        action: action,
      );

  const InlineNotice.warning(String text, {Key? key, Widget? action})
    : this(
        key: key,
        icon: Icons.warning_amber_rounded,
        tint: AraColors.accentWarning,
        text: text,
        action: action,
      );

  const InlineNotice.info(String text, {Key? key, Widget? action})
    : this(
        key: key,
        icon: Icons.info_outline,
        tint: AraColors.accentInfo,
        text: text,
        action: action,
      );

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Container(
      padding: AraBreakpoints.isShort(context)
          ? const EdgeInsets.fromLTRB(14, 8, 14, 8)
          : const EdgeInsets.fromLTRB(14, 12, 14, 12),
      decoration: BoxDecoration(
        color: tint.withValues(alpha: 0.12),
        borderRadius: BorderRadius.circular(10),
      ),
      child: Row(
        children: [
          Icon(icon, size: 18, color: tint),
          const SizedBox(width: 10),
          Expanded(child: Text(text, style: theme.textTheme.bodyMedium)),
          if (action != null) ...[const SizedBox(width: 10), action!],
        ],
      ),
    );
  }
}

/// Centred empty state: a quiet icon, a title, one line of guidance.
class EmptyState extends StatelessWidget {
  final IconData icon;
  final String title;
  final String message;
  final double height;
  const EmptyState({
    super.key,
    required this.icon,
    required this.title,
    required this.message,
    this.height = 180,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return SizedBox(
      height: height,
      child: Center(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icon, size: 36, color: AraColors.textDisabled),
            const SizedBox(height: 10),
            Text(title, style: theme.textTheme.titleMedium),
            const SizedBox(height: 4),
            ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 420),
              child: Text(
                message,
                textAlign: TextAlign.center,
                style: theme.textTheme.bodySmall?.copyWith(
                  color: AraColors.textSecondary,
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// A collapsed "Details" disclosure holding the secondary facts.
class DetailsDisclosure extends StatelessWidget {
  final List<(String, String)> rows;
  const DetailsDisclosure({super.key, required this.rows});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Material(
      color: Colors.transparent,
      child: Theme(
        data: theme.copyWith(dividerColor: Colors.transparent),
        child: ExpansionTile(
          tilePadding: EdgeInsets.zero,
          childrenPadding: const EdgeInsets.only(bottom: 4),
          dense: true,
          title: Text(
            'Details',
            style: theme.textTheme.bodyMedium?.copyWith(
              color: AraColors.textSecondary,
            ),
          ),
          children: [
            for (final (label, value) in rows)
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 3),
                child: Row(
                  children: [
                    Expanded(
                      child: Text(
                        label,
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: AraColors.textSecondary,
                        ),
                      ),
                    ),
                    Text(
                      value,
                      style: theme.textTheme.bodyMedium?.copyWith(
                        fontFeatures: const [FontFeature.tabularFigures()],
                      ),
                    ),
                  ],
                ),
              ),
          ],
        ),
      ),
    );
  }
}
