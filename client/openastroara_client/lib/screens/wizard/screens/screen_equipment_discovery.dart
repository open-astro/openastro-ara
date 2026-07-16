import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/discovered_device.dart';
import '../../../models/profile_draft.dart';
import '../../../models/server.dart';
import '../../../services/equipment_discovery_api.dart';
import '../../../state/saved_server_state.dart';
import '../../../state/settings/equipment_connection_state.dart';
import '../../../state/wizard_state.dart';
import '../../../theme/ara_colors.dart';
import '../wizard_form_kit.dart';

/// Injectable factory for the daemon discovery API — tests swap a fake so the
/// §68.2 Next-gate can be exercised without a live daemon.
final equipmentDiscoveryApiFactoryProvider =
    Provider<EquipmentDiscoveryApi Function(AraServer)>(
        (_) => EquipmentDiscoveryApi.new);

/// §37.2 Screen 2 — Connect to AlpacaBridge.
///
/// The daemon runs Alpaca UDP discovery (port 32227) on its own subnet, so
/// the address field here is an optional override/record; the probe checks
/// the daemon's discovery path and reports reachability.
///
/// §68.2 — the probe runs automatically on entry, and **Next is gated on a
/// successful handshake**: a clean discovery response (even an empty device
/// list — the bridge being up matters, connected gear doesn't). When the
/// bridge isn't reachable the screen shows the install command prominently
/// with [Retry detection]; the only way past without a handshake is the
/// explicit non-standard-bridge skip, which requires an address override.
/// (Post-§68.1-removal, "handshake" means reachability — Alpaca has no
/// version endpoint by design.)
class ScreenAlpacaConnect extends ConsumerStatefulWidget {
  const ScreenAlpacaConnect({super.key});

  @override
  ConsumerState<ScreenAlpacaConnect> createState() =>
      _ScreenAlpacaConnectState();
}

class _ScreenAlpacaConnectState extends ConsumerState<ScreenAlpacaConnect> {
  late final ProfileDraft _draft;
  String? _result;
  bool _ok = false;
  bool _testing = false;
  bool _skipped = false;
  // True when the last probe couldn't even start (no daemon connection) — a
  // different problem than "bridge unreachable", so it gets a plain banner,
  // not the install-command panel (installing a bridge wouldn't help).
  bool _noServer = false;


