import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/profile_draft.dart';
import '../../../state/sky_atlas/data_manager_state.dart';
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

// ── Screen 17 — Sky data downloads ──────────────────────────────────────────

/// §37.6 — optional sky-data packages (star catalogs, target lists) to fetch.
/// The user ticks what they want; the selected ids are queued for download (via
/// the Data Manager) after the profile is saved — see the wizard shell. The set
/// of selected ids lives on the draft so it survives back/forward navigation.
class ScreenSkyData extends ConsumerStatefulWidget {
  const ScreenSkyData({super.key});
  @override
  ConsumerState<ScreenSkyData> createState() => _ScreenSkyDataState();
}

class _ScreenSkyDataState extends ConsumerState<ScreenSkyData> {
  @override
  Widget build(BuildContext context) {
    // Watch (not read) the controller so this screen rebuilds if the draft object
    // is ever replaced (e.g. a future reset-to-defaults); `selected` is the live
    // mutable set on the draft, and our setState calls repaint after we mutate it.
    final selected = ref.watch(wizardControllerProvider).draft.skyDataDownloadIds;
    final async = ref.watch(dataManagerPackagesProvider);
    return WizardScreenScaffold(
      step: 17,
      intro: 'Optional sky-data downloads — star catalogs and target lists. Tick '
          'any you want and they download in the background after you finish. You '
          'can manage the full catalog later in Settings → Data.',
      children: [
        async.when(
          loading: () => const Padding(
            padding: EdgeInsets.symmetric(vertical: 32),
            child: Center(child: CircularProgressIndicator()),
          ),
          error: (e, _) => _Message(
            'Couldn\'t load the sky-data catalog. You can add packages later in '
            'Settings → Data.',
          ),
          data: (packages) {
            if (packages == null) {
              return _Message(
                  'Connect to a daemon to see available sky-data packages.');
            }
            final available =
                packages.where((p) => !p.isInstalled).toList(growable: false);
            if (available.isEmpty) {
              return _Message('All sky-data packages are already installed.');
            }
            return Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    TextButton(
                      onPressed: available.every((p) => selected.contains(p.id))
                          ? null
                          : () => setState(() =>
                              selected.addAll(available.map((p) => p.id))),
                      child: const Text('Select all'),
                    ),
                    TextButton(
                      onPressed: selected.isEmpty
                          ? null
                          : () => setState(() => selected.clear()),
                      child: const Text('Clear'),
                    ),
                  ],
                ),
                ...available.map((p) => CheckboxListTile(
                      value: selected.contains(p.id),
                      onChanged: (v) => setState(() => v == true
                          ? selected.add(p.id)
                          : selected.remove(p.id)),
                      title: Text(p.name),
                      subtitle: Text(
                        // Drop empty parts so a package with no description
                        // doesn't render a dangling "  ·  1.0 MB" separator.
                        [p.description, formatBytes(p.sizeBytes)]
                            .where((s) => s.isNotEmpty)
                            .join('  ·  '),
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                        style: const TextStyle(color: AraColors.textSecondary),
                      ),
                      controlAffinity: ListTileControlAffinity.leading,
                      contentPadding: EdgeInsets.zero,
                    )),
              ],
            );
          },
        ),
      ],
    );
  }
}

class _Message extends StatelessWidget {
  const _Message(this.text);
  final String text;
  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 24),
        child: Text(text,
            style: const TextStyle(color: AraColors.textSecondary)),
      );
}

// ── Screen 18 — Review + Save ────────────────────────────────────────────────

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
    if (e.switchDeviceId != null) 'Switch',
    if (e.safetyMonitorDeviceId != null) 'Safety Monitor',
    if (e.flatPanelDeviceId != null) 'Flat Panel',
    if (e.guiderDeviceId != null) 'Guider',
  ];
  return labels.isEmpty ? 'None assigned' : labels.join(', ');
}

