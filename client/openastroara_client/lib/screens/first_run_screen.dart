import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/server.dart';
import '../services/server_api.dart';
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

  @override
  void dispose() {
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
      appBar: AppBar(title: const Text('Connect to OpenAstro Ara')),
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
                  ? const Center(child: Text('Scanning… (mDNS broadcasts every few seconds)'))
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
