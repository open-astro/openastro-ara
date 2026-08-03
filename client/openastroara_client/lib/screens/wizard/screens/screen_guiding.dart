import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/profile_draft.dart';
import '../../../services/profile_api.dart';
import '../../../state/guider/guider_equipment_state.dart';
import '../../../state/guider/guider_state.dart';
import '../../../state/saved_server_state.dart';
import '../../../state/wizard_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../util/guide_optics.dart';
import '../../../util/host_port.dart';
import '../../../widgets/guider/guider_setup_wizard.dart'
    show
        alpacaDeviceNamesApiProvider,
        friendlyAlpacaChoiceLabel,
        mergedAlpacaOptions,
        parseAlpacaChoiceEndpoint;
import '../../../widgets/profile/profile_import_flow.dart'
    show friendlyDaemonError;
import '../wizard_form_kit.dart';
import '../wizard_save.dart' show resolveGuiderSetupType;

// ── shared parse helpers (same guards as the other wizard screens) ──────────

int? _toInt(String raw) {
  final t = raw.trim();
  return t.isEmpty ? null : int.tryParse(t);
}

/// Assign a parsed double to the draft: clear on empty, but KEEP the prior
/// value on partial/invalid input (a lone "-" or "1." mid-keystroke) instead
/// of nulling it. Mirrors the guard in screen_profile_basics.dart.
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

/// The daemon's standard dark-exposure duration list (10 ms … 15 s), shared
/// with the §63.17 Setup-tab darks pane so the wizard's range picker and the
/// rebuild UI can never drift on the allowed steps.
const List<int> kGuideExposuresMs = [
  10, 20, 50, 100, 200, 500, 1000, 1500, 2000, 2500, 3000, //
  3500, 4000, 4500, 5000, 6000, 7000, 8000, 9000, 10000, 15000,
];

/// OpenAstro Guider's exposure label format: "0.05 s", "1.0 s", "15.0 s".
String guideExposureLabel(int ms) => ms < 1000
    ? '${(ms / 1000).toStringAsFixed(2)} s'
    : '${(ms / 1000).toStringAsFixed(1)} s';

/// §76.2 Screen 4 — Guiding: the wizard's only REAL guiding decisions (guide
/// scope vs OAG, the guide exposure range, build-darks-now), with everything
/// tunable-but-defaultable in an Advanced disclosure. The exposure range is
/// one choice with two consumers — dark-library coverage AND the guider's
/// exposure bounds — so they cannot disagree (fast mounts want 0.5–2 s,
/// slower mounts want longer; the wizard must not guess).
class ScreenGuider extends ConsumerStatefulWidget {
  const ScreenGuider({super.key});
  @override
  ConsumerState<ScreenGuider> createState() => _ScreenGuiderState();
}

class _ScreenGuiderState extends ConsumerState<ScreenGuider> {
  late final GuiderSettings _g = _draftOf(ref).guider;

  // §63.19 — base-profile values the guide-setup UI must agree with the save
  // mapper about: new wizard profiles clone the active profile, so an
  // untouched dropdown means "keep the base setup type", and the OAG
  // focal-length preview must use the same reducer factor the save uses.
  // Nulls (offline / not yet loaded) fall back to guide_scope / 1.0 — the
  // same fallbacks applyDraftToPhd2 applies when the base is unavailable.
  String? _baseSetupType;
  double? _baseReducerFactor;
  // The base profile's guide-camera choice string: [_cameraEndpoint] falls
  // back to it when the picker is untouched this session (null draft), so the
  // mount/rotator server restriction holds without re-picking the camera
  // (review r4 — a cross-server pick would silently no-op on Save).
  String? _baseGuiderCamera;

  bool _testing = false;
  String? _testStatus;
  bool _testOk = false;

  // §76.1 pixel-size autofill bookkeeping: true when the shown value came off
  // the Alpaca driver (displayed as a read fact; the Advanced manual field
  // hides).
  bool _pixelSizeFromDriver = false;

