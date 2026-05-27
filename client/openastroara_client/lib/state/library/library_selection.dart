import 'package:flutter_riverpod/flutter_riverpod.dart';

/// §40.8 multi-select state for the Image Library. Holds the set of
/// currently-selected frame ids. Selection-mode UX: long-press a thumbnail
/// to enter selection mode + add that frame; subsequent taps toggle
/// selection. The bulk action bar shows whenever the set is non-empty.
///
/// Phase 12f.3a ships the selection state + bulk action bar UI; the action
/// handlers (Rate / Tag / Move / Delete / Export) wire up in 12f.3b once
/// `/api/v1/frames/bulk` is defined.
class LibrarySelectionNotifier extends Notifier<Set<String>> {
  @override
  // const empty set is already unmodifiable, so consumers can't mutate
  // the initial state to bypass the notifier.
  Set<String> build() => const <String>{};

  void toggle(String frameId) {
    final next = Set<String>.from(state);
    if (!next.add(frameId)) next.remove(frameId);
    // Wrap in Set.unmodifiable so external consumers can't bypass the
    // notifier by mutating the published set directly (would cause UI
    // drift since the notifier wouldn't see the change).
    state = Set<String>.unmodifiable(next);
  }

  void clear() {
    // const empty set is already unmodifiable; no need to wrap.
    if (state.isNotEmpty) state = const <String>{};
  }

  bool contains(String frameId) => state.contains(frameId);
}

final librarySelectionProvider =
    NotifierProvider<LibrarySelectionNotifier, Set<String>>(
        LibrarySelectionNotifier.new);

/// True when selection mode is active (one or more frames are selected).
final librarySelectionActiveProvider = Provider<bool>(
  (ref) => ref.watch(librarySelectionProvider).isNotEmpty,
);