/// §37.7 — final review. A read-only, section-grouped summary of everything the
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
      step: 18,
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
        _ReviewSection(title: 'Telescope', step: 4, onEdit: edit, rows: [
          ('Name', reviewValue(t.name)),
          ('Focal length', _unit(t.focalLengthMm, 'mm')),
          ('Aperture', _unit(t.apertureMm, 'mm')),
          ('Focal ratio', fr),
        ]),
        _ReviewSection(title: 'Camera', step: 5, onEdit: edit, rows: [
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
        _ReviewSection(title: 'Filter wheel', step: 6, onEdit: edit, rows: [
          ('Filters', draft.filterWheel.filters.isEmpty
              ? 'None defined'
              : '${draft.filterWheel.filters.length} defined'),
        ]),
        _ReviewSection(title: 'Focuser', step: 7, onEdit: edit, rows: [
          ('Step size', _unit(f.stepSizeMicrons, 'µm')),
          ('Backlash in/out',
              '${reviewValue(f.backlashInSteps)} / ${reviewValue(f.backlashOutSteps)}'),
          ('Temp. compensation', reviewValue(f.temperatureCompensationEnabled)),
        ]),
        _ReviewSection(title: 'Mount', step: 8, onEdit: edit, rows: [
          ('Name', reviewValue(m.name)),
          ('Slew rate', _unit(m.slewRateDegPerSec, '°/s')),
          ('Meridian flip', switch (m.meridianFlip) {
            MeridianFlipBehavior.auto => 'Auto',
            MeridianFlipBehavior.prompt => 'Prompt',
            MeridianFlipBehavior.never => 'Never',
          }),
          ('Settle time', formatDuration(m.settleTimeAfterSlew)),
        ]),
        _ReviewSection(title: 'Rotator', step: 9, onEdit: edit, rows: [
          // A range needs both ends — if either is unset, fall back to "Not set"
          // rather than rendering a half-filled "Not set – 45.0°".
          ('Range', (r.minAngleDeg == null || r.maxAngleDeg == null)
              ? _notSet
              : '${_deg(r.minAngleDeg)} – ${_deg(r.maxAngleDeg)}'),
          ('Step', _deg(r.stepDeg)),
          ('Reversed', reviewValue(r.reverse)),
        ]),
        // PHD2 fields are non-nullable with sensible defaults, so they always
        // show a value (never "Not set"); routing them through the same
        // formatters as every other row keeps display consistent. The section's
        // skipped banner conveys when the user didn't customise them.
        _ReviewSection(title: 'Guider (PHD2)', step: 10, onEdit: edit, rows: [
          ('Host:port', reviewValue(g.hostPort)),
          ('Dither', _unit(g.ditherPixels, 'px')),
          ('Settle threshold', _unit(g.settleThresholdPx, 'px')),
          ('Calibration', switch (g.calibrationCadence) {
            CalibrationCadence.eachSession => 'Each session',
            CalibrationCadence.onceReuse => 'Once, then reuse',
            CalibrationCadence.neverRecalibrate => 'Never recalibrate',
          }),
        ]),
        _ReviewSection(title: 'Plate solving', step: 11, onEdit: edit, rows: [
          ('ASTAP binary', reviewValue(ps.astapBinaryPath)),
          ('Star database', reviewValue(ps.starDatabasePath)),
          ('Search radius', _deg(ps.searchRadiusDeg)),
          ('Downsample', reviewValue(ps.downsampleFactor)),
        ]),
        _ReviewSection(title: 'Autofocus', step: 12, onEdit: edit, rows: [
          ('Exposure', af.exposureSeconds == null
              ? _notSet
              : '${af.exposureSeconds} s'),
          ('Steps', reviewValue(af.steps)),
          ('Step size', reviewValue(af.stepSize)),
          ('After filter change', reviewValue(af.runAfterFilterChange)),
        ]),
        _ReviewSection(title: 'File saving', step: 13, onEdit: edit, rows: [
          ('Directory', reviewValue(fs.saveDirectory)),
          ('Format', switch (fs.format) {
            ImageFormat.fits => 'FITS',
            ImageFormat.xisf => 'XISF',
            null => _notSet,
          }),
          ('Compress', reviewValue(fs.compress)),
          ('Filename template', reviewValue(fs.filenameTemplate)),
        ]),
        _ReviewSection(title: 'Imaging defaults', step: 14, onEdit: edit, rows: [
          ('Exposure', formatDuration(id.exposure)),
          ('Frame type', switch (id.frameType) {
            FrameType.light => 'Light',
            FrameType.dark => 'Dark',
            FrameType.bias => 'Bias',
            FrameType.flat => 'Flat',
            null => _notSet,
          }),
        ]),
        _ReviewSection(title: 'Safety', step: 15, onEdit: edit, rows: [
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
        _ReviewSection(title: 'Site preferences', step: 16, onEdit: edit, rows: [
          ('Horizon floor', _deg(si.hardMinAltitudeDeg)),
          ('Twilight', switch (si.twilight) {
            TwilightOption.civil => 'Civil',
            TwilightOption.nautical => 'Nautical',
            TwilightOption.astronomical => 'Astronomical',
            null => _notSet,
          }),
        ]),
        _ReviewSection(title: 'Sky data', step: 17, onEdit: edit, rows: [
          ('Queued downloads', draft.skyDataDownloadIds.isEmpty
              ? 'None'
              : '${draft.skyDataDownloadIds.length} selected'),
        ]),
      ],
    );
  }
}

/// One titled block in the review list: a header with an Edit-jump button, a
/// "skipped" hint when the matching screen was skipped, then label/value rows.
class _ReviewSection extends ConsumerWidget {
  const _ReviewSection({
    required this.title,
    required this.step,
    required this.rows,
    required this.onEdit,
  });

  final String title;
  final int step;
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
              TextButton.icon(
                onPressed: () => onEdit(step),
                icon: const Icon(Icons.edit, size: 16),
                label: const Text('Edit'),
              ),
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
