import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/discovered_device.dart';
import '../../services/equipment_discovery_api.dart';
import '../../state/equipment/alpaca_selection_state.dart';
import '../../state/saved_server_state.dart';
import '../../state/settings/equipment_connection_state.dart';
import '../../theme/ara_colors.dart';

/// §52.2 Alpaca device chooser. Calls `/api/v1/equipment/discover/{type}`
/// against the active server, shows the discovered devices, lets the user
/// pick one. Selection lives in `alpacaSelectionProvider` and is consumed
/// by each equipment panel's connection row.
Future<void> showAlpacaChooserDialog(
  BuildContext context,
  EquipmentDeviceType type, {
  required String deviceTypeLabel,
}) {
  return showDialog<void>(
    context: context,
    builder: (_) => _AlpacaChooserDialog(
      type: type,
      deviceTypeLabel: deviceTypeLabel,
    ),
  );
}

class _AlpacaChooserDialog extends ConsumerStatefulWidget {
  final EquipmentDeviceType type;
  final String deviceTypeLabel;

  const _AlpacaChooserDialog({
    required this.type,
    required this.deviceTypeLabel,
  });

  @override
  ConsumerState<_AlpacaChooserDialog> createState() =>
      _AlpacaChooserDialogState();
}

class _AlpacaChooserDialogState extends ConsumerState<_AlpacaChooserDialog> {
  late Future<List<DiscoveredDevice>> _future;

  @override
  void initState() {
    super.initState();
    _future = _runDiscovery(forceRefresh: false);
  }

  Future<List<DiscoveredDevice>> _runDiscovery({required bool forceRefresh}) {
    final servers = ref.read(savedServersProvider).maybeWhen(
          data: (list) => list,
          orElse: () => const [],
        );
    if (servers.isEmpty) {
      return Future.error('No active server — connect to a daemon first.');
    }
    // Most-recently-saved server is the de-facto active one, same convention
    // as the help dialog (§54).
    final api = EquipmentDiscoveryApi(servers.last);
    return api.discover(widget.type, forceRefresh: forceRefresh);
  }

  void _refresh() {
    setState(() {
      _future = _runDiscovery(forceRefresh: true);
    });
  }

  void _pick(DiscoveredDevice device) {
    ref.read(alpacaSelectionProvider.notifier).select(widget.type, device);
    Navigator.of(context).pop();
  }

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: Row(
        children: [
          Text('Choose ${widget.deviceTypeLabel}'),
          const Spacer(),
          IconButton(
            tooltip: 'Re-scan',
            onPressed: _refresh,
            icon: const Icon(Icons.refresh),
          ),
        ],
      ),
      content: SizedBox(
        width: 480,
        height: 320,
        child: FutureBuilder<List<DiscoveredDevice>>(
          future: _future,
          builder: (context, snap) {
            if (snap.connectionState == ConnectionState.waiting) {
              return const Center(child: CircularProgressIndicator());
            }
            if (snap.hasError) {
              return _ErrorState(
                error: snap.error,
                onRetry: _refresh,
              );
            }
            final devices = snap.data ?? const <DiscoveredDevice>[];
            if (devices.isEmpty) {
              return const _EmptyState();
            }
            return ListView.separated(
              itemCount: devices.length,
              separatorBuilder: (_, _) =>
                  const Divider(height: 1, color: AraColors.border),
              itemBuilder: (_, i) => _DeviceTile(
                device: devices[i],
                onTap: () => _pick(devices[i]),
              ),
            );
          },
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
      ],
    );
  }
}

class _DeviceTile extends StatelessWidget {
  final DiscoveredDevice device;
  final VoidCallback onTap;

  const _DeviceTile({required this.device, required this.onTap});

  @override
  Widget build(BuildContext context) {
    final scheme = device.useHttps ? 'https' : 'http';
    return ListTile(
      title: Text(device.name),
      subtitle: Text(
        '$scheme://${device.hostName.isNotEmpty ? device.hostName : device.ipAddress}:${device.ipPort}'
        '  ·  device #${device.alpacaDeviceNumber}',
        style: Theme.of(context).textTheme.bodySmall?.copyWith(
              color: AraColors.textSecondary,
            ),
      ),
      trailing: const Icon(Icons.chevron_right),
      onTap: onTap,
    );
  }
}

class _EmptyState extends StatelessWidget {
  const _EmptyState();

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          const Icon(Icons.search_off, size: 48, color: AraColors.textDisabled),
          const SizedBox(height: 12),
          Text(
            'No devices found',
            style: Theme.of(context).textTheme.titleMedium,
          ),
          const SizedBox(height: 4),
          Text(
            'Make sure your Alpaca driver is running + reachable on the daemon\'s subnet.',
            textAlign: TextAlign.center,
            style: Theme.of(context).textTheme.bodySmall?.copyWith(
                  color: AraColors.textSecondary,
                ),
          ),
        ],
      ),
    );
  }
}

class _ErrorState extends StatelessWidget {
  final Object? error;
  final VoidCallback onRetry;

  const _ErrorState({required this.error, required this.onRetry});

  @override
  Widget build(BuildContext context) {
    final message = switch (error) {
      DioException e =>
        '${e.message ?? 'Network error'} (${e.response?.statusCode ?? 'no response'})',
      Object e => e.toString(),
      _ => 'Unknown error',
    };
    return Center(
      child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          const Icon(Icons.error_outline,
              size: 48, color: AraColors.accentBusy),
          const SizedBox(height: 12),
          Text(
            'Discovery failed',
            style: Theme.of(context).textTheme.titleMedium,
          ),
          const SizedBox(height: 4),
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 16),
            child: Text(
              message,
              textAlign: TextAlign.center,
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                    color: AraColors.textSecondary,
                  ),
            ),
          ),
          const SizedBox(height: 12),
          TextButton.icon(
            onPressed: onRetry,
            icon: const Icon(Icons.refresh, size: 16),
            label: const Text('Retry'),
          ),
        ],
      ),
    );
  }
}
