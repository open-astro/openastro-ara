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

  /// A bounded-integer field: blank → null (keep base); a value in [[min], [max]]
  /// is written; anything else surfaces an inline error without writing (so an
  /// out-of-range value never reaches the daemon). [max] null = no upper bound.
  Widget _posIntField({
    required String key,
    required String label,
    required String hint,
    required String helper,
    required int? value,
    required void Function(int?) set,
    int min = 1,
    int? max,
  }) {
    final rangeError = max == null
        ? 'Enter a whole number of at least $min.'
        : 'Enter a whole number between $min and $max.';
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
        if (n != null && n >= min && (max == null || n <= max)) {
          setState(() => _errors[key] = null);
          set(n);
        } else {
          setState(() => _errors[key] = rangeError);
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
          // Bounds match AutofocusSettingsNotifier.setSteps — the V-curve fit
          // needs at least 3 points, and the routine caps at 31.
          min: 3,
          max: 31,
          helper: 'How many focus points to sample across the V-curve (3–31).',
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

// ── Screen 13 — File saving ─────────────────────────────────────────────────

/// §37.4 — where captures are written and in what shape. Binds to the draft's
/// [FileSavingSettings] bag; the wizard Save maps it onto the profile's storage
/// section.
class ScreenFileSaving extends ConsumerStatefulWidget {
  const ScreenFileSaving({super.key});
  @override
  ConsumerState<ScreenFileSaving> createState() => _ScreenFileSavingState();
}

class _ScreenFileSavingState extends ConsumerState<ScreenFileSaving> {
  late final FileSavingSettings _fs = _draftOf(ref).fileSaving;

  @override
  Widget build(BuildContext context) {
    return WizardScreenScaffold(
      step: 13,
      intro: 'Where ARA saves captured frames and how it names them. A USB drive '
          'is recommended (§29) so a full system disk can\'t stall a session.',
      children: [
        WizardTextField(
          label: 'Save directory',
          initialValue: _fs.saveDirectory,
          hint: r'/media/usb/astro  ·  D:\Astro\Captures',
          helperText: 'Leave blank to keep the profile default.',
          onChanged: (v) =>
              _fs.saveDirectory = v.trim().isEmpty ? null : v.trim(),
        ),
        WizardDropdown<ImageFormat?>(
          label: 'File format',
          value: _fs.format,
          helperText: 'FITS is the broadly-compatible default; XISF is '
              'PixInsight-native.',
          entries: const [
            DropdownMenuEntry(value: null, label: 'Keep profile default'),
            DropdownMenuEntry(value: ImageFormat.fits, label: 'FITS'),
            DropdownMenuEntry(value: ImageFormat.xisf, label: 'XISF'),
          ],
          onChanged: (v) => setState(() => _fs.format = v),
        ),
        WizardDropdown<bool?>(
          label: 'Compress files',
          value: _fs.compress,
          helperText: 'Lossless Rice compression — smaller files, no quality '
              'loss. (Choose Gzip later in Settings → Storage.)',
          entries: const [
            DropdownMenuEntry(value: null, label: 'Keep profile default'),
            DropdownMenuEntry(value: true, label: 'Yes (Rice)'),
            DropdownMenuEntry(value: false, label: 'No'),
          ],
          onChanged: (v) => setState(() => _fs.compress = v),
        ),
        WizardTextField(
          label: 'Filename template',
          initialValue: _fs.filenameTemplate,
          helperText: r'Tokens like $$DATETIME$$, $$FILTER$$, $$EXPOSURETIME$$. '
              'Leave blank to keep the profile default.',
          onChanged: (v) =>
              _fs.filenameTemplate = v.trim().isEmpty ? null : v.trim(),
        ),
      ],
    );
  }
}

// ── Screen 14 — Imaging defaults ────────────────────────────────────────────

/// §37.4 — the default capture parameters a new exposure starts from. Binds to
/// the draft's [ImagingDefaults] bag (exposure + frame kind); gain / offset /
/// binning + cooling come from the Camera screen (5). The wizard Save folds both
/// into the profile's imaging-defaults section.
class ScreenImagingDefaults extends ConsumerStatefulWidget {
  const ScreenImagingDefaults({super.key});
  @override
  ConsumerState<ScreenImagingDefaults> createState() =>
      _ScreenImagingDefaultsState();
}

class _ScreenImagingDefaultsState
    extends ConsumerState<ScreenImagingDefaults> {
  late final ImagingDefaults _img = _draftOf(ref).imagingDefaults;
  String? _exposureError;

  @override
  Widget build(BuildContext context) {
    return WizardScreenScaffold(
      step: 14,
      intro: 'The default capture parameters a new exposure starts from. Gain, '
          'offset, binning and cooling come from your camera setup (screen 5).',
      children: [
        WizardTextField(
          label: 'Default exposure (s)',
          initialValue: _img.exposure?.inSeconds.toString(),
          hint: 'e.g. 120',
          helperText: 'Leave blank to keep the profile default.',
          errorText: _exposureError,
          keyboardType: const TextInputType.numberWithOptions(decimal: false),
          inputFormatters: WizardInput.unsignedInt,
          onChanged: (v) {
            final t = v.trim();
            if (t.isEmpty) {
              setState(() => _exposureError = null);
              _img.exposure = null;
              return;
            }
            final n = int.tryParse(t);
            if (n != null && n > 0) {
              setState(() => _exposureError = null);
              _img.exposure = Duration(seconds: n);
            } else {
              setState(() =>
                  _exposureError = 'Enter a whole number of seconds greater than 0.');
            }
          },
        ),
        WizardDropdown<FrameType?>(
          label: 'Default frame type',
          value: _img.frameType,
          helperText: 'What a fresh capture defaults to — usually Light.',
          entries: const [
            DropdownMenuEntry(value: null, label: 'Keep profile default'),
            DropdownMenuEntry(value: FrameType.light, label: 'Light'),
            DropdownMenuEntry(value: FrameType.dark, label: 'Dark'),
            DropdownMenuEntry(value: FrameType.flat, label: 'Flat'),
            DropdownMenuEntry(value: FrameType.bias, label: 'Bias'),
          ],
          onChanged: (v) => setState(() => _img.frameType = v),
        ),
      ],
    );
  }
}
