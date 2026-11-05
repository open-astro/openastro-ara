import 'dart:io' show Platform;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../state/app_shell_state.dart';
import '../state/night_mode_state.dart';
import '../state/settings/settings_nav.dart';
import '../theme/ara_colors.dart';
import '../widgets/notifications/notification_center.dart';
import '../widgets/backup_stream_chip.dart';
import '../widgets/command_palette.dart';
import '../widgets/emergency_stop_button.dart';
import 'shell_listeners.dart';
import '../widgets/equipment/equipment_status_chip.dart';

import 'package:flutter/foundation.dart' show defaultTargetPlatform;

import '../services/notch_side.dart';
import '../widgets/equipment_chip.dart'
    show EquipmentChipCompact, kEquipmentChipCompactWidth, kEquipmentChipWidth;
import '../widgets/help_dialog.dart';
import '../widgets/settings_gear_button.dart';
import '../widgets/simple_mode_switch_button.dart';
import '../widgets/launchpad_button.dart';
import '../widgets/ws_connection_indicator.dart';
import 'calibration/calibration_screen.dart';
import 'library/image_library_screen.dart';
import 'stats/stats_dashboard_screen.dart';
import 'tabs/imaging_tab.dart';
import 'tabs/options_tab.dart';
import 'tabs/planning_tab.dart';
import 'tabs/sequencer_tab.dart';
import 'tabs/setup_tab.dart';
import 'wizard/wizard_shell.dart';

/// Main app shell — replaces the first-run screen once a server is saved
/// (playbook §25 layout: nav rail on left, top equipment bar, center
/// workspace, bottom status). Per-area content lives in the 5 tab widgets, in
/// workflow order — the rail reads as the night: Planning (find + frame the
/// target) → Setup (the dusk ritual: connect, polar align, calibration) →
/// Run (build + run the sequence) → Live (capture / live view) → Options. Support (§54 logs +
/// bug report) folds into the Options settings tree rather than being its own
/// destination. (Planning merges the old Sky Atlas + Framing tabs,
/// PORT_DECISIONS §36/§25.5.)
/// Equipment chips along the top are placeholder until Phase 12c wires the
/// chooser bottom-sheet + Alpaca discovery flow per §25.3.
class AppShell extends ConsumerStatefulWidget {
  const AppShell({super.key});

  @override
  ConsumerState<AppShell> createState() => _AppShellState();
}

class _AppShellState extends ConsumerState<AppShell> {
  static const _tabs = <_TabSpec>[
    _TabSpec(icon: Icons.public, label: 'Planning', body: PlanningTab()),
    _TabSpec(icon: Icons.checklist, label: 'Checklist', body: SetupTab()),
    _TabSpec(
      icon: Icons.play_circle_outline,
      label: 'Sequence',
      body: SequencerTab(),
    ),
    _TabSpec(icon: Icons.camera_alt, label: 'Live', body: ImagingTab()),
    _TabSpec(icon: Icons.settings, label: 'Settings', body: OptionsTab()),
  ];

