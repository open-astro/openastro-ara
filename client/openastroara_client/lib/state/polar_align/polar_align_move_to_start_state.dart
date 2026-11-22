import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/polar_align.dart';
import 'polar_align_preflight_state.dart';
import 'polar_align_routine_state.dart';
import 'polar_align_state.dart';

/// #2166 the move to the start position is under way. It lives here, not in
/// the pre-flight list's widget: a tab or mode switch mid-move rebuilds the
/// list, and its progress line and Stop must come back while the mount still
/// moves. Not autoDispose; a server switch starts it over (false).
class PolarMoveToStartNotifier extends Notifier<bool> {
  @override
  bool build() {
    ref.watch(polarAlignApiProvider);
    return false;
  }

  /// Ask the rig to move (the caller has shown the motion confirm). [failed]
  /// words a move the rig ran and failed, [describe] a call that threw; either
  /// goes to the panel's error line. The list is read again at the end.
  Future<void> move({
    required String Function(PolarAlignMoveToStartResult result) failed,
    required String Function(Object error) describe,
  }) async {
    final api = ref.read(polarAlignApiProvider);
    if (api == null || state) return;
    state = true;
    String? failure;
    try {
      final result = await api.moveToStart();
      if (result.outcome == PolarMoveToStartOutcomes.failed) {
        failure = failed(result);
      }
    } catch (e) {
      failure = describe(e);
    }
    if (!ref.mounted) return;
    state = false;
    if (failure != null) {
      ref.read(polarAlignActionProvider.notifier).fail(failure);
    }
    await ref.read(polarAlignPreflightProvider.notifier).refresh();
  }
}

final polarMoveToStartProvider =
    NotifierProvider<PolarMoveToStartNotifier, bool>(
      PolarMoveToStartNotifier.new,
    );
