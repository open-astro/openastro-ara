import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Selected tab index for `AppShell` (Imaging=0, Planning=1, Sequencer=2,
/// Options=3). Lifted from local widget state in Phase 12h.3 so the §61 ⌘K
/// palette can jump straight to a settings panel by selecting the Options tab
/// and updating `selectedSettingsPanelProvider`. (Planning merged the old Sky
/// Atlas + Framing tabs — PORT_DECISIONS §36/§25.5 — so the count dropped 5→4
/// and Options moved 4→3.)
class SelectedTabIndexNotifier extends Notifier<int> {
  static const _tabCount = 4; // Imaging / Planning / Sequencer / Options
  @override
  int build() => 0;
  void select(int index) {
    if (index < 0 || index >= _tabCount) return;
    state = index;
  }
}

final selectedTabIndexProvider =
    NotifierProvider<SelectedTabIndexNotifier, int>(
        SelectedTabIndexNotifier.new);
