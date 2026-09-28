import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Selected tab index for `AppShell` (Planning=0, Setup=1, Run=2, Live=3,
/// Options=4).
/// Lifted from local widget state in Phase 12h.3 so the §61 ⌘K palette can jump
/// straight to a settings panel by selecting the Options tab and updating
/// `selectedSettingsPanelProvider`. (Planning merged the old Sky Atlas + Framing
/// tabs — PORT_DECISIONS §36/§25.5; the §54 Support area now lives inside the
/// Options settings tree, not its own tab.)
class SelectedTabIndexNotifier extends Notifier<int> {
  static const _tabCount = 5; // Planning / Setup / Run / Live / Options
  @override
  int build() => 0;
  void select(int index) {
    if (index < 0 || index >= _tabCount) return;
    state = index;
    // Mark here, at the one place a tab can be chosen, rather than from the
    // shell's build (#1111 PR C): the shell used to remember visits in its
    // own State via a post-frame setState, which made every first visit a
    // build with side effects plus an extra rebuild.
    ref.read(visitedTabsProvider.notifier).mark(index);
  }
}

/// Tabs that have been shown at least once this shell lifetime. `AppShell`
/// builds a tab's real body only once it is in this set (or is the current
/// selection), and keeps it alive thereafter so the Planning webview and
/// other tab state persist across switches.
///
/// autoDispose: the set lives exactly as long as something watches it (the
/// shell), so a remounted shell starts lazy again instead of eagerly
/// inflating every tab a previous shell had opened.
class VisitedTabsNotifier extends Notifier<Set<int>> {
  @override
  Set<int> build() => {ref.read(selectedTabIndexProvider)};

  void mark(int index) {
    if (state.contains(index)) return;
    state = {...state, index};
  }
}

final visitedTabsProvider =
    NotifierProvider.autoDispose<VisitedTabsNotifier, Set<int>>(
        VisitedTabsNotifier.new);

final selectedTabIndexProvider =
    NotifierProvider<SelectedTabIndexNotifier, int>(
        SelectedTabIndexNotifier.new);
