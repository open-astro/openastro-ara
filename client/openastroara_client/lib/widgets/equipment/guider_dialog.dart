import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/guider_status.dart';
import '../../state/guider/guider_state.dart';

/// Opens the §63 guider connect/disconnect dialog.
Future<void> showGuiderDialog(BuildContext context) {
  return showDialog<void>(context: context, builder: (_) => const _GuiderDialog());
}

/// Human-readable link-state label.
String guiderConnectionLabel(GuiderConnectionState state) {
  switch (state) {
    case GuiderConnectionState.connected:
      return 'Connected';
    case GuiderConnectionState.connecting:
      return 'Connecting…';
    case GuiderConnectionState.disconnected:
      return 'Disconnected';
    case GuiderConnectionState.error:
      return 'Error';
    case GuiderConnectionState.unknown:
      return 'Unknown';
  }
}

/// Human-readable runtime-phase label.
String guiderRuntimeLabel(GuiderRuntimeState state) {
  switch (state) {
    case GuiderRuntimeState.stopped:
      return 'Stopped';
    case GuiderRuntimeState.calibrating:
      return 'Calibrating';
    case GuiderRuntimeState.guiding:
      return 'Guiding';
    case GuiderRuntimeState.paused:
      return 'Paused';
    case GuiderRuntimeState.starLost:
      return 'Star lost';
    case GuiderRuntimeState.dithering:
      return 'Dithering';
    case GuiderRuntimeState.unknown:
      return 'Unknown';
  }
}

class _GuiderDialog extends ConsumerWidget {
  const _GuiderDialog();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(guiderStatusProvider);
    final notifier = ref.read(guiderStatusProvider.notifier);
    final busy = async.isLoading;
    final status = async.asData?.value;
    final connected = status?.isConnected ?? false;

    return AlertDialog(
      title: const Text('Guider'),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          if (async.hasError)
            Text('Failed: ${async.error}', style: TextStyle(color: Theme.of(context).colorScheme.error))
          else if (status == null)
            const Text('No guider configured on this server.')
          else
            _StatusBody(status: status),
        ],
      ),
      actions: [
        TextButton(
          onPressed: busy ? null : () => notifier.refresh(),
          child: const Text('Refresh'),
        ),
        if (connected)
          FilledButton(
            onPressed: busy ? null : () => notifier.disconnect(),
            child: const Text('Disconnect'),
          )
        else
          FilledButton(
            onPressed: busy ? null : () => notifier.connect(),
            child: busy
                ? const SizedBox(
                    width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2))
                : const Text('Connect'),
          ),
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Close'),
        ),
      ],
    );
  }
}

class _StatusBody extends StatelessWidget {
  final GuiderStatus status;
  const _StatusBody({required this.status});

  @override
  Widget build(BuildContext context) {
    final rms = status.rmsTotal;
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _row(context, 'Link', guiderConnectionLabel(status.connectionState)),
        _row(context, 'State', guiderRuntimeLabel(status.runtimeState)),
        if (status.currentProfile != null) _row(context, 'Profile', status.currentProfile!),
        if (rms != null) _row(context, 'RMS', '${rms.toStringAsFixed(2)}"'),
      ],
    );
  }

  Widget _row(BuildContext context, String label, String value) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 2),
      child: Row(
        children: [
          SizedBox(width: 64, child: Text(label, style: Theme.of(context).textTheme.bodySmall)),
          Expanded(child: Text(value, style: Theme.of(context).textTheme.bodyMedium)),
        ],
      ),
    );
  }
}
