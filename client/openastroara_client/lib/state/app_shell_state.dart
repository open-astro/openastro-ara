import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Selected tab index for `AppShell` (Imaging=0, Framing=1, Sequencer=2,
/// Sky Atlas=3, Options=4). Lifted from local widget state in Phase 12h.3
/// so the §61 ⌘K palette can jump straight to a settings panel by selecting
/// the Options tab and updating `selectedSettingsPanelProvider`.
class SelectedTabIndexNotifier extends Notifier<int> {
  @override
  int build() => 0;
  void select(int index) => state = index;
}

final selectedTabIndexProvider =
    NotifierProvider<SelectedTabIndexNotifier, int>(
        SelectedTabIndexNotifier.new);
