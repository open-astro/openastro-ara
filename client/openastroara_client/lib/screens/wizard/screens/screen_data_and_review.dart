import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/profile_draft.dart';
import '../../../util/friendly_error.dart';
import '../../../state/sky_atlas/data_manager_state.dart';
import '../../../models/solver_recommendation.dart';
import '../../../state/wizard_state.dart';
import '../../../theme/ara_colors.dart';
import '../wizard_form_kit.dart';

/// Human-readable size for a package (`1.2 GB`, `340.0 MB`, `1.5 KB`). One
/// decimal place for every scaled unit (KB/MB/GB) so sizes round consistently —
/// raw bytes stay whole. A non-positive size (a malformed/absent server value)
/// floors to `0 B` rather than rendering a nonsensical negative.
String formatBytes(int bytes) {
  if (bytes <= 0) return '0 B';
  if (bytes >= 1 << 30) return '${(bytes / (1 << 30)).toStringAsFixed(1)} GB';
  if (bytes >= 1 << 20) return '${(bytes / (1 << 20)).toStringAsFixed(1)} MB';
  if (bytes >= 1 << 10) return '${(bytes / (1 << 10)).toStringAsFixed(1)} KB';
  return '$bytes B';
}

// ── Screen 17 — Review + Save ────────────────────────────────────────────────

const String _notSet = 'Not set';

/// Render a scalar draft value for the review summary: null / blank → "Not set",
/// bool → Yes/No, double via [formatNumber] (no float noise), everything else
/// via toString. Keeps the section row builders terse and gives one consistent
/// "unset → default applies" wording (§37.8).
String reviewValue(Object? v) {
  if (v == null) return _notSet;
  if (v is String) return v.trim().isEmpty ? _notSet : v.trim();
  if (v is bool) return v ? 'Yes' : 'No';
  if (v is double) return formatNumber(v);
  return v.toString();
}

/// Format a double for display without floating-point noise: round to
/// [decimals] places then strip trailing zeros (`51.50000001` → `51.5`,
/// `45.0` → `45`). Used for every inline numeric field so a GPS- or
/// user-typed value never renders as `31.200000000000003`.
String formatNumber(double v, {int decimals = 4}) {
  var s = v.toStringAsFixed(decimals);
  if (s.contains('.')) {
    s = s.replaceAll(RegExp(r'0+$'), '').replaceAll(RegExp(r'\.$'), '');
  }
  return s;
}

/// A double with a unit suffix (`102 mm`, `-10 °C`), or "Not set" when null.
String _unit(double? v, String unit, {int decimals = 4}) =>
    v == null ? _notSet : '${formatNumber(v, decimals: decimals)} $unit';

/// A duration as the user thinks of it: whole minutes when it divides evenly
/// (`5 min`), otherwise seconds (`90 s`); null → "Not set".
String formatDuration(Duration? d) {
  if (d == null) return _notSet;
  final s = d.inSeconds;
  if (s >= 60 && s % 60 == 0) return '${s ~/ 60} min';
  return '$s s';
}

/// A degrees value with the unit appended (`31.2°`), or "Not set" when null.
String _deg(double? v) => v == null ? _notSet : '${formatNumber(v)}°';

/// The equipment slots the user assigned, as a comma-joined label list, or
/// "None assigned" when every slot is left empty.
String assignedEquipment(EquipmentSlots e) {
  final labels = <String>[
    if (e.cameraDeviceId != null) 'Camera',
    if (e.filterWheelDeviceId != null) 'Filter Wheel',
    if (e.focuserDeviceId != null) 'Focuser',
    if (e.mountDeviceId != null) 'Mount',
    if (e.rotatorDeviceId != null) 'Rotator',
    if (e.domeDeviceId != null) 'Dome',
    if (e.observingConditionsDeviceId != null) 'Conditions',
    if (e.switchDeviceIds.isNotEmpty)
      e.switchDeviceIds.length == 1
          ? 'Switch'
          : 'Switches ×${e.switchDeviceIds.length}',
    if (e.safetyMonitorDeviceId != null) 'Safety Monitor',
    if (e.flatPanelDeviceId != null) 'Flat Panel',
  ];
  return labels.isEmpty ? 'None assigned' : labels.join(', ');
}

