import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../models/discovered_device.dart';
import '../../screens/wizard/screens/screen_equipment_discovery.dart'
    show equipmentDiscoveryApiFactoryProvider;
import '../../state/equipment/alpaca_selection_state.dart';
import '../../state/saved_server_state.dart';
import '../../state/settings/equipment_connection_state.dart';
import '../../theme/ara_colors.dart';
import '../../util/friendly_error.dart';
import '../inline_choices.dart';

/// §52.2 Alpaca device chooser. Calls `/api/v1/equipment/discover/{type}`
/// against the active server, shows the discovered devices, lets the user
/// pick one. By default the pick is stored in `alpacaSelectionProvider` (the
/// one-device-per-type selection each panel's connection row consumes).
///
/// Pass [onPick] to override that for a multi-instance type (Switch): the
/// callback receives the chosen device instead of the default single-selection
/// write, so the caller can e.g. connect an additional device.
Future<void> showAlpacaChooserDialog(
  BuildContext context,
  EquipmentDeviceType type, {
  required String deviceTypeLabel,
  void Function(DiscoveredDevice device)? onPick,
}) {
  return showDialog<void>(
    context: context,
    builder: (_) => AlpacaDeviceChooser(
      type: type,
      deviceTypeLabel: deviceTypeLabel,
      onPick: onPick,
    ),
  );
}

/// The discovery list: a dialog's body, or laid out in place when the host
/// passes [onClose] (Simple Mode has no popups).
class AlpacaDeviceChooser extends ConsumerStatefulWidget {
  final EquipmentDeviceType type;
  final String deviceTypeLabel;
  final void Function(DiscoveredDevice device)? onPick;

  /// Inline: closes it, on Cancel or after a pick.
  final VoidCallback? onClose;

  const AlpacaDeviceChooser({
    super.key,
    required this.type,
    required this.deviceTypeLabel,
    this.onPick,
    this.onClose,
  });

  @override
  ConsumerState<AlpacaDeviceChooser> createState() =>
      _AlpacaDeviceChooserState();
}

class _AlpacaDeviceChooserState extends ConsumerState<AlpacaDeviceChooser> {
  bool get _inline => widget.onClose != null;

  void _close() =>
      _inline ? widget.onClose!() : Navigator.of(context).pop();

  late Future<List<DiscoveredDevice>> _future;

  @override
  void initState() {
    super.initState();
    _future = _runDiscovery(forceRefresh: false);
  }

  Future<List<DiscoveredDevice>> _runDiscovery({required bool forceRefresh}) {
    final server = ref.read(activeServerProvider);
    if (server == null) {
      return Future.error(const _NotConnected());
    }
    // Built through the shared factory seam so tests can fake discovery.
    final api = ref.read(equipmentDiscoveryApiFactoryProvider)(server);
    return api.discover(widget.type, forceRefresh: forceRefresh);
  }

  void _refresh() {
    setState(() {
      _future = _runDiscovery(forceRefresh: true);
    });
  }

  void _pick(DiscoveredDevice device) {
    if (widget.onPick != null) {
      widget.onPick!(device);
    } else {
      ref.read(alpacaSelectionProvider.notifier).select(widget.type, device);
    }
    _close();
  }

