import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../state/saved_server_state.dart' show activeServerProvider;
import '../../state/simple_mode_state.dart' show companionModeProvider;
import '../../state/ws/ws_providers.dart' show serverLinkUpProvider;
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../widgets/emergency_stop_button.dart';
import 'simple_screen.dart';
import 'simple_shell.dart';

/// #1775 — Companion Mode's portrait shell: a phone held upright folds both
/// rails into one deck at the bottom (DESIGN.md "Simple Mode on a phone",
/// Layout B). The landscape phone and every other device keep the rails.
bool companionPortrait(BuildContext context, WidgetRef ref) =>
    ref.watch(companionModeProvider) &&
    MediaQuery.sizeOf(context).height > MediaQuery.sizeOf(context).width;

/// The section words of the deck's top row, in order; Monitor is the sixth
/// (board pick, 2026-11-12) and opens the read-only page.
const _kDeckSections = [
  SimpleModeScreen.preview,
  SimpleModeScreen.focus,
  SimpleModeScreen.polarAlign,
  SimpleModeScreen.guide,
  SimpleModeScreen.plan,
];

/// The bottom deck. Top row: the section names, swipeable, the current one
/// lit. Second row: Sky Map, Info and Library; the shutter and the settings
/// chip; the 60 px STOP. Past 1.1x text the second row splits in two, so no
/// label is cut short.
class CompanionDeck extends ConsumerWidget {
  const CompanionDeck({
    super.key,
    required this.mode,
    required this.chipLabel,
    required this.settingsOpen,
    required this.sky,
    required this.info,
    required this.onSection,
    required this.onMonitor,
    required this.onSky,
    required this.onInfo,
    required this.onLibrary,
    required this.onSettings,
  });

  /// What shows (Preflight's step included).
  final SimpleModeScreen mode;
  final String chipLabel;
  final bool settingsOpen;
  final bool sky;
  final bool info;
  final ValueChanged<SimpleModeScreen> onSection;
  final VoidCallback onMonitor;
  final VoidCallback onSky;
  final VoidCallback onInfo;
  final VoidCallback onLibrary;
  final VoidCallback onSettings;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = context.l10n;
    final scale = simpleRailScale(context);
    final stopSize = kSimpleStopSize * scale;
    // Calibration is the Guider's second half.
    final lit = mode == SimpleModeScreen.calibration
        ? SimpleModeScreen.guide
        : mode;
    final left = [
      _DeckButton(
        key: const Key('simple-sky'),
        width: 48 * scale,
        glyph: (c) => SkyChartIcon(color: c),
        label: l10n.shellSky,
        selected: sky,
        onTap: onSky,
      ),
      _DeckButton(
        key: const Key('simple-info'),
        width: 48 * scale,
        icon: Icons.info_outline,
        label: l10n.shellInfo,
        selected: info,
        onTap: onInfo,
      ),
      _DeckButton(
        key: const Key('simple-library'),
        width: 48 * scale,
        icon: Icons.photo_library_outlined,
        label: l10n.shellLibrary,
        onTap: onLibrary,
      ),
    ];
    final middle = [
      SimpleScreen.of(mode).shutter,
      _DeckButton(
        key: const Key('simple-settings'),
        icon: Icons.tune,
        label: chipLabel,
        selected: settingsOpen,
        onTap: onSettings,
        lines: 2,
        width: 48 * scale,
      ),
    ];
    // Disabled only on first run: no link and no rig ever chosen. After a
    // dropped link it stays live and says there is no server.
    final stop = SizedBox(
      width: stopSize,
      height: stopSize,
      child: EmergencyStopButton(
        compact: true,
        enabled:
            ref.watch(serverLinkUpProvider) ||
            ref.watch(activeServerProvider) != null,
      ),
    );
    return SimplePanel(
      padding: const EdgeInsets.symmetric(vertical: AraSpace.s8, horizontal: 6),
      child: Column(
        key: const Key('companion-deck'),
        mainAxisSize: MainAxisSize.min,
        spacing: AraSpace.s8,
        children: [
          SingleChildScrollView(
            key: const Key('companion-sections'),
            scrollDirection: Axis.horizontal,
            child: Row(
              spacing: 4,
              children: [
                for (final m in _kDeckSections)
                  _Section(
                    key: Key('companion-section-${m.name}'),
                    label: m.railLabelOf(l10n),
                    selected: m == lit,
                    onTap: () => onSection(m),
                  ),
                _Section(
                  key: const Key('companion-section-monitor'),
                  label: l10n.monitorTitle,
                  selected: false,
                  onTap: onMonitor,
                ),
              ],
            ),
          ),
          if (scale <= 1)
            Row(
              children: [
                ...left,
                const Spacer(),
                ...middle,
                const Spacer(),
                stop,
              ],
            )
          else ...[
            Row(children: [for (final b in left) Expanded(child: b)]),
            Row(children: [...middle, const Spacer(), stop]),
          ],
        ],
      ),
    );
  }
}

/// One word of the section row: a 44 px-high chip, lit when current.
class _Section extends StatelessWidget {
  const _Section({
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
      color: selected ? AraColors.selectionBg : Colors.transparent,
      borderRadius: BorderRadius.circular(AraRadius.container),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(AraRadius.container),
        child: ConstrainedBox(
          constraints: const BoxConstraints(minHeight: 44, minWidth: 44),
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: AraSpace.s12),
            child: Center(
              child: Text(
                label,
                maxLines: 1,
                style: kSimpleText.copyWith(
                  fontWeight: FontWeight.w600,
                  color: selected
                      ? AraColors.selectionFg
                      : AraColors.textSecondary,
                ),
              ),
            ),
          ),
        ),
      ),
    ),
  );
}

/// An icon over a short caption, at least 44 px each way. The caption shrinks
/// to fit its button rather than being cut.
class _DeckButton extends StatelessWidget {
  const _DeckButton({
    super.key,
    this.icon,
    this.glyph,
    required this.label,
    required this.onTap,
    this.selected = false,
    this.lines = 1,
    this.width,
  });

  /// Null: the width the parent gives (the split row's shares).
  final double? width;

  final IconData? icon;
  final Widget Function(Color color)? glyph;
  final String label;
  final VoidCallback onTap;
  final bool selected;
  final int lines;

  @override
  Widget build(BuildContext context) {
    final color = selected ? AraColors.textPrimary : AraColors.textSecondary;
    final scale = simpleRailScale(context);
    return Semantics(
      button: true,
      selected: selected,
      label: label,
      excludeSemantics: true,
      child: Material(
        color: selected ? AraColors.selectionTint : Colors.transparent,
        borderRadius: BorderRadius.circular(AraRadius.container),
        child: InkWell(
          onTap: onTap,
          borderRadius: BorderRadius.circular(AraRadius.container),
          child: SizedBox(
            width: width,
            height: 56 * scale,
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 2),
              child: Column(
                mainAxisAlignment: MainAxisAlignment.center,
                children: [
                  glyph != null
                      ? glyph!(color)
                      : Icon(icon, size: 24, color: color),
                  FittedBox(
                    fit: BoxFit.scaleDown,
                    child: Text(
                      label,
                      maxLines: lines,
                      textAlign: TextAlign.center,
                      style: AraText.caption.copyWith(color: color),
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}
