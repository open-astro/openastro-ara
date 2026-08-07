import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/guider_equipment_choices.dart';
import '../../models/guider_status.dart';
import '../../services/alpaca_device_names_api.dart';
import '../../state/guider/guider_build_activity_state.dart';
import '../../state/guider/guider_calibration_state.dart';
import '../../state/guider/guider_equipment_state.dart';
import '../../state/guider/guider_state.dart';
import '../../state/profile_management_state.dart';
import '../../state/settings/optics_settings_state.dart';
import '../../state/settings/phd2_settings_state.dart';
import '../../theme/ara_colors.dart';
import '../../util/guide_optics.dart';
import '../profile/profile_import_flow.dart' show friendlyDaemonError;

/// Parsed Alpaca endpoint from a daemon camera-choice string, e.g.
/// `"Alpaca Camera [rc91.lan:6800/1]"` → host `rc91.lan`, port `6800`,
/// device `1`. Null when the choice doesn't carry the `[host:port/N]` suffix
/// (non-Alpaca drivers, `"None"`). Pure — unit-tested.
/// Builds the Alpaca management-API reader. Overridable in tests.
final alpacaDeviceNamesApiProvider = Provider<AlpacaDeviceNamesClient>((ref) {
  final api = AlpacaDeviceNamesApi();
  ref.onDispose(api.close);
  return api;
});

/// Friendly display label for a daemon choice string: the REAL device name
/// from the Alpaca management API when known, with the endpoint kept as a
/// disambiguator — `"ZWO ASI290MM Mini (192.168.1.118:6800/1)"`. Falls back
/// to the verbatim choice when the name isn't known (non-Alpaca drivers,
/// unreachable management API). [alpacaType] is the Alpaca device type for
/// this slot (camera / telescope / rotator). Pure — unit-tested.
String friendlyAlpacaChoiceLabel(
    String choice, String alpacaType, Map<String, String> namesByEndpoint) {
  final endpoint = parseAlpacaChoiceEndpoint(choice);
  if (endpoint == null) return choice;
  final name = namesByEndpoint[
      '${endpoint.host}:${endpoint.port}|$alpacaType/${endpoint.device}'];
  if (name == null || name.isEmpty) return choice;
  return '$name (${endpoint.host}:${endpoint.port}/${endpoint.device})';
}

/// §63.20 cross-server options (shared with the §76 wizard's guiding screen):
/// the daemon only ever offers ONE Alpaca choice per slot (its currently-
/// configured server/device), so gear on other Alpaca servers would be
/// invisible. Synthesize a daemon-format `Alpaca <Slot> [host:port/N]` choice
/// for every device of [alpacaType] known in [namesByEndpoint] (keys
/// `"host:port|type/N"`). Daemon-offered strings stay first; duplicates
/// collapse. [onlyEndpoint] ("host:port") limits synthesis to one server —
/// the guider has a SINGLE Alpaca server per profile, derived from the
/// CAMERA's endpoint, so mount/rotator slots must only offer that server.
/// Pure — unit-testable.
List<String> mergedAlpacaOptions(List<String> daemonChoices, String alpacaType,
    String slotWord, Map<String, String> namesByEndpoint,
    {String? onlyEndpoint}) {
  final merged = <String>[...daemonChoices];
  for (final key in namesByEndpoint.keys) {
    final bar = key.indexOf('|');
    if (bar < 0) continue;
    final endpoint = key.substring(0, bar);
    if (onlyEndpoint != null && endpoint != onlyEndpoint) continue;
    final typeSlot = key.substring(bar + 1);
    if (!typeSlot.startsWith('$alpacaType/')) continue;
    final synthesized =
        'Alpaca $slotWord [$endpoint/${typeSlot.substring(alpacaType.length + 1)}]';
    if (!merged.contains(synthesized)) merged.add(synthesized);
  }
  return merged;
}

({String host, int port, int device})? parseAlpacaChoiceEndpoint(String choice) {
  final m = RegExp(r'\[([^\[\]:]+):(\d+)/(\d+)\]\s*$').firstMatch(choice);
  if (m == null) return null;
  final port = int.tryParse(m.group(2)!);
  final device = int.tryParse(m.group(3)!);
  if (port == null || port < 1 || port > 65535 || device == null) return null;
  return (host: m.group(1)!, port: port, device: device);
}

/// Open the §63 guider setup wizard.
Future<void> showGuiderSetupWizard(BuildContext context) => showDialog<void>(
      context: context,
      // Multi-step with in-flight daemon calls — an accidental outside tap
      // shouldn't discard the walk-through.
      barrierDismissible: false,
      builder: (_) => const GuiderSetupWizard(),
    );

