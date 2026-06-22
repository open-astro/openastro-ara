import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/profile_draft.dart';
import '../../../models/server.dart';
import '../../../services/camera_geometry_api.dart';
import '../../../services/filter_wheel_names_api.dart';
import '../../../services/telescope_optics_api.dart';
import '../../../state/saved_server_state.dart';
import '../../../state/wizard_state.dart';
import '../../../theme/ara_colors.dart';
import '../wizard_form_kit.dart';

// ── shared parse helpers ────────────────────────────────────────────────────

int? _toInt(String raw) {
  final t = raw.trim();
  return t.isEmpty ? null : int.tryParse(t);
}

/// Assign a parsed double to the draft: clear on empty, but KEEP the prior
/// value on partial/invalid input (a lone "-" or "1." mid-keystroke) instead
/// of nulling it — otherwise typing a negative number transiently wipes the
/// field. Mirrors the guard in screen_profile_basics.dart.
void _assignDouble(String raw, void Function(double?) set) {
  final t = raw.trim();
  if (t.isEmpty) {
    set(null);
    return;
  }
  final v = double.tryParse(t);
  if (v != null) set(v);
}

ProfileDraft _draftOf(WidgetRef ref) =>
    ref.read(wizardControllerProvider).draft;

/// The active daemon server, or null when none is connected — the wizard's
/// "Refresh from connected device" affordances read the device through it.
AraServer? _activeServer(WidgetRef ref) => ref.read(savedServersProvider).maybeWhen(
      data: (list) => list.isEmpty ? null : list.last,
      orElse: () => null,
    );

/// "Refresh from connected device" button shared by the per-device wizard
/// screens (§37): pulls the device's real settings off AlpacaBridge into the
/// draft so the user doesn't hand-type what the daemon already knows. Shows a
/// spinner while reading; the screen owns the actual read + draft write.
class _RefreshFromDeviceButton extends StatelessWidget {
  const _RefreshFromDeviceButton({
    required this.label,
    required this.busy,
    required this.onPressed,
  });
  final String label;
  final bool busy;
  final VoidCallback onPressed;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: Align(
        alignment: Alignment.centerLeft,
        child: OutlinedButton.icon(
          onPressed: busy ? null : onPressed,
          icon: busy
              ? const SizedBox(
                  width: 14, height: 14, child: CircularProgressIndicator(strokeWidth: 2))
              : const Icon(Icons.download_for_offline_outlined, size: 16),
          label: Text(busy ? 'Reading…' : label),
        ),
      ),
    );
  }
}

// ── Screen 4 — Telescope ────────────────────────────────────────────────────

class ScreenTelescope extends ConsumerStatefulWidget {
  const ScreenTelescope({super.key});
  @override
  ConsumerState<ScreenTelescope> createState() => _ScreenTelescopeState();
}

class _ScreenTelescopeState extends ConsumerState<ScreenTelescope> {
  late final TelescopeSettings _t = _draftOf(ref).telescope;
  // Bumped on a successful refresh so the focal-length/aperture fields — which
  // are uncontrolled (WizardTextField seeds from initialValue once) — re-seed to
  // show the pulled values instead of the user's stale/empty entry.
  int _seed = 0;
  bool _refreshing = false;

  String get _focalRatio {
    final fl = _t.focalLengthMm, ap = _t.apertureMm;
    if (fl == null || ap == null || ap == 0) return '—';
    return 'f/${(fl / ap).toStringAsFixed(1)}';
  }

