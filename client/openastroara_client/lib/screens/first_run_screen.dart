import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/server.dart';
import '../services/server_api.dart';
import '../state/launch_gate_state.dart';
import '../state/saved_server_state.dart';
import '../state/server_state.dart';

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

  @override
  void initState() {
    super.initState();
    // mDNS lookup is one-shot per provider instance, so a daemon that starts up
    // after the first scan would never appear. Re-run discovery on a loop while
    // this screen is shown so freshly-started servers turn up on their own
    // (deduped into _discovered) — the manual ⟳ button forces an immediate pass.
    //
    // This periodic pass is deliberately ADDITIVE (it does not clear _discovered):
    // re-clearing every 4s would make the live list flicker (empty → repopulate) on
    // every tick. The trade-off is that a server which moves ports / goes away leaves
    // a stale entry until the user taps ⟳ Rescan, which DOES clear first (see
    // [_rescan]). mDNS one-shot lookups don't surface goodbye/departure events, so
    // pruning the dead entry automatically would need a TTL/liveness probe — out of
    // scope here; the manual rescan is the clear-stale path.
    _rescanTimer = Timer.periodic(const Duration(seconds: 4), (_) {
      if (mounted) ref.invalidate(discoveredServersProvider);
    });
  }

  @override
  void dispose() {
    _rescanTimer?.cancel();
    _manualHostCtrl.dispose();
    _manualPortCtrl.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    ref.listen(discoveredServersProvider, (prev, next) {
      next.whenData((server) => setState(() => _discovered.add(server)));
    });
    final selected = ref.watch(selectedServerProvider);
    final handshake = ref.watch(serverHandshakeProvider);

    return Scaffold(
      appBar: AppBar(
        title: const Text('Connect to OpenAstro Ara'),
        actions: [
          IconButton(
            icon: const Icon(Icons.refresh),
            tooltip: 'Rescan for servers',
            onPressed: _rescan,
          ),
        ],
      ),
      body: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text('Discovered servers (_openastroara._tcp.local):',
                style: Theme.of(context).textTheme.titleMedium),
            const SizedBox(height: 8),
            Expanded(
              child: _discovered.isEmpty
                  ? const Center(
                      child: Padding(
                        padding: EdgeInsets.all(16),
                        child: Text(
                          'Looking for servers… rescanning every few seconds.\n'
                          'If yours doesn’t appear, tap ⟳ (top right) or add it '
                          'manually below.',
                          textAlign: TextAlign.center,
                        ),
                      ),
                    )
                  : ListView(
                      children: _discovered
                          .map((s) => ListTile(
                                leading: const Icon(Icons.dns),
                                title: Text(s.mdnsName ?? '${s.hostname}:${s.port}'),
                                subtitle: Text('${s.hostname}:${s.port}'),
                                selected: selected == s,
                                onTap: () => ref
                                    .read(selectedServerProvider.notifier)
                                    .select(s),
                              ))
                          .toList(),
                    ),
            ),
            const Divider(),
            Text('Or add manually:',
                style: Theme.of(context).textTheme.titleMedium),
            const SizedBox(height: 8),
            Row(children: [
              Expanded(
                child: TextField(
                  controller: _manualHostCtrl,
                  decoration: const InputDecoration(labelText: 'Hostname or IP'),
                ),
              ),
              const SizedBox(width: 8),
              SizedBox(
                width: 100,
                child: TextField(
                  controller: _manualPortCtrl,
                  decoration: const InputDecoration(labelText: 'Port'),
                  keyboardType: TextInputType.number,
                ),
              ),
              const SizedBox(width: 8),
              FilledButton(
                onPressed: _addManual,
                child: const Text('Use'),
              ),
            ]),
            const SizedBox(height: 16),
            // §2 offline planning — WILMA can do real work without the Pi:
            // enter the shell with no server to build the night's plan; drafts
            // push to a daemon once one is connected.
            Align(
              alignment: Alignment.centerLeft,
              child: TextButton.icon(
                onPressed: () =>
                    ref.read(offlineModeProvider.notifier).enter(),
                icon: const Icon(Icons.cloud_off_outlined, size: 18),
                label: const Text('Plan offline — set up your night without a server'),
              ),
            ),
            const SizedBox(height: 8),
            if (selected != null)
              _HandshakePanel(
                handshake: handshake,
                server: selected,
                onConfirm: () async {
                  await ref
                      .read(savedServersProvider.notifier)
                      .add(selected);
                  // The _RootRouter watching savedServersProvider rebuilds
                  // and swaps in AppShell automatically once the list is
                  // non-empty.
                },
              ),
          ],
        ),
      ),
    );
  }

  // mDNS discovery is a one-shot lookup per provider instance — a server that
  // comes up *after* the initial scan won't appear on its own. Rescan clears the
  // accumulated list and re-runs the lookup so freshly-started daemons show up.
  void _rescan() {
    setState(() => _discovered.clear());
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
              : Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Text('Connected to ${info.name}',
                      style: Theme.of(context).textTheme.titleMedium),
                  Text('Server version: ${info.version}'),
                  Text('API version: ${info.apiVersion}'),
                  Text('Endpoint: ${server.baseUrl}'),
                  const SizedBox(height: 12),
                  FilledButton.icon(
                    onPressed: onConfirm,
                    icon: const Icon(Icons.arrow_forward),
                    label: const Text('Save & continue'),
                  ),
                ]),
          loading: () => Row(children: [
            const SizedBox(width: 24, height: 24, child: CircularProgressIndicator(strokeWidth: 2)),
            const SizedBox(width: 12),
            Text('Connecting to ${server.baseUrl}…'),
          ]),
          error: (e, _) => Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text('Connection failed: ${server.baseUrl}',
                style: TextStyle(color: Theme.of(context).colorScheme.error)),
            Text('$e', style: Theme.of(context).textTheme.bodySmall),
          ]),
        ),
      ),
    );
  }
}
