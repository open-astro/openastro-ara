import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../state/app_shell_state.dart';
import '../theme/ara_colors.dart';
import '../widgets/command_palette.dart';
import '../widgets/equipment/equipment_status_chip.dart';
import '../widgets/help_dialog.dart';
import '../widgets/ws_connection_indicator.dart';
import 'calibration/calibration_screen.dart';
import 'library/image_library_screen.dart';
import 'stats/stats_dashboard_screen.dart';
import 'tabs/imaging_tab.dart';
import 'tabs/options_tab.dart';
import 'tabs/planning_tab.dart';
import 'tabs/sequencer_tab.dart';
import 'wizard/wizard_shell.dart';

/// Main app shell — replaces the first-run screen once a server is saved
/// (playbook §25 layout: nav rail on left, top equipment bar, center
/// workspace, bottom status). Per-area content lives in the 4 tab widgets, in
/// workflow order: Planning (find + frame the target) → Run (build + run the
/// sequence) → Live (capture / live view) → Options. Support (§54 logs +
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
    _TabSpec(icon: Icons.play_circle_outline, label: 'Run', body: SequencerTab()),
    _TabSpec(icon: Icons.camera_alt, label: 'Live', body: ImagingTab()),
    _TabSpec(icon: Icons.settings, label: 'Options', body: OptionsTab()),
  ];

  // Indices that have been visited at least once. A tab isn't built until first
  // selected; once built it stays in this set so the IndexedStack keeps it alive
  // (see the IndexedStack comment below). Monotonic — never removed.
  final Set<int> _builtTabs = <int>{};
  // Tabs with a post-frame "mark visited" callback already queued. Guards against
  // a second build() in the same frame (e.g. two provider updates before the
  // callback fires) queuing a duplicate callback — which would otherwise trigger
  // a spurious extra rebuild on first open.
  final Set<int> _pendingTabs = <int>{};

  @override
  Widget build(BuildContext context) {
    final selectedTab = ref.watch(selectedTabIndexProvider);
    // Record the current tab as visited via a post-frame callback rather than
    // mutating _builtTabs directly in build() (keeps build side-effect-free). The
    // local `liveTabs` below already includes selectedTab, so the tab renders this
    // frame without waiting for the callback — no first-open flash. `_pendingTabs`
    // ensures only one callback is queued per unvisited tab.
    if (!_builtTabs.contains(selectedTab) && _pendingTabs.add(selectedTab)) {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        _pendingTabs.remove(selectedTab);
        if (mounted) setState(() => _builtTabs.add(selectedTab));
      });
    }
    final liveTabs = {..._builtTabs, selectedTab};
    // Ties kOptionsTabIndex (used by the equipment chips to route to a device's
    // settings panel) to the actual tab order — a reorder of _tabs that forgets
    // to update the constant trips this in debug instead of silently navigating
    // to the wrong tab.
    assert(_tabs[kOptionsTabIndex].label == 'Options',
        'kOptionsTabIndex must point at the Options tab — update it if _tabs is reordered.');
    return Scaffold(
      body: SafeArea(
        child: CallbackShortcuts(
          // §61 ⌘K on macOS, Ctrl+K elsewhere — both bound so the palette
          // is reachable regardless of host platform.
          bindings: <ShortcutActivator, VoidCallback>{
            const SingleActivator(LogicalKeyboardKey.keyK, meta: true): () =>
                showCommandPalette(context),
            const SingleActivator(LogicalKeyboardKey.keyK, control: true): () =>
                showCommandPalette(context),
          },
          child: Focus(
            autofocus: true,
            child: Column(
              children: [
                const _TopEquipmentBar(),
                Expanded(
                  child: Row(
                    children: [
                      NavigationRail(
                        selectedIndex: selectedTab,
                        onDestinationSelected: (i) => ref
                            .read(selectedTabIndexProvider.notifier)
                            .select(i),
                        labelType: NavigationRailLabelType.all,
                        destinations: [
                          for (final t in _tabs)
                            NavigationRailDestination(
                              icon: Icon(t.icon),
                              label: Text(t.label),
                            ),
                        ],
                      ),
                      const VerticalDivider(width: 1, thickness: 1),
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
                      // selected (it's in _builtTabs) and stays alive thereafter —
                      // so the atlas still persists across switches once opened.
                      Expanded(
                        child: IndexedStack(
                          index: selectedTab,
                          children: [
                            for (var i = 0; i < _tabs.length; i++)
                              liveTabs.contains(i)
                                  ? _tabs[i].body
                                  : const SizedBox.shrink(),
                          ],
                        ),
                      ),
                    ],
                  ),
                ),
                const _BottomStatusBar(),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _TabSpec {
  final IconData icon;
  final String label;
  final Widget body;
  const _TabSpec({required this.icon, required this.label, required this.body});
}

class _TopEquipmentBar extends StatelessWidget {
  const _TopEquipmentBar();

  @override
  Widget build(BuildContext context) {
    final shortcutLabel = Theme.of(context).platform == TargetPlatform.macOS
        ? '⌘K'
        : 'Ctrl+K';
    return Container(
      height: 64,
      decoration: const BoxDecoration(
        color: AraColors.bgPanel,
        border: Border(bottom: BorderSide(color: AraColors.border)),
      ),
      child: Row(
        children: [
          const Expanded(
            child: SingleChildScrollView(
              scrollDirection: Axis.horizontal,
              padding: EdgeInsets.symmetric(horizontal: 8),
              // Per §25.3 device-type order; each chip is live (status dot) +
              // clickable (routes to its Settings panel). See TopEquipmentChips.
              child: TopEquipmentChips(),
            ),
          ),
          // §61.1 — visible magnifying-glass icon on the right side of the
          // top bar. Opens the same `showCommandPalette` dialog that ⌘K
          // launches; tooltip surfaces the platform-correct shortcut so
          // keyboard-first users learn it.
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 8),
            child: IconButton(
              icon: const Icon(Icons.search, size: 22),
              tooltip: 'Search settings ($shortcutLabel)',
              onPressed: () => showCommandPalette(context),
            ),
          ),
        ],
      ),
    );
  }
}

class _BottomStatusBar extends StatelessWidget {
  const _BottomStatusBar();

  @override
  Widget build(BuildContext context) {
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
          const Spacer(),
          // Image Library entry (§40). Full-screen route — captured frames
          // grouped by session per 12f.1's in-memory demo; real backend in
          // 12f.2.
          TextButton.icon(
            onPressed: () => Navigator.of(context).push(
              MaterialPageRoute<void>(
                builder: (_) => const ImageLibraryScreen(),
              ),
            ),
            icon: const Icon(Icons.photo_library_outlined, size: 16),
            label: const Text('Image Library'),
            style: TextButton.styleFrom(
              foregroundColor: AraColors.textSecondary,
              textStyle: Theme.of(context).textTheme.bodySmall,
            ),
          ),
          // Calibration entry (§39.10). Full-screen route — live calibration
          // sessions + matching-flats generation + the dark library.
          TextButton.icon(
            onPressed: () => Navigator.of(context).push(
              MaterialPageRoute<void>(
                builder: (_) => const CalibrationScreen(),
              ),
            ),
            icon: const Icon(Icons.flare_outlined, size: 16),
            label: const Text('Calibration'),
            style: TextButton.styleFrom(
              foregroundColor: AraColors.textSecondary,
              textStyle: Theme.of(context).textTheme.bodySmall,
            ),
          ),
          // Stats dashboard entry (§50). Full-screen route — overview tiles
          // + Targets rollup + Best Frames over the library demo data.
          // Real charts (fl_chart) + per-target detail land in 12g.2/.3.
          TextButton.icon(
            onPressed: () => Navigator.of(context).push(
              MaterialPageRoute<void>(
                builder: (_) => const StatsDashboardScreen(),
              ),
            ),
            icon: const Icon(Icons.insights, size: 16),
            label: const Text('Stats'),
            style: TextButton.styleFrom(
              foregroundColor: AraColors.textSecondary,
              textStyle: Theme.of(context).textTheme.bodySmall,
            ),
          ),
          // Profile wizard entry (§37). Launches the 18-screen wizard as a
          // full-screen route; per-screen content is being filled in across
          // Phase 12b follow-up PRs.
          TextButton.icon(
            onPressed: () => Navigator.of(context).push(
              MaterialPageRoute<void>(
                builder: (_) => const WizardShell(),
                fullscreenDialog: true,
              ),
            ),
            icon: const Icon(Icons.tune, size: 16),
            label: const Text('Run profile wizard'),
            style: TextButton.styleFrom(
              foregroundColor: AraColors.textSecondary,
              textStyle: Theme.of(context).textTheme.bodySmall,
            ),
          ),
          // Bug-report entry (§54) — wired in a Phase 12a follow-up.
          IconButton(
            icon: const Icon(Icons.help_outline, size: 18),
            tooltip: 'Help / Report a bug (§54)',
            onPressed: () => showHelpDialog(context),
          ),
          const SizedBox(width: 4),
        ],
      ),
    );
  }
}
