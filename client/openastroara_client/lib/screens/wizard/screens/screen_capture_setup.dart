import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/profile_draft.dart';
import '../../../state/wizard_state.dart';
import '../wizard_form_kit.dart';

// Read once: the wizard controller reuses a single ProfileDraft instance for the
// whole run (it's never replaced), so caching it as `late final` can't go stale.
ProfileDraft _draftOf(WidgetRef ref) =>
    ref.read(wizardControllerProvider).draft;

// Publish whether this screen's inline validation currently passes, so the
// wizard shell can gate Next / Save Profile (see wizardStepValidProvider).
void _reportStepValid(WidgetRef ref, bool valid) =>
    ref.read(wizardStepValidProvider.notifier).setValid(valid);

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
  late final ProfileDraft _draft = _draftOf(ref);
  late final PlateSolveSettings _ps = _draft.plateSolve;
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
        // The paths carry a clear affordance (the reset suffix): the wizard's
        // new profile clones the active one, so blank alone KEEPS the old
        // rig's path — reset marks the field to take the daemon default on
        // Save; typing anything un-marks it (the typed value wins).
        WizardTextField(
          label: 'ASTAP binary path',
          initialValue: _ps.astapBinaryPath,
          hint: r'/usr/bin/astap  ·  C:\Program Files\astap\astap.exe',
          onChanged: (v) {
            final t = v.trim();
            _ps.astapBinaryPath = t.isEmpty ? null : t;
            if (t.isNotEmpty) {
              _draft.clearedFields.remove(ClearableField.astapBinaryPath);
            }
          },
          onCleared: () {
            _ps.astapBinaryPath = null;
            _draft.clearedFields.add(ClearableField.astapBinaryPath);
          },
        ),
        WizardTextField(
          label: 'Star database path',
          initialValue: _ps.starDatabasePath,
          helperText:
              'Folder holding the ASTAP star database (e.g. the D50 index).',
          onChanged: (v) {
            final t = v.trim();
            _ps.starDatabasePath = t.isEmpty ? null : t;
            if (t.isNotEmpty) {
              _draft.clearedFields.remove(ClearableField.starDatabasePath);
            }
          },
          onCleared: () {
            _ps.starDatabasePath = null;
            _draft.clearedFields.add(ClearableField.starDatabasePath);
          },
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
            String? err;
            if (t.isEmpty) {
              _ps.searchRadiusDeg = null;
            } else {
              final d = double.tryParse(t);
              if (d != null && d > 0 && d <= 180) {
                _ps.searchRadiusDeg = d;
              } else {
                // Out of range OR unparseable (e.g. a lone "1." the formatter
                // lets through): surface the hint and don't write, so no
                // invalid/stale value reaches Save without feedback.
                err = 'Must be greater than 0 and at most 180°.';
              }
            }
            setState(() => _radiusError = err);
            _reportStepValid(ref, err == null);
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
        String? err;
        if (t.isEmpty) {
          set(null);
        } else {
          final n = int.tryParse(t);
          if (n != null && n >= min && (max == null || n <= max)) {
            set(n);
          } else {
            err = rangeError;
          }
        }
        setState(() => _errors[key] = err);
        // The screen is valid only when none of its fields has an error.
        _reportStepValid(ref, _errors.values.every((e) => e == null));
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
        // §59.4/§59.14 — the optical design, declared once. Smart Focus reads
        // per-design defocus features from it (donut diameters on obstructed
        // scopes, FWHM patterns on refractors) and can learn which SIDE of focus
        // a frame is on. Nullable: "Keep profile default" preserves the base.
        WizardDropdown<String?>(
          label: 'Telescope type',
          value: _af.telescopeType,
          helperText: 'Out-of-focus stars look different per optical design — '
              'declaring yours lets autofocus read defocus (and its direction) '
              'from fewer exposures. "Other" is always safe.',
          entries: const [
            DropdownMenuEntry(value: null, label: 'Keep profile default'),
            DropdownMenuEntry(value: 'refractor', label: 'Refractor'),
            DropdownMenuEntry(value: 'sct', label: 'Schmidt-Cassegrain (SCT)'),
            DropdownMenuEntry(value: 'mak', label: 'Maksutov-Cassegrain'),
            DropdownMenuEntry(value: 'rc', label: 'Ritchey-Chrétien (RC)'),
            DropdownMenuEntry(value: 'newtonian', label: 'Newtonian'),
            DropdownMenuEntry(value: 'other', label: 'Other / unknown'),
          ],
          onChanged: (v) => setState(() => _af.telescopeType = v),
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
  late final ProfileDraft _draft = _draftOf(ref);
  late final FileSavingSettings _fs = _draft.fileSaving;

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
          helperText: 'Blank keeps the current profile\'s directory; the reset '
              'button takes the daemon default instead.',
          onChanged: (v) {
            final t = v.trim();
            _fs.saveDirectory = t.isEmpty ? null : t;
            if (t.isNotEmpty) {
              _draft.clearedFields.remove(ClearableField.saveDirectory);
            }
          },
          onCleared: () {
            _fs.saveDirectory = null;
            _draft.clearedFields.add(ClearableField.saveDirectory);
          },
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
              'Blank keeps the current template; reset takes the default.',
          onChanged: (v) {
            final t = v.trim();
            _fs.filenameTemplate = t.isEmpty ? null : t;
            if (t.isNotEmpty) {
              _draft.clearedFields.remove(ClearableField.filenameTemplate);
            }
          },
          onCleared: () {
            _fs.filenameTemplate = null;
            _draft.clearedFields.add(ClearableField.filenameTemplate);
          },
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
            String? err;
            if (t.isEmpty) {
              _img.exposure = null;
            } else {
              final n = int.tryParse(t);
              if (n != null && n > 0) {
                _img.exposure = Duration(seconds: n);
              } else {
                err = 'Enter a whole number of seconds greater than 0.';
              }
            }
            setState(() => _exposureError = err);
            _reportStepValid(ref, err == null);
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

// ── Screen 15 — Safety ──────────────────────────────────────────────────────

/// §37.5 — the compact safety setup: what ARA does when the safety monitor
/// reports unsafe conditions, and whether it auto-resumes. Binds to the draft's
/// [SafetyPolicies] bag; the wizard Save maps it onto the profile's safety
/// section. (Weather-type-granular actions + alarms live in Settings → Safety.)
class ScreenSafety extends ConsumerStatefulWidget {
  const ScreenSafety({super.key});
  @override
  ConsumerState<ScreenSafety> createState() => _ScreenSafetyState();
}

class _ScreenSafetyState extends ConsumerState<ScreenSafety> {
  late final SafetyPolicies _sp = _draftOf(ref).safety;
  String? _windError;
  String? _humidityError;
  String? _dewError;

  bool get _allValid =>
      _delayError == null &&
      _windError == null &&
      _humidityError == null &&
      _dewError == null;
  String? _delayError;

  @override
  Widget build(BuildContext context) {
    return WizardScreenScaffold(
      step: 15,
      intro: 'What ARA does when conditions become unsafe (clouds, wind, rain, '
          'or a safety monitor tripping). Fine-grained policies live in '
          'Settings → Safety.',
      children: [
        WizardDropdown<UnsafeConditionAction?>(
          label: 'When conditions are unsafe',
          value: _sp.onUnsafe,
          helperText: 'How the running sequence reacts to an unsafe report.',
          entries: const [
            DropdownMenuEntry(value: null, label: 'Keep profile default'),
            DropdownMenuEntry(
                value: UnsafeConditionAction.pauseAndPark,
                label: 'Pause & park'),
            DropdownMenuEntry(
                value: UnsafeConditionAction.parkOnly, label: 'Park only'),
            DropdownMenuEntry(
                value: UnsafeConditionAction.abortAndPark,
                label: 'Abort & park'),
            DropdownMenuEntry(
                value: UnsafeConditionAction.ignore, label: 'Ignore'),
          ],
          onChanged: (v) {
            setState(() {
              _sp.onUnsafe = v;
              // Auto-resume + delay are meaningless when ignoring unsafe
              // conditions (nothing pauses), so clear them when Ignore is picked
              // — the fields hide below, and we don't want stale values reaching
              // Save.
              if (v == UnsafeConditionAction.ignore) {
                _sp.autoResumeWhenSafe = null;
                _sp.resumeDelayMin = null;
                _delayError = null;
              }
            });
            // Hiding/clearing the delay field can resolve a standing error.
            _reportStepValid(ref, _allValid);
          },
        ),
        // Hidden under Ignore — there's nothing to resume.
        if (_sp.onUnsafe != UnsafeConditionAction.ignore) ...[
          WizardDropdown<bool?>(
            label: 'Auto-resume when safe again',
            value: _sp.autoResumeWhenSafe,
            helperText: 'Resume a paused sequence once conditions clear.',
            entries: const [
              DropdownMenuEntry(value: null, label: 'Keep profile default'),
              DropdownMenuEntry(value: true, label: 'Yes'),
              DropdownMenuEntry(value: false, label: 'No'),
            ],
            onChanged: (v) => setState(() => _sp.autoResumeWhenSafe = v),
          ),
          WizardTextField(
            label: 'Resume delay (minutes)',
            initialValue: _sp.resumeDelayMin?.toString(),
            hint: 'default 10',
            helperText: 'Wait this long after conditions clear before resuming. '
                'Leave blank to keep the profile default.',
            errorText: _delayError,
            keyboardType: const TextInputType.numberWithOptions(decimal: false),
            inputFormatters: WizardInput.unsignedInt,
            onChanged: (v) {
              final t = v.trim();
              String? err;
              if (t.isEmpty) {
                _sp.resumeDelayMin = null;
              } else {
                final n = int.tryParse(t);
                // 0 is valid (resume immediately); only a non-parse fails here.
                if (n != null && n >= 0) {
                  _sp.resumeDelayMin = n;
                } else {
                  err = 'Enter a whole number of minutes.';
                }
              }
              setState(() => _delayError = err);
              _reportStepValid(ref, _allValid);
            },
          ),
        ],
        // §35.1 weather thresholds — enforced by the daemon now (a breach
        // reacts via the on-unsafe policy above); the fine editor lives in
        // Settings → Safety → Policies.
        WizardDropdown<bool?>(
          label: 'React to weather-station thresholds',
          value: _sp.weatherTriggersEnabled,
          helperText: 'With a weather station connected, a breached wind / '
              'humidity / dew-delta limit counts as unsafe.',
          entries: const [
            DropdownMenuEntry(value: null, label: 'Keep profile default (off)'),
            DropdownMenuEntry(value: true, label: 'On'),
            DropdownMenuEntry(value: false, label: 'Off'),
          ],
          onChanged: (v) {
            setState(() {
              _sp.weatherTriggersEnabled = v;
              if (v != true) {
                // Thresholds are meaningless while the master is off — clear
                // them so stale values (and errors) don't reach Save.
                _sp.maxWindKmh = null;
                _sp.maxHumidityPct = null;
                _sp.minDewDeltaC = null;
                _windError = null;
                _humidityError = null;
                _dewError = null;
              }
            });
            _reportStepValid(ref, _allValid);
          },
        ),
        if (_sp.weatherTriggersEnabled == true) ...[
          WizardTextField(
            label: 'Max wind (km/h, sustained or gust)',
            initialValue: _sp.maxWindKmh?.toString(),
            hint: 'default 36',
            helperText: 'Leave blank to keep the profile default.',
            errorText: _windError,
            keyboardType: const TextInputType.numberWithOptions(decimal: false),
            inputFormatters: WizardInput.unsignedInt,
            onChanged: (v) {
              final t = v.trim();
              String? err;
              if (t.isEmpty) {
                _sp.maxWindKmh = null;
              } else {
                final n = int.tryParse(t);
                if (n != null && n > 0) {
                  _sp.maxWindKmh = n;
                } else {
                  err = 'Enter a positive whole number of km/h.';
                }
              }
              setState(() => _windError = err);
              _reportStepValid(ref, _allValid);
            },
          ),
          WizardTextField(
            label: 'Max humidity (%)',
            initialValue: _sp.maxHumidityPct?.toString(),
            hint: 'default 85',
            helperText: 'Leave blank to keep the profile default.',
            errorText: _humidityError,
            keyboardType: const TextInputType.numberWithOptions(decimal: false),
            inputFormatters: WizardInput.unsignedInt,
            onChanged: (v) {
              final t = v.trim();
              String? err;
              if (t.isEmpty) {
                _sp.maxHumidityPct = null;
              } else {
                final n = int.tryParse(t);
                if (n != null && n > 0 && n <= 100) {
                  _sp.maxHumidityPct = n;
                } else {
                  err = 'Enter a percentage between 1 and 100.';
                }
              }
              setState(() => _humidityError = err);
              _reportStepValid(ref, _allValid);
            },
          ),
          WizardTextField(
            label: 'Min dew delta (\u00b0C above dew point)',
            initialValue: _sp.minDewDeltaC?.toString(),
            hint: 'default 2.0',
            helperText: 'Below this margin, optics are about to fog. Leave '
                'blank to keep the profile default.',
            errorText: _dewError,
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            inputFormatters: WizardInput.unsignedDecimal,
            onChanged: (v) {
              final t = v.trim();
              String? err;
              if (t.isEmpty) {
                _sp.minDewDeltaC = null;
              } else {
                final n = double.tryParse(t);
                if (n != null && n >= 0) {
                  _sp.minDewDeltaC = n;
                } else {
                  err = 'Enter a non-negative number of \u00b0C.';
                }
              }
              setState(() => _dewError = err);
              _reportStepValid(ref, _allValid);
            },
          ),
        ],
      ],
    );
  }
}

// ── Screen 16 — Site & altitude ─────────────────────────────────────────────

/// §37.5 — the horizon floor below which targets aren't observed, and how dark
/// it must be to image. Binds to the draft's [SitePreferences] bag; the wizard
/// Save folds it into the profile's site section (location is from screen 1).
class ScreenSiteAltitude extends ConsumerStatefulWidget {
  const ScreenSiteAltitude({super.key});
  @override
  ConsumerState<ScreenSiteAltitude> createState() =>
      _ScreenSiteAltitudeState();
}

class _ScreenSiteAltitudeState extends ConsumerState<ScreenSiteAltitude> {
  late final SitePreferences _site = _draftOf(ref).site;
  String? _horizonError;

  @override
  Widget build(BuildContext context) {
    return WizardScreenScaffold(
      step: 16,
      intro: 'How low ARA will image and how dark it waits for. Targets below '
          'the horizon altitude are skipped.',
      children: [
        WizardTextField(
          label: 'Horizon altitude (°)',
          initialValue: _site.hardMinAltitudeDeg?.toString(),
          hint: 'default 20',
          helperText: 'Minimum altitude (0–90°) a target must clear to be '
              'imaged. Leave blank to keep the profile default.',
          errorText: _horizonError,
          keyboardType: const TextInputType.numberWithOptions(decimal: true),
          inputFormatters: WizardInput.unsignedDecimal,
          onChanged: (v) {
            final t = v.trim();
            String? err;
            if (t.isEmpty) {
              _site.hardMinAltitudeDeg = null;
            } else {
              final d = double.tryParse(t);
              if (d != null && d >= 0 && d <= 90) {
                _site.hardMinAltitudeDeg = d;
              } else {
                err = 'Enter a value between 0 and 90°.';
              }
            }
            setState(() => _horizonError = err);
            _reportStepValid(ref, err == null);
          },
        ),
        WizardDropdown<TwilightOption?>(
          label: 'Start/end imaging at',
          value: _site.twilight,
          helperText: 'How dark it must be before imaging — astronomical is '
              'darkest (best for deep-sky).',
          entries: const [
            DropdownMenuEntry(value: null, label: 'Keep profile default'),
            DropdownMenuEntry(
                value: TwilightOption.civil, label: 'Civil twilight'),
            DropdownMenuEntry(
                value: TwilightOption.nautical, label: 'Nautical twilight'),
            DropdownMenuEntry(
                value: TwilightOption.astronomical,
                label: 'Astronomical twilight'),
          ],
          onChanged: (v) => setState(() => _site.twilight = v),
        ),
      ],
    );
  }
}
