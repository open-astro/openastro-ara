import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/guider_status.dart';
import '../../state/guider/guider_state.dart';
import 'guider_calibration_dialog.dart';

/// Opens the §63 guider connect/disconnect dialog.
Future<void> showGuiderDialog(BuildContext context) {
  // The builder's context is intentionally unused: `_GuiderDialog` is a
  // ConsumerWidget that resolves `ref`/Navigator from its own subtree under the
  // navigator + ProviderScope (which sit above MaterialApp), so it needs nothing
  // from the caller's context.
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

class _GuiderDialog extends ConsumerStatefulWidget {
  const _GuiderDialog();

  @override
  ConsumerState<_GuiderDialog> createState() => _GuiderDialogState();
}

class _GuiderDialogState extends ConsumerState<_GuiderDialog> {
  // Local feedback for a manual Refresh: the notifier's refresh() deliberately
  // doesn't emit a loading state (so the body doesn't flash blank), so the
  // button tracks its own in-flight flag to disable + spinner.
  bool _refreshing = false;

  Future<void> _refresh() async {
    setState(() => _refreshing = true);
    try {
      await ref.read(guiderStatusProvider.notifier).refresh();
    } finally {
      if (mounted) setState(() => _refreshing = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final async = ref.watch(guiderStatusProvider);
    final notifier = ref.read(guiderStatusProvider.notifier);
    final busy = async.isLoading;
    final status = async.asData?.value;
    final connected = status?.isConnected ?? false;
    final actionsLocked = busy || _refreshing;

    return AlertDialog(
      title: const Text('Guider'),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          if (busy && status == null)
            const Padding(
              padding: EdgeInsets.all(8),
              child: Center(child: CircularProgressIndicator()),
            )
          else if (async.hasError)
            Text(
              // Don't interpolate the raw error — a DioException can carry URLs/
              // headers/stack excerpts into a user-visible string.
              'Could not reach the guider. Check the connection and try again.',
              style: TextStyle(color: Theme.of(context).colorScheme.error),
            )
          else if (status == null)
            const Text('No guider configured on this server.')
          else
            _StatusBody(status: status),
          if (connected && !busy)
            Align(
              alignment: Alignment.centerLeft,
              child: TextButton.icon(
                onPressed: () => showGuiderCalibrationDialog(context),
                icon: const Icon(Icons.build, size: 16),
                label: const Text('Calibration…'),
              ),
            ),
        ],
      ),
      actions: [
        TextButton(
          onPressed: actionsLocked ? null : _refresh,
          child: _refreshing ? const _BusySpinner() : const Text('Refresh'),
        ),
        if (connected)
          FilledButton(
            onPressed: actionsLocked ? null : () => notifier.disconnect(),
            child: busy ? const _BusySpinner() : const Text('Disconnect'),
          )
        else
          FilledButton(
            onPressed: actionsLocked ? null : () => notifier.connect(),
            child: busy ? const _BusySpinner() : const Text('Connect'),
          ),
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Close'),
        ),
      ],
    );
  }
}

class _BusySpinner extends StatelessWidget {
  const _BusySpinner();
  @override
  Widget build(BuildContext context) =>
      const SizedBox(width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2));
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