/// Guider setup wizard — the OpenAstro Guider "new profile wizard" flow, redone for Ara
/// (mirrors the original `profile_wizard.cpp` steps: connection → guide
/// camera → guide optics → mount → apply → dark library). Every gear picker
/// is fed by the daemon's LIVE choice strings (`GET /equipment/guider/
/// choices`), so a selection can never name a device the daemon doesn't have
/// — the failure mode of hand-typed / stale-profile names.
///
/// Transient-surface draft pattern: the wizard edits a local [Phd2Settings]
/// draft fetched directly from the daemon; nothing touches the shared
/// provider until Apply succeeds, and Cancel discards everything.
class GuiderSetupWizard extends ConsumerStatefulWidget {
  const GuiderSetupWizard({super.key});

  @override
  ConsumerState<GuiderSetupWizard> createState() => _GuiderSetupWizardState();
}

enum _WizStep { connection, camera, optics, mount, apply, darks }

class _GuiderSetupWizardState extends ConsumerState<GuiderSetupWizard> {
  _WizStep _step = _WizStep.connection;

  // The local draft + the daemon's copy it was forked from (see the class
  // comment — Apply PUTs `_serverCopy + edited fields`, never provider state).
  Phd2Settings? _draft;
  Phd2Settings? _serverCopy;

  GuiderEquipmentChoices? _choices;
  bool _busy = false;
  String? _error;
  bool _applied = false;
  bool _darksStarted = false;

