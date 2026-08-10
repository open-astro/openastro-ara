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
        _result = 'Connect to your rig to look for equipment.';
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
        // §68.2 — the handshake gate is REACHABILITY, not gear presence: a
        // clean discovery response means the bridge is up. Advertised devices
        // are NOT verified connected — a registered-but-absent slot (vendor
        // SDK name with no hardware behind it) must not read as a connected
        // camera — so the copy reports the count as "advertised" only.
        _result = devices.isEmpty
            ? 'AlpacaBridge found — reachable (no devices advertised).'
            : 'AlpacaBridge found — reachable; '
                  '${devices.length} device(s) advertised, '
                  'connectivity not verified.';
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
          'address blank to let Ara auto-discover devices over UDP.',
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
                  'it on Ara host, then retry:',
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

/// Result of [DiscoverySheet]: a chosen device, or an explicit "— None".
/// Shared by the §76 "Your equipment" screen's Choose/Change affordances.
class DeviceChoice {
  final DiscoveredDevice? device; // null = "— None"
  const DeviceChoice(this.device);
}

class DiscoverySheet extends StatefulWidget {
  final String slotLabel;
  final EquipmentDeviceType type;
  final EquipmentDiscoveryApi? api;

  const DiscoverySheet({
    super.key,
    required this.slotLabel,
    required this.type,
    required this.api,
  });

  @override
  State<DiscoverySheet> createState() => _DiscoverySheetState();
}

class _DiscoverySheetState extends State<DiscoverySheet> {
  late Future<List<DiscoveredDevice>> _future;

  @override
  void initState() {
    super.initState();
    _future = _run();
  }

  Future<List<DiscoveredDevice>> _run() {
    final api = widget.api;
    if (api == null) {
      return Future.error('Connect to your rig to look for equipment.');
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
              onTap: () => Navigator.of(context).pop(const DeviceChoice(null)),
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
                          'Ara\'s subnet, then re-scan.',
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
                          '$scheme://$host:${d.ipPort} ·  '
                          'device #${d.alpacaDeviceNumber}\n'
                          'Advertised — connectivity is verified when you connect',
                          style: Theme.of(context).textTheme.bodySmall?.copyWith(
                                color: AraColors.textSecondary,
                              ),
                        ),
                        trailing: const Icon(Icons.chevron_right),
                        onTap: () => Navigator.of(context).pop(DeviceChoice(d)),
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
