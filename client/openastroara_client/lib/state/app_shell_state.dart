import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Selected tab index for `AppShell` (Planning=0, Run=1, Live=2, Options=3).
/// Lifted from local widget state in Phase 12h.3 so the §61 ⌘K palette can jump
/// straight to a settings panel by selecting the Options tab and updating
/// `selectedSettingsPanelProvider`. (Planning merged the old Sky Atlas + Framing
/// tabs — PORT_DECISIONS §36/§25.5; the §54 Support area now lives inside the
/// Options settings tree, not its own tab.)
class SelectedTabIndexNotifier extends Notifier<int> {
  static const _tabCount = 4; // Planning / Run / Live / Options
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
