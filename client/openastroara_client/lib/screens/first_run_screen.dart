import 'dart:async';

import 'package:flutter/foundation.dart' show defaultTargetPlatform;
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/server.dart';
import '../services/last_rig_service.dart';
import '../services/multicast_lock.dart';
import '../services/server_api.dart';
import '../state/launch_gate_state.dart';
import '../state/saved_server_state.dart';
import '../state/server_state.dart';
import '../util/friendly_error.dart';
import '../l10n/l10n.dart';
import '../widgets/adaptive_refresh.dart';
import '../widgets/plan_offline_button.dart';
import '../widgets/adaptive_controls.dart';

/// Phase 11 first-run screen — server discovery + selection + handshake.
/// Polish + UX lands in Phase 12a (app shell + §25 visual design); this is
/// just the functional scaffold that proves discovery + handshake wire up.
class FirstRunScreen extends ConsumerStatefulWidget {
  const FirstRunScreen({super.key});

  @override
  ConsumerState<FirstRunScreen> createState() => _FirstRunScreenState();
}

class _FirstRunScreenState extends ConsumerState<FirstRunScreen> {
  final _discovered = <AraServer>{};
  final _manualHostCtrl = TextEditingController();
  final _manualPortCtrl = TextEditingController(text: '5555');
  Timer? _rescanTimer;

  /// The last rig, picked without a tap this visit (ALP-4890); its handshake
  /// confirms it the same way.
  AraServer? _autoPicked;
  bool _autoConfirmed = false;

  @override
  void initState() {
    super.initState();
    // Android drops mDNS answers without a multicast lock; hold it only while
    // this screen scans (#1129).
    unawaited(MulticastLock.acquire());
    // mDNS lookup is one-shot per provider instance, so a daemon that starts up
    // after the first scan would never appear. Re-run discovery on a loop while
    // this screen is shown so freshly-started servers turn up on their own
    // (deduped into _discovered) — the manual ⟳ button forces an immediate pass.
    //
    // This periodic pass is deliberately ADDITIVE (it does not clear _discovered):
    // re-clearing every 4s would make the live list flicker (empty → repopulate) on
    // every tick. The trade-off is that a server which moves ports / goes away leaves
    // a stale entry until the user taps ⟳ Rescan, which DOES clear first and drops
    // the service's cached sweep (see [_rescan]). mDNS one-shot lookups don't surface
    // goodbye/departure events, so pruning the dead entry automatically would need a
    // TTL/liveness probe — out of scope here; the manual rescan is the clear-stale path.
    _rescanTimer = Timer.periodic(const Duration(seconds: 4), (_) {
      if (mounted) ref.invalidate(discoveredServersProvider);
    });
  }