  @override
  void initState() {
    super.initState();
    _draft = ref.read(wizardControllerProvider).draft;
    // Gate Next until the handshake (or the skip) succeeds, and auto-run the
    // probe so the happy path unblocks with zero clicks. Both touch providers,
    // so they run post-frame — a provider can't be modified mid-build. Accepted
    // consequence: the very first frame renders with Next enabled (the shell's
    // navigation reset marks steps valid synchronously); a human can't click
    // within that frame, and the probe re-gates immediately after it.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      ref.read(wizardStepValidProvider.notifier).setValid(false);
      _test();
    });
  }

  void _setValid(bool valid) =>
      ref.read(wizardStepValidProvider.notifier).setValid(valid);

  Future<void> _test() async {
    final server = ref.read(activeServerProvider);
    if (server == null) {
      setState(() {
        _ok = false;
        _noServer = true;
        _result = 'No active server — connect to a daemon first.';
      });
      // Same re-gate as the failure paths below (a retry can land here after
      // an earlier success if the active server was removed meanwhile).
      _setValid(_skipped);
      return;
    }
    setState(() {
      _testing = true;
      _noServer = false;
      _result = null;
    });
    // One-shot client per probe — close it (each instance owns its own Dio;
    // the auto-run-on-entry + retry cadence would otherwise stack leaked pools).
    final api = ref.read(equipmentDiscoveryApiFactoryProvider)(server);
    try {
      // Probe the daemon's discovery path with a single type; a clean response
      // (even an empty list) means the AlpacaBridge path is reachable.
      final devices =
          await api.discover(EquipmentDeviceType.camera, forceRefresh: true);
      if (!mounted) return;
      setState(() {
        _ok = true;
        _result = 'AlpacaBridge reachable via the daemon — '
            '${devices.length} camera(s) seen on this scan.';
      });
      _setValid(true); // §68.2 — handshake succeeded, Next unblocks
    } on DioException catch (e) {
      if (!mounted) return;
      setState(() {
        _ok = false;
        _result =
            '${e.message ?? 'network error'} '
            '(${e.response?.statusCode ?? 'no response'})';
      });
      // Re-gate on EVERY failure, not just the initial one: a retry that fails
      // after an earlier success (bridge power-cycled) must re-lock Next — a
      // still-granted non-standard-bridge skip stands on its own.
      _setValid(_skipped);
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _ok = false;
        _result = '$e';
      });
      _setValid(_skipped);
    } finally {
      api.close();
      if (mounted) setState(() => _testing = false);
    }
  }

  // §68.2 — the only way past Screen 2 without a handshake: an explicit skip
  // for a non-standard bridge install, which requires the address override to
  // be filled in (there's nothing to skip TO otherwise).
  void _skip() {
    setState(() => _skipped = true);
    _setValid(true);
  }

  bool get _hasAddressOverride =>
      (_draft.alpacaBridgeAddress ?? '').trim().isNotEmpty;

  @override
  Widget build(BuildContext context) {
    final failed = _result != null && !_ok && !_testing && !_noServer;
    final noServer = _result != null && !_testing && _noServer;
    return WizardScreenScaffold(
      step: 2,
      intro: 'ARA speaks ASCOM Alpaca only. INDI/INDIGO users connect through '
          'a bridge (AlpacaPi, INDIGO Sky\'s -A Alpaca server). Leave the '
          'address blank to let the daemon auto-discover devices over UDP.',
      children: [
        WizardTextField(
          label: 'AlpacaBridge address',
          initialValue: _draft.alpacaBridgeAddress,
          hint: 'auto-discover (UDP 32227) — or host:port to override',
          // setState so the skip button's enablement tracks the override text.
          onChanged: (v) => setState(() {
            _draft.alpacaBridgeAddress = v.trim().isEmpty ? null : v.trim();
            // A granted skip is contingent on the override it skipped TO —
            // clearing the address revokes it and re-gates Next (unless the
            // handshake itself has succeeded, which stands on its own).
            if (_skipped && !_hasAddressOverride) {
              _skipped = false;
              _setValid(_ok);
            }
          }),
        ),
        Align(
          alignment: Alignment.centerLeft,
          child: FilledButton.tonalIcon(
            onPressed: _testing ? null : _test,
            icon: _testing
                ? const SizedBox(
                    width: 16,
                    height: 16,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.wifi_tethering, size: 18),
            label: Text(_testing ? 'Detecting…' : 'Retry detection'),
          ),
        ),
        if (_ok && _result != null) ...[
          const SizedBox(height: 16),
          Container(
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
            decoration: BoxDecoration(
              color: AraColors.accentConnected.withValues(alpha: 0.15),
              borderRadius: BorderRadius.circular(4),
              border: Border.all(color: AraColors.accentConnected),
            ),
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                const Icon(Icons.check_circle,
                    size: 18, color: AraColors.accentConnected),
                const SizedBox(width: 8),
                Expanded(
                  child: Text(_result!,
                      style: Theme.of(context).textTheme.bodySmall),
                ),
              ],
            ),
          ),
        ],
        if (noServer) ...[
          const SizedBox(height: 16),
          // Not a bridge problem — no daemon connection at all, so the install
          // command would be a red herring. Plain banner, no skip.
          Container(
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
            decoration: BoxDecoration(
              color: AraColors.accentError.withValues(alpha: 0.12),
              borderRadius: BorderRadius.circular(4),
              border: Border.all(color: AraColors.accentError),
            ),
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                const Icon(Icons.error_outline,
                    size: 18, color: AraColors.accentError),
                const SizedBox(width: 8),
                Expanded(
                  child: Text(_result!,
                      style: Theme.of(context).textTheme.bodySmall),
                ),
              ],
            ),
          ),
        ],
        if (failed) ...[
          const SizedBox(height: 16),
          // §68.2 — the prominent missing-bridge panel (playbook wording).
          Container(
            padding: const EdgeInsets.all(14),
            decoration: BoxDecoration(
              color: AraColors.accentError.withValues(alpha: 0.12),
              borderRadius: BorderRadius.circular(4),
              border: Border.all(color: AraColors.accentError),
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    const Icon(Icons.error_outline,
                        size: 18, color: AraColors.accentError),
                    const SizedBox(width: 8),
                    Text('AlpacaBridge not detected.',
                        style: Theme.of(context)
                            .textTheme
                            .titleSmall
                            ?.copyWith(fontWeight: FontWeight.w600)),
                  ],
                ),
                const SizedBox(height: 8),
                Text(
                  'AlpacaBridge is ARA\'s equipment hub. It should have been '
                  'installed alongside ARA Core via apt. If it wasn\'t, install '
                  'it on the daemon host, then retry:',
                  style: Theme.of(context).textTheme.bodySmall,
                ),
                const SizedBox(height: 8),
                Container(
                  width: double.infinity,
                  padding:
                      const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
                  decoration: BoxDecoration(
                    color: AraColors.bgPrimary,
                    borderRadius: BorderRadius.circular(4),
                  ),
                  child: Text('sudo apt install alpaca-bridge',
                      style: Theme.of(context)
                          .textTheme
                          .bodySmall
                          ?.copyWith(fontFamily: 'monospace')),
                ),
                const SizedBox(height: 6),
                Text('Details: $_result',
                    style: Theme.of(context)
                        .textTheme
                        .bodySmall
                        ?.copyWith(color: AraColors.textSecondary)),
                const SizedBox(height: 8),
                // Non-standard install escape hatch: needs the address override
                // filled in — there's nothing to skip TO otherwise.
                Align(
                  alignment: Alignment.centerLeft,
                  child: Tooltip(
                    message: _hasAddressOverride
                        ? 'Continue with the address above; detection is skipped.'
                        : 'Enter your bridge\'s host:port above to enable.',
                    child: TextButton(
                      onPressed:
                          _hasAddressOverride && !_skipped ? _skip : null,
                      child: Text(_skipped
                          ? 'Continuing with the address override.'
                          : 'Skip — I\'m using a non-standard bridge address'),
                    ),
                  ),
                ),
              ],
            ),
          ),
        ],
      ],
    );
  }
}

