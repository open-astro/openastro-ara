import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'mount_state.dart';

/// Home asked from the app (the Mount window, Polar Align), from the tap
/// until the mount stops moving: the screens that sent it read Going Home….
/// Nothing moving within a few seconds (already home, or a fast mount between
/// two reads) clears it.
class MountHomeNotifier extends Notifier<bool> {
  bool _moved = false;
  Timer? _still;

  @override
  bool build() {
    ref.onDispose(() => _still?.cancel());
    ref.listen(mountProvider.select((s) => s.value), (_, m) {
      if (!state) return;
      if (m == null || !m.isConnected) return clear();
      if (m.isBusy) {
        _moved = true;
        _still?.cancel();
        _still = null;
      } else if (_moved) {
        clear();
      }
    });
    return false;
  }

  /// Sends the mount home; true when it took the ask (it throws why not).
  Future<bool> home() async {
    state = true;
    _moved = false;
    _still?.cancel();
    _still = Timer(const Duration(seconds: 3), () {
      if (!_moved) clear();
    });
    try {
      final ok = await ref.read(mountProvider.notifier).findHome();
      if (!ok) clear();
      return ok;
    } catch (_) {
      clear();
      rethrow;
    }
  }

  void clear() {
    _still?.cancel();
    _still = null;
    state = false;
  }
}

final mountHomeProvider = NotifierProvider<MountHomeNotifier, bool>(
  MountHomeNotifier.new,
);
