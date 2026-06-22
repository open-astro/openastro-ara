import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/discovered_device.dart';
import '../../../models/profile_draft.dart';
import '../../../services/equipment_discovery_api.dart';
import '../../../state/saved_server_state.dart';
import '../../../state/settings/equipment_connection_state.dart';
import '../../../state/wizard_state.dart';
import '../../../theme/ara_colors.dart';
import '../wizard_form_kit.dart';

/// §37.2 Screen 2 — Connect to AlpacaBridge.
///
/// The daemon runs Alpaca UDP discovery (port 32227) on its own subnet, so
/// the address field here is an optional override/record; "Test connection"
/// probes the daemon's discovery path and reports reachability.
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

  @override
  void initState() {
    super.initState();
    _draft = ref.read(wizardControllerProvider).draft;
  }

  Future<void> _test() async {
    final servers = ref.read(savedServersProvider).maybeWhen(
          data: (list) => list,
          orElse: () => const [],
        );
    if (servers.isEmpty) {
      setState(() {
        _ok = false;
        _result = 'No active server — connect to a daemon first.';
      });
      return;
    }
    setState(() {
      _testing = true;
      _result = null;
    });
    try {
      // Probe the daemon's discovery path with a single type; a clean response
      // (even an empty list) means the AlpacaBridge path is reachable.
      final api = EquipmentDiscoveryApi(servers.last);
      final devices =
          await api.discover(EquipmentDeviceType.camera, forceRefresh: true);
      if (!mounted) return;
      setState(() {
        _ok = true;
        _result = 'AlpacaBridge reachable via the daemon — '
            '${devices.length} camera(s) seen on this scan.';
      });
    } on DioException catch (e) {
      if (!mounted) return;
      setState(() {
        _ok = false;
        _result =
            'Could not reach the AlpacaBridge: ${e.message ?? 'network error'} '
            '(${e.response?.statusCode ?? 'no response'}).';
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _ok = false;
        _result = 'Could not reach the AlpacaBridge: $e';
      });
    } finally {
      if (mounted) setState(() => _testing = false);
    }
  }

  @override
  Widget build(BuildContext context) {
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
          onChanged: (v) =>
              _draft.alpacaBridgeAddress = v.trim().isEmpty ? null : v.trim(),
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
            label: Text(_testing ? 'Testing…' : 'Test connection'),
          ),
        ),
        if (_result != null) ...[
          const SizedBox(height: 16),
          Container(
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
            decoration: BoxDecoration(
              color: (_ok ? AraColors.accentConnected : AraColors.accentError)
                  .withValues(alpha: 0.15),
              borderRadius: BorderRadius.circular(4),
              border: Border.all(
                  color: _ok ? AraColors.accentConnected : AraColors.accentError),
            ),
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Icon(_ok ? Icons.check_circle : Icons.error_outline,
                    size: 18,
                    color:
                        _ok ? AraColors.accentConnected : AraColors.accentError),
                const SizedBox(width: 8),
                Expanded(
                  child: Text(_result!,
                      style: Theme.of(context).textTheme.bodySmall),
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
  // Switch discovers like any other Alpaca device (§6.4 multi-switch is wired).
  // The wizard assigns one switch as the profile's default; additional switches
  // are added at runtime from Settings → Switch.
  _Slot('Switch', EquipmentDeviceType.switchDevice, _gSwitch, _sSwitch),
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
String? _gSwitch(ProfileDraft d) => d.equipment.switchDeviceId;
void _sSwitch(ProfileDraft d, String? v) => d.equipment.switchDeviceId = v;

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

  @override
  void initState() {
    super.initState();
    _draft = ref.read(wizardControllerProvider).draft;
  }

  Future<void> _choose(_Slot slot) async {
    final type = slot.type;
    final servers = ref.read(savedServersProvider).maybeWhen(
          data: (list) => list,
          orElse: () => const [],
        );
    final picked = await showModalBottomSheet<_Choice>(
      context: context,
      backgroundColor: AraColors.bgPanel,
      isScrollControlled: true,
      builder: (_) => _DiscoverySheet(
        slotLabel: slot.label,
        type: type,
        api: servers.isEmpty ? null : EquipmentDiscoveryApi(servers.last),
      ),
    );
    if (picked == null) return; // dismissed without choosing
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
      ],
    );
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
                      style: Theme.of(context).textTheme.bodyMedium),
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