/// A single assignable equipment slot on Screen 3 — binds a discovery [type] to
/// a draft getter/setter.
class _Slot {
  final String label;
  final EquipmentDeviceType type;
  final String? Function(ProfileDraft) get;
  final void Function(ProfileDraft, String?) set;
  const _Slot(this.label, this.type, this.get, this.set);
}

const List<_Slot> _slots = <_Slot>[
  _Slot('Camera', EquipmentDeviceType.camera, _gCamera, _sCamera),
  _Slot('Filter Wheel', EquipmentDeviceType.filterWheel, _gFw, _sFw),
  _Slot('Focuser', EquipmentDeviceType.focuser, _gFoc, _sFoc),
  _Slot('Mount (Telescope)', EquipmentDeviceType.mount, _gMount, _sMount),
  _Slot('Rotator', EquipmentDeviceType.rotator, _gRot, _sRot),
  _Slot('Dome', EquipmentDeviceType.dome, _gDome, _sDome),
  _Slot('Observing Conditions', EquipmentDeviceType.weather, _gOc, _sOc),
  _Slot('Safety Monitor', EquipmentDeviceType.safetyMonitor, _gSafe, _sSafe),
  _Slot('Flat Panel', EquipmentDeviceType.flatPanel, _gFlat, _sFlat),
  _Slot('Guider (PHD2)', EquipmentDeviceType.guider, _gGuider, _sGuider),
  // Switch is NOT a slot: a rig can carry several switch hubs (§6.4
  // multi-switch), so it gets its own add-as-many-as-you-like section below.
];