  @override
  void dispose() {
    unawaited(MulticastLock.release());
    _rescanTimer?.cancel();
    _manualHostCtrl.dispose();
    _manualPortCtrl.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    ref.listen(discoveredServersProvider, (prev, next) {
      // Every rig found is listed and the user taps the one they want (a site
      // can have several rigs, some not yet up, #1129) — except the last rig
      // this client used, which connects on its own (ALP-4890).
      next.whenData((server) {
        setState(() => _discovered.add(server));
        _maybeAutoPick();
      });
    });
    ref.listen(lastRigProvider, (_, _) => _maybeAutoPick());
    ref.listen(serverHandshakeProvider, (_, next) {
      final picked = _autoPicked;
      if (picked != null &&
          !_autoConfirmed &&
          next.value != null &&
          ref.read(selectedServerProvider) == picked) {
        // Once: a later handshake answer (a refresh) is the same rig (#1485).
        _autoConfirmed = true;
        unawaited(_confirm(picked));
      }
    });
    final selected = ref.watch(selectedServerProvider);
    final handshake = ref.watch(serverHandshakeProvider);

    return Scaffold(
      appBar: AppBar(
        title: Text(context.l10n.firstRunTitle),
        actions: [
          IconButton(
            icon: const Icon(Icons.refresh),
            tooltip: context.l10n.firstRunRescanTooltip,
            onPressed: _rescan,
          ),
        ],
      ),
      body: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              context.l10n.firstRunRigsFound,
              style: Theme.of(context).textTheme.titleMedium,
            ),
            const SizedBox(height: 8),
            // #1111 — the OS refused our multicast queries, so discovery
            // cannot work until the user allows it; say so instead of
            // showing "looking for servers" forever.
            ValueListenableBuilder<bool>(
              valueListenable:
                  ref.watch(discoveryServiceProvider).localNetworkBlocked,
              builder: (context, blocked, _) => blocked
                  ? const _LocalNetworkBlockedBanner()
                  : const SizedBox.shrink(),
            ),
            Expanded(
              // Pull down to rescan, the same as ⟳ (the list must always
              // scroll for the gesture to work when it is empty or short).
              child: AdaptiveRefresh(
                onRefresh: () async {
                  _rescan();
                  await Future<void>.delayed(const Duration(seconds: 1));
                },
                slivers: [
                  if (_discovered.isEmpty)
                    SliverToBoxAdapter(
                      child: Padding(
                        padding: const EdgeInsets.all(16),
                        child: Text(
                          context.l10n.firstRunLooking,
                          textAlign: TextAlign.center,
                        ),
                      ),
                    )
                  else
                    SliverList.list(
                      children: _discovered
                          .map(
                            (s) => ListTile(
                              leading: const Icon(Icons.dns),
                              title: Text(
                                s.mdnsName ?? '${s.hostname}:${s.port}',
                              ),
                              subtitle: Text('${s.hostname}:${s.port}'),
                              selected: selected == s,
                              onTap: () => ref
                                  .read(selectedServerProvider.notifier)
                                  .select(s),
                            ),
                          )
                          .toList(),
                    ),
                ],
              ),
            ),
            const Divider(),
            Text(
              context.l10n.firstRunAddManually,
              style: Theme.of(context).textTheme.titleMedium,
            ),
            const SizedBox(height: 8),
            Row(
              children: [
                Expanded(
                  child: TextField(
                    controller: _manualHostCtrl,
                    decoration: InputDecoration(
                      labelText: context.l10n.firstRunHostLabel,
                    ),
                  ),
                ),
                const SizedBox(width: 8),
                SizedBox(
                  width: 100,
                  child: TextField(
                    controller: _manualPortCtrl,
                    decoration:
                        InputDecoration(labelText: context.l10n.firstRunPortLabel),
                    keyboardType: TextInputType.number,
                  ),
                ),
                const SizedBox(width: 8),
                FilledButton(onPressed: _addManual, child: Text(context.l10n.firstRunUse)),
              ],
            ),
            const SizedBox(height: 16),
            // §2 offline planning — the client can do real work without the Pi:
            // enter the shell with no server to build the night's plan; drafts
            // push to a daemon once one is connected. Self-gated: disabled
            // until a profile has been cached from a connected session.
            Align(
              alignment: Alignment.centerLeft,
              child: PlanOfflineButton(
                label: context.l10n.firstRunPlanOffline,
              ),
            ),
            const SizedBox(height: 8),
            if (selected != null)
              _HandshakePanel(
                handshake: handshake,
                server: selected,
                onConfirm: () => _confirm(selected),
              ),
          ],
        ),
      ),
    );
  }

  // mDNS discovery is a one-shot lookup per provider instance — a server that
  // comes up *after* the initial scan won't appear on its own. Rescan clears the
  // accumulated list, forgets the shared subnet sweep (otherwise a daemon that
  // just went away would be replayed straight back onto the list without a
  // probe) and re-runs discovery so freshly-started daemons show up.
  void _rescan() {
    setState(() => _discovered.clear());
    ref.read(discoveryServiceProvider).resetSweepCache();
    ref.invalidate(discoveredServersProvider);
  }

  void _addManual() {
    final host = _manualHostCtrl.text.trim();
    final port = int.tryParse(_manualPortCtrl.text.trim()) ?? 5555;
    if (host.isEmpty) return;
    ref
        .read(selectedServerProvider.notifier)
        .select(AraServer(hostname: host, port: port));
  }

  /// Select the last rig once it shows up, unless the user came here to
  /// choose (Launchpad, "Choose a different rig") or already picked one.
  void _maybeAutoPick() {
    if (_autoPicked != null ||
        ref.read(serverChooserRequestedProvider) ||
        ref.read(selectedServerProvider) != null) {
      return;
    }
    final pick = autoPickRig(_discovered, ref.read(lastRigProvider).value);
    if (pick == null) return;
    _autoPicked = pick;
    ref.read(selectedServerProvider.notifier).select(pick);
  }

  Future<void> _confirm(AraServer server) async {
    final info = ref.read(serverHandshakeProvider).value;
    if (info != null) {
      unawaited(ref
          .read(lastRigServiceProvider)
          .save(LastRig(info.name, uuid: info.uuid)));
    }
    await ref.read(savedServersProvider.notifier).add(server);
    // A Launchpad-forced visit ends here: the server is chosen, so the router
    // may resume the normal flow (profile box). The _RootRouter watching
    // savedServersProvider swaps the screen once the list is non-empty.
    ref.read(serverChooserRequestedProvider.notifier).clear();
  }
}

