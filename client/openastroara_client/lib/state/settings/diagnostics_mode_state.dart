import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/profile_api.dart';

/// §51 diagnostics mode. Phase 12h.6j wires the daemon round-trip via
/// [ProfileApi] (`/api/v1/profile/diagnostics-mode`). The picker
/// auto-saves on each radio tap (no Save button — single-choice UX),
/// so [persistToServer] runs from `setMode`.

enum DiagnosticsMode { notifyOnly, pauseOnCritical, abortOnCritical }

class DiagnosticsModeNotifier extends Notifier<DiagnosticsMode> {
  @override
  DiagnosticsMode build() => DiagnosticsMode.notifyOnly;

  void setMode(DiagnosticsMode m) => state = m;

  Future<void> hydrateFromServer(ProfileApi api) async {
    state = await api.getDiagnosticsMode();
  }

  Future<DiagnosticsMode> persistToServer(ProfileApi api) async {
    final echoed = await api.putDiagnosticsMode(state);
    state = echoed;
    return echoed;
  }
}

final diagnosticsModeProvider =
    NotifierProvider<DiagnosticsModeNotifier, DiagnosticsMode>(
        DiagnosticsModeNotifier.new);