  Future<void> _refreshFromMount() async {
    final messenger = ScaffoldMessenger.of(context);
    final server = _activeServer(ref);
    if (server == null) {
      messenger.showSnackBar(const SnackBar(
          content: Text('Connect to a daemon first to read the mount.')));
      return;
    }
    setState(() => _refreshing = true);
    try {
      final optics = await TelescopeOpticsApi(server).read();
      if (!mounted) return;
      if (optics == null || !optics.hasAny) {
        messenger.showSnackBar(const SnackBar(
            content: Text('Connect a mount that reports its focal length/aperture '
                '(many don\'t), or enter them manually.')));
        return;
      }
      setState(() {
        if (optics.focalLengthMm != null) _t.focalLengthMm = optics.focalLengthMm;
        if (optics.apertureMm != null) _t.apertureMm = optics.apertureMm;
        _seed++;
      });
      // Be specific about what came back: a mount often reports one but not the
      // other, so a blanket "filled optics" would hide that the user still needs
      // to enter the missing field by hand.
      final filled = [
        if (optics.focalLengthMm != null) 'focal length',
        if (optics.apertureMm != null) 'aperture',
      ].join(' + ');
      final missingNote = optics.focalLengthMm == null || optics.apertureMm == null
          ? ' (the mount didn\'t report the other — enter it manually)'
          : '';
      messenger.showSnackBar(SnackBar(
          content: Text('Filled $filled from the connected mount.$missingNote')));
    } catch (e) {
      if (mounted) {
        messenger.showSnackBar(
            SnackBar(content: Text('Could not read the mount: $e')));
      }
    } finally {
      if (mounted) setState(() => _refreshing = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return WizardScreenScaffold(
      step: 4,
      intro: 'Your imaging telescope. Focal length and aperture drive the FOV, '
          'image scale, and survey recommendations.',
      children: [
        _RefreshFromDeviceButton(
          label: 'Refresh from connected mount',
          busy: _refreshing,
          onPressed: _refreshFromMount,
        ),
        WizardTextField(
          label: 'Telescope name',
          initialValue: _t.name,
          hint: 'e.g. ES ED102',
          onChanged: (v) => _t.name = v.trim().isEmpty ? null : v.trim(),
        ),
        KeyedSubtree(
          key: ValueKey('fl-$_seed'),
          child: WizardTextField(
            label: 'Focal length (mm)',
            required: true,
            initialValue: _t.focalLengthMm?.toString(),
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            inputFormatters: WizardInput.unsignedDecimal,
            onChanged: (v) =>
                setState(() => _assignDouble(v, (d) => _t.focalLengthMm = d)),
          ),
        ),
        KeyedSubtree(
          key: ValueKey('ap-$_seed'),
          child: WizardTextField(
            label: 'Aperture (mm)',
            required: true,
            initialValue: _t.apertureMm?.toString(),
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            inputFormatters: WizardInput.unsignedDecimal,
            onChanged: (v) =>
                setState(() => _assignDouble(v, (d) => _t.apertureMm = d)),
          ),
        ),
        WizardDerivedValue(label: 'Focal ratio', value: _focalRatio),
      ],
    );
  }
}

// ── Screen 5 — Camera ───────────────────────────────────────────────────────

class ScreenCamera extends ConsumerStatefulWidget {
  const ScreenCamera({super.key});
  @override
  ConsumerState<ScreenCamera> createState() => _ScreenCameraState();
}

class _ScreenCameraState extends ConsumerState<ScreenCamera> {
  late final ProfileDraft _draft = _draftOf(ref);
  late final CameraSettings _c = _draft.camera;
  // Re-seeds the (uncontrolled) pixel-size field after a refresh — see the
  // telescope screen for the same pattern.
  int _seed = 0;
  bool _refreshing = false;

  Future<void> _refreshFromCamera() async {
    final messenger = ScaffoldMessenger.of(context);
    final server = _activeServer(ref);
    if (server == null) {
      messenger.showSnackBar(const SnackBar(
          content: Text('Connect to a daemon first to read the camera.')));
      return;
    }
    setState(() => _refreshing = true);
    try {
      final geometry = await CameraGeometryApi(server).read();
      if (!mounted) return;
      if (geometry == null) {
        messenger.showSnackBar(const SnackBar(
            content: Text('Connect a camera that reports its sensor first.')));
        return;
      }
      setState(() {
        _c.pixelSizeMicrons = geometry.pixelSizeUm;
        _seed++;
      });
      messenger.showSnackBar(const SnackBar(
          content: Text('Filled pixel size from the connected camera.')));
    } catch (e) {
      if (mounted) {
        messenger.showSnackBar(
            SnackBar(content: Text('Could not read the camera: $e')));
      }
    } finally {
      if (mounted) setState(() => _refreshing = false);
    }
  }

  String get _imageScale {
    final px = _c.pixelSizeMicrons, fl = _draft.telescope.focalLengthMm;
    if (px == null || fl == null || fl == 0) {
      return '— (set pixel size + focal length)';
    }
    // arcsec/px = 206.265 * pixel_um / focal_length_mm
    final scale = 206.265 * px / fl;
    final band = scale > 2.5
        ? 'very wide-field'
        : scale > 1.0
            ? 'wide-field DSO'
            : 'high-resolution';
    return '${scale.toStringAsFixed(2)} arcsec/pixel — $band';
  }

  @override
  Widget build(BuildContext context) {
    return WizardScreenScaffold(
      step: 5,
      intro: 'Cooling, default capture parameters, and sensor geometry.',
      children: [
        _RefreshFromDeviceButton(
          label: 'Refresh from connected camera',
          busy: _refreshing,
          onPressed: _refreshFromCamera,
        ),
        WizardTextField(
          label: 'Cooling target (°C)',
          initialValue: _c.coolingTargetC?.toString(),
          hint: 'default −10',
          keyboardType:
              const TextInputType.numberWithOptions(signed: true, decimal: true),
          inputFormatters: WizardInput.signedDecimal,
          onChanged: (v) => _assignDouble(v, (d) => _c.coolingTargetC = d),
        ),
        WizardTextField(
          label: 'Cooler ramp rate (°C/min)',
          initialValue: _c.coolerRampRateCPerMin?.toString(),
          hint: 'default 1',
          keyboardType: const TextInputType.numberWithOptions(decimal: true),
          inputFormatters: WizardInput.unsignedDecimal,
          onChanged: (v) =>
              _assignDouble(v, (d) => _c.coolerRampRateCPerMin = d),
        ),
        WizardDropdown<CoolerWarmupMode>(
          label: 'Warmup at session end',
          value: _c.warmupMode,
          helperText: 'Gradual warmup protects the sensor from thermal shock.',
          entries: const [
            DropdownMenuEntry(value: CoolerWarmupMode.off, label: 'Off'),
            DropdownMenuEntry(
                value: CoolerWarmupMode.ramp, label: 'Ramp at 1°C/min'),
            DropdownMenuEntry(
                value: CoolerWarmupMode.immediate, label: 'Immediate'),
          ],
          onChanged: (v) =>
              setState(() => _c.warmupMode = v ?? CoolerWarmupMode.off),
        ),
        WizardTextField(
          label: 'Default gain',
          initialValue: _c.defaultGain?.toString(),
          keyboardType: TextInputType.number,
          inputFormatters: WizardInput.unsignedInt,
          onChanged: (v) => _c.defaultGain = _toInt(v),
        ),
        WizardTextField(
          label: 'Default offset',
          initialValue: _c.defaultOffset?.toString(),
          keyboardType: TextInputType.number,
          inputFormatters: WizardInput.unsignedInt,
          onChanged: (v) => _c.defaultOffset = _toInt(v),
        ),
        WizardTextField(
          label: 'Default bin',
          initialValue: _c.defaultBin?.toString(),
          hint: 'e.g. 1 for 1×1',
          keyboardType: TextInputType.number,
          inputFormatters: WizardInput.unsignedInt,
          onChanged: (v) => _c.defaultBin = _toInt(v),
        ),
        KeyedSubtree(
          key: ValueKey('px-$_seed'),
          child: WizardTextField(
            label: 'Pixel size (µm)',
            initialValue: _c.pixelSizeMicrons?.toString(),
            helperText: 'Auto-filled from Alpaca when connected; editable.',
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            inputFormatters: WizardInput.unsignedDecimal,
            onChanged: (v) =>
                setState(() => _assignDouble(v, (d) => _c.pixelSizeMicrons = d)),
          ),
        ),
        WizardDerivedValue(label: 'Image scale', value: _imageScale),
      ],
    );
  }
}

// ── Screen 6 — Filter Wheel ─────────────────────────────────────────────────

class ScreenFilterWheel extends ConsumerStatefulWidget {
  const ScreenFilterWheel({super.key});
  @override
  ConsumerState<ScreenFilterWheel> createState() => _ScreenFilterWheelState();
}

class _ScreenFilterWheelState extends ConsumerState<ScreenFilterWheel> {
  late final FilterWheelSettings _fw = _draftOf(ref).filterWheel;
  bool _refreshing = false;

  Future<void> _refreshFromWheel() async {
    final messenger = ScaffoldMessenger.of(context);
    final server = _activeServer(ref);
    if (server == null) {
      messenger.showSnackBar(const SnackBar(
          content: Text('Connect to a daemon first to read the filter wheel.')));
      return;
    }
    setState(() => _refreshing = true);
    try {
      final wheel = await FilterWheelNamesApi(server).read();
      if (!mounted) return;
      if (wheel == null || wheel.slots.isEmpty) {
        messenger.showSnackBar(const SnackBar(
            content: Text('Connect a filter wheel that reports its slots first.')));
        return;
      }
      setState(() {
        // Replace the slots with what the wheel reports — fresh FilterDef objects
        // so each row's ObjectKey changes and its (uncontrolled) name field
        // re-seeds. type/wavelength have no Alpaca source, so they stay blank for
        // the user to fill.
        _fw.filters
          ..clear()
          ..addAll(wheel.slots.map((s) => FilterDef()
            ..name = s.name.trim().isEmpty ? null : s.name.trim()
            ..focusOffsetSteps = s.focusOffset));
      });
      messenger.showSnackBar(SnackBar(
          content: Text('Loaded ${wheel.slots.length} slot(s) from the connected '
              'filter wheel.')));
    } catch (e) {
      if (mounted) {
        messenger.showSnackBar(
            SnackBar(content: Text('Could not read the filter wheel: $e')));
      }
    } finally {
      if (mounted) setState(() => _refreshing = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return WizardScreenScaffold(
      step: 6,
      intro: 'Name each filter slot. Focus offsets are left blank — they '
          'populate automatically on the first autofocus run per filter.',
      children: [
        _RefreshFromDeviceButton(
          label: 'Refresh from connected filter wheel',
          busy: _refreshing,
          onPressed: _refreshFromWheel,
        ),
        for (int i = 0; i < _fw.filters.length; i++)
          _filterRow(context, i, _fw.filters[i]),
        const SizedBox(height: 4),
        Align(
          alignment: Alignment.centerLeft,
          child: TextButton.icon(
            onPressed: () => setState(() => _fw.filters.add(FilterDef())),
            icon: const Icon(Icons.add, size: 18),
            label: const Text('Add filter slot'),
          ),
        ),
      ],
    );
  }

  Widget _filterRow(BuildContext context, int index, FilterDef f) {
    return Container(
      key: ObjectKey(f),
      margin: const EdgeInsets.only(bottom: 12),
      padding: const EdgeInsets.fromLTRB(12, 12, 12, 4),
      decoration: BoxDecoration(
        color: AraColors.bgPanel,
        borderRadius: BorderRadius.circular(4),
        border: Border.all(color: AraColors.border),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Text('Slot ${index + 1}',
                  style: Theme.of(context).textTheme.labelLarge?.copyWith(
                        color: AraColors.textSecondary,
                      )),
              const Spacer(),
              IconButton(
                tooltip: 'Remove slot',
                visualDensity: VisualDensity.compact,
                onPressed: () => setState(() => _fw.filters.removeAt(index)),
                icon: const Icon(Icons.delete_outline, size: 18),
              ),
            ],
          ),
          TextFormField(
            initialValue: f.name,
            style: const TextStyle(color: AraColors.textPrimary),
            decoration: const InputDecoration(
              labelText: 'Name (L / R / G / B / Hα / OIII / …)',
              filled: true,
              fillColor: AraColors.bgInput,
              border: OutlineInputBorder(),
            ),
            onChanged: (v) => f.name = v.trim().isEmpty ? null : v.trim(),
          ),
          const SizedBox(height: 12),
          WizardDropdown<FilterType?>(
            label: 'Type',
            value: f.type,
            entries: const [
              DropdownMenuEntry(value: null, label: '— Unset'),
              DropdownMenuEntry(
                  value: FilterType.broadband, label: 'Broadband'),
              DropdownMenuEntry(
                  value: FilterType.narrowband, label: 'Narrowband'),
              DropdownMenuEntry(value: FilterType.clear, label: 'Clear'),
              DropdownMenuEntry(
                  value: FilterType.luminance, label: 'Luminance'),
            ],
            onChanged: (v) => setState(() => f.type = v),
          ),
          TextFormField(
            initialValue: f.wavelengthNm?.toString(),
            keyboardType: TextInputType.number,
            inputFormatters: WizardInput.unsignedInt,
            style: const TextStyle(color: AraColors.textPrimary),
            decoration: const InputDecoration(
              labelText: 'Wavelength (nm) — optional',
              filled: true,
              fillColor: AraColors.bgInput,
              border: OutlineInputBorder(),
            ),
            onChanged: (v) => f.wavelengthNm = _toInt(v),
          ),
          const SizedBox(height: 12),
        ],
      ),
    );
  }
}

// ── Screen 7 — Focuser ──────────────────────────────────────────────────────

class ScreenFocuser extends ConsumerStatefulWidget {
  const ScreenFocuser({super.key});
  @override
  ConsumerState<ScreenFocuser> createState() => _ScreenFocuserState();
}

class _ScreenFocuserState extends ConsumerState<ScreenFocuser> {
  late final FocuserSettings _f = _draftOf(ref).focuser;

