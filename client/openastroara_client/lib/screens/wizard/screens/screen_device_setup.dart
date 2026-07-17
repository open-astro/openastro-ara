import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/profile_draft.dart';
import '../../../services/equipment_device_api.dart' show EquipmentDeviceClient;
import '../../../state/equipment/camera_state.dart';
import '../../../state/equipment/filter_wheel_state.dart';
import '../../../state/equipment/focuser_state.dart';
import '../../../state/equipment/mount_state.dart';
import '../../../state/equipment/rotator_state.dart';
import '../../../state/guider/guider_state.dart';
import '../../../util/host_port.dart';
import '../../../widgets/profile/profile_import_flow.dart'
    show friendlyDaemonError;
import '../../../models/server.dart';
import '../../../services/camera_geometry_api.dart';
import '../../../services/equipment_discovery_api.dart';
import '../../../services/filter_wheel_names_api.dart';
import '../../../services/focuser_props_api.dart';
import '../../../services/rotator_props_api.dart';
import '../../../services/telescope_optics_api.dart';
import '../../../state/saved_server_state.dart';
import '../../../state/settings/equipment_connection_state.dart';
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

/// A short, user-facing message for a refresh failure — never the full
/// DioException dump (which carries the request URL / response body / internal
/// addresses).
String _describeError(Object e) =>
    e is DioException ? (e.message ?? 'network error') : e.toString();

/// Connect the device the user assigned on the Discover step, then poll
/// [read] until the daemon reports it (or ~15 s pass). The wizard assigns
/// devices WITHOUT connecting them (connections are runtime state), so a bare
/// read on a stage-3 screen almost always finds nothing — the AlpacaBridge
/// config is fine, the daemon just hasn't opened the device yet. Returns null
/// when nothing is assigned, the assigned device isn't on the bridge, or the
/// connect never completes.
Future<T?> _connectAssignedThenRead<T>(
  WidgetRef ref,
  AraServer server, {
  required EquipmentDeviceType type,
  required String? assignedId,
  required EquipmentDeviceClient<Object?> Function(AraServer) apiFactory,
  required Future<T?> Function() read,
  required bool Function() isMounted,
}) async {
  if (assignedId == null) return null; // nothing assigned — nothing to connect
  final discovery = EquipmentDiscoveryApi(server);
  try {
    final devices = await discovery.discover(type);
    final device = devices.where((d) => d.uniqueId == assignedId).firstOrNull;
    if (device == null) return null; // assigned device not on the bridge now
    final api = apiFactory(server);
    try {
      await api.connect(device);
      // Poll until the daemon reports the device (Alpaca connects can take
      // several seconds on real hardware).
      for (var i = 0; i < 20; i++) {
        await Future<void>.delayed(const Duration(milliseconds: 750));
        if (!isMounted()) return null;
        final result = await read();
        if (result != null) return result;
      }
      return null;
    } finally {
      api.close();
    }
  } finally {
    discovery.close();
  }
}

