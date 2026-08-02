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
import '../../../widgets/profile/profile_import_flow.dart'
    show friendlyDaemonError;
import '../wizard_form_kit.dart';
import '../wizard_save.dart' show resolveGuiderSetupType;

// ── shared parse helpers (carried over from the retired §37 device screens) ─

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

/// §76.2 Screen 4 — Guiding (interim S2 form; the §76.5 S3 slice replaces
/// this with the decisions-plus-darks screen).
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

  bool _testing = false;
  String? _testStatus;
  bool _testOk = false;

  // §63.17 — guide-camera picker + on-demand profile push.
  bool _applying = false;
  String? _applyStatus;
  bool _applyOk = false;

  @override
  void initState() {
    super.initState();
    unawaited(_loadBaseGuideSetup());
  }

  /// Fetch the base profile's guide setup type + optics reducer so the
  /// display branch and the OAG preview resolve exactly like wizard-save
  /// does. Errors are swallowed: offline keeps the guide_scope / 1.0
  /// fallbacks, which is also what the save mapper falls back to.
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
      });
    } catch (_) {
      // Offline / no daemon — keep the fallbacks.
    }
  }

  /// Merge the draft's guide-camera pick into the daemon-side OpenAstro Guider settings,
  /// then ask the daemon to re-push the profile to the guider — so the wizard
  /// selection takes effect without waiting for the final wizard Save.
  Future<void> _applyCameraToGuider() async {
    final server = ref.read(activeServerProvider);
    if (server == null) {
      setState(() => _applyStatus =
          'Not connected to a server — the server is what talks to the guider.');
      return;
    }
    setState(() {
      _applying = true;
      _applyOk = false;
      _applyStatus = null;
    });
    final api = ProfileApi(server);
    try {
      final base = await api.getPhd2Settings();
      await api.putPhd2Settings(base.copyWith(guiderCamera: _g.guiderCamera));
      await ref.read(guiderEquipmentProvider.notifier).pushProfile();
      if (!mounted) return;
      setState(() {
        _applyOk = true;
        _applyStatus = 'Camera selection pushed to the guider.';
      });
    } catch (e) {
      if (!mounted) return;
      setState(() => _applyStatus =
          friendlyDaemonError(e, fallback: "Couldn't apply the selection"));
    } finally {
      if (mounted) setState(() => _applying = false);
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

  @override
  Widget build(BuildContext context) {
    return WizardScreenScaffold(
      step: 4,
      intro: 'ARA connects to OpenAstro Guider over its JSON-RPC interface (not Alpaca).',
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
        // §63.17 — guide-camera pick from the daemon's own choice strings.
        // The current draft value stays representable even when the guider is
        // disconnected (empty choices) or the pick isn't in the daemon's list.
        Builder(builder: (context) {
          final equipment = ref.watch(guiderEquipmentProvider);
          final envelope = equipment.value;
          final connected = envelope?.connected ?? false;
          final cameras = <String>{
            '',
            ...?envelope?.choices?.cameras,
            if (_g.guiderCamera != null && _g.guiderCamera!.isNotEmpty)
              _g.guiderCamera!,
          };
          return Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              WizardDropdown<String>(
                label: 'Guide camera',
                value: cameras.contains(_g.guiderCamera ?? '')
                    ? (_g.guiderCamera ?? '')
                    : '',
                entries: [
                  for (final c in cameras)
                    DropdownMenuEntry(
                        value: c, label: c.isEmpty ? '(daemon default)' : c),
                ],
                onChanged: (v) => setState(
                    () => _g.guiderCamera = (v == null || v.isEmpty) ? null : v),
              ),
              Padding(
                padding: const EdgeInsets.only(bottom: 12),
                child: Wrap(spacing: 12, runSpacing: 8, children: [
                  OutlinedButton.icon(
                    onPressed: equipment.isLoading
                        ? null
                        : () => unawaited(ref
                            .read(guiderEquipmentProvider.notifier)
                            .refresh()),
                    icon: const Icon(Icons.refresh, size: 18),
                    label: const Text('Refresh choices'),
                  ),
                  OutlinedButton.icon(
                    onPressed: (_applying || !connected)
                        ? null
                        : () => unawaited(_applyCameraToGuider()),
                    icon: _applying
                        ? const SizedBox(
                            width: 16, height: 16,
                            child: CircularProgressIndicator(strokeWidth: 2))
                        : const Icon(Icons.send, size: 18),
                    label: const Text('Apply to guider'),
                  ),
                ]),
              ),
              if (_applyStatus != null)
                Padding(
                  padding: const EdgeInsets.only(bottom: 12),
                  child: Text(
                    _applyStatus!,
                    style: TextStyle(
                      fontSize: 12,
                      color: _applyOk
                          ? AraColors.accentConnected
                          : AraColors.textSecondary,
                    ),
                  ),
                ),
            ],
          );
        }),
        // §63.19 — guide setup type: separate guide scope (focal length
        // user-entered) or off-axis guider (focal length derived from the
        // telescope screen's optics). Null draft = keep the base profile, so
        // the DISPLAY resolves through the same helper as wizard-save.
        WizardDropdown<String>(
          label: 'Guide setup',
          value: resolveGuiderSetupType(
              _g.setupType, _baseSetupType ?? 'guide_scope'),
          entries: const [
            DropdownMenuEntry(value: 'guide_scope', label: 'Guide scope'),
            DropdownMenuEntry(
                value: 'oag', label: 'Off-axis guider (OAG)'),
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
                        'enter the telescope focal length first.'
                    : 'Guide focal length: $derived mm '
                        '(derived from the main optics).',
                style: const TextStyle(
                    fontSize: 12, color: AraColors.textSecondary),
              ),
            );
          })
        else
          WizardTextField(
            label: 'Guide focal length (mm)',
            initialValue: _g.guideFocalLengthMm?.toString(),
            keyboardType: TextInputType.number,
            inputFormatters: WizardInput.unsignedInt,
            onChanged: (v) => _g.guideFocalLengthMm = _toInt(v),
          ),
        WizardTextField(
          label: 'Guide pixel size (µm)',
          initialValue: _g.guidePixelSizeUm?.toString(),
          keyboardType: const TextInputType.numberWithOptions(decimal: true),
          inputFormatters: WizardInput.unsignedDecimal,
          onChanged: (v) => _assignDouble(v, (d) => _g.guidePixelSizeUm = d),
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