  @override
  Widget build(BuildContext context) {
    return WizardScreenScaffold(
      step: 7,
      intro: 'Step size and backlash compensation. Values are pulled from '
          'Alpaca where the driver reports them.',
      children: [
        WizardTextField(
          label: 'Step size (µm/step)',
          initialValue: _f.stepSizeMicrons?.toString(),
          keyboardType: const TextInputType.numberWithOptions(decimal: true),
          inputFormatters: WizardInput.unsignedDecimal,
          onChanged: (v) => _assignDouble(v, (d) => _f.stepSizeMicrons = d),
        ),
        const WizardSectionHeader('Backlash compensation'),
        WizardTextField(
          label: 'In steps',
          initialValue: _f.backlashInSteps?.toString(),
          keyboardType: TextInputType.number,
          inputFormatters: WizardInput.unsignedInt,
          onChanged: (v) => _f.backlashInSteps = _toInt(v),
        ),
        WizardTextField(
          label: 'Out steps',
          initialValue: _f.backlashOutSteps?.toString(),
          keyboardType: TextInputType.number,
          inputFormatters: WizardInput.unsignedInt,
          onChanged: (v) => _f.backlashOutSteps = _toInt(v),
        ),
        const WizardSectionHeader('Temperature compensation'),
        SwitchListTile.adaptive(
          contentPadding: EdgeInsets.zero,
          title: const Text('Enable temperature compensation'),
          value: _f.temperatureCompensationEnabled,
          onChanged: (v) =>
              setState(() => _f.temperatureCompensationEnabled = v),
        ),
        if (_f.temperatureCompensationEnabled)
          WizardTextField(
            label: 'Slope (steps/°C)',
            initialValue: _f.temperatureCompensationSlope?.toString(),
            keyboardType: const TextInputType.numberWithOptions(
                signed: true, decimal: true),
            inputFormatters: WizardInput.signedDecimal,
            onChanged: (v) =>
                _assignDouble(v, (d) => _f.temperatureCompensationSlope = d),
          ),
      ],
    );
  }
}

// ── Screen 8 — Mount ────────────────────────────────────────────────────────

class ScreenMount extends ConsumerStatefulWidget {
  const ScreenMount({super.key});
  @override
  ConsumerState<ScreenMount> createState() => _ScreenMountState();
}

class _ScreenMountState extends ConsumerState<ScreenMount> {
  late final MountSettings _m = _draftOf(ref).mount;

