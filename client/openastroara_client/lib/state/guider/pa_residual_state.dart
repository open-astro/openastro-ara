import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/pa_residual.dart';
import '../ws/ws_providers.dart';
import 'guider_state.dart';

/// #1311 — the polar alignment residual the daemon is measuring or measured
/// from guiding; null when nothing has been measured since the guider
/// connected. Fed by `guider.pa_residual` and seeded from the guider status
/// (`runtime.pa_residual`), so a client that connects mid-run catches up.
/// Root-scoped like the guide graph: it keeps up while another tab is open.
class PaResidualNotifier extends Notifier<PaResidual?> {
  @override
  PaResidual? build() {
    ref.listen(wsEventsProvider, (prev, next) {
      final event = next.asData?.value;
      if (event == null || event.type != 'guider.pa_residual') return;
      // `{status: "idle"}` (a dropped measurement with nothing before it)
      // parses to null and clears the readout.
      state = PaResidual.fromJson(event.payload);
    });
    ref.listen(guiderStatusProvider, (prev, next) {
      final status = next.asData?.value;
      if (status == null) return;
      final snapshot = status.paResidual;
      if (isStale(state, snapshot)) return;
      state = snapshot;
    });
    return ref.read(guiderStatusProvider).asData?.value?.paResidual;
  }

  /// A REST poll that left before a WS event can land after it. For the same
  /// run, never step back from a finished result to `measuring`, nor to less
  /// progress than already shown.
  static bool isStale(PaResidual? current, PaResidual? snapshot) {
    if (current == null || snapshot == null || current.id != snapshot.id) {
      return false;
    }
    if (snapshot.status != PaResidualStatus.measuring) return false;
    return current.status != PaResidualStatus.measuring ||
        snapshot.sampleSeconds < current.sampleSeconds;
  }
}

final paResidualProvider = NotifierProvider<PaResidualNotifier, PaResidual?>(
  PaResidualNotifier.new,
);