String? _gCamera(ProfileDraft d) => d.equipment.cameraDeviceId;
void _sCamera(ProfileDraft d, String? v) => d.equipment.cameraDeviceId = v;
String? _gFw(ProfileDraft d) => d.equipment.filterWheelDeviceId;
void _sFw(ProfileDraft d, String? v) => d.equipment.filterWheelDeviceId = v;
String? _gFoc(ProfileDraft d) => d.equipment.focuserDeviceId;
void _sFoc(ProfileDraft d, String? v) => d.equipment.focuserDeviceId = v;
String? _gMount(ProfileDraft d) => d.equipment.mountDeviceId;
void _sMount(ProfileDraft d, String? v) => d.equipment.mountDeviceId = v;
String? _gRot(ProfileDraft d) => d.equipment.rotatorDeviceId;
void _sRot(ProfileDraft d, String? v) => d.equipment.rotatorDeviceId = v;
String? _gDome(ProfileDraft d) => d.equipment.domeDeviceId;
void _sDome(ProfileDraft d, String? v) => d.equipment.domeDeviceId = v;
String? _gOc(ProfileDraft d) => d.equipment.observingConditionsDeviceId;
void _sOc(ProfileDraft d, String? v) =>
    d.equipment.observingConditionsDeviceId = v;
String? _gSafe(ProfileDraft d) => d.equipment.safetyMonitorDeviceId;
void _sSafe(ProfileDraft d, String? v) => d.equipment.safetyMonitorDeviceId = v;
String? _gFlat(ProfileDraft d) => d.equipment.flatPanelDeviceId;
void _sFlat(ProfileDraft d, String? v) => d.equipment.flatPanelDeviceId = v;
String? _gGuider(ProfileDraft d) => d.equipment.guiderDeviceId;
void _sGuider(ProfileDraft d, String? v) => d.equipment.guiderDeviceId = v;

/// §37.2 Screen 3 — Discover + assign equipment.
class ScreenEquipmentAssign extends ConsumerStatefulWidget {
  const ScreenEquipmentAssign({super.key});

  @override
  ConsumerState<ScreenEquipmentAssign> createState() =>
      _ScreenEquipmentAssignState();
}

class _ScreenEquipmentAssignState extends ConsumerState<ScreenEquipmentAssign> {
  late final ProfileDraft _draft;

  // Friendly names for already-assigned devices, remembered for this wizard
  // session (the draft only persists the device id).
  final Map<EquipmentDeviceType, String> _assignedNames = {};

  // id → name for the multi-assign switch section (several per rig).
  final Map<String, String> _switchNames = {};

  @override
  void initState() {
    super.initState();
    _draft = ref.read(wizardControllerProvider).draft;
  }

  Future<void> _choose(_Slot slot) async {
    final type = slot.type;
    final server = ref.read(activeServerProvider);
    // One-shot client for the sheet's scans — closed when the sheet is done
    // (same leak avoidance as the Screen-2 probe).
    final api = server == null ? null : EquipmentDiscoveryApi(server);
    final picked = await showModalBottomSheet<_Choice>(
      context: context,
      backgroundColor: AraColors.bgPanel,
      isScrollControlled: true,
      builder: (_) => _DiscoverySheet(
        slotLabel: slot.label,
        type: type,
        api: api,
      ),
    );
    api?.close();
    if (picked == null) return; // dismissed without choosing
    if (!mounted) return;
    setState(() {
      slot.set(_draft, picked.device?.uniqueId);
      if (picked.device != null) {
        _assignedNames[type] = picked.device!.name;
      } else {
        _assignedNames.remove(type);
      }
    });
  }

  @override
  Widget build(BuildContext context) {
    return WizardScreenScaffold(
      step: 3,
      intro: 'Scan for Alpaca devices and assign each slot. Leave a slot as '
          '"— None" if you don\'t use it. The guider connects to PHD2\'s '
          'JSON-RPC, not Alpaca.',
      children: [
        for (final slot in _slots) _slotRow(context, slot),
        _switchSection(context),
      ],
    );
  }