  @override
  Widget build(BuildContext context) {
    return WizardScreenScaffold(
      step: 8,
      intro: 'Mount name is auto-pulled from the Alpaca driver. Settle time '
          'comes from the driver\'s SlewSettleTime and is editable.',
      children: [
        WizardTextField(
          label: 'Mount name',
          initialValue: _m.name,
          onChanged: (v) => _m.name = v.trim().isEmpty ? null : v.trim(),
        ),
        WizardTextField(
          label: 'Slew rate (°/sec)',
          initialValue: _m.slewRateDegPerSec?.toString(),
          keyboardType: const TextInputType.numberWithOptions(decimal: true),
          inputFormatters: WizardInput.unsignedDecimal,
          onChanged: (v) => _assignDouble(v, (d) => _m.slewRateDegPerSec = d),
        ),
        WizardDropdown<ParkPositionMode>(
          label: 'Park position',
          value: _m.parkMode,
          entries: const [
            DropdownMenuEntry(
                value: ParkPositionMode.syncCurrent,
                label: 'Sync to current pointing'),
            DropdownMenuEntry(
                value: ParkPositionMode.defineManually,
                label: 'Define manually'),
          ],
          onChanged: (v) =>
              setState(() => _m.parkMode = v ?? ParkPositionMode.syncCurrent),
        ),
        WizardDropdown<MeridianFlipBehavior>(
          label: 'Meridian flip',
          value: _m.meridianFlip,
          entries: const [
            DropdownMenuEntry(value: MeridianFlipBehavior.auto, label: 'Auto'),
            DropdownMenuEntry(
                value: MeridianFlipBehavior.prompt, label: 'Prompt'),
            DropdownMenuEntry(
                value: MeridianFlipBehavior.never, label: 'Never'),
          ],
          onChanged: (v) =>
              setState(() => _m.meridianFlip = v ?? MeridianFlipBehavior.auto),
        ),
        WizardTextField(
          label: 'Settle time after slew (s)',
          initialValue: _m.settleTimeAfterSlew?.inSeconds.toString(),
          keyboardType: TextInputType.number,
          inputFormatters: WizardInput.unsignedInt,
          onChanged: (v) {
            final s = _toInt(v);
            _m.settleTimeAfterSlew = s == null ? null : Duration(seconds: s);
          },
        ),
      ],
    );
  }
}

// ── Screen 9 — Rotator ──────────────────────────────────────────────────────

class ScreenRotator extends ConsumerStatefulWidget {
  const ScreenRotator({super.key});
  @override
  ConsumerState<ScreenRotator> createState() => _ScreenRotatorState();
}

class _ScreenRotatorState extends ConsumerState<ScreenRotator> {
  late final RotatorSettings _r = _draftOf(ref).rotator;