  // §63.20 pixel-size autofill: bumped whenever the driver read lands so the
  // (initialValue-based) pixel-size field rebuilds with the fetched value;
  // the note tells the user where the number came from.
  int _pixelSizeFill = 0;
  bool _pixelSizeFromDriver = false;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _hydrate());
  }

  Future<void> _hydrate() async {
    final api = ref.read(profileApiProvider);
    if (api == null) {
      setState(() => _error = 'Not connected — connect to your rig to save this.');
      return;
    }
    setState(() => _busy = true);
    try {
      final fresh = await api.getPhd2Settings();
      if (!mounted) return;
      setState(() {
        _serverCopy = fresh;
        _draft = fresh;
        _error = null;
      });
      await _refreshChoices();
    } catch (e) {
      if (mounted) {
        setState(() => _error =
            friendlyDaemonError(e, fallback: 'Could not load guider settings'));
      }
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  // Real device names from the Alpaca management API, keyed
  // "host:port|type/N" — overlays the daemon's generic choice labels.
  final Map<String, String> _alpacaNames = {};

  Future<void> _refreshChoices() async {
    final api = ref.read(guiderEquipmentApiProvider);
    if (api == null) return;
    try {
      final res = await api.getChoices();
      if (mounted) setState(() => _choices = res.choices);
      await _refreshAlpacaNames(res.choices);
    } catch (_) {
      // Choices need the daemon link; the connection step surfaces that.
    }
  }

  /// Resolve the real device names behind every Alpaca endpoint the choice
  /// lists mention (usually a single host). Best-effort — the generic labels
  /// stay when the management API can't be reached.
  Future<void> _refreshAlpacaNames(GuiderEquipmentChoices? choices) async {
    if (choices == null) return;
    final namesApi = ref.read(alpacaDeviceNamesApiProvider);
    final endpoints = <String, ({String host, int port})>{};
    for (final choice in [
      ...choices.cameras,
      ...choices.mounts,
      ...choices.auxMounts,
      ...choices.rotators,
    ]) {
      final e = parseAlpacaChoiceEndpoint(choice);
      if (e != null) endpoints['${e.host}:${e.port}'] = (host: e.host, port: e.port);
    }
    // Parallel: an unreachable server costs its own timeout, not everyone's
    // (review r1 — serial awaits added up across servers).
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

  /// Label map for a picker slot: every option labeled with its real Alpaca
  /// device name when known.
  Map<String, String> _labelsFor(List<String> options, String alpacaType) => {
        for (final o in options)
          o: friendlyAlpacaChoiceLabel(o, alpacaType, _alpacaNames),
      };

  /// See [mergedAlpacaOptions] — bound to this wizard's discovered names.
  List<String> _mergedOptions(
          List<String> daemonChoices, String alpacaType, String slotWord,
          {String? onlyEndpoint}) =>
      mergedAlpacaOptions(daemonChoices, alpacaType, slotWord, _alpacaNames,
          onlyEndpoint: onlyEndpoint);

  /// The selected guide camera's Alpaca server ("host:port"), or null for
  /// non-Alpaca / unset camera selections.
  String? get _cameraEndpoint {
    final e = parseAlpacaChoiceEndpoint(_draft?.guiderCamera ?? '');
    return e == null ? null : '${e.host}:${e.port}';
  }

  /// True when [selection] names a device on a DIFFERENT Alpaca server than
  /// the camera — the push would silently drop its device number (OpenAstro Guider has
  /// one Alpaca server, derived from the camera).
  bool _isCrossServerPick(String selection) {
    final cam = _cameraEndpoint;
    final e = parseAlpacaChoiceEndpoint(selection);
    return cam != null && e != null && '${e.host}:${e.port}' != cam;
  }

  /// §63.20 — sweep the network for Alpaca servers (daemon-side UDP
  /// discovery) and read each one's device names, so gear on servers the
  /// daemon isn't pointed at yet (AlpacaBridge) becomes pickable.
  Future<void> _discoverAlpacaServers() async {
    final api = ref.read(guiderEquipmentApiProvider);
    final namesApi = ref.read(alpacaDeviceNamesApiProvider);
    if (api == null) return;
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final servers = await api.discoverAlpaca();
      final targets = <({String host, int port})>[];
      for (final server in servers) {
        final sep = server.lastIndexOf(':');
        if (sep <= 0) continue;
        final host = server.substring(0, sep);
        final port = int.tryParse(server.substring(sep + 1));
        if (port == null) continue;
        targets.add((host: host, port: port));
      }
      // Parallel fetch — an unreachable server costs its own timeout only.
      final fetched = await Future.wait([
        for (final t in targets)
          namesApi
              .fetchNames(t.host, t.port)
              .then((names) => (endpoint: '${t.host}:${t.port}', names: names)),
      ]);
      if (!mounted) return;
      var found = 0;
      setState(() {
        for (final result in fetched) {
          found += result.names.length;
          result.names.forEach((typeSlot, name) {
            _alpacaNames['${result.endpoint}|$typeSlot'] = name;
          });
        }
      });
      if (mounted && found == 0) {
        setState(() => _error =
            'Discovery found no Alpaca devices — check the servers are running.');
      }
    } catch (e) {
      if (mounted) {
        setState(() =>
            _error = friendlyDaemonError(e, fallback: 'Alpaca discovery failed'));
      }
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  /// Connect the daemon link with the draft's host/port, then wait for the
  /// link to settle so the camera step opens with live choices.
  Future<void> _connect() async {
    final api = ref.read(guiderApiProvider);
    final draft = _draft;
    if (api == null || draft == null) return;
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      await api.connect(host: draft.host, port: draft.port);
      // Connect is 202-accepted; poll the status until the link settles.
      final status = ref.read(guiderStatusProvider.notifier);
      for (var i = 0; i < 15; i++) {
        await Future<void>.delayed(const Duration(seconds: 1));
        await status.refresh();
        if (!mounted) return;
        if (_linkConnected) break;
      }
      await _refreshChoices();
    } catch (e) {
      if (mounted) {
        setState(() =>
            _error = friendlyDaemonError(e, fallback: 'Connect failed'));
      }
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  bool get _linkConnected =>
      ref.read(guiderStatusProvider).asData?.value?.connectionState ==
      GuiderConnectionState.connected;

  /// Save the draft to the daemon profile, push it to the guider (the §63.17
  /// push cycles the daemon's equipment inside a disconnected window), then
  /// reflect ONLY the applied fields into the shared provider.
  Future<void> _apply() async {
    final api = ref.read(profileApiProvider);
    final equipment = ref.read(guiderEquipmentApiProvider);
    final serverCopy = _serverCopy;
    final draft = _draft;
    if (api == null || equipment == null || serverCopy == null || draft == null) {
      return;
    }
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final toSave = serverCopy.copyWith(
        host: draft.host,
        port: draft.port,
        guiderCamera: draft.guiderCamera,
        guiderCameraId: draft.guiderCameraId,
        guidePixelSize: draft.guidePixelSize,
        guiderSetupType: draft.guiderSetupType,
        guideFocalLength: draft.guideFocalLength,
        guiderMount: draft.guiderMount,
        guiderAuxMount: draft.guiderAuxMount,
        guiderRotator: draft.guiderRotator,
        guiderAlpacaHost: draft.guiderAlpacaHost,
        guiderAlpacaPort: draft.guiderAlpacaPort,
      );
      final echoed = await api.putPhd2Settings(toSave);
      if (!mounted) return;
      setState(() => _serverCopy = echoed);
      await equipment.pushProfile();
      if (!mounted) return;
      // The wizard's fields are now Ara's truth — reflect them into
      // the shared provider through the bounded setters.
      final n = ref.read(phd2SettingsProvider.notifier);
      n.setHost(draft.host);
      n.setPort(draft.port);
      n.setGuiderCamera(draft.guiderCamera);
      n.setGuiderCameraId(draft.guiderCameraId);
      n.setGuidePixelSize(draft.guidePixelSize);
      n.setGuiderSetupType(draft.guiderSetupType);
      n.setGuideFocalLength(draft.guideFocalLength);
      n.setGuiderMount(draft.guiderMount);
      n.setGuiderAuxMount(draft.guiderAuxMount);
      n.setGuiderRotator(draft.guiderRotator);
      n.setGuiderAlpacaHost(draft.guiderAlpacaHost);
      n.setGuiderAlpacaPort(draft.guiderAlpacaPort);
      setState(() => _applied = true);
    } catch (e) {
      if (mounted) {
        setState(() => _error = friendlyDaemonError(e,
            fallback: 'Apply failed — Ara could not take the profile'));
      }
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  // Dark-library build parameters, mirroring the original OpenAstro Guider darks dialog:
  // read-only exposure dropdowns over the daemon's standard duration list
  // (10 ms … 15 s) with OpenAstro Guider's defaults (min 1.0 s, max 6.0 s), and a
  // 1–20 frame count (spin range in the original; a dropdown here).
  static const darkExposuresMs = [
    10, 20, 50, 100, 200, 500, 1000, 1500, 2000, 2500, 3000, //
    3500, 4000, 4500, 5000, 6000, 7000, 8000, 9000, 10000, 15000,
  ];
  int _darkFrameCount = 5;
  int _darkMinExpMs = 1000;
  int _darkMaxExpMs = 6000;

  /// OpenAstro Guider's exposure label format: "0.05 s", "1.0 s", "15.0 s".
  static String exposureLabel(int ms) => ms < 1000
      ? '${(ms / 1000).toStringAsFixed(2)} s'
      : '${(ms / 1000).toStringAsFixed(1)} s';

  Future<void> _buildDarks() async {
    final api = ref.read(guiderCalibrationApiProvider);
    if (api == null) return;
    if (_darkMinExpMs > _darkMaxExpMs) {
      setState(() =>
          _error = 'Shortest exposure must not exceed the longest exposure.');
      return;
    }
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      await api.buildDarkLibrary(
        frameCount: _darkFrameCount,
        minExposureMs: _darkMinExpMs,
        maxExposureMs: _darkMaxExpMs,
      );
      if (mounted) setState(() => _darksStarted = true);
    } catch (e) {
      if (mounted) {
        setState(() => _error =
            friendlyDaemonError(e, fallback: 'Dark-library build failed'));
      }
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  /// §63.20 — after a camera pick, ask the daemon to read the sensor's pixel
  /// size from its Alpaca driver and pre-fill the field. Best-effort: any
  /// failure leaves the field as-is for manual entry. The choice string's
  /// `[host:port/N]` suffix names the exact device; a non-Alpaca choice
  /// (no suffix) is skipped.
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
        _draft = _draft?.copyWith(guidePixelSize: size);
        _pixelSizeFill++;
        _pixelSizeFromDriver = true;
      });
    } catch (_) {
      // Manual entry remains — the field never blocks on the driver.
    }
  }

  void _edit(Phd2Settings Function(Phd2Settings) change) {
    final draft = _draft;
    if (draft == null) return;
    setState(() => _draft = change(draft));
  }

  static const _stepTitles = <_WizStep, String>{
    _WizStep.connection: 'Guider connection',
    _WizStep.camera: 'Guide camera',
    _WizStep.optics: 'Guide optics',
    _WizStep.mount: 'Mount',
    _WizStep.apply: 'Review & apply',
    _WizStep.darks: 'Dark library',
  };

  @override
  Widget build(BuildContext context) {
    final draft = _draft;
    return AlertDialog(
      backgroundColor: AraColors.bgPanel,
      title: Row(
        children: [
          const Icon(Icons.auto_fix_high, size: 20),
          const SizedBox(width: 8),
          Expanded(
              child: Text('Guider setup — ${_stepTitles[_step]!}',
                  overflow: TextOverflow.ellipsis)),
          Text('${_step.index + 1}/${_WizStep.values.length}',
              style: Theme.of(context)
                  .textTheme
                  .bodySmall
                  ?.copyWith(color: AraColors.textSecondary)),
        ],
      ),
      content: SizedBox(
        width: 520,
        child: SingleChildScrollView(
          child: draft == null
              ? Padding(
                  padding: const EdgeInsets.all(24),
                  child: _error != null
                      ? Text(_error!,
                          style:
                              const TextStyle(color: AraColors.accentError))
                      : const Center(child: CircularProgressIndicator()),
                )
              : Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    ..._stepBody(draft),
                    if (_error != null) ...[
                      const SizedBox(height: 12),
                      Text(_error!,
                          style:
                              const TextStyle(color: AraColors.accentError)),
                    ],
                  ],
                ),
        ),
      ),
      actions: _actions(),
    );
  }

  List<Widget> _stepBody(Phd2Settings draft) {
    switch (_step) {
      case _WizStep.connection:
        final status = ref.watch(guiderStatusProvider).asData?.value;
        final connected =
            status?.connectionState == GuiderConnectionState.connected;
        return [
          const Text(
              'Where is the OpenAstro Guider server running? Ara connects to '
              'it over the network; Ara owns the guide camera.'),
          const SizedBox(height: 12),
          _textField(
            label: 'Host',
            value: draft.host,
            onChanged: (s) {
              final v = s.trim();
              if (v.isNotEmpty) _edit((d) => d.copyWith(host: v));
            },
          ),
          _textField(
            label: 'Port',
            value: '${draft.port}',
            onChanged: (s) {
              final v = int.tryParse(s.trim());
              if (v != null && v >= 1024 && v <= 65535) {
                _edit((d) => d.copyWith(port: v));
              }
            },
          ),
          const SizedBox(height: 12),
          Row(
            children: [
              FilledButton.icon(
                onPressed: _busy ? null : _connect,
                icon: _busy
                    ? const SizedBox(
                        width: 14,
                        height: 14,
                        child: CircularProgressIndicator(strokeWidth: 2))
                    : const Icon(Icons.link, size: 16),
                label: const Text('Connect'),
              ),
              const SizedBox(width: 12),
              Icon(Icons.circle,
                  size: 10,
                  color: connected
                      ? AraColors.accentConnected
                      : AraColors.textDisabled),
              const SizedBox(width: 6),
              Flexible(
                child: Text(
                  connected
                      ? 'Connected${status?.name.isNotEmpty == true ? ' — ${status!.name}' : ''}'
                      : 'Not connected',
                  overflow: TextOverflow.ellipsis,
                ),
              ),
            ],
          ),
        ];
      case _WizStep.camera:
        final cameraOptions =
            _mergedOptions(_choices?.cameras ?? const [], 'camera', 'Camera');
        return [
          const Text(
              'Pick the guide camera. The list covers every Alpaca server '
              'found on the network, not just the one the guider server is '
              'currently pointed at — use "Search network" if yours is '
              'missing.'),
          const SizedBox(height: 12),
          _choiceDropdown(
            label: 'Guide camera',
            value: draft.guiderCamera,
            options: cameraOptions,
            labels: _labelsFor(cameraOptions, 'camera'),
            onChanged: (v) {
              _edit((d) => d.copyWith(guiderCamera: v));
              // A camera on another Alpaca server retargets the daemon's
              // Alpaca config on Apply — keep the profile's host/port in step.
              final e = parseAlpacaChoiceEndpoint(v);
              if (e != null) {
                _edit((d) =>
                    d.copyWith(guiderAlpacaHost: e.host, guiderAlpacaPort: e.port));
              }
              _autofillPixelSize(v);
            },
          ),
          KeyedSubtree(
            // Rebuild with the driver-read value when the autofill lands
            // (TextFormField only honors initialValue on first build).
            key: ValueKey('wiz-pixel-fill-$_pixelSizeFill'),
            child: _textField(
              label: 'Pixel size (µm)',
              value: draft.guidePixelSize == 0 ? '' : '${draft.guidePixelSize}',
              hint: 'e.g. 2.9 for an ASI290MM Mini',
              onChanged: (s) {
                final v = double.tryParse(s.trim());
                if (v != null && v >= 0) {
                  _pixelSizeFromDriver = false;
                  _edit((d) => d.copyWith(guidePixelSize: v));
                }
              },
            ),
          ),
          if (_pixelSizeFromDriver)
            Text(
              'Read from the camera\'s Alpaca driver — edit if it looks wrong.',
              style: Theme.of(context)
                  .textTheme
                  .bodySmall
                  ?.copyWith(color: AraColors.textSecondary),
            ),
          _refreshChoicesButton(),
        ];
      case _WizStep.optics:
        final isOag = draft.guiderSetupType == 'oag';
        return [
          const Text(
              'How does the guide camera see the sky? Through its own guide '
              'scope, or an off-axis guider (OAG) on the main scope?'),
          const SizedBox(height: 12),
          _choiceDropdown(
            label: 'Setup type',
            value: draft.guiderSetupType,
            options: Phd2SettingsNotifier.guiderSetupTypes,
            labels: const {
              'guide_scope': 'Guide scope',
              'oag': 'Off-axis guider (OAG)',
            },
            onChanged: (v) {
              final norm = Phd2SettingsNotifier.normalizeGuiderSetupType(v);
              _edit((d) => d.copyWith(guiderSetupType: norm));
              if (norm == 'oag') {
                // §63.19 — OAG derives the guide focal length from the main
                // optics (focal length × reducer), same rule as the panel.
                final optics = ref.read(opticsSettingsProvider);
                _edit((d) => d.copyWith(
                    guideFocalLength: derivedOagGuideFocalLength(
                        optics.focalLengthMm, optics.reducerFactor)));
              }
            },
          ),
          if (isOag)
            Padding(
              padding: const EdgeInsets.only(top: 4),
              child: Text(
                'Guide focal length: ${draft.guideFocalLength} mm — derived '
                'from the main optics (Options → Imaging → Optics).',
                style: Theme.of(context)
                    .textTheme
                    .bodySmall
                    ?.copyWith(color: AraColors.textSecondary),
              ),
            )
          else
            _textField(
              label: 'Guide focal length (mm)',
              value:
                  draft.guideFocalLength == 0 ? '' : '${draft.guideFocalLength}',
              hint: 'e.g. 240 for a 60mm f/4 guide scope',
              onChanged: (s) {
                final v = int.tryParse(s.trim());
                if (v != null && v >= 0) {
                  _edit((d) => d.copyWith(guideFocalLength: v));
                }
              },
            ),
        ];
      case _WizStep.mount:
        return [
          const Text(
              'Pick the mount Ara sends guide pulses to. An aux mount '
              'is only needed when the guiding connection can\'t report '
              'pointing (leave it on server default otherwise).'),
          const SizedBox(height: 12),
          // Mount/rotator synthesis is limited to the CAMERA's Alpaca server:
          // OpenAstro Guider has one Alpaca server per profile (derived from the camera on
          // Apply), so a pick on any other server would be silently dropped.
          _choiceDropdown(
            label: 'Mount',
            value: draft.guiderMount,
            options: _mergedOptions(_choices?.mounts ?? const [], 'telescope',
                'Mount',
                onlyEndpoint: _cameraEndpoint),
            labels: _labelsFor(
                _mergedOptions(_choices?.mounts ?? const [], 'telescope',
                    'Mount',
                    onlyEndpoint: _cameraEndpoint),
                'telescope'),
            onChanged: (v) => _edit((d) => d.copyWith(guiderMount: v)),
          ),
          if (_isCrossServerPick(draft.guiderMount))
            Text(
              'This mount lives on a different Alpaca server than the guide '
              'camera — Ara supports one server, so this selection '
              'won\'t take effect. Pick a mount on the camera\'s server.',
              style: Theme.of(context)
                  .textTheme
                  .bodySmall
                  ?.copyWith(color: AraColors.accentBusy),
            ),
          _choiceDropdown(
            label: 'Aux mount',
            value: draft.guiderAuxMount,
            options: _choices?.auxMounts ?? const [],
            labels: _labelsFor(_choices?.auxMounts ?? const [], 'telescope'),
            allowUnset: true,
            onChanged: (v) => _edit((d) => d.copyWith(guiderAuxMount: v)),
          ),
          // Guide-field rotators are rare but the daemon supports them
          // (mirrors the original wizard's rotator page) — "None" is the
          // normal pick.
          _choiceDropdown(
            label: 'Rotator',
            value: draft.guiderRotator,
            options: _mergedOptions(
                _choices?.rotators ?? const [], 'rotator', 'Rotator',
                onlyEndpoint: _cameraEndpoint),
            labels: _labelsFor(
                _mergedOptions(_choices?.rotators ?? const [], 'rotator',
                    'Rotator',
                    onlyEndpoint: _cameraEndpoint),
                'rotator'),
            allowUnset: true,
            onChanged: (v) => _edit((d) => d.copyWith(guiderRotator: v)),
          ),
          if (_isCrossServerPick(draft.guiderRotator))
            Text(
              'This rotator lives on a different Alpaca server than the guide '
              'camera — it won\'t take effect on Apply.',
              style: Theme.of(context)
                  .textTheme
                  .bodySmall
                  ?.copyWith(color: AraColors.accentBusy),
            ),
          _refreshChoicesButton(),
        ];
      case _WizStep.apply:
        return [
          const Text('These settings go to the guider — its '
              'equipment reconnects with these selections.'),
          const SizedBox(height: 12),
          _summaryRow('Ara', '${draft.host}:${draft.port}'),
          _summaryRow(
              'Guide camera',
              draft.guiderCamera.isEmpty
                  ? '(use the guider\'s own setting)'
                  : friendlyAlpacaChoiceLabel(
                      draft.guiderCamera, 'camera', _alpacaNames)),
          _summaryRow(
              'Pixel size',
              draft.guidePixelSize == 0
                  ? '(unset)'
                  : '${draft.guidePixelSize} µm'),
          _summaryRow(
              'Optics',
              '${draft.guiderSetupType == 'oag' ? 'OAG' : 'Guide scope'}'
              '${draft.guideFocalLength > 0 ? ' — ${draft.guideFocalLength} mm' : ''}'),
          _summaryRow(
              'Mount',
              draft.guiderMount.isEmpty
                  ? '(use the guider\'s own setting)'
                  : friendlyAlpacaChoiceLabel(
                      draft.guiderMount, 'telescope', _alpacaNames)),
          _summaryRow(
              'Aux mount',
              draft.guiderAuxMount.isEmpty
                  ? '(use the guider\'s own setting)'
                  : friendlyAlpacaChoiceLabel(
                      draft.guiderAuxMount, 'telescope', _alpacaNames)),
          _summaryRow(
              'Rotator',
              draft.guiderRotator.isEmpty
                  ? '(use the guider\'s own setting)'
                  : friendlyAlpacaChoiceLabel(
                      draft.guiderRotator, 'rotator', _alpacaNames)),
          // Belt for a stale draft: the mount step restricts new picks to the
          // camera's server, but a selection made before a camera change can
          // still be cross-server — say so rather than implying it will land.
          if (_isCrossServerPick(draft.guiderMount) ||
              _isCrossServerPick(draft.guiderRotator))
            Padding(
              padding: const EdgeInsets.only(top: 8),
              child: Text(
                'A mount/rotator selection is on a different Alpaca server '
                'than the guide camera and will NOT take effect — Ara '
                'supports one Alpaca server (the camera\'s). Go Back to fix it.',
                style: Theme.of(context)
                    .textTheme
                    .bodySmall
                    ?.copyWith(color: AraColors.accentBusy),
              ),
            ),
          const SizedBox(height: 12),
          if (_applied)
            const Row(children: [
              Icon(Icons.check_circle,
                  size: 16, color: AraColors.accentConnected),
              SizedBox(width: 6),
              Expanded(
                  child:
                      Text('Applied — equipment reconnected.')),
            ])
          else
            FilledButton.icon(
              onPressed: _busy ? null : _apply,
              icon: _busy
                  ? const SizedBox(
                      width: 14,
                      height: 14,
                      child: CircularProgressIndicator(strokeWidth: 2))
                  : const Icon(Icons.send, size: 16),
              label: const Text('Apply to guider'),
            ),
        ];
      case _WizStep.darks:
        final activity = ref.watch(guiderBuildActivityProvider)[
            CalibrationArtifact.darkLibrary];
        return [
          const Text(
              'A dark library removes hot pixels from guide frames — worth '
              'building once per camera/cooling setup. The server captures '
              'frames at a range of exposures; cover the guide scope first.'),
          const SizedBox(height: 12),
          if (!_darksStarted) ...[
            _intDropdown(
              label: 'Frames per exposure',
              value: _darkFrameCount,
              options: [for (var n = 1; n <= 20; n++) n],
              labelOf: (n) => '$n',
              onChanged: (v) => setState(() => _darkFrameCount = v),
            ),
            _intDropdown(
              label: 'Shortest exposure',
              value: _darkMinExpMs,
              options: darkExposuresMs,
              labelOf: exposureLabel,
              onChanged: (v) => setState(() => _darkMinExpMs = v),
            ),
            _intDropdown(
              label: 'Longest exposure',
              value: _darkMaxExpMs,
              options: darkExposuresMs,
              labelOf: exposureLabel,
              onChanged: (v) => setState(() => _darkMaxExpMs = v),
            ),
            const SizedBox(height: 8),
            FilledButton.icon(
              onPressed: _busy ? null : _buildDarks,
              icon: _busy
                  ? const SizedBox(
                      width: 14,
                      height: 14,
                      child: CircularProgressIndicator(strokeWidth: 2))
                  : const Icon(Icons.dark_mode_outlined, size: 16),
              label: const Text('Build dark library'),
            ),
          ] else
            _DarkBuildProgress(activity: activity),
          const SizedBox(height: 8),
          Text('You can also build or rebuild it later from the Guider chip\'s '
              'Calibration screen.',
              style: Theme.of(context)
                  .textTheme
                  .bodySmall
                  ?.copyWith(color: AraColors.textSecondary)),
        ];
    }
  }

  List<Widget> _actions() {
    final canBack = _step.index > 0 && !_busy;
    final isLast = _step == _WizStep.darks;
    // Apply gates the step after it: no skipping ahead with an unpushed draft.
    final canNext = !_busy &&
        _draft != null &&
        (_step != _WizStep.apply || _applied) &&
        !isLast;
    return [
      // The final step's primary action is Finish — a disabled Next there
      // read as "something's incomplete". Cancel disappears once the setup
      // is applied (there is nothing left to cancel).
      if (!isLast)
        TextButton(
          onPressed: _busy ? null : () => Navigator.of(context).pop(),
          child: Text(_applied ? 'Done' : 'Cancel'),
        ),
      TextButton(
        onPressed: canBack
            ? () => setState(() {
                  _error = null;
                  _step = _WizStep.values[_step.index - 1];
                })
            : null,
        child: const Text('Back'),
      ),
      if (isLast)
        FilledButton(
          onPressed: _busy ? null : () => Navigator.of(context).pop(),
          child: const Text('Finish'),
        )
      else
        FilledButton(
          onPressed: canNext
              ? () {
                  final next = _WizStep.values[_step.index + 1];
                  setState(() {
                    _error = null;
                    _step = next;
                  });
                  // Re-fetch the device lists whenever a picker step opens: the
                  // initState fetch can miss (active-server providers hydrate
                  // async) and the daemon's lists change as its Alpaca host /
                  // connection state does — a stale list here reads as "my
                  // mount is missing".
                  if (next == _WizStep.camera || next == _WizStep.mount) {
                    _refreshChoices();
                  }
                }
              : null,
          child: const Text('Next'),
        ),
    ];
  }

  /// Read-only value dropdown (the original wizard's combo-box style) for the
  /// darks parameters.
  Widget _intDropdown({
    required String label,
    required int value,
    required List<int> options,
    required String Function(int) labelOf,
    required ValueChanged<int> onChanged,
  }) =>
      Padding(
        padding: const EdgeInsets.symmetric(vertical: 6),
        child: DropdownButtonFormField<int>(
          key: ValueKey('wiz-$label'),
          isExpanded: true,
          initialValue: options.contains(value) ? value : options.first,
          decoration: InputDecoration(
            labelText: label,
            isDense: true,
            border: const OutlineInputBorder(),
          ),
          items: [
            for (final o in options)
              DropdownMenuItem(value: o, child: Text(labelOf(o))),
          ],
          onChanged: (v) {
            if (v != null) onChanged(v);
          },
        ),
      );

  Widget _refreshChoicesButton() => Padding(
        padding: const EdgeInsets.only(top: 4),
        child: Wrap(
          spacing: 8,
          children: [
            TextButton.icon(
              onPressed: _busy ? null : _refreshChoices,
              icon: const Icon(Icons.refresh, size: 16),
              label: const Text('Refresh device list'),
            ),
            TextButton.icon(
              onPressed: _busy ? null : _discoverAlpacaServers,
              icon: const Icon(Icons.wifi_find, size: 16),
              label: const Text('Search network for Alpaca servers'),
            ),
          ],
        ),
      );

  Widget _textField({
    required String label,
    required String value,
    required ValueChanged<String> onChanged,
    String? hint,
  }) =>
      Padding(
        padding: const EdgeInsets.symmetric(vertical: 6),
        child: TextFormField(
          key: ValueKey('wiz-$label'),
          initialValue: value,
          decoration: InputDecoration(
            labelText: label,
            hintText: hint,
            isDense: true,
            border: const OutlineInputBorder(),
          ),
          onChanged: onChanged,
        ),
      );

  /// Dropdown over the daemon's choice strings. The current value is always
  /// representable even when the daemon doesn't list it (disconnected or a
  /// stale selection) — mirrored from the Guider settings panel's rule.
  Widget _choiceDropdown({
    required String label,
    required String value,
    required List<String> options,
    required ValueChanged<String> onChanged,
    Map<String, String>? labels,
    bool allowUnset = false,
  }) {
    final items = <String>[
      if (allowUnset) '',
      ...options,
      if (value.isNotEmpty && !options.contains(value)) value,
    ];
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 6),
      child: DropdownButtonFormField<String>(
        key: ValueKey('wiz-$label'),
        // Long friendly labels ("ZWO ASI290MM Mini (192.168.1.118:6800/1)")
        // must ellipsize inside the field, not overflow it.
        isExpanded: true,
        // A value the items don't carry (an unset '' on a non-allowUnset
        // slot — the unconfigured first run) renders as a REAL placeholder,
        // never as items.first: initialValue is only honored on first build,
        // so falling back to the first device would display a selection the
        // draft doesn't hold and Apply would then push '' while the dialog
        // implied a device was chosen (review r2).
        initialValue: items.contains(value) ? value : null,
        hint: const Text('Select a device…'),
        decoration: InputDecoration(
          labelText: label,
          isDense: true,
          border: const OutlineInputBorder(),
        ),
        items: [
          for (final o in items)
            DropdownMenuItem(
              value: o,
              child: Text(
                o.isEmpty ? '(use the guider\'s own setting)' : (labels?[o] ?? o),
                overflow: TextOverflow.ellipsis,
              ),
            ),
        ],
        onChanged: (v) {
          if (v != null) onChanged(v);
        },
      ),
    );
  }

  Widget _summaryRow(String label, String value) => _WizSummaryRow(
      label: label, value: value);
}

