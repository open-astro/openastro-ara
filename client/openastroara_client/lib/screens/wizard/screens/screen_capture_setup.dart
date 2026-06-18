import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/profile_draft.dart';
import '../../../state/wizard_state.dart';
import '../wizard_form_kit.dart';

ProfileDraft _draftOf(WidgetRef ref) =>
    ref.read(wizardControllerProvider).draft;

// ── Screen 11 — Plate solving (ASTAP) ───────────────────────────────────────

/// §37.4 — points ARA's local plate solver at the ASTAP binary + star database
/// and tunes the search. Binds to the draft's [PlateSolveSettings] bag; the
/// wizard Save maps it onto the profile's plate-solve section.
class ScreenPlateSolve extends ConsumerStatefulWidget {
  const ScreenPlateSolve({super.key});
  @override
  ConsumerState<ScreenPlateSolve> createState() => _ScreenPlateSolveState();
}

class _ScreenPlateSolveState extends ConsumerState<ScreenPlateSolve> {
  late final PlateSolveSettings _ps = _draftOf(ref).plateSolve;

  @override
  Widget build(BuildContext context) {
    return WizardScreenScaffold(
      step: 11,
      intro: 'ASTAP solves your frames to confirm pointing and drive centering. '
          'Point ARA at the ASTAP program and its star database.',
      children: [
        WizardTextField(
          label: 'ASTAP binary path',
          initialValue: _ps.astapBinaryPath,
          hint: r'/usr/local/bin/astap  ·  C:\Program Files\astap\astap.exe',
          onChanged: (v) =>
              _ps.astapBinaryPath = v.trim().isEmpty ? null : v.trim(),
        ),
        WizardTextField(
          label: 'Star database path',
          initialValue: _ps.starDatabasePath,
          helperText:
              'Folder holding the ASTAP star database (e.g. the D50 index).',
          onChanged: (v) =>
              _ps.starDatabasePath = v.trim().isEmpty ? null : v.trim(),
        ),
        WizardTextField(
          label: 'Search radius (°)',
          initialValue: _ps.searchRadiusDeg.toString(),
          helperText:
              'How far from the expected position ASTAP searches. Default 30.',
          keyboardType: const TextInputType.numberWithOptions(decimal: true),
          inputFormatters: WizardInput.unsignedDecimal,
          // Non-nullable in the draft — keep the prior value on a blank/partial
          // keystroke rather than resetting to a default.
          onChanged: (v) {
            final d = double.tryParse(v.trim());
            if (d != null) _ps.searchRadiusDeg = d;
          },
        ),
        WizardDropdown<int>(
          label: 'Downsample factor',
          value: _ps.downsampleFactor,
          helperText:
              'Bin the image before solving — faster on large sensors. Default 2.',
          entries: const [
            DropdownMenuEntry(value: 1, label: '1 — none'),
            DropdownMenuEntry(value: 2, label: '2 — recommended'),
            DropdownMenuEntry(value: 4, label: '4 — large sensors'),
          ],
          onChanged: (v) {
            if (v != null) setState(() => _ps.downsampleFactor = v);
          },
        ),
      ],
    );
  }
}