  @override
  Widget build(BuildContext context) {
    return WizardScreenScaffold(
      step: 9,
      intro: 'Mechanical limits and step size for your rotator.',
      children: [
        WizardTextField(
          label: 'Min angle (°)',
          initialValue: _r.minAngleDeg?.toString(),
          keyboardType:
              const TextInputType.numberWithOptions(signed: true, decimal: true),
          inputFormatters: WizardInput.signedDecimal,
          onChanged: (v) => _assignDouble(v, (d) => _r.minAngleDeg = d),
        ),
        WizardTextField(
          label: 'Max angle (°)',
          initialValue: _r.maxAngleDeg?.toString(),
          keyboardType:
              const TextInputType.numberWithOptions(signed: true, decimal: true),
          inputFormatters: WizardInput.signedDecimal,
          onChanged: (v) => _assignDouble(v, (d) => _r.maxAngleDeg = d),
        ),
        WizardTextField(
          label: 'Angle step (°)',
          initialValue: _r.stepDeg?.toString(),
          keyboardType: const TextInputType.numberWithOptions(decimal: true),
          inputFormatters: WizardInput.unsignedDecimal,
          onChanged: (v) => _assignDouble(v, (d) => _r.stepDeg = d),
        ),
        SwitchListTile.adaptive(
          contentPadding: EdgeInsets.zero,
          title: const Text('Reverse direction'),
          value: _r.reverse,
          onChanged: (v) => setState(() => _r.reverse = v),
        ),
      ],
    );
  }
}

// ── Screen 10 — Guider (PHD2) ───────────────────────────────────────────────

class ScreenGuider extends ConsumerStatefulWidget {
  const ScreenGuider({super.key});
  @override
  ConsumerState<ScreenGuider> createState() => _ScreenGuiderState();
}

class _ScreenGuiderState extends ConsumerState<ScreenGuider> {
  late final GuiderSettings _g = _draftOf(ref).guider;