/// §76.2 Screen 5 — final review. A read-only, section-grouped summary of everything the
/// wizard collected, each section with an "Edit" affordance that jumps back to
/// the relevant screen. The Save itself is the shell's bottom-nav "Save Profile"
/// button on this last step — this screen doesn't own persistence.
class ScreenReview extends ConsumerWidget {
  const ScreenReview({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final draft = ref.watch(wizardControllerProvider).draft;
    void edit(int step) =>
        ref.read(wizardControllerProvider.notifier).jumpTo(step);

    final t = draft.telescope;
    final fr = (t.focalLengthMm != null &&
            t.apertureMm != null &&
            t.apertureMm! > 0)
        ? 'f/${(t.focalLengthMm! / t.apertureMm!).toStringAsFixed(1)}'
        : _notSet;
    final c = draft.camera;
    final f = draft.focuser;
    final m = draft.mount;
    final r = draft.rotator;
    final g = draft.guider;
    final ps = draft.plateSolve;
    final af = draft.autofocus;
    final fs = draft.fileSaving;
    final id = draft.imagingDefaults;
    final sf = draft.safety;
    final si = draft.site;

    return WizardScreenScaffold(
      step: 5,
      intro: 'Review your profile. Tap Edit on any section to jump back and '
          'change it, then choose Save Profile below to finish. Anything left '
          '"Not set" keeps the built-in default — you can adjust it later in '
          'Settings.',
      children: [
        _ReviewSection(title: 'Profile basics', step: 1, onEdit: edit, rows: [
          ('Name', reviewValue(draft.profileName)),
          ('Site name', reviewValue(draft.siteName)),
          ('Latitude', _deg(draft.latitudeDeg)),
          ('Longitude', _deg(draft.longitudeDeg)),
          ('Altitude', _unit(draft.altitudeMeters, 'm')),
          ('Time zone', reviewValue(draft.timezone)),
        ]),
        // AlpacaBridge address is set on step 2 (connect); device assignment on
        // step 3 — keep them as separate Edit targets so each jumps to its own
        // screen.
        _ReviewSection(title: 'Connection', step: 2, onEdit: edit, rows: [
          ('AlpacaBridge', reviewValue(draft.alpacaBridgeAddress)),
        ]),
        _ReviewSection(title: 'Equipment', step: 3, onEdit: edit, rows: [
          ('Assigned', assignedEquipment(draft.equipment)),
        ]),
        _ReviewSection(title: 'Telescope', step: 3, onEdit: edit, rows: [
          ('Name', reviewValue(t.name)),
          ('Focal length', _unit(t.focalLengthMm, 'mm')),
          ('Aperture', _unit(t.apertureMm, 'mm')),
          // Null = untouched with no rig to read the base profile from: Save
          // then keeps the profile's own reducer, so never claim 1× here.
          ('Reducer / Barlow', t.reducerFactor == null ? "Profile's value (not set here)" : '${t.reducerFactor}× → ${t.effectiveFocalLengthMm?.toStringAsFixed(0) ?? '?'} mm'),
          ('Focal ratio', fr),
        ]),
        _ReviewSection(title: 'Camera', step: 3, onEdit: edit, rows: [
          ('Cooling target', _unit(c.coolingTargetC, '°C')),
          ('Warmup', switch (c.warmupMode) {
            CoolerWarmupMode.off => 'Off',
            CoolerWarmupMode.ramp => 'Ramp',
            CoolerWarmupMode.immediate => 'Immediate',
          }),
          ('Gain / Offset', '${reviewValue(c.defaultGain)} / '
              '${reviewValue(c.defaultOffset)}'),
          ('Binning', reviewValue(c.defaultBin)),
          ('Pixel size', _unit(c.pixelSizeMicrons, 'µm')),
          ('Read noise', _unit(c.readNoiseE, 'e⁻')),
          ('Peak QE', _unit(c.qePeakPct, '%')),
        ]),
        _ReviewSection(title: 'Filter wheel', step: 3, onEdit: edit, rows: [
          ('Filters', draft.filterWheel.filters.isEmpty
              ? 'None defined'
              : '${draft.filterWheel.filters.length} defined'),
        ]),
        _ReviewSection(title: 'Focuser', step: 3, onEdit: edit, rows: [
          ('Step size', _unit(f.stepSizeMicrons, 'µm')),
          ('Backlash in/out',
              '${reviewValue(f.backlashInSteps)} / ${reviewValue(f.backlashOutSteps)}'),
          ('Temp. compensation', reviewValue(f.temperatureCompensationEnabled)),
        ]),
        _ReviewSection(title: 'Mount', step: 3, onEdit: edit, rows: [
          ('Name', reviewValue(m.name)),
          ('Slew rate', _unit(m.slewRateDegPerSec, '°/s')),
          ('Meridian flip', switch (m.meridianFlip) {
            MeridianFlipBehavior.auto => 'Auto',
            MeridianFlipBehavior.prompt => 'Prompt',
            MeridianFlipBehavior.never => 'Never',
          }),
          ('Settle time', formatDuration(m.settleTimeAfterSlew)),
        ]),
        _ReviewSection(title: 'Rotator', step: 3, onEdit: edit, rows: [
          // A range needs both ends — if either is unset, fall back to "Not set"
          // rather than rendering a half-filled "Not set – 45.0°".
          ('Range', (r.minAngleDeg == null || r.maxAngleDeg == null)
              ? _notSet
              : '${_deg(r.minAngleDeg)} – ${_deg(r.maxAngleDeg)}'),
          ('Step', _deg(r.stepDeg)),
          ('Reversed', reviewValue(r.reverse)),
        ]),
        // OpenAstro Guider fields are non-nullable with sensible defaults, so they always
        // show a value (never "Not set"); routing them through the same
        // formatters as every other row keeps display consistent. The section's
        // skipped banner conveys when the user didn't customise them.
        _ReviewSection(title: 'OpenAstro Guider', step: 4, onEdit: edit, rows: [
          ('Host:port', reviewValue(g.hostPort)),
          ('Dither', _unit(g.ditherPixels, 'px')),
          ('Settle threshold', _unit(g.settleThresholdPx, 'px')),
          ('Calibration', switch (g.calibrationCadence) {
            CalibrationCadence.eachSession => 'Each session',
            CalibrationCadence.onceReuse => 'Once, then reuse',
            CalibrationCadence.neverRecalibrate => 'Never recalibrate',
          }),
        ]),
        // §36.10 — the star database(s) this rig needs, from the optics above
        // (and the guide camera, which solves polar-alignment frames). Chosen
        // here and downloaded on the rig after Save, so setup stays 5 screens.
        const _SolverDatabaseSection(),
        _ReviewSection(title: 'Plate solving', step: null, onEdit: edit, rows: [
          ('ASTAP binary', reviewValue(ps.astapBinaryPath)),
          ('Star database', reviewValue(ps.starDatabasePath)),
          ('Search radius', _deg(ps.searchRadiusDeg)),
          ('Downsample', reviewValue(ps.downsampleFactor)),
        ]),
        _ReviewSection(title: 'Autofocus', step: null, onEdit: edit, rows: [
          ('Exposure', af.exposureSeconds == null
              ? _notSet
              : '${af.exposureSeconds} s'),
          ('Steps', reviewValue(af.steps)),
          ('Step size', reviewValue(af.stepSize)),
          ('After filter change', reviewValue(af.runAfterFilterChange)),
        ]),
        _ReviewSection(title: 'File saving', step: null, onEdit: edit, rows: [
          ('Directory', reviewValue(fs.saveDirectory)),
          ('Format', switch (fs.format) {
            ImageFormat.fits => 'FITS',
            ImageFormat.xisf => 'XISF',
            null => _notSet,
          }),
          ('Compress', reviewValue(fs.compress)),
          ('Filename template', reviewValue(fs.filenameTemplate)),
        ]),
        _ReviewSection(title: 'Imaging defaults', step: null, onEdit: edit, rows: [
          ('Exposure', formatDuration(id.exposure)),
          ('Frame type', switch (id.frameType) {
            FrameType.light => 'Light',
            FrameType.dark => 'Dark',
            FrameType.bias => 'Bias',
            FrameType.flat => 'Flat',
            null => _notSet,
          }),
        ]),
        _ReviewSection(title: 'Safety', step: null, onEdit: edit, rows: [
          ('When unsafe', switch (sf.onUnsafe) {
            UnsafeConditionAction.pauseAndPark => 'Pause & park',
            UnsafeConditionAction.parkOnly => 'Park only',
            UnsafeConditionAction.abortAndPark => 'Abort & park',
            UnsafeConditionAction.ignore => 'Ignore',
            null => _notSet,
          }),
          ('Auto-resume when safe', reviewValue(sf.autoResumeWhenSafe)),
          ('Resume delay', sf.resumeDelayMin == null
              ? _notSet
              : '${sf.resumeDelayMin} min'),
        ]),
        _ReviewSection(title: 'Site preferences', step: null, onEdit: edit, rows: [
          ('Horizon floor', _deg(si.hardMinAltitudeDeg)),
          ('Twilight', switch (si.twilight) {
            TwilightOption.civil => 'Civil',
            TwilightOption.nautical => 'Nautical',
            TwilightOption.astronomical => 'Astronomical',
            null => _notSet,
          }),
        ]),
      ],
    );
  }
}

/// One titled block in the review list: a header with an Edit-jump button, a
/// "skipped" hint when the matching screen was skipped, then label/value rows.
/// [step] is null for §76 sections with no wizard screen anymore (defaults
/// apply; the values are edited post-save in Options) — no Edit button.
class _ReviewSection extends ConsumerWidget {
  const _ReviewSection({
    required this.title,
    required this.step,
    required this.rows,
    required this.onEdit,
  });