  /// Switches are add-as-many-as-you-use (§6.4 multi-switch): power boxes,
  /// dew controllers, relay boards can all coexist on one rig.
  Widget _switchSection(BuildContext context) {
    final ids = _draft.equipment.switchDeviceIds;
    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
        decoration: BoxDecoration(
          color: AraColors.bgPanel,
          borderRadius: BorderRadius.circular(4),
          border: Border.all(color: AraColors.border),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text('Switches',
                        style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                              color: AraColors.textPrimary,
                            )),
                    const SizedBox(height: 2),
                    Text(
                      ids.isEmpty
                          ? '— None (add every switch hub your rig uses)'
                          : '${ids.length} assigned',
                      style: Theme.of(context).textTheme.bodySmall?.copyWith(
                            color: ids.isNotEmpty
                                ? AraColors.accentConnected
                                : AraColors.textSecondary,
                          ),
                    ),
                  ],
                ),
              ),
              TextButton(
                onPressed: () => unawaited(_addSwitch()),
                child: const Text('Add switch'),
              ),
            ]),
            if (ids.isNotEmpty) ...[
              const SizedBox(height: 8),
              Wrap(
                spacing: 8,
                runSpacing: 8,
                children: [
                  for (final id in ids)
                    InputChip(
                      label: Text(_switchNames[id] ?? id),
                      onDeleted: () => setState(() {
                        ids.remove(id);
                        _switchNames.remove(id);
                      }),
                    ),
                ],
              ),
            ],
          ],
        ),
      ),
    );
  }

  Future<void> _addSwitch() async {
    final server = ref.read(activeServerProvider);
    final api = server == null ? null : EquipmentDiscoveryApi(server);
    final picked = await showModalBottomSheet<_Choice>(
      context: context,
      backgroundColor: AraColors.bgPanel,
      isScrollControlled: true,
      builder: (_) => _DiscoverySheet(
        slotLabel: 'Switch',
        type: EquipmentDeviceType.switchDevice,
        api: api,
      ),
    );
    api?.close();
    if (picked?.device == null || !mounted) return;
    final device = picked!.device!;
    setState(() {
      if (!_draft.equipment.switchDeviceIds.contains(device.uniqueId)) {
        _draft.equipment.switchDeviceIds.add(device.uniqueId);
      }
      _switchNames[device.uniqueId] = device.name;
    });
  }

  Widget _slotRow(BuildContext context, _Slot slot) {
    final assignedId = slot.get(_draft);
    final assignedLabel = assignedId == null
        ? '— None'
        : (_assignedNames[slot.type] ?? assignedId);

    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
        decoration: BoxDecoration(
          color: AraColors.bgPanel,
          borderRadius: BorderRadius.circular(4),
          border: Border.all(color: AraColors.border),
        ),
        child: Row(
          children: [
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(slot.label,
                      style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                            color: AraColors.textPrimary,
                          )),
                  const SizedBox(height: 2),
                  Text(
                    assignedLabel,
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(
                          color: assignedId != null
                              ? AraColors.accentConnected
                              : AraColors.textSecondary,
                        ),
                  ),
                ],
              ),
            ),
            TextButton(
              onPressed: () => _choose(slot),
              child: Text(assignedId == null ? 'Choose' : 'Change'),
            ),
          ],
        ),
      ),
    );
  }
}

/// Result of the discovery sheet: a chosen device, or an explicit "— None".
class _Choice {
  final DiscoveredDevice? device; // null = "— None"
  const _Choice(this.device);
}

class _DiscoverySheet extends StatefulWidget {
  final String slotLabel;
  final EquipmentDeviceType type;
  final EquipmentDiscoveryApi? api;

  const _DiscoverySheet({
    required this.slotLabel,
    required this.type,
    required this.api,
  });

  @override
  State<_DiscoverySheet> createState() => _DiscoverySheetState();
}

class _DiscoverySheetState extends State<_DiscoverySheet> {
  late Future<List<DiscoveredDevice>> _future;

  @override
  void initState() {
    super.initState();
    _future = _run();
  }

  Future<List<DiscoveredDevice>> _run() {
    final api = widget.api;
    if (api == null) {
      return Future.error('No active server — connect to a daemon first.');
    }
    return api.discover(widget.type, forceRefresh: true);
  }

  void _rescan() => setState(() => _future = _run());

  // Height for the discovered-device list: cap at 300 but shrink on short
  // screens (e.g. a phone in landscape) so the sheet never overflows. Returns
  // a double directly — no clamp()/toDouble() round-trip.
  static const double _minListHeight = 180;
  static const double _maxListHeight = 300;