class _HandshakePanel extends StatelessWidget {
  final AsyncValue<ServerInfo?> handshake;
  final AraServer server;
  final VoidCallback onConfirm;
  const _HandshakePanel({
    required this.handshake,
    required this.server,
    required this.onConfirm,
  });

  @override
  Widget build(BuildContext context) {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: handshake.when(
          data: (info) => info == null
              ? const SizedBox.shrink()
              : Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      context.l10n.firstRunConnectedTo(info.name),
                      style: Theme.of(context).textTheme.titleMedium,
                    ),
                    Text(context.l10n.firstRunServerVersion(info.version)),
                    Text(context.l10n.firstRunApiVersion(info.apiVersion)),
                    Text(context.l10n.firstRunAddress(server.baseUrl)),
                    const SizedBox(height: 12),
                    FilledButton.icon(
                      onPressed: onConfirm,
                      icon: const Icon(Icons.arrow_forward),
                      label: Text(context.l10n.firstRunContinue),
                    ),
                  ],
                ),
          loading: () => Row(
            children: [
              const SizedBox(
                width: 24,
                height: 24,
                child: AdaptiveProgressIndicator(strokeWidth: 2),
              ),
              const SizedBox(width: 12),
              Text(context.l10n.firstRunConnecting(server.baseUrl)),
            ],
          ),
          error: (e, _) => Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                context.l10n.firstRunConnectionFailed(server.baseUrl),
                style: TextStyle(color: Theme.of(context).colorScheme.error),
              ),
              Text(
                friendlyError(context.l10n, e),
                style: Theme.of(context).textTheme.bodySmall,
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Shown while [ServerDiscoveryService.localNetworkBlocked] is true.
class _LocalNetworkBlockedBanner extends StatelessWidget {
  const _LocalNetworkBlockedBanner();

  static const macMessage =
      'macOS is blocking local network access for OpenAstro Ara, so '
      'auto-discovery cannot see your rig. Allow it in System Settings → '
      'Privacy & Security → Local Network, then tap ⟳. Adding the rig '
      'manually below works regardless.';
  static const iosMessage =
      'iOS is blocking local network access for OpenAstro Ara, so '
      'auto-discovery cannot see your rig. Turn it on in Settings → '
      'Privacy & Security → Local Network → OpenAstro Ara, then tap ⟳. '
      'Adding the rig manually below works regardless.';
  static const otherMessage =
      'This device is blocking local network (multicast) access for '
      'OpenAstro Ara, so auto-discovery cannot see your rig. Check its '
      'network privacy settings, then tap ⟳. Adding the rig manually below '
      'works regardless.';

  /// Where the setting lives differs per OS (#1129); the rest get the generic
  /// wording. defaultTargetPlatform rather than dart:io Platform so tests can
  /// override it.
  @visibleForTesting
  static String messageFor(TargetPlatform platform) => switch (platform) {
        TargetPlatform.macOS => macMessage,
        TargetPlatform.iOS => iosMessage,
        _ => otherMessage,
      };

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: Material(
        color: theme.colorScheme.errorContainer,
        borderRadius: BorderRadius.circular(8),
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Icon(Icons.wifi_off,
                  size: 20, color: theme.colorScheme.onErrorContainer),
              const SizedBox(width: 10),
              Expanded(
                child: Text(
                  messageFor(defaultTargetPlatform),
                  style: theme.textTheme.bodySmall
                      ?.copyWith(color: theme.colorScheme.onErrorContainer),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

