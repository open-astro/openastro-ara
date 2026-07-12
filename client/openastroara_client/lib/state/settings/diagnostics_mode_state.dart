import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'settings_sync_mixin.dart';

import '../../services/profile_api.dart';

/// §51 diagnostics mode. Phase 12h.6j wires the daemon round-trip via
/// [ProfileApi] (`/api/v1/profile/diagnostics-mode`). The picker
/// auto-saves on each radio tap (no Save button — single-choice UX),
/// so [persistToServer] runs from the panel's `_selectAndSave` after
/// it calls [setMode] to update local state optimistically.

enum DiagnosticsMode { notifyOnly, pauseOnCritical, abortOnCritical }

class DiagnosticsModeNotifier extends Notifier<DiagnosticsMode>
    with SettingsSyncMixin<DiagnosticsMode> {
  @override
  DiagnosticsMode build() => DiagnosticsMode.notifyOnly;

  void setMode(DiagnosticsMode m) => state = m;

  Future<void> hydrateFromServer(ProfileApi api) =>
      hydrateGuarded(() => api.getDiagnosticsMode());

  Future<DiagnosticsMode> persistToServer(ProfileApi api) =>
      persistGuarded((sent) => api.putDiagnosticsMode(sent));
}

final diagnosticsModeProvider =
    NotifierProvider<DiagnosticsModeNotifier, DiagnosticsMode>(
        DiagnosticsModeNotifier.new);