  @override
  Widget build(BuildContext context) {
    return WizardScreenScaffold(
      step: 10,
      intro: 'ARA connects to PHD2 over its JSON-RPC interface (not Alpaca).',
      children: [
        WizardTextField(
          label: 'PHD2 host:port',
          initialValue: _g.hostPort,
          hint: 'localhost:4400',
          onChanged: (v) =>
              _g.hostPort = v.trim().isEmpty ? 'localhost:4400' : v.trim(),
        ),
        WizardTextField(
          label: 'Dither (pixels)',
          initialValue: _g.ditherPixels.toString(),
          keyboardType: const TextInputType.numberWithOptions(decimal: true),
          inputFormatters: WizardInput.unsignedDecimal,
          onChanged: (v) => _assignDouble(v, (d) {
            if (d != null) _g.ditherPixels = d;
          }),
        ),
        WizardTextField(
          label: 'Settle threshold (px)',
          initialValue: _g.settleThresholdPx.toString(),
          keyboardType: const TextInputType.numberWithOptions(decimal: true),
          inputFormatters: WizardInput.unsignedDecimal,
          onChanged: (v) => _assignDouble(v, (d) {
            if (d != null) _g.settleThresholdPx = d;
          }),
        ),
        WizardTextField(
          label: 'Settle time (s)',
          initialValue: _g.settleDuration.inSeconds.toString(),
          keyboardType: TextInputType.number,
          inputFormatters: WizardInput.unsignedInt,
          onChanged: (v) {
            final s = _toInt(v);
            if (s != null) _g.settleDuration = Duration(seconds: s);
          },
        ),
        WizardDropdown<CalibrationCadence>(
          label: 'Calibration cadence',
          value: _g.calibrationCadence,
          entries: const [
            DropdownMenuEntry(
                value: CalibrationCadence.eachSession, label: 'Each session'),
            DropdownMenuEntry(
                value: CalibrationCadence.onceReuse,
                label: 'Once, then reuse'),
            DropdownMenuEntry(
                value: CalibrationCadence.neverRecalibrate,
                label: 'Never recalibrate'),
          ],
          onChanged: (v) => setState(() =>
              _g.calibrationCadence = v ?? CalibrationCadence.eachSession),
        ),
      ],
    );
  }
}