  @override
  void initState() {
    super.initState();
    // Re-derive the range gate from the persisted draft on every mount
    // (review r1): the controller resets step validity to true on ANY
    // navigation and Back is unguarded, so without this a Back → Next hop
    // would carry an inverted range straight past Next. Post-frame because
    // providers can't be written mid-build (same pattern as the §68.2
    // connect screen).
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) _setRange();
    });
    unawaited(_loadBaseGuideSetup());
    // A guide camera may already be chosen (draft re-entry / base profile) —
    // try the pixel-size read for it on entry.
    final cam = _g.guiderCamera;
    if (cam != null && cam.isNotEmpty) unawaited(_autofillPixelSize(cam));
    // Populate the real Alpaca device names on entry so the pickers open with
    // actual cameras/mounts instead of the guider's generic driver labels.
    unawaited(_sweepAlpaca());
  }

  // Real device names from the Alpaca management API, keyed
  // "host:port|type/N" — same overlay the §63.17 setup wizard uses.
  final Map<String, String> _alpacaNames = {};
  bool _sweeping = false;

  /// Resolve real device names behind every Alpaca server we know about: the
  /// endpoints named in the daemon's choice strings PLUS a daemon-side UDP
  /// discovery sweep (§63.20) so cameras on servers the guider isn't pointed
  /// at yet are still pickable. Best-effort — failures leave generic labels.
  Future<void> _sweepAlpaca() async {
    final namesApi = ref.read(alpacaDeviceNamesApiProvider);
    final endpoints = <String, ({String host, int port})>{};
    final c = ref.read(guiderEquipmentProvider).value?.choices;
    for (final choice in [
      ...?c?.cameras,
      ...?c?.mounts,
      ...?c?.auxMounts,
      ...?c?.rotators,
    ]) {
      final e = parseAlpacaChoiceEndpoint(choice);
      if (e != null) {
        endpoints['${e.host}:${e.port}'] = (host: e.host, port: e.port);
      }
    }
    try {
      final servers =
          await ref.read(guiderEquipmentProvider.notifier).discoverAlpaca();
      for (final server in servers) {
        final sep = server.lastIndexOf(':');
        if (sep <= 0) continue;
        final port = int.tryParse(server.substring(sep + 1));
        if (port == null) continue;
        endpoints[server] = (host: server.substring(0, sep), port: port);
      }
    } catch (_) {
      // No daemon / discovery unavailable — choice-string endpoints remain.
    }
    if (endpoints.isEmpty) return;
    // Parallel: an unreachable server costs its own timeout, not everyone's.
    final fetched = await Future.wait([
      for (final entry in endpoints.entries)
        namesApi
            .fetchNames(entry.value.host, entry.value.port)
            .then((names) => (endpoint: entry.key, names: names)),
    ]);
    if (!mounted) return;
    setState(() {
      for (final result in fetched) {
        result.names.forEach((typeSlot, name) {
          _alpacaNames['${result.endpoint}|$typeSlot'] = name;
        });
      }
    });
  }

  /// Refresh-choices button action: re-read the daemon's lists AND re-sweep
  /// the network for Alpaca devices, so the tap always has a visible effect.
  Future<void> _refreshAll() async {
    setState(() => _sweeping = true);
    try {
      await ref.read(guiderEquipmentProvider.notifier).refresh();
      await _sweepAlpaca();
    } finally {
      if (mounted) setState(() => _sweeping = false);
    }
  }

  /// The selected guide camera's Alpaca server ("host:port"), or null — the
  /// guider has ONE Alpaca server per profile, derived from the camera, so
  /// mount/rotator pickers only offer devices on that server. Falls back to
  /// the BASE profile's camera when the picker is untouched this session
  /// (review r4): the restriction must hold for a user who only came to add
  /// a mount/rotator.
  String? get _cameraEndpoint {
    final choice = (_g.guiderCamera?.isNotEmpty ?? false)
        ? _g.guiderCamera!
        : (_baseGuiderCamera ?? '');
    final e = parseAlpacaChoiceEndpoint(choice);
    return e == null ? null : '${e.host}:${e.port}';
  }

  /// Shared picker over daemon choice strings + synthesized Alpaca devices,
  /// labeled with real device names. Blank = keep the guider's current pick.
  ///
  /// [onPick] receives the RAW selection — '' for the blank entry — because
  /// the draft fields distinguish '' (explicit "keep guider's current",
  /// clears a stored override on Save) from null (untouched, keeps the base
  /// profile's value). Collapsing '' to null here made the blank entry a
  /// no-op for mount/aux/rotator (review r3); each call site owns the map.
  Widget _choicePicker({
    required String label,
    required String? current,
    required List<String> options,
    required String alpacaType,
    required void Function(String) onPick,
  }) {
    final values = <String>{
      '',
      ...options,
      if (current != null && current.isNotEmpty) current,
    };
    return WizardDropdown<String>(
      label: label,
      value: values.contains(current ?? '') ? (current ?? '') : '',
      entries: [
        for (final v in values)
          DropdownMenuEntry(
              value: v,
              label: v.isEmpty
                  ? "(keep guider's current)"
                  : friendlyAlpacaChoiceLabel(v, alpacaType, _alpacaNames)),
      ],
      onChanged: (v) {
        if (v != null) onPick(v);
      },
    );
  }

  Future<void> _loadBaseGuideSetup() async {
    final server = ref.read(activeServerProvider);
    if (server == null) return;
    final api = ProfileApi(server);
    try {
      final phd2 = await api.getPhd2Settings();
      final optics = await api.getOptics();
      if (!mounted) return;
      setState(() {
        _baseSetupType = phd2.guiderSetupType;
        _baseReducerFactor = optics.reducerFactor;
        _baseGuiderCamera =
            phd2.guiderCamera.isEmpty ? null : phd2.guiderCamera;
      });
    } catch (_) {
      // Offline / no daemon — keep the guide_scope / 1.0 fallbacks.
    }
  }

  /// §76.1 — the guide camera's pixel size is a DEVICE fact: read it off the
  /// camera's Alpaca driver (daemon-relayed, §63.20 endpoint) instead of
  /// asking. Best-effort; a failure leaves the Advanced manual field in play.
  Future<void> _autofillPixelSize(String cameraChoice) async {
    final endpoint = parseAlpacaChoiceEndpoint(cameraChoice);
    if (endpoint == null) return;
    final api = ref.read(guiderEquipmentApiProvider);
    if (api == null) return;
    try {
      final size = await api.getAlpacaCameraPixelSize(
        host: endpoint.host,
        port: endpoint.port,
        device: endpoint.device,
      );
      if (!mounted || size == null || size <= 0) return;
      setState(() {
        _g.guidePixelSizeUm = size;
        _pixelSizeFromDriver = true;
      });
    } catch (_) {
      // Manual entry remains — the field never blocks on the driver.
    }
  }

  /// Ask the DAEMON to reach the guider at the entered host:port — the
  /// connection under test is server→OpenAstro Guider (the SBC's network), not this
  /// client's. POST /equipment/guider/connect is 202-accepted; poll the
  /// status until the link resolves.
  Future<void> _testConnection() async {
    final api = ref.read(guiderApiProvider);
    if (api == null) {
      setState(() => _testStatus =
          'Not connected to a server — the server is what talks to OpenAstro Guider.');
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
          'No OpenAstro Guider answered at $host:$port within 10 s. Check that OpenAstro Guider is '
          'running on that machine and its server is enabled '
          '(Tools → Enable Server).');
    } catch (e) {
      if (!mounted) return;
      setState(() => _testStatus =
          friendlyDaemonError(e, fallback: "Couldn't reach OpenAstro Guider at $host:$port"));
    } finally {
      if (mounted) setState(() => _testing = false);
    }
  }

  /// Publish range sanity to the shell's Next gate. Dropdown steps make an
  /// inverted range user-reachable (pick shortest > longest), and it would
  /// silently corrupt BOTH consumers (darks coverage + guider bounds).
  void _setRange({int? minMs, int? maxMs}) {
    setState(() {
      if (minMs != null) _g.darkMinExposureMs = minMs;
      if (maxMs != null) _g.darkMaxExposureMs = maxMs;
    });
    ref
        .read(wizardStepValidProvider.notifier)
        .setValid(_g.darkMinExposureMs <= _g.darkMaxExposureMs);
  }

  @override
  Widget build(BuildContext context) {
    final rangeInvalid = _g.darkMinExposureMs > _g.darkMaxExposureMs;
    return WizardScreenScaffold(
      step: 4,
      intro: 'Three decisions: how you guide, your exposure range, and '
          'whether to shoot the dark library now (the scope is covered '
          'anyway — perfect darks weather). Everything else is defaulted '
          'under Advanced.',
      children: [
        WizardTextField(
          label: 'OpenAstro Guider host:port',
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
        // §63.17 — guide-camera pick from the daemon's own choice strings,
        // with the §76.1 pixel-size read on every pick. The current draft
        // value stays representable even when the guider is disconnected.
        Builder(builder: (context) {
          final equipment = ref.watch(guiderEquipmentProvider);
          final choices = equipment.value?.choices;
          return Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              _choicePicker(
                label: 'Guide camera',
                current: _g.guiderCamera,
                options: mergedAlpacaOptions(
                    choices?.cameras ?? const [], 'camera', 'Camera',
                    _alpacaNames),
                alpacaType: 'camera',
                onPick: (v) {
                  // Camera has no ''-vs-null distinction: blank = untouched.
                  final sel = v.isEmpty ? null : v;
                  setState(() {
                    _g.guiderCamera = sel;
                    _pixelSizeFromDriver = false;
                  });
                  if (sel != null) unawaited(_autofillPixelSize(sel));
                },
              ),
              _choicePicker(
                label: 'Guide mount (pulse target)',
                current: _g.guiderMount,
                options: mergedAlpacaOptions(
                    choices?.mounts ?? const [], 'telescope', 'Mount',
                    _alpacaNames,
                    onlyEndpoint: _cameraEndpoint),
                alpacaType: 'telescope',
                onPick: (v) => setState(() => _g.guiderMount = v),
              ),
              _choicePicker(
                label: 'Aux mount (optional)',
                current: _g.guiderAuxMount,
                options: choices?.auxMounts ?? const [],
                alpacaType: 'telescope',
                onPick: (v) => setState(() => _g.guiderAuxMount = v),
              ),
              _choicePicker(
                label: 'Rotator (optional)',
                current: _g.guiderRotator,
                options: mergedAlpacaOptions(
                    choices?.rotators ?? const [], 'rotator', 'Rotator',
                    _alpacaNames,
                    onlyEndpoint: _cameraEndpoint),
                alpacaType: 'rotator',
                onPick: (v) => setState(() => _g.guiderRotator = v),
              ),
              Padding(
                padding: const EdgeInsets.only(bottom: 12),
                child: Row(children: [
                  OutlinedButton.icon(
                    onPressed: equipment.isLoading || _sweeping
                        ? null
                        : () => unawaited(_refreshAll()),
                    icon: _sweeping
                        ? const SizedBox(
                            width: 16, height: 16,
                            child: CircularProgressIndicator(strokeWidth: 2))
                        : const Icon(Icons.refresh, size: 18),
                    label: const Text('Refresh choices'),
                  ),
                  if (_pixelSizeFromDriver) ...[
                    const SizedBox(width: 12),
                    Expanded(
                      child: Text(
                        'Pixel size: ${_g.guidePixelSizeUm} µm — read from '
                        'the camera driver.',
                        style: const TextStyle(
                            fontSize: 12, color: AraColors.accentConnected),
                      ),
                    ),
                  ],
                ]),
              ),
            ],
          );
        }),
        // §63.19 — guide scope vs OAG (OAG derives the focal length from the
        // main optics; the wizard already read those from Alpaca).
        WizardDropdown<String>(
          label: 'Guide setup',
          value: resolveGuiderSetupType(
              _g.setupType, _baseSetupType ?? 'guide_scope'),
          entries: const [
            DropdownMenuEntry(value: 'guide_scope', label: 'Guide scope'),
            DropdownMenuEntry(value: 'oag', label: 'Off-axis guider (OAG)'),
          ],
          onChanged: (v) => setState(() => _g.setupType = v),
        ),
        if (resolveGuiderSetupType(
                _g.setupType, _baseSetupType ?? 'guide_scope') ==
            'oag')
          Builder(builder: (context) {
            final mainFl = _draftOf(ref).telescope.focalLengthMm ?? 0;
            // Same reducer rule as applyDraftToPhd2, so the previewed number
            // is the persisted number.
            final derived = derivedOagGuideFocalLength(
                mainFl, effectiveOagReducerFactor(_baseReducerFactor));
            return Padding(
              padding: const EdgeInsets.only(bottom: 12),
              child: Text(
                derived == 0
                    ? 'Guide focal length: derived from the main optics — '
                        'fix the telescope focal length on the equipment '
                        'screen first.'
                    : 'Guide focal length: $derived mm '
                        '(derived from the main optics).',
                style: const TextStyle(
                    fontSize: 12, color: AraColors.textSecondary),
              ),
            );
          })
        else
          WizardTextField(
            label: 'Guide scope focal length (mm)',
            initialValue: _g.guideFocalLengthMm?.toString(),
            keyboardType: TextInputType.number,
            inputFormatters: WizardInput.unsignedInt,
            onChanged: (v) => _g.guideFocalLengthMm = _toInt(v),
          ),
        const WizardSectionHeader('Guide exposure range'),
        Padding(
          padding: const EdgeInsets.only(bottom: 4),
          child: Row(children: [
            Expanded(
              child: WizardDropdown<int>(
                label: 'Shortest',
                value: kGuideExposuresMs.contains(_g.darkMinExposureMs)
                    ? _g.darkMinExposureMs
                    : 1000,
                entries: [
                  for (final ms in kGuideExposuresMs)
                    DropdownMenuEntry(value: ms, label: guideExposureLabel(ms)),
                ],
                onChanged: (v) => _setRange(minMs: v),
              ),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: WizardDropdown<int>(
                label: 'Longest',
                value: kGuideExposuresMs.contains(_g.darkMaxExposureMs)
                    ? _g.darkMaxExposureMs
                    : 6000,
                entries: [
                  for (final ms in kGuideExposuresMs)
                    DropdownMenuEntry(value: ms, label: guideExposureLabel(ms)),
                ],
                onChanged: (v) => _setRange(maxMs: v),
              ),
            ),
          ]),
        ),
        Padding(
          padding: const EdgeInsets.only(bottom: 12),
          child: Text(
            rangeInvalid
                ? 'Shortest exposure must not exceed the longest.'
                : 'One choice, two consumers: the dark library covers exactly '
                    'this range, and guiding stays within it — they can never '
                    'disagree. Fast mounts (iOptron-class) often guide best '
                    'short (0.5–2 s); slower mounts may want 2–4 s.',
            style: TextStyle(
              fontSize: 12,
              color: rangeInvalid
                  ? AraColors.accentError
                  : AraColors.textSecondary,
            ),
          ),
        ),
        Padding(
          padding: const EdgeInsets.only(bottom: 4),
          child: SwitchListTile(
            contentPadding: EdgeInsets.zero,
            title: const Text('Build dark library now'),
            subtitle: const Text(
                'Cover the guide scope first. Runs in the background after '
                'Save — you can walk away.'),
            value: _g.buildDarksOnFinish,
            onChanged: (v) =>
                setState(() => _g.buildDarksOnFinish = v),
          ),
        ),
        // Advanced: tunables with good defaults — a disclosure, not a step
        // (§76.1).
        ExpansionTile(
          tilePadding: EdgeInsets.zero,
          childrenPadding: const EdgeInsets.only(top: 8),
          title: Text('Advanced',
              style: Theme.of(context)
                  .textTheme
                  .bodySmall
                  ?.copyWith(color: AraColors.textSecondary)),
          children: [
            if (!_pixelSizeFromDriver)
              WizardTextField(
                label: 'Guide pixel size (µm)',
                initialValue: _g.guidePixelSizeUm?.toString(),
                helperText: 'Normally read from the camera driver — only '
                    'needed for a non-Alpaca guide camera.',
                keyboardType:
                    const TextInputType.numberWithOptions(decimal: true),
                inputFormatters: WizardInput.unsignedDecimal,
                onChanged: (v) =>
                    _assignDouble(v, (d) => _g.guidePixelSizeUm = d),
              ),
            WizardDropdown<int>(
              label: 'Dark frames per exposure',
              value: _g.darkFrameCount,
              entries: [
                for (var n = 1; n <= 20; n++)
                  DropdownMenuEntry(value: n, label: '$n'),
              ],
              onChanged: (v) =>
                  setState(() => _g.darkFrameCount = v ?? 5),
            ),
            WizardTextField(
              label: 'Dither (pixels)',
              initialValue: _g.ditherPixels.toString(),
              keyboardType:
                  const TextInputType.numberWithOptions(decimal: true),
              inputFormatters: WizardInput.unsignedDecimal,
              onChanged: (v) => _assignDouble(v, (d) {
                if (d != null) _g.ditherPixels = d;
              }),
            ),
            WizardTextField(
              label: 'Settle threshold (px)',
              initialValue: _g.settleThresholdPx.toString(),
              keyboardType:
                  const TextInputType.numberWithOptions(decimal: true),
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
                    value: CalibrationCadence.eachSession,
                    label: 'Each session'),
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
        ),
      ],
    );
  }
}