/// §63.8 live dark-library build progress — a real bar driven by the
/// `guider.dark_library.progress` WS ticks (indeterminate until the first
/// tick), then a green check on complete / the error on failed.
class _DarkBuildProgress extends StatelessWidget {
  final CalibrationBuildActivity? activity;
  const _DarkBuildProgress({required this.activity});

  @override
  Widget build(BuildContext context) {
    switch (activity?.phase) {
      case CalibrationBuildPhase.complete:
        return const Row(children: [
          Icon(Icons.check_circle, size: 16, color: AraColors.accentConnected),
          SizedBox(width: 6),
          Expanded(child: Text('Dark library built and loaded.')),
        ]);
      case CalibrationBuildPhase.failed:
        return Row(children: [
          const Icon(Icons.error_outline,
              size: 16, color: AraColors.accentError),
          const SizedBox(width: 6),
          Expanded(
              child: Text('Build failed: ${activity?.error ?? 'unknown error'}',
                  style: const TextStyle(color: AraColors.accentError))),
        ]);
      case CalibrationBuildPhase.building:
      case null: // started, first started/progress event not folded yet
        final a = activity;
        final detail = a?.exposureIndex != null && a?.exposureCount != null
            ? 'Exposure ${a!.exposureIndex}/${a.exposureCount}'
                '${a.exposureMs != null ? ' (${(a.exposureMs! / 1000).toStringAsFixed(1)} s)' : ''}'
                '${a.frame != null && a.frameCount != null ? ' — frame ${a.frame}/${a.frameCount}' : ''}'
            : 'Capturing dark frames…';
        return Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            LinearProgressIndicator(value: a?.fraction),
            const SizedBox(height: 6),
            Text(detail, style: Theme.of(context).textTheme.bodySmall),
          ],
        );
    }
  }
}

class _WizSummaryRow extends StatelessWidget {
  final String label;
  final String value;
  const _WizSummaryRow({required this.label, required this.value});

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 2),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            SizedBox(
                width: 120,
                child: Text(label,
                    style: Theme.of(context)
                        .textTheme
                        .bodySmall
                        ?.copyWith(color: AraColors.textSecondary))),
            Expanded(child: Text(value)),
          ],
        ),
      );
}
