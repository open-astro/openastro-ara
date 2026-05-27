import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Live View loop state per §64. Lifted out of `_ImagingTabState`'s local
/// widget state (12c.1 CR finding) so the bottom status bar can mirror
/// "Looping" / "Off" and any other component can observe it.
///
/// 12c.2 wires the toggle to a UI-only flag. The actual frame-loop polling
/// (debounced exposure scheduler that calls /api/v1/sequence/exposure on a
/// short repeat) lands in Phase 12c.3 once the daemon's exposure endpoint
/// is service-side functional.
class LiveViewController extends Notifier<bool> {
  @override
  bool build() => false;

  void toggle() => state = !state;
  void set(bool v) => state = v;
}

final liveViewControllerProvider =
    NotifierProvider<LiveViewController, bool>(LiveViewController.new);