  // §61 — the shell's shortcuts only see keys when focus is inside it. The
  // root NightHotkey's autofocus wins first, so a plain `autofocus` here is
  // ignored; take focus explicitly once mounted.
  final _focus = FocusNode(debugLabel: 'AppShell');

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) _focus.requestFocus();
    });
  }

  @override
  void dispose() {
    _focus.dispose();
    super.dispose();
  }

  void _openSettings() =>
      ref.read(selectedTabIndexProvider.notifier).select(kOptionsTabIndex);

  @override
  Widget build(BuildContext context) {
    final selectedTab = ref.watch(selectedTabIndexProvider);
    final narrow =
        MediaQuery.sizeOf(context).width < kEquipmentChipCompactWidth;
    // Visited tabs are provider state marked from select() (#1111 PR C), so
    // this build has no side effects and no post-frame setState. The union
    // with selectedTab keeps the invariant even if the two providers ever
    // publish out of step.
    final visited = ref.watch(visitedTabsProvider);
    final liveTabs = {...visited, selectedTab};
    // Ties kOptionsTabIndex (used by the equipment chips to route to a device's
    // settings panel) to the actual tab order — a reorder of _tabs that forgets
    // to update the constant trips this in debug instead of silently navigating
    // to the wrong tab.
    assert(
      _tabs[kOptionsTabIndex].label == 'Settings' &&
          kOptionsTabIndex == _tabs.length - 1,
      'kOptionsTabIndex must point at the Settings tab, the last one (the rail lists the tabs before it).',
    );
    assert(
      _tabs[kRunTabIndex].label == 'Sequence',
      'kRunTabIndex must point at the Sequence tab — update it if _tabs is reordered.',
    );
    assert(
      _tabs[kPlanningTabIndex].label == 'Planning',
      'kPlanningTabIndex must point at the Planning tab — update it if _tabs is reordered.',
    );
    assert(
      _tabs[kSetupTabIndex].label == 'Checklist',
      'kSetupTabIndex must point at the Checklist tab — update it if _tabs is reordered.',
    );
    assert(
      _tabs[kLiveTabIndex].label == 'Live',
      'kLiveTabIndex must point at the Live tab — update it if _tabs is reordered.',
    );
    return Scaffold(
      // §27 — daemon-initiated connection-policy modals (takeover request /
      // session transferred) listen here, at the always-alive shell level.
      // §48 — the "capture calibration tonight?" prompt nests inside for the
      // same reason: it must fire no matter which tab is showing.
      // The always-alive listeners (policy modals, alarms, Stop Mount, ...)
      // are shared with SimpleShell so both shells react the same way.
      body: ShellListeners(
        child: _EdgeToEdge(
          child: CallbackShortcuts(
            // §61 ⌘K on macOS, Ctrl+K elsewhere — both bound so the palette
            // is reachable regardless of host platform.
            bindings: <ShortcutActivator, VoidCallback>{
              const SingleActivator(LogicalKeyboardKey.keyK, meta: true): () =>
                  showCommandPalette(context),
              const SingleActivator(
                LogicalKeyboardKey.keyK,
                control: true,
              ): () =>
                  showCommandPalette(context),
              // ⌘, / Ctrl+, — the platform-standard Settings shortcut.
              const SingleActivator(LogicalKeyboardKey.comma, meta: true):
                  _openSettings,
              const SingleActivator(LogicalKeyboardKey.comma, control: true):
                  _openSettings,
            },
            child: Focus(
              focusNode: _focus,
              child: Column(
                children: [
                  _TopEquipmentBar(showChips: !narrow),
                  Expanded(
                    child: Row(
                      // Stretch so the device column fills the height
                      // and starts at the top, like the rail.
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        // A phone-width window swaps the bars: the
                        // devices stand in a column here and the tabs
                        // move to a bottom bar (below).
                        if (narrow)
                          const _DeviceColumn()
                        else
                          NavigationRail(
                            // Settings opens from the top-bar gear, so the
                            // rail lists the workflow tabs only.
                            selectedIndex: railIndexFor(selectedTab),
                            onDestinationSelected: (i) => ref
                                .read(selectedTabIndexProvider.notifier)
                                .select(i),
                            labelType: NavigationRailLabelType.all,
                            destinations: [
                              for (final t in _tabs.take(kOptionsTabIndex))
                                NavigationRailDestination(
                                  icon: Icon(t.icon),
                                  label: Text(t.label),
                                ),
                            ],
                          ),
                        // Ara's own border tone, not the theme default —
                        // which renders as a bright line against the
                        // near-black nav rail.
                        const VerticalDivider(
                          width: 1,
                          thickness: 1,
                          color: AraColors.border,
                        ),
                        // IndexedStack (not `_tabs[selectedTab].body`) so a tab,
                        // once built, is KEPT ALIVE and merely hidden when another is
                        // selected. Critical for the Planning/Sky Atlas tab: its native
                        // webview must persist — tearing it down on a tab-switch and
                        // re-creating it reloads the planetarium and loses atlas state.
                        //
                        // Lazy build: an unvisited tab renders an empty placeholder
                        // instead of its real body, so we DON'T run every tab's
                        // initState at startup (no eager API/poll calls before the
                        // user even opens that tab). A tab builds the first time it's
                        // selected (it's in visitedTabsProvider) and stays alive thereafter —
                        // so the atlas still persists across switches once opened.
                        Expanded(
                          child: buildTabStack(
                            selected: selectedTab,
                            visited: liveTabs,
                            bodies: [for (final t in _tabs) t.body],
                          ),
                        ),
                      ],
                    ),
                  ),
                  const _BottomStatusBar(),
                  if (narrow)
                    NavigationBar(
                      // Settings (from the gear) has no tab here:
                      // nothing is highlighted while it shows.
                      selectedIndex: railIndexFor(selectedTab) ?? 0,
                      indicatorColor: railIndexFor(selectedTab) == null
                          ? Colors.transparent
                          : null,
                      onDestinationSelected: (i) =>
                          ref.read(selectedTabIndexProvider.notifier).select(i),
                      destinations: [
                        for (final t in _tabs.take(kOptionsTabIndex))
                          NavigationDestination(
                            icon: Icon(t.icon),
                            label: t.label,
                          ),
                      ],
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

/// The shell's tab stack: an [IndexedStack] showing [selected], with every
/// tab in [visited] holding its real body and the rest a placeholder. Kept
/// as a plain function of its inputs so `app_shell_tab_stack_test` can pin
/// the keep-alive and lazy-build contract without the shell's providers.
@visibleForTesting
Widget buildTabStack({
  required int selected,
  required Set<int> visited,
  required List<Widget> bodies,
}) {
  return IndexedStack(
    index: selected,
    children: [
      for (var i = 0; i < bodies.length; i++)
        visited.contains(i) ? bodies[i] : const SizedBox.shrink(),
    ],
  );
}

class _TabSpec {
  final IconData icon;
  final String label;
  final Widget body;
  const _TabSpec({required this.icon, required this.label, required this.body});
}

class _TopEquipmentBar extends StatelessWidget {
  const _TopEquipmentBar({required this.showChips});

  /// False on a phone-width window, where the devices sit in [_DeviceColumn].
  final bool showChips;

  @override
  Widget build(BuildContext context) {
    final shortcutLabel = Theme.of(context).platform == TargetPlatform.macOS
        ? '⌘K'
        : 'Ctrl+K';
    return Container(
      height: showChips ? 64 : 48,
      decoration: const BoxDecoration(
        color: AraColors.bgPanel,
        border: Border(bottom: BorderSide(color: AraColors.border)),
      ),
      child: Row(
        children: [
          if (!showChips)
            const Spacer()
          else
            Expanded(
              // Names only when every named chip fits; otherwise icons only, so
              // no device is pushed off screen (an iPad in portrait, a phone in
              // landscape, a narrowed desktop window).
              child: LayoutBuilder(
                builder: (context, constraints) => EquipmentChipCompact(
                  compact:
                      constraints.maxWidth <
                      TopEquipmentChips.count * kEquipmentChipWidth + 16,
                  child: const SingleChildScrollView(
                    scrollDirection: Axis.horizontal,
                    padding: EdgeInsets.symmetric(horizontal: 8),
                    // Per §25.3 order (most used first); each chip is live (status dot) +
                    // clickable (routes to its Settings panel). See TopEquipmentChips.
                    child: TopEquipmentChips(),
                  ),
                ),
              ),
            ),
          // §61.1 — visible magnifying-glass icon on the right side of the
          // top bar. Opens the same `showCommandPalette` dialog that ⌘K
          // launches; tooltip surfaces the platform-correct shortcut so
          // keyboard-first users learn it.
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 2),
            child: IconButton(
              icon: const Icon(Icons.search, size: 22),
              tooltip: 'Search settings ($shortcutLabel)',
              onPressed: () => showCommandPalette(context),
            ),
          ),
          // Night mode toggle — one tap from any tab; the same as the
          // Ctrl+N / Cmd+N hotkey.
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 2),
            child: Consumer(
              builder: (context, ref, _) {
                final night = switch (ref.watch(nightModeProvider)) {
                  AsyncData(:final value) => value,
                  _ => false,
                };
                return IconButton(
                  icon: Icon(
                    night ? Icons.nights_stay : Icons.nights_stay_outlined,
                    size: 22,
                    color: night ? AraColors.accentInfo : null,
                  ),
                  tooltip: night
                      ? 'Night mode on (${Platform.isMacOS ? '⌘' : 'Ctrl+'}N)'
                      : 'Night mode (${Platform.isMacOS ? '⌘' : 'Ctrl+'}N)',
                  onPressed: () =>
                      ref.read(nightModeProvider.notifier).set(!night),
                );
              },
            ),
          ),
          // ALP-4810 — into Simple Mode, behind Settings → System → Experimental.
          const SimpleModeSwitchButton(),
          // Settings last, at the right edge.
          const Padding(
            padding: EdgeInsets.only(right: 6),
            child: SettingsGearButton(),
          ),
        ],
      ),
    );
  }
}

/// [SafeArea] whose margins (a phone's notch on both sides in landscape, the
/// status bar, the home indicator) are painted in the bars' colour instead of
/// left black, so the shell reads as full screen while its content stays clear
/// of them.
class _EdgeToEdge extends StatefulWidget {
  const _EdgeToEdge({required this.child});

  final Widget child;

  @override
  State<_EdgeToEdge> createState() => _EdgeToEdgeState();
}

class _EdgeToEdgeState extends State<_EdgeToEdge> with WidgetsBindingObserver {
  NotchSide _notch = NotchSide.unknown;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _probe();
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    super.dispose();
  }

  // A rotation changes the window's metrics: ask again which side the island is on.
  @override
  void didChangeMetrics() => _probe();

  Future<void> _probe() async {
    if (defaultTargetPlatform != TargetPlatform.iOS) return;
    final side = await NotchSideProbe.side();
    if (mounted && side != _notch) setState(() => _notch = side);
  }

  @override
  Widget build(BuildContext context) {
    final pad = MediaQuery.paddingOf(context);
    // Equal side margins = iOS's symmetric landscape reservation; only then is
    // the side without the island free to use.
    final symmetric = pad.left > 0 && pad.left == pad.right;
    return ColoredBox(
      color: AraColors.bgPanel,
      child: SafeArea(
        left: !(symmetric && _notch == NotchSide.right),
        right: !(symmetric && _notch == NotchSide.left),
        child: ColoredBox(
          color: Theme.of(context).scaffoldBackgroundColor,
          child: widget.child,
        ),
      ),
    );
  }
}

/// The device chips standing in a column down the left edge, for a
/// phone-width window (the tabs take the bottom bar there).
class _DeviceColumn extends StatelessWidget {
  const _DeviceColumn();

  @override
  Widget build(BuildContext context) => const ColoredBox(
    color: AraColors.bgPanel,
    child: SingleChildScrollView(
      padding: EdgeInsets.symmetric(vertical: 4),
      child: TopEquipmentChips(direction: Axis.vertical),
    ),
  );
}

/// One status-bar launcher: a full-screen route or an action.
class _Launcher {
  const _Launcher(this.label, this.icon, this.open);
  final String label;
  final IconData icon;
  final void Function(BuildContext, WidgetRef) open;
}

void _push(
  BuildContext context,
  Widget screen, {
  bool fullscreenDialog = false,
}) => Navigator.of(context).push(
  MaterialPageRoute<void>(
    builder: (_) => screen,
    fullscreenDialog: fullscreenDialog,
  ),
);

final _launchers = <_Launcher>[
  // Image Library (§40), Calibration (§39.10), Stats dashboard (§50),
  // back to the launchpad (§30), profile wizard (§37).
  _Launcher(
    'Image Library',
    Icons.photo_library_outlined,
    (c, _) => _push(c, const ImageLibraryScreen()),
  ),
  _Launcher(
    'Calibration',
    Icons.flare_outlined,
    (c, _) => _push(c, const CalibrationScreen()),
  ),
  _Launcher(
    'Stats',
    Icons.insights,
    (c, _) => _push(c, const StatsDashboardScreen()),
  ),
  _Launcher('Launchpad', Icons.rocket_launch_outlined, backToLaunchpad),
  _Launcher(
    'Run profile wizard',
    Icons.tune,
    (c, _) => _push(c, const WizardShell(), fullscreenDialog: true),
  ),
];

// Room the labelled form needs beyond the text: button padding (2 x 12), icon
// (16) and the icon-label gap (8). An icon-only button is 40 wide.
const double _labelExtra = 48;
const double _iconSlot = 40;
// Notification bell + help button (48 each) + the trailing 4 px.
const double _tailWidth = 100;

class _BottomStatusBar extends ConsumerWidget {
  const _BottomStatusBar();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    return Container(
      // 40px gives `TextButton.icon` enough breathing room at default
      // and 110% text-scale; 32 was clipping the launchers.
      height: 40,
      decoration: const BoxDecoration(
        color: AraColors.bgPanel,
        border: Border(top: BorderSide(color: AraColors.border)),
      ),
      child: Row(
        children: [
          const SizedBox(width: 8),
          const WsConnectionIndicator(),
          // §35.3 — the big red button lives on the always-visible status
          // bar: reachable in one tap from any tab, exactly when panicking.
          const EmergencyStopButton(),
          // §44 — the backup stream's live pulse (hidden while disabled).
          const BackupStreamChip(),
          // The launcher group takes the width left over and picks its form
          // from it: labels, then icons, then icons plus a More menu.
          Expanded(
            child: LayoutBuilder(
              builder: (context, box) => Align(
                alignment: Alignment.centerRight,
                child: _launcherGroup(context, ref, box.maxWidth),
              ),
            ),
          ),
        ],
      ),
    );
  }

  Widget _launcherGroup(BuildContext context, WidgetRef ref, double width) {
    final style = Theme.of(context).textTheme.bodySmall;
    final labelsWidth = _launchers.fold<double>(0, (sum, l) {
      final painter = TextPainter(
        text: TextSpan(text: l.label, style: style),
        textDirection: TextDirection.ltr,
        textScaler: MediaQuery.textScalerOf(context),
      )..layout();
      return sum + painter.width + _labelExtra;
    });
    final labelled = labelsWidth + _tailWidth <= width;
    final iconSlots = ((width - _tailWidth) / _iconSlot).floor();
    // Every launcher as an icon, or as many as fit plus a More button.
    final shown = labelled || iconSlots >= _launchers.length
        ? _launchers.length
        : (iconSlots - 1).clamp(0, _launchers.length);
    final overflow = _launchers.sublist(shown);

    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        for (final l in _launchers.take(shown))
          labelled
              ? TextButton.icon(
                  onPressed: () => l.open(context, ref),
                  icon: Icon(l.icon, size: 16),
                  label: Text(l.label),
                  style: TextButton.styleFrom(
                    foregroundColor: AraColors.textSecondary,
                    textStyle: style,
                  ),
                )
              : IconButton(
                  icon: Icon(l.icon, size: 16),
                  tooltip: l.label,
                  visualDensity: VisualDensity.compact,
                  color: AraColors.textSecondary,
                  onPressed: () => l.open(context, ref),
                ),
        if (overflow.isNotEmpty)
          PopupMenuButton<String>(
            tooltip: 'More',
            icon: const Icon(Icons.more_horiz, size: 18),
            onSelected: (label) =>
                overflow.firstWhere((l) => l.label == label).open(context, ref),
            itemBuilder: (_) => [
              for (final l in overflow)
                PopupMenuItem<String>(
                  value: l.label,
                  child: Row(
                    children: [
                      Icon(l.icon, size: 16),
                      const SizedBox(width: 8),
                      Text(l.label),
                    ],
                  ),
                ),
            ],
          ),
        // Everything Ara decided while you weren't looking (§46).
        const NotificationBell(),
        // Bug-report entry (§54) — wired in a Phase 12a follow-up.
        IconButton(
          icon: const Icon(Icons.help_outline, size: 18),
          tooltip: 'Help & report a bug',
          onPressed: () => showHelpDialog(context),
        ),
        const SizedBox(width: 4),
      ],
    );
  }
}