  final String title;
  final int? step;
  final List<(String, String)> rows;
  final void Function(int step) onEdit;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final wasSkipped = ref.watch(wizardControllerProvider
        .select((s) => s.draft.skippedScreens.contains(step)));
    return Padding(
      padding: const EdgeInsets.only(bottom: 20),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(title,
                    style: Theme.of(context).textTheme.titleMedium),
              ),
              if (step != null)
                TextButton.icon(
                  onPressed: () => onEdit(step!),
                  icon: const Icon(Icons.edit, size: 16),
                  label: const Text('Edit'),
                )
              else
                Text('Defaults · edit later in Options',
                    style: Theme.of(context)
                        .textTheme
                        .bodySmall
                        ?.copyWith(color: AraColors.textSecondary)),
            ],
          ),
          if (wasSkipped)
            const Padding(
              padding: EdgeInsets.only(bottom: 4),
              child: Text('Skipped — defaults will apply.',
                  style: TextStyle(
                      color: AraColors.accentBusy, fontSize: 12)),
            ),
          const Divider(height: 8),
          ...rows.map((row) => Padding(
                padding: const EdgeInsets.symmetric(vertical: 3),
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    SizedBox(
                      width: 150,
                      child: Text(row.$1,
                          style: const TextStyle(
                              color: AraColors.textSecondary)),
                    ),
                    Expanded(child: Text(row.$2)),
                  ],
                ),
              )),
        ],
      ),
    );
  }
}

