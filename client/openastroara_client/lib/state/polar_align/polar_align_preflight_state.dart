import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/polar_align.dart';
import 'polar_align_state.dart';

/// #2068 (ARA-18) the Start card's pre-flight list, as the rig evaluates it:
/// every check a start refuses on. Read when a Start card opens
/// (autoDispose), whenever the routine's phase changes (a run started, ended
/// or failed: the mount may stand elsewhere), and after the clearance dialog
/// saves ([refresh]). Null with no rig, or from a rig that predates the list
/// (the card then shows none, and the rig's own start refusals stand).
class PolarAlignPreflightNotifier extends AsyncNotifier<PolarAlignPreflight?> {
  @override
  Future<PolarAlignPreflight?> build() async {
    final api = ref.watch(polarAlignApiProvider);
    if (api == null) return null;
    ref.listen(polarAlignLiveProvider.select((l) => l.phase), (prev, next) {
      if (prev != next) unawaited(refresh());
    });
    return api.preflight();
  }

  /// Read the list again. A failed read is shown (with Retry); it never keeps
  /// an old list that might say a failing check passes.
  Future<void> refresh() async {
    final api = ref.read(polarAlignApiProvider);
    if (api == null) return;
    try {
      final next = await api.preflight();
      if (ref.mounted) state = AsyncData(next);
    } catch (e, st) {
      if (ref.mounted) state = AsyncError(e, st);
    }
  }
}

final polarAlignPreflightProvider =
    AsyncNotifierProvider.autoDispose<
      PolarAlignPreflightNotifier,
      PolarAlignPreflight?
    >(PolarAlignPreflightNotifier.new);

/// True when a check of the rig's list fails, so Start is off: the rig would
/// refuse it. While the list loads, after a failed read, or from a rig with no
/// list, Start stays on and the rig's own start refusals stand. Pure —
/// unit-tested through the panels.
bool polarPreflightBlocksStart(AsyncValue<PolarAlignPreflight?> preflight) =>
    switch (preflight) {
      AsyncData(:final value) => value?.blocksStart ?? false,
      _ => false,
    };
