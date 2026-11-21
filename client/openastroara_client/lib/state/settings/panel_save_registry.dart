import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// §25.5.5 — the currently visible settings panel's Save action, rendered by
/// the settings shell as a single Save button in the panel HEADER (top-right).
/// Panels used to each carry their own Save at the bottom of their scroll view,
/// which was invisible until the user scrolled — the action now lives in fixed
/// chrome. Null = the visible panel has nothing to save (read-only panels,
/// equipment panels that apply instantly).
final panelSaveActionProvider =
    NotifierProvider<PanelSaveActionNotifier, Future<void> Function()?>(
        PanelSaveActionNotifier.new);

class PanelSaveActionNotifier extends Notifier<Future<void> Function()?> {
  @override
  Future<void> Function()? build() => null;

  void register(Future<void> Function() action) => state = action;

  /// Clear only when [action] is still the registered one — when the user
  /// switches panels, the NEW panel may register before the old one's dispose
  /// runs, and that registration must survive. (Same-instance method tear-offs
  /// compare equal; another panel's never does.)
  void unregister(Future<void> Function() action) {
    // The unregister is microtask-deferred, so the whole container can be gone
    // by the time it runs (app teardown, test ProviderScope disposal).
    if (!ref.mounted) return;
    if (state == action) state = null;
  }
}

/// Mixin for a settings panel with a Save round-trip: registers [panelSave]
/// with the shell header on mount and unregisters on dispose. The header owns
/// the button + its busy spinner; the panel keeps owning what saving MEANS
/// (which sections to PUT, error snackbars, partial-failure wording).
mixin PanelSaveRegistration<W extends ConsumerStatefulWidget>
    on ConsumerState<W> {
  /// The panel's save round-trip. Must be safe to call repeatedly; surface
  /// errors to the user itself (snackbar) — the header only awaits it.
  Future<void> panelSave();

  PanelSaveActionNotifier? _registry;

  @override
  void initState() {
    super.initState();
    // Post-frame: a provider must not be mutated while the tree is building.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      _registry = ref.read(panelSaveActionProvider.notifier);
      _registry!.register(panelSave);
    });
  }

  @override
  void dispose() {
    final registry = _registry;
    final own = panelSave;
    if (registry != null) {
      // Deferred so the unregister never mutates the provider mid-build (panel
      // switches dispose during a rebuild).
      scheduleMicrotask(() => registry.unregister(own));
    }
    super.dispose();
  }
}