/// The active daemon server, or null when none is connected — the wizard's
/// "Refresh from connected device" affordances read the device through it.
AraServer? _activeServer(WidgetRef ref) => ref.read(activeServerProvider);

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
      var optics = await TelescopeOpticsApi(server).read();
      if (!mounted) return;
      if (optics == null) {
        // No mount connected on the daemon yet — connect the one assigned on
        // the Discover step and retry (the common wizard-time state).
        optics = await _connectAssignedThenRead(
          ref, server,
          type: EquipmentDeviceType.mount,
          assignedId: _draftOf(ref).equipment.mountDeviceId,
          apiFactory: ref.read(mountApiFactoryProvider),
          read: () => TelescopeOpticsApi(server).read(),
          isMounted: () => mounted,
        );
        if (!mounted) return;
      }
      if (optics == null) {
        messenger.showSnackBar(const SnackBar(
            content: Text('The mount didn\'t come up. If it\'s assigned on '
                'the Discover step, the daemon connects it automatically — a '
                'timeout there usually means the BRIDGE can\'t reach the '
                'mount itself: check it\'s powered on and its connection in '
                'AlpacaBridge. You can also enter the values manually.')));
        return;
      }
      if (!optics.hasAny) {
        messenger.showSnackBar(const SnackBar(
            content: Text('The mount doesn\'t report its focal length/aperture '
                '(many don\'t) — enter them manually.')));
        return;
      }
      final o = optics; // non-null after the guards; promote for the closures
      setState(() {
        if (o.focalLengthMm != null) _t.focalLengthMm = o.focalLengthMm;
        if (o.apertureMm != null) _t.apertureMm = o.apertureMm;
        _seed++;
      });
      // Be specific about what came back: a mount often reports one but not the
      // other, so a blanket "filled optics" would hide that the user still needs
      // to enter the missing field by hand.
      final filled = [
        if (o.focalLengthMm != null) 'focal length',
        if (o.apertureMm != null) 'aperture',
      ].join(' + ');
      final missingNote = o.focalLengthMm == null || o.apertureMm == null
          ? ' (the mount didn\'t report the other — enter it manually)'
          : '';
      messenger.showSnackBar(SnackBar(
          content: Text('Filled $filled from the connected mount.$missingNote')));
    } catch (e) {
      if (mounted) {
        messenger.showSnackBar(
            SnackBar(content: Text('Could not read the mount: ${_describeError(e)}')));
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
  // Bin options offered before the camera has been read: 1×1–4×4 covers
  // typical cameras; "Refresh from connected camera" replaces this with the
  // camera's real MaxBin.
  int _maxBin = 4;

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
      var geometry = await CameraGeometryApi(server).read();
      if (!mounted) return;
      geometry ??= await _connectAssignedThenRead(
        ref, server,
        type: EquipmentDeviceType.camera,
        assignedId: _draft.equipment.cameraDeviceId,
        apiFactory: ref.read(cameraStatusApiFactoryProvider),
        read: () => CameraGeometryApi(server).read(),
        isMounted: () => mounted,
      );
      if (!mounted) return;
      if (geometry == null) {
        messenger.showSnackBar(const SnackBar(
            content: Text('The camera didn\'t come up. If it\'s assigned on '
                'the Discover step, the daemon connects it automatically — a '
                'timeout there usually means the bridge can\'t reach the '
                'camera itself: check power/USB in AlpacaBridge. You can also '
                'enter the values manually.')));
        return;
      }
      final g = geometry; // non-null after the guard; promote for the closure
      setState(() {
        _c.pixelSizeMicrons = g.pixelSizeUm;
        _maxBin = g.maxBin;
        // A previously chosen bin the camera can't do would silently persist
        // outside the new options — clear it so the user re-picks.
        final bin = _c.defaultBin;
        if (bin != null && bin > g.maxBin) _c.defaultBin = null;
        _seed++;
      });
      messenger.showSnackBar(const SnackBar(
          content: Text('Filled pixel size + binning options from the '
              'connected camera.')));
    } catch (e) {
      if (mounted) {
        messenger.showSnackBar(
            SnackBar(content: Text('Could not read the camera: ${_describeError(e)}')));
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
        WizardDropdown<int?>(
          label: 'Default bin',
          value: _c.defaultBin,
          helperText: 'Refresh from the camera to list what it supports; '
              '1×1 is the usual choice for deep-sky imaging.',
          entries: [
            const DropdownMenuEntry<int?>(value: null, label: '— Unset'),
            // Keep an out-of-range manual value selectable so it stays visible.
            for (var b = 1;
                b <= (_c.defaultBin != null && _c.defaultBin! > _maxBin
                    ? _c.defaultBin!
                    : _maxBin);
                b++)
              DropdownMenuEntry<int?>(value: b, label: '$b×$b'),
          ],
          onChanged: (v) => setState(() => _c.defaultBin = v),
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
        // NEXTGEN §4 — the two electronics values a camera never reports over
        // ASCOM. Optional: leaving them blank just means the Optimal Sub
        // advisor waits for them (Settings → Imaging → Camera electronics).
        WizardTextField(
          label: 'Read noise (e⁻, optional)',
          initialValue: _c.readNoiseE?.toString(),
          helperText:
              "From the sensor's spec sheet at your imaging gain — never reported by the camera. Feeds the Optimal Sub advisor.",
          keyboardType: const TextInputType.numberWithOptions(decimal: true),
          inputFormatters: WizardInput.unsignedDecimal,
          onChanged: (v) => _assignDouble(v, (d) => _c.readNoiseE = d),
        ),
        WizardTextField(
          label: 'Peak quantum efficiency (%, optional)',
          initialValue: _c.qePeakPct?.toString(),
          helperText: 'Spec-sheet peak QE, e.g. 80 for 80%.',
          keyboardType: const TextInputType.numberWithOptions(decimal: true),
          inputFormatters: WizardInput.unsignedDecimal,
          onChanged: (v) => _assignDouble(v, (d) => _c.qePeakPct = d),
        ),
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
      var wheel = await FilterWheelNamesApi(server).read();
      if (!mounted) return;
      wheel ??= await _connectAssignedThenRead(
        ref, server,
        type: EquipmentDeviceType.filterWheel,
        assignedId: _draftOf(ref).equipment.filterWheelDeviceId,
        apiFactory: ref.read(filterWheelApiFactoryProvider),
        read: () => FilterWheelNamesApi(server).read(),
        isMounted: () => mounted,
      );
      if (!mounted) return;
      if (wheel == null || wheel.slots.isEmpty) {
        messenger.showSnackBar(const SnackBar(
            content: Text('The filter wheel didn\'t come up. If it\'s '
                'assigned on the Discover step, the daemon connects it '
                'automatically — a timeout there usually means the bridge '
                'can\'t reach the wheel itself: check power/USB in '
                'AlpacaBridge. You can also name the slots manually.')));
        return;
      }
      final w = wheel; // non-null after the guard; promote for the closures
      // Replacing the list wipes anything the user already typed (names,
      // wavelengths, offsets). Confirm first if there's existing data, so a
      // fat-fingered Refresh can't silently destroy it.
      final hasUserData = _fw.filters.any((f) =>
          (f.name?.isNotEmpty ?? false) ||
          f.type != null ||
          f.wavelengthNm != null ||
          f.focusOffsetSteps != null);
      if (hasUserData) {
        final replace = await showDialog<bool>(
          context: context,
          builder: (ctx) => AlertDialog(
            title: const Text('Replace your filters?'),
            content: Text('Load ${w.slots.length} slot(s) from the connected '
                'wheel, replacing your ${_fw.filters.length} current filter(s)? '
                'Names + focus offsets come from the wheel; any wavelengths/types '
                'you entered are cleared.'),
            actions: [
              TextButton(
                  onPressed: () => Navigator.of(ctx).pop(false),
                  child: const Text('Cancel')),
              FilledButton(
                  onPressed: () => Navigator.of(ctx).pop(true),
                  child: const Text('Replace')),
            ],
          ),
        );
        if (replace != true || !mounted) return;
      }
      setState(() {
        // Replace the slots with what the wheel reports — fresh FilterDef objects
        // so each row's ObjectKey changes and its (uncontrolled) name field
        // re-seeds. type/wavelength have no Alpaca source, so they stay blank for
        // the user to fill.
        _fw.filters
          ..clear()
          ..addAll(w.slots.map((s) => FilterDef()
            ..name = s.name.trim().isEmpty ? null : s.name.trim()
            ..focusOffsetSteps = s.focusOffset));
      });
      messenger.showSnackBar(SnackBar(
          content: Text('Loaded ${w.slots.length} slot(s) from the connected '
              'filter wheel.')));
    } catch (e) {
      if (mounted) {
        messenger.showSnackBar(
            SnackBar(content: Text('Could not read the filter wheel: ${_describeError(e)}')));
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
              helperText: 'Just the number: 656 for Hα, 501 for OIII, 672 for '
                  'SII. Leave blank for broadband (L/R/G/B).',
              helperMaxLines: 2,
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
  // Re-seeds the (uncontrolled) step-size field after a refresh — same
  // pattern as the telescope screen.
  int _seed = 0;
  bool _refreshing = false;

  Future<void> _refreshFromFocuser() async {
    final messenger = ScaffoldMessenger.of(context);
    final server = _activeServer(ref);
    if (server == null) {
      messenger.showSnackBar(const SnackBar(
          content: Text('Connect to a daemon first to read the focuser.')));
      return;
    }
    setState(() => _refreshing = true);
    try {
      var props = await FocuserPropsApi(server).read();
      if (!mounted) return;
      props ??= await _connectAssignedThenRead(
        ref, server,
        type: EquipmentDeviceType.focuser,
        assignedId: _draftOf(ref).equipment.focuserDeviceId,
        apiFactory: ref.read(focuserApiFactoryProvider),
        read: () => FocuserPropsApi(server).read(),
        isMounted: () => mounted,
      );
      if (!mounted) return;
      if (props == null) {
        messenger.showSnackBar(const SnackBar(
            content: Text('The focuser didn\'t come up. If it\'s assigned '
                'on the Discover step, the daemon connects it automatically — '
                'a timeout there usually means the bridge can\'t reach the '
                'focuser itself: check power/USB in AlpacaBridge.')));
        return;
      }
      final p = props;
      setState(() {
        if (p.stepSizeUm != null) _f.stepSizeMicrons = p.stepSizeUm;
        _seed++;
      });
      messenger.showSnackBar(SnackBar(
          content: Text(p.stepSizeUm != null
              ? 'Filled step size from the connected focuser.'
              : 'The focuser driver doesn\'t report its step size (most '
                  'don\'t) — it\'s fine to leave it blank.'
              '${p.canTempComp ? '' : ' It also has no temperature-'
                  'compensation support, so leave that off.'}')));
    } catch (e) {
      if (mounted) {
        messenger.showSnackBar(SnackBar(
            content: Text('Could not read the focuser: ${_describeError(e)}')));
      }
    } finally {
      if (mounted) setState(() => _refreshing = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return WizardScreenScaffold(
      step: 7,
      intro: 'Step size and backlash compensation. Every field here is '
          'OPTIONAL — autofocus works in raw steps, so if you don\'t know a '
          'value, leave it blank and refine it later in Settings.',
      children: [
        _RefreshFromDeviceButton(
          label: 'Refresh from connected focuser',
          busy: _refreshing,
          onPressed: _refreshFromFocuser,
        ),
        WizardTextField(
          key: ValueKey('wiz-focuser-step-$_seed'),
          label: 'Step size (µm/step)',
          initialValue: _f.stepSizeMicrons?.toString(),
          keyboardType: const TextInputType.numberWithOptions(decimal: true),
          inputFormatters: WizardInput.unsignedDecimal,
          helperText: 'From the motor\'s spec sheet if the driver doesn\'t '
              'report it. Only used for display — blank is fine.',
          onChanged: (v) => _assignDouble(v, (d) => _f.stepSizeMicrons = d),
        ),
        const WizardSectionHeader('Backlash compensation'),
        const Padding(
          padding: EdgeInsets.only(bottom: 8),
          child: Text(
            'Don\'t know your backlash? Leave both at 0 — the first '
            'autofocus runs will show it as a lopsided V-curve, and you can '
            'measure it then: move the focuser far in one direction, reverse, '
            'and count the steps before the star size actually changes. '
            'Typical geared focusers: 0–200 steps.',
            style: TextStyle(color: AraColors.textSecondary, fontSize: 12),
          ),
        ),
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
        const Padding(
          padding: EdgeInsets.only(bottom: 8),
          child: Text(
            'Leave OFF unless your focuser supports it and you already know '
            'the slope — re-running autofocus on a schedule (set later, in '
            'the Autofocus step) handles temperature drift for most rigs.',
            style: TextStyle(color: AraColors.textSecondary, fontSize: 12),
          ),
        ),
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
  // Re-seeds the (uncontrolled) name/slew-rate fields after a refresh — same
  // pattern as the telescope screen.
  int _seed = 0;
  bool _refreshing = false;

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
      var props = await TelescopeOpticsApi(server).readProps();
      if (!mounted) return;
      props ??= await _connectAssignedThenRead(
        ref, server,
        type: EquipmentDeviceType.mount,
        assignedId: _draftOf(ref).equipment.mountDeviceId,
        apiFactory: ref.read(mountApiFactoryProvider),
        read: () => TelescopeOpticsApi(server).readProps(),
        isMounted: () => mounted,
      );
      if (!mounted) return;
      if (props == null || !props.hasAny) {
        messenger.showSnackBar(const SnackBar(
            content: Text('The mount didn\'t come up. If it\'s assigned on '
                'the Discover step, the daemon connects it automatically — a '
                'timeout there usually means the bridge can\'t reach the '
                'mount itself: check power/connection in AlpacaBridge. You '
                'can also enter the values manually.')));
        return;
      }
      final p = props; // non-null after the guard; promote for the closure
      setState(() {
        if (p.name != null) _m.name = p.name;
        if (p.maxSlewRateDegPerSec != null) {
          _m.slewRateDegPerSec = p.maxSlewRateDegPerSec;
        }
        _seed++;
      });
      final filled = [
        if (p.name != null) 'name',
        if (p.maxSlewRateDegPerSec != null) 'max slew rate',
      ].join(' + ');
      messenger.showSnackBar(
          SnackBar(content: Text('Filled $filled from the connected mount.')));
    } catch (e) {
      if (mounted) {
        messenger.showSnackBar(SnackBar(
            content: Text('Could not read the mount: ${_describeError(e)}')));
      }
    } finally {
      if (mounted) setState(() => _refreshing = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return WizardScreenScaffold(
      step: 8,
      intro: 'Refresh pulls the name and fastest slew rate from the Alpaca '
          'driver. The rest are behavior choices only you can make — the '
          'defaults are safe.',
      children: [
        _RefreshFromDeviceButton(
          label: 'Refresh from connected mount',
          busy: _refreshing,
          onPressed: _refreshFromMount,
        ),
        KeyedSubtree(
          key: ValueKey('mname-$_seed'),
          child: WizardTextField(
            label: 'Mount name',
            initialValue: _m.name,
            onChanged: (v) => _m.name = v.trim().isEmpty ? null : v.trim(),
          ),
        ),
        KeyedSubtree(
          key: ValueKey('mslew-$_seed'),
          child: WizardTextField(
            label: 'Slew rate (°/sec)',
            initialValue: _m.slewRateDegPerSec?.toString(),
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            inputFormatters: WizardInput.unsignedDecimal,
            onChanged: (v) => _assignDouble(v, (d) => _m.slewRateDegPerSec = d),
          ),
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
  int _seed = 0;
  bool _refreshing = false;

  Future<void> _refreshFromRotator() async {
    final messenger = ScaffoldMessenger.of(context);
    final server = _activeServer(ref);
    if (server == null) {
      messenger.showSnackBar(const SnackBar(
          content: Text('Connect to a daemon first to read the rotator.')));
      return;
    }
    setState(() => _refreshing = true);
    try {
      var props = await RotatorPropsApi(server).read();
      if (!mounted) return;
      props ??= await _connectAssignedThenRead(
        ref, server,
        type: EquipmentDeviceType.rotator,
        assignedId: _draftOf(ref).equipment.rotatorDeviceId,
        apiFactory: ref.read(rotatorApiFactoryProvider),
        read: () => RotatorPropsApi(server).read(),
        isMounted: () => mounted,
      );
      if (!mounted) return;
      if (props == null) {
        messenger.showSnackBar(const SnackBar(
            content: Text('The rotator didn\'t come up. If it\'s assigned on '
                'the Discover step, the daemon connects it automatically — a '
                'timeout there usually means the bridge can\'t reach the '
                'rotator itself: check power/USB in AlpacaBridge. You can '
                'also enter the values manually.')));
        return;
      }
      final p = props; // non-null after the guard; promote for the closure
      setState(() {
        if (p.stepDeg != null) _r.stepDeg = p.stepDeg;
        _r.reverse = p.reverse;
        _seed++;
      });
      messenger.showSnackBar(SnackBar(
          content: Text(p.stepDeg != null
              ? 'Filled step size + reverse from the connected rotator.'
              : 'Filled reverse from the connected rotator (it doesn\'t '
                  'report a step size — most don\'t; leaving it blank is fine).')));
    } catch (e) {
      if (mounted) {
        messenger.showSnackBar(SnackBar(
            content:
                Text('Could not read the rotator: ${_describeError(e)}')));
      }
    } finally {
      if (mounted) setState(() => _refreshing = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return WizardScreenScaffold(
      step: 9,
      intro: 'Refresh pulls step size + reverse from the Alpaca driver. Min/max '
          'angles are YOUR mechanical limits (cable wrap) — no driver knows '
          'them; leave blank for full 0–360° travel.',
      children: [
        _RefreshFromDeviceButton(
          label: 'Refresh from connected rotator',
          busy: _refreshing,
          onPressed: _refreshFromRotator,
        ),
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
        KeyedSubtree(
          key: ValueKey('rstep-$_seed'),
          child: WizardTextField(
            label: 'Angle step (°)',
            initialValue: _r.stepDeg?.toString(),
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            inputFormatters: WizardInput.unsignedDecimal,
            onChanged: (v) => _assignDouble(v, (d) => _r.stepDeg = d),
          ),
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

  bool _testing = false;
  String? _testStatus;
  bool _testOk = false;

  /// Ask the DAEMON to reach the guider at the entered host:port — the
  /// connection under test is server→PHD2 (the SBC's network), not this
  /// client's. POST /equipment/guider/connect is 202-accepted; poll the
  /// status until the link resolves.
  Future<void> _testConnection() async {
    final api = ref.read(guiderApiProvider);
    if (api == null) {
      setState(() => _testStatus =
          'Not connected to a server — the server is what talks to PHD2.');
      return;
    }
    // Same parser as the save mapper (applyDraftToPhd2) so the tested target
    // and the saved target can't drift — incl. IPv6 literal handling.
    final parsed = parseHostPort(_g.hostPort);
    final host = parsed.host ?? 'localhost';
    final port = parsed.port ?? 4400;
    setState(() {
      _testing = true;
      _testOk = false;
      _testStatus = null;
    });
    try {
      await api.connect(host: host, port: port);
      // The connect is async on the daemon — poll briefly for the outcome.
      for (var i = 0; i < 20; i++) {
        await Future<void>.delayed(const Duration(milliseconds: 500));
        if (!mounted) return;
        final status = await api.getStatus();
        if (status?.isConnected ?? false) {
          setState(() {
            _testOk = true;
            _testStatus = 'Connected to ${status!.name} at $host:$port.';
          });
          return;
        }
      }
      if (!mounted) return;
      setState(() => _testStatus =
          'No PHD2 answered at $host:$port within 10 s. Check that PHD2 is '
          'running on that machine and its server is enabled '
          '(Tools → Enable Server).');
    } catch (e) {
      if (!mounted) return;
      setState(() => _testStatus =
          friendlyDaemonError(e, fallback: "Couldn't reach PHD2 at $host:$port"));
    } finally {
      if (mounted) setState(() => _testing = false);
    }
  }

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
        Padding(
          padding: const EdgeInsets.only(bottom: 12),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              OutlinedButton.icon(
                onPressed: _testing ? null : () => unawaited(_testConnection()),
                icon: _testing
                    ? const SizedBox(
                        width: 16, height: 16,
                        child: CircularProgressIndicator(strokeWidth: 2))
                    : const Icon(Icons.network_check, size: 18),
                label: const Text('Test connection'),
              ),
              if (_testStatus != null) ...[
                const SizedBox(height: 4),
                Text(
                  _testStatus!,
                  style: TextStyle(
                    fontSize: 12,
                    color: _testOk
                        ? AraColors.accentConnected
                        : AraColors.textSecondary,
                  ),
                ),
              ],
            ],
          ),
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