  double _listHeight(BuildContext context) {
    final desired = MediaQuery.of(context).size.height * 0.45;
    if (desired < _minListHeight) return _minListHeight;
    if (desired > _maxListHeight) return _maxListHeight;
    return desired;
  }

  @override
  Widget build(BuildContext context) {
    return SafeArea(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Text('Choose ${widget.slotLabel}',
                    style: Theme.of(context).textTheme.titleMedium),
                const Spacer(),
                IconButton(
                  tooltip: 'Re-scan',
                  onPressed: _rescan,
                  icon: const Icon(Icons.refresh),
                ),
              ],
            ),
            const SizedBox(height: 8),
            ListTile(
              leading: const Icon(Icons.block, color: AraColors.textSecondary),
              title: const Text('— None'),
              subtitle: const Text('Don\'t use this device type'),
              onTap: () => Navigator.of(context).pop(const _Choice(null)),
            ),
            const Divider(height: 1, color: AraColors.border),
            SizedBox(
              height: _listHeight(context),
              child: FutureBuilder<List<DiscoveredDevice>>(
                future: _future,
                builder: (context, snap) {
                  if (snap.connectionState == ConnectionState.waiting) {
                    return const Center(child: CircularProgressIndicator());
                  }
                  if (snap.hasError) {
                    return _SheetMessage(
                      icon: Icons.error_outline,
                      color: AraColors.accentError,
                      title: 'Discovery failed',
                      detail: _describe(snap.error),
                      onRetry: _rescan,
                    );
                  }
                  final devices = snap.data ?? const <DiscoveredDevice>[];
                  if (devices.isEmpty) {
                    return _SheetMessage(
                      icon: Icons.search_off,
                      color: AraColors.textDisabled,
                      title: 'No devices found',
                      detail: 'Make sure the driver is running and reachable on '
                          'the daemon\'s subnet, then re-scan.',
                      onRetry: _rescan,
                    );
                  }
                  return ListView.separated(
                    itemCount: devices.length,
                    separatorBuilder: (_, _) =>
                        const Divider(height: 1, color: AraColors.border),
                    itemBuilder: (_, i) {
                      final d = devices[i];
                      final scheme = d.useHttps ? 'https' : 'http';
                      final host =
                          d.hostName.isNotEmpty ? d.hostName : d.ipAddress;
                      return ListTile(
                        title: Text(d.name),
                        subtitle: Text(
                          '$scheme://$host:${d.ipPort}  ·  device #${d.alpacaDeviceNumber}',
                          style: Theme.of(context).textTheme.bodySmall?.copyWith(
                                color: AraColors.textSecondary,
                              ),
                        ),
                        trailing: const Icon(Icons.chevron_right),
                        onTap: () => Navigator.of(context).pop(_Choice(d)),
                      );
                    },
                  );
                },
              ),
            ),
          ],
        ),
      ),
    );
  }

  String _describe(Object? error) => switch (error) {
        DioException e =>
          '${e.message ?? 'Network error'} (${e.response?.statusCode ?? 'no response'})',
        Object e => e.toString(),
        _ => 'Unknown error',
      };
}

class _SheetMessage extends StatelessWidget {
  final IconData icon;
  final Color color;
  final String title;
  final String detail;
  final VoidCallback onRetry;

  const _SheetMessage({
    required this.icon,
    required this.color,
    required this.title,
    required this.detail,
    required this.onRetry,
  });

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          Icon(icon, size: 44, color: color),
          const SizedBox(height: 12),
          Text(title, style: Theme.of(context).textTheme.titleMedium),
          const SizedBox(height: 4),
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 24),
            child: Text(detail,
                textAlign: TextAlign.center,
                style: Theme.of(context)
                    .textTheme
                    .bodySmall
                    ?.copyWith(color: AraColors.textSecondary)),
          ),
          const SizedBox(height: 12),
          TextButton.icon(
            onPressed: onRetry,
            icon: const Icon(Icons.refresh, size: 16),
            label: const Text('Re-scan'),
          ),
        ],
      ),
    );
  }
}