/// §36.10 — asks the daemon which ASTAP database(s) the draft's optics need and
/// offers to download them after Save. One pick per camera whose field is
/// known (imaging, guide), deduplicated; a field below the solver's floor is
/// reported as "cannot plate-solve" instead of a download. The choice lives
/// on the draft (`plateSolve.solverDatabasePackageIds`) so Save can queue it.
class _SolverDatabaseSection extends ConsumerStatefulWidget {
  const _SolverDatabaseSection();

  @override
  ConsumerState<_SolverDatabaseSection> createState() => _SolverDatabaseSectionState();
}

class _SolverDatabaseSectionState extends ConsumerState<_SolverDatabaseSection> {
  SolverRecommendation? _rec;
  String? _error;
  bool _loading = true;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _load());
  }

  Future<void> _load() async {
    final api = ref.read(dataManagerApiProvider);
    final draft = ref.read(wizardControllerProvider).draft;
    if (api == null) {
      // Same rule as the error branch: nothing from an earlier visit may be queued.
      draft.plateSolve.solverDatabasePackageIds = const [];
      setState(() {
        _loading = false;
        _error = 'No rig in reach: the database is chosen when the profile reaches a rig.';
      });
      return;
    }
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final t = draft.telescope;
      final c = draft.camera;
      final g = draft.guider;
      final rec = await api.solverRecommendation(SolverRecommendationRequest(
        focalLengthMm: t.focalLengthMm,
        reducerFactor: t.reducerFactor,
        sensorWidthPx: c.sensorWidthPx,
        sensorHeightPx: c.sensorHeightPx,
        pixelSizeUm: c.pixelSizeMicrons,
        guideFocalLengthMm: g.guideFocalLengthMm?.toDouble(),
        guidePixelSizeUm: g.guidePixelSizeUm,
        // No guide sensor here: the profile never holds it, the daemon reads
        // it from the connected guider.
        guideSetupType: g.setupType,
      ));
      if (!mounted) return;
      final ids = rec.packages
          .where((p) => p.packageId != null && !p.isInstalled)
          .map((p) => p.packageId!)
          .toSet()
          .toList();
      // The draft remembers what Save should queue; a re-run of this screen
      // recomputes it from the latest optics.
      draft.plateSolve.solverDatabasePackageIds = ids;
      setState(() {
        _rec = rec;
        _loading = false;
      });
    } catch (e) {
      if (!mounted) return;
      // A failed recheck must not leave ids from an earlier visit (the optics
      // may have changed since): Save queues nothing until a recheck succeeds.
      draft.plateSolve.solverDatabasePackageIds = const [];
      setState(() {
        _loading = false;
        _error = friendlyError(e, action: 'work out which star database this rig needs');
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final draft = ref.watch(wizardControllerProvider).draft;
    final rec = _rec;
    return Padding(
      padding: const EdgeInsets.only(bottom: 20),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Text('Plate-solver star database', style: theme.textTheme.titleMedium),
              ),
              TextButton.icon(
                onPressed: _loading ? null : _load,
                icon: const Icon(Icons.refresh, size: 18),
                label: const Text('Recheck'),
              ),
            ],
          ),
          const SizedBox(height: 4),
          if (_loading)
            const Padding(
              padding: EdgeInsets.symmetric(vertical: 8),
              child: Row(children: [
                SizedBox(width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2)),
                SizedBox(width: 8),
                Text('Working out the field of view…'),
              ]),
            )
          else if (_error != null)
            Text(_error!, style: theme.textTheme.bodySmall?.copyWith(color: theme.colorScheme.error))
          else if (rec != null) ...[
            if (rec.packages.isEmpty)
              Text(rec.reason, style: theme.textTheme.bodySmall)
            else
              for (final p in rec.packages) _pickRow(context, p),
            if (rec.packages.any((p) => p.packageId != null && !p.isInstalled))
              CheckboxListTile(
                key: const ValueKey('review-solver-db-download'),
                contentPadding: EdgeInsets.zero,
                controlAffinity: ListTileControlAffinity.leading,
                value: draft.plateSolve.downloadSolverDatabase,
                onChanged: (v) => setState(() => draft.plateSolve.downloadSolverDatabase = v ?? true),
                title: Text(
                  'Download after setup (${formatBytes(rec.packages.where((p) => p.packageId != null && !p.isInstalled).fold<int>(0, (s, p) => s + p.sizeBytes))}, '
                  'the rig needs internet once)',
                ),
                subtitle: const Text('Runs on the rig in the background; progress in Settings → Sky data.'),
              ),
          ],
        ],
      ),
    );
  }

  Widget _pickRow(BuildContext context, SolverDatabasePick p) {
    final theme = Theme.of(context);
    final role = p.role == 'guide camera' ? 'Guide camera' : 'Camera';
    final field = '$role: ${p.fovDeg.toStringAsFixed(2)}° field (${p.band})';
    if (p.tooNarrow) {
      return Padding(
        padding: const EdgeInsets.symmetric(vertical: 4),
        child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Icon(Icons.warning_amber_rounded, size: 18, color: theme.colorScheme.error),
          const SizedBox(width: 8),
          Expanded(
            child: Text('$field — cannot plate-solve. ${p.reason}',
                style: theme.textTheme.bodySmall?.copyWith(color: theme.colorScheme.error)),
          ),
        ]),
      );
    }
    final status = p.isInstalled ? 'already on the rig' : formatBytes(p.sizeBytes);
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Icon(p.isInstalled ? Icons.check_circle_outline : Icons.download_outlined, size: 18, color: theme.colorScheme.primary),
        const SizedBox(width: 8),
        Expanded(
          child: Text('$field → ${p.packageName ?? p.packageId} ($status)',
              style: theme.textTheme.bodySmall),
        ),
      ]),
    );
  }
}
