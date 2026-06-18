import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/profile_draft.dart';
import '../../../state/wizard_state.dart';
import '../wizard_form_kit.dart';

// Read once: the wizard controller reuses a single ProfileDraft instance for the
// whole run (it's never replaced), so caching it as `late final` can't go stale.
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
  // Advisory display only: an invalid radius is never written to the draft, so
  // Save is safe even if the user advances with the error showing. The wizard
  // shell has no per-screen validity gate yet (tracked in design/PORT_TODO.md).
  String? _radiusError;

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
          hint: r'/usr/bin/astap  ·  C:\Program Files\astap\astap.exe',
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
          initialValue: _ps.searchRadiusDeg?.toString(),
          hint: 'default 30',
          helperText: 'How far from the expected position ASTAP searches '
              '(0–180°). Leave blank to keep the profile default.',
          errorText: _radiusError,
          keyboardType: const TextInputType.numberWithOptions(decimal: true),
          inputFormatters: WizardInput.unsignedDecimal,
          // Blank → null (preserve base on Save). Out of the notifier's 0 < v ≤ 180
          // range → surface an error and don't write, so an invalid value isn't
          // silently dropped (and never reaches the daemon to fail late).
          onChanged: (v) {
            final t = v.trim();
            if (t.isEmpty) {
              setState(() => _radiusError = null);
              _ps.searchRadiusDeg = null;
              return;
            }
            final d = double.tryParse(t);
            if (d != null && d > 0 && d <= 180) {
              setState(() => _radiusError = null);
              _ps.searchRadiusDeg = d;
            } else {
              // Out of range OR unparseable (e.g. a lone "1." the formatter lets
              // through): surface the hint and don't write, so no invalid/stale
              // value reaches Save without feedback.
              setState(() =>
                  _radiusError = 'Must be greater than 0 and at most 180°.');
            }
          },
        ),
        // Nullable so the selected entry communicates "not set" the way a blank
        // text field does: null shows "Keep profile default" and Save preserves
        // the base; picking 1/2/4 overrides it (no misleading visual fallback).
        WizardDropdown<int?>(
          label: 'Downsample factor',
          value: _ps.downsampleFactor,
          helperText:
              'Bin the image before solving — faster on large sensors.',
          entries: const [
            DropdownMenuEntry(value: null, label: 'Keep profile default'),
            DropdownMenuEntry(value: 1, label: '1 — none'),
            DropdownMenuEntry(value: 2, label: '2 — recommended'),
            DropdownMenuEntry(value: 4, label: '4 — large sensors'),
          ],
          onChanged: (v) => setState(() => _ps.downsampleFactor = v),
        ),
      ],
    );
  }
}

// ── Screen 12 — Autofocus ───────────────────────────────────────────────────

/// §37.4 — the routine ARA runs to refocus. Binds to the draft's
/// [AutofocusSettings] bag; the wizard Save maps it onto the profile's autofocus
/// section. All fields are optional (blank/untouched preserves the base).
class ScreenAutofocus extends ConsumerStatefulWidget {
  const ScreenAutofocus({super.key});
  @override
  ConsumerState<ScreenAutofocus> createState() => _ScreenAutofocusState();
}

class _ScreenAutofocusState extends ConsumerState<ScreenAutofocus> {
  late final AutofocusSettings _af = _draftOf(ref).autofocus;
  // Per-field validation messages (advisory: an invalid value is never written).
  final Map<String, String?> _errors = {};

  /// A positive-integer field: blank → null (keep base), a valid `> 0` value is
  /// written, and anything else surfaces an inline error without writing.
  Widget _posIntField({
    required String key,
    required String label,
    required String hint,
    required String helper,
    required int? value,
    required void Function(int?) set,
  }) {
    return WizardTextField(
      label: label,
      initialValue: value?.toString(),
      hint: hint,
      helperText: helper,
      errorText: _errors[key],
      keyboardType: const TextInputType.numberWithOptions(decimal: false),
      inputFormatters: WizardInput.unsignedInt,
      onChanged: (v) {
        final t = v.trim();
        if (t.isEmpty) {
          setState(() => _errors[key] = null);
          set(null);
          return;
        }
        final n = int.tryParse(t);
        if (n != null && n > 0) {
          setState(() => _errors[key] = null);
          set(n);
        } else {
          setState(() => _errors[key] = 'Enter a whole number greater than 0.');
        }
      },
    );
  }

  @override
  Widget build(BuildContext context) {
    return WizardScreenScaffold(
      step: 12,
      intro: 'How ARA refocuses — the exposure it takes at each step, how many '
          'steps it samples, and the focuser travel between them.',
      children: [
        _posIntField(
          key: 'exposure',
          label: 'Autofocus exposure (s)',
          hint: 'default 5',
          helper: 'Exposure for each autofocus sample. Leave blank to keep the '
              'profile default.',
          value: _af.exposureSeconds,
          set: (n) => _af.exposureSeconds = n,
        ),
        _posIntField(
          key: 'steps',
          label: 'Steps',
          hint: 'default 7',
          helper: 'How many focus points to sample across the V-curve.',
          value: _af.steps,
          set: (n) => _af.steps = n,
        ),
        _posIntField(
          key: 'stepSize',
          label: 'Step size (focuser steps)',
          hint: 'default 50',
          helper: 'Focuser travel between each autofocus point.',
          value: _af.stepSize,
          set: (n) => _af.stepSize = n,
        ),
        // Nullable so "Keep profile default" preserves the base (same pattern as
        // the downsample dropdown); Yes/No override.
        WizardDropdown<bool?>(
          label: 'Re-run autofocus after a filter change',
          value: _af.runAfterFilterChange,
          helperText: 'Refocus when the sequence switches filters '
              '(focus shifts between filters).',
          entries: const [
            DropdownMenuEntry(value: null, label: 'Keep profile default'),
            DropdownMenuEntry(value: true, label: 'Yes'),
            DropdownMenuEntry(value: false, label: 'No'),
          ],
          onChanged: (v) => setState(() => _af.runAfterFilterChange = v),
        ),
      ],
    );
  }
}
