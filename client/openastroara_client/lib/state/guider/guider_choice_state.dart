import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/guider_equipment_choices.dart';
import '../../services/alpaca_device_names_api.dart';
import '../../util/host_port.dart';
import '../saved_server_state.dart';
import '../settings/phd2_settings_state.dart';
import 'guider_equipment_state.dart';

// ADR 0013 rule 4 (#1698): the Guider's equipment choice is one module for the
// setup wizard, Settings › Equipment › Guider and first-run setup: discovery,
// friendly names, UniqueIDs, and the rules a pick follows.

/// Builds the Alpaca management-API reader. Overridable in tests.
final alpacaDeviceNamesApiProvider = Provider<AlpacaDeviceNamesClient>((ref) {
  // Bound to the active daemon (#1067): the daemon reads the Alpaca host's
  // management API, the client never dials equipment itself.
  final server = ref.watch(activeServerProvider);
  if (server == null) return const NoServerAlpacaDeviceNames();
  final api = AlpacaDeviceNamesApi(server);
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

/// Shown wherever a profile has no guide camera: a freshly installed guider
/// has none of its own, so leaving the slot blank is not a working default.
const String kNoGuideCameraWarning =
    'No guide camera picked. A new guider has no camera of its own, so '
    'guiding, guide-camera focus and polar alignment fail until you pick one. '
    'Pick "None" if this rig has no guide camera.';

/// §63.20 / #1306 — mirror of the daemon's `DescribeUnservedAlpacaSelection`:
/// an "Alpaca …" choice is only pushable when a server is known, from the
/// guide camera's `[host:port/N]` or the stored Alpaca host AND port;
/// otherwise the guider stays on localhost:0 and its equipment never
/// connects. Covers the bare generic choice and a suffixed mount/rotator
/// beside a non-Alpaca camera. Returns the reason naming the slot, or null
/// when pushable. Pure — unit-tested; keep in step with the C# rule.
String? describeUnservedAlpacaSelection(Phd2Settings s) {
  final cameraEp = parseAlpacaChoiceEndpoint(s.guiderCamera);
  final hasHost = cameraEp != null || s.guiderAlpacaHost.trim().isNotEmpty;
  final hasPort = cameraEp != null || s.guiderAlpacaPort > 0;
  if (hasHost && hasPort) return null;
  final slots = <(String, String)>[
    ('guide camera', s.guiderCamera),
    ('guide mount', s.guiderMount),
    ('aux mount', s.guiderAuxMount),
    ('rotator', s.guiderRotator),
  ];
  final missing = !hasHost && !hasPort
      ? 'host and port'
      : !hasHost
          ? 'host'
          : 'port';
  for (final (slot, selection) in slots) {
    if (!isAlpacaChoice(selection)) continue;
    final name = selection.trim();
    final ep = parseAlpacaChoiceEndpoint(name);
    if (ep != null) {
      final verb = missing.contains(' ') ? 'are' : 'is';
      return "The $slot '$name' is on Alpaca server ${ep.host}:${ep.port} but "
          "the guider's Alpaca $missing $verb unset: pick the guide camera on "
          'that server or set the Alpaca host and port to it.';
    }
    return "The $slot '$name' names an Alpaca driver but no server: pick a "
        "device such as '$name [host:port/N]' from the list (Discover Alpaca "
        "finds them) or set the guider's Alpaca $missing.";
  }
  return null;
}

/// #1308 — the Alpaca UniqueID behind a camera choice, from the resolved
/// `"host:port|camera/N"` ids; "" for a non-Alpaca choice or an unknown id.
String alpacaCameraUniqueIdFor(String choice, Map<String, String> uniqueIds) {
  final e = parseAlpacaChoiceEndpoint(choice);
  if (e == null) return '';
  return uniqueIds['${e.host}:${e.port}|camera/${e.device}'] ?? '';
}

/// True for the daemon's "Alpaca …" choice strings, bare or suffixed.
bool isAlpacaChoice(String selection) =>
    selection.trim().toLowerCase().startsWith('alpaca ');

/// True for the daemon's generic "Alpaca …" choice string with no endpoint.
bool isBareAlpacaChoice(String selection) =>
    isAlpacaChoice(selection) && parseAlpacaChoiceEndpoint(selection.trim()) == null;

/// Parsed Alpaca endpoint from a daemon camera-choice string, e.g.
/// `"Alpaca Camera [rc91.lan:6800/1]"` → host `rc91.lan`, port `6800`,
/// device `1`. Null when the choice doesn't carry the `[host:port/N]` suffix
/// (non-Alpaca drivers, `"None"`). Pure — unit-tested.
({String host, int port, int device})? parseAlpacaChoiceEndpoint(String choice) {
  final m = RegExp(r'\[([^\[\]:]+):(\d+)/(\d+)\]\s*$').firstMatch(choice);
  if (m == null) return null;
  final port = int.tryParse(m.group(2)!);
  final device = int.tryParse(m.group(3)!);
  if (port == null || port < 1 || port > 65535 || device == null) return null;
  return (host: m.group(1)!, port: port, device: device);
}

/// True when the guide-camera [guideChoice] is the same Alpaca device as one of
/// the Main Camera's [mainEndpoints] ("host:port/N"): same port, device number
/// and host, with loopback spellings equal. Mirrors the daemon's
/// `CameraConnectGuard.IsGuiderCamera`, minus "this machine's other addresses"
/// (shortcut: the client can't list the rig's addresses; the daemon still
/// refuses those at connect, and the finish note then says why).
bool guideCameraIsMainCamera(String? guideChoice, List<String> mainEndpoints) {
  final guide = parseAlpacaChoiceEndpoint(guideChoice ?? '');
  if (guide == null) return false;
  String norm(String h) {
    final t = h.trim().toLowerCase().replaceAll(RegExp(r'\.$'), '');
    return t == '127.0.0.1' || t == '::1' ? 'localhost' : t;
  }
  for (final e in mainEndpoints) {
    final main = parseAlpacaChoiceEndpoint('[$e]');
    if (main != null &&
        main.port == guide.port &&
        main.device == guide.device &&
        norm(main.host) == norm(guide.host)) {
      return true;
    }
  }
  return false;
}

/// True when [selection] names a device on a different Alpaca server than
/// [serving] ("host:port", the guide camera's): the push would drop its
/// device number (the guider has one Alpaca server, derived from the camera).
bool isCrossServerPick(String? serving, String selection) {
  final e = parseAlpacaChoiceEndpoint(selection);
  return serving != null && e != null && '${e.host}:${e.port}' != serving;
}

/// What the Alpaca servers the guider can reach call their devices, keyed
/// "host:port|type/N": the real names over the daemon's generic labels, and
/// the UniqueIDs a camera pick is pinned by (#1308).
typedef GuiderAlpacaDevices = ({
  Map<String, String> names,
  Map<String, String> uniqueIds,
});

final guiderAlpacaDevicesProvider =
    NotifierProvider<GuiderAlpacaDevicesNotifier, GuiderAlpacaDevices>(
      GuiderAlpacaDevicesNotifier.new,
    );

class GuiderAlpacaDevicesNotifier extends Notifier<GuiderAlpacaDevices> {
  final Set<String> _named = {};
  // Two triggers landing together must not fetch the same server twice.
  final Set<String> _resolving = {};

  @override
  GuiderAlpacaDevices build() => (names: const {}, uniqueIds: const {});

  /// The UniqueID behind a camera [choice]; "" when unknown or not Alpaca.
  String uniqueIdFor(String choice) =>
      alpacaCameraUniqueIdFor(choice, state.uniqueIds);

  /// Read the device names on each "host:port" not yet named (in parallel: an
  /// unreachable server costs only its own timeout). A server that answered
  /// nothing is asked again next time, so a brief outage does not leave the
  /// labels generic.
  Future<void> resolve(Iterable<String> endpoints) async {
    final namesApi = ref.read(alpacaDeviceNamesApiProvider);
    final targets = <({String endpoint, String host, int port})>[];
    for (final endpoint in endpoints.toSet()) {
      if (_named.contains(endpoint) || _resolving.contains(endpoint)) continue;
      final parsed = parseHostPort(endpoint);
      if (parsed.host == null || parsed.port == null) continue;
      targets.add((endpoint: endpoint, host: parsed.host!, port: parsed.port!));
    }
    if (targets.isEmpty) return;
    _resolving.addAll(targets.map((t) => t.endpoint));
    final List<({String endpoint, Map<String, AlpacaDeviceInfo> devices})>
    fetched;
    try {
      fetched = await Future.wait([
        for (final t in targets)
          namesApi
              .fetchDevices(t.host, t.port)
              .then((devices) => (endpoint: t.endpoint, devices: devices)),
      ]);
    } finally {
      _resolving.removeAll(targets.map((t) => t.endpoint));
    }
    if (!ref.mounted) return;
    final names = {...state.names};
    final uniqueIds = {...state.uniqueIds};
    for (final result in fetched) {
      if (result.devices.isEmpty) continue;
      _named.add(result.endpoint);
      result.devices.forEach((typeSlot, device) {
        names['${result.endpoint}|$typeSlot'] = device.name;
        if (device.uniqueId.isNotEmpty) {
          uniqueIds['${result.endpoint}|$typeSlot'] = device.uniqueId;
        }
      });
    }
    state = (names: names, uniqueIds: uniqueIds);
  }

  /// Sweep the network for Alpaca servers (daemon-side UDP discovery) and name
  /// their devices; returns the servers found ("host:port").
  Future<List<String>> discover() async {
    final servers = await ref
        .read(guiderEquipmentProvider.notifier)
        .discoverAlpaca();
    await resolve(servers);
    return servers;
  }
}

/// "host:port" of every server the daemon's choice strings name.
Iterable<String> choiceEndpoints(GuiderEquipmentChoices? choices) sync* {
  if (choices == null) return;
  for (final choice in [
    ...choices.cameras,
    ...choices.mounts,
    ...choices.auxMounts,
    ...choices.rotators,
  ]) {
    final e = parseAlpacaChoiceEndpoint(choice);
    if (e != null) yield '${e.host}:${e.port}';
  }
}