  Widget _list() => FutureBuilder<List<DiscoveredDevice>>(
    future: _future,
    builder: (context, snap) {
      if (snap.connectionState == ConnectionState.waiting) {
        return const Center(heightFactor: 2, child: CircularProgressIndicator());
      }
      if (snap.hasError) {
        return _ErrorState(error: snap.error, onRetry: _refresh);
      }
      final devices = snap.data ?? const <DiscoveredDevice>[];
      if (devices.isEmpty) {
        return const _EmptyState();
      }
      return ListView.separated(
        shrinkWrap: _inline,
        itemCount: devices.length,
        separatorBuilder: (_, _) =>
            const Divider(height: 1, color: AraColors.border),
        itemBuilder: (_, i) => _DeviceTile(
          device: devices[i],
          onTap: () => _pick(devices[i]),
        ),
      );
    },
  );

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final words = l10n.deviceChooserTitle(widget.deviceTypeLabel);
    final title = Text(words);
    final rescan = IconButton(
      tooltip: l10n.deviceChooserRescan,
      onPressed: _refresh,
      icon: const Icon(Icons.refresh),
    );
    if (_inline) {
      return InlineAttention(
        title: words,
        child: Material(
          type: MaterialType.transparency,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Row(children: [Expanded(child: title), rescan]),
              ConstrainedBox(
                constraints: const BoxConstraints(maxHeight: 320),
                child: _list(),
              ),
              Align(
                alignment: Alignment.centerRight,
                child: TextButton(
                  key: const Key('alpaca-chooser-cancel'),
                  style: TextButton.styleFrom(minimumSize: const Size(0, 44)),
                  onPressed: _close,
                  child: Text(l10n.planCancel),
                ),
              ),
            ],
          ),
        ),
      );
    }
    return AlertDialog(
      title: Row(children: [title, const Spacer(), rescan]),
      content: SizedBox(width: 480, height: 320, child: _list()),
      actions: [TextButton(onPressed: _close, child: Text(l10n.planCancel))],
    );
  }
}

/// No rig to ask: said in the chooser's own language when it shows.
class _NotConnected {
  const _NotConnected();
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
        device.isManualFilterWheel
            ? context.l10n.deviceChooserManualWheel
            : '$scheme://${device.hostName.isNotEmpty ? device.hostName : device.ipAddress}:${device.ipPort}'
                ' ·  device #${device.alpacaDeviceNumber}',
        style: Theme.of(context).textTheme.bodySmall?.copyWith(
              color: AraColors.textSecondary,
            ),
      ),
      trailing: const Icon(Icons.chevron_right),
      onTap: onTap,
    );
  }
}

/// §68.2 — an empty discovery post-setup most commonly means AlpacaBridge
/// itself is missing/stopped (no Alpaca responder on the daemon's subnet),
/// so the empty state carries the same guidance the wizard's blocked panel
/// shows: the install command and the systemd diagnostic.
class _EmptyState extends StatelessWidget {
  const _EmptyState();

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Center(
      child: SingleChildScrollView(
        padding: const EdgeInsets.symmetric(horizontal: 16),
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            const Icon(Icons.search_off, size: 48, color: AraColors.textDisabled),
            const SizedBox(height: 12),
            Text(context.l10n.deviceChooserNoneTitle, style: theme.textTheme.titleMedium),
            const SizedBox(height: 4),
            Text(
              context.l10n.deviceChooserNoneBody,
              textAlign: TextAlign.center,
              style: theme.textTheme.bodySmall?.copyWith(
                    color: AraColors.textSecondary,
                  ),
            ),
            const SizedBox(height: 8),
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
              decoration: BoxDecoration(
                color: AraColors.bgPrimary,
                borderRadius: BorderRadius.circular(4),
              ),
              child: Text(
                'sudo apt install alpaca-bridge\n'
                'systemctl status alpaca-bridge',
                style: theme.textTheme.bodySmall
                    ?.copyWith(fontFamily: 'monospace'),
              ),
            ),
            const SizedBox(height: 8),
            Text(
              context.l10n.deviceChooserOtherHost,
              textAlign: TextAlign.center,
              style: theme.textTheme.bodySmall?.copyWith(
                    color: AraColors.textSecondary,
                  ),
            ),
          ],
        ),
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
      _NotConnected() => context.l10n.deviceChooserNotConnected,
      Object e => friendlyError(context.l10n, e, action: context.l10n.deviceChooserFindAction),
      _ => 'Unknown error',
    };
    return Center(
      child: SingleChildScrollView(
        child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          const Icon(Icons.error_outline,
              size: 48, color: AraColors.accentError),
          const SizedBox(height: 12),
          Text(
            context.l10n.deviceChooserFailed,
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
            label: Text(context.l10n.deviceChooserRetry),
          ),
        ],
        ),
      ),
    );
  }
}
