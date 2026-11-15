import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'loaded_section.dart';

import '../../services/profile_api.dart';

/// §51 diagnostics mode. Phase 12h.6j wires the daemon round-trip via
/// [ProfileApi] (`/api/v1/profile/diagnostics-mode`). The picker
/// auto-saves on each radio tap (no Save button — single-choice UX),
/// so [persistToServer] runs from the panel's `_selectAndSave` after
/// it calls [setMode] to update local state optimistically.

enum DiagnosticsMode { notifyOnly, pauseOnCritical, abortOnCritical }

class DiagnosticsModeNotifier extends SectionNotifier<DiagnosticsMode> {
  void setMode(DiagnosticsMode m) => edit((c) => m);

  @override
  Future<Tagged<DiagnosticsMode>> fetch(ProfileApi api) =>
      api.getDiagnosticsModeSection();

  @override
  Future<Tagged<DiagnosticsMode>> send(
    ProfileApi api,
    DiagnosticsMode value,
    String? etag,
  ) => api.putDiagnosticsModeSection(value, ifMatch: etag);
}

final diagnosticsModeProvider =
    NotifierProvider<DiagnosticsModeNotifier, LoadedSection<DiagnosticsMode>>(
      DiagnosticsModeNotifier.new,
    );
