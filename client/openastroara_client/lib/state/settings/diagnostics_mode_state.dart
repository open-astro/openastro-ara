import 'package:flutter_riverpod/flutter_riverpod.dart';

/// §51 diagnostics mode. Phase 12h.2-diagnostics holds state in memory;
/// 12h.2b wires `/api/v1/profile/diagnostics-mode` for daemon round-trip.

enum DiagnosticsMode { notifyOnly, pauseOnCritical, abortOnCritical }

class DiagnosticsModeNotifier extends Notifier<DiagnosticsMode> {
  @override
  DiagnosticsMode build() => DiagnosticsMode.notifyOnly;

  void setMode(DiagnosticsMode m) => state = m;
}

final diagnosticsModeProvider =
    NotifierProvider<DiagnosticsModeNotifier, DiagnosticsMode>(
        DiagnosticsModeNotifier.new);
