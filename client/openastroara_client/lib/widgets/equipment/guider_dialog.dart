import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/equipment_device_status.dart';
import '../../models/guider_status.dart';
import '../../state/guider/guider_state.dart';
import '../../theme/ara_colors.dart';
import 'equipment_state_chip.dart';

/// Human-readable runtime-phase label (one wording for every screen, #1364).
String guiderRuntimeLabel(GuiderRuntimeState state) => guiderRuntimeText(state);

/// The §63 guider link: status rows plus Refresh and Connect/Disconnect. Lives
/// at the top of the guider's Settings panel, where the top-bar GUIDE chip
/// lands like every other device chip.
class GuiderConnectionSection extends ConsumerStatefulWidget {
  const GuiderConnectionSection({super.key});

  @override
  ConsumerState<GuiderConnectionSection> createState() => _GuiderConnectionSectionState();
}

class _GuiderConnectionSectionState extends ConsumerState<GuiderConnectionSection> {
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
    final text = Theme.of(context).textTheme;

    // The same card as the mount / camera / focuser connection
    // (EquipmentConnectionCard): name + link-state chip + link action, divider,
    // then the live body.
    return Card(
      color: AraColors.bgPanel,
      margin: const EdgeInsets.only(top: 4, bottom: 8),
      child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 12, 8, 12),
        child: busy && status == null
            ? const Padding(
                padding: EdgeInsets.all(12),
                child: Center(child: CircularProgressIndicator()),
              )
            : async.hasError
                ? Row(
                    children: [
                      const Icon(Icons.error_outline, color: AraColors.accentError, size: 20),
                      const SizedBox(width: 8),
                      // Don't interpolate the raw error — a DioException can carry URLs/
                      // headers/stack excerpts into a user-visible string.
                      const Expanded(
                        child: Text('Could not reach the guider. Check the connection and try again.'),
                      ),
                      TextButton(
                        onPressed: actionsLocked ? null : _refresh,
                        child: _refreshing ? const _BusySpinner() : const Text('Retry'),
                      ),
                    ],
                  )
                : Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Row(
                        children: [
                          Expanded(
                            child: Text(status?.name ?? 'OpenAstro Guider', style: text.titleMedium),
                          ),
                          EquipmentStateChip(state: _chipState(status?.connectionState)),
                          if (connected)
                            IconButton(
                              tooltip: 'Disconnect',
                              icon: busy ? const _BusySpinner() : const Icon(Icons.link_off, size: 18),
                              onPressed: actionsLocked ? null : () => notifier.disconnect(),
                            )
                          else
                            IconButton(
                              tooltip: 'Connect',
                              icon: busy ? const _BusySpinner() : const Icon(Icons.link, size: 18),
                              onPressed: actionsLocked ? null : () => notifier.connect(),
                            ),
                        ],
                      ),
                      const Divider(height: 20, color: AraColors.border),
                      if (status == null)
                        Text('No guider configured on this server.',
                            style: text.bodySmall?.copyWith(color: AraColors.textSecondary))
                      else if (!connected)
                        Text('Not connected.',
                            style: text.bodySmall?.copyWith(color: AraColors.textSecondary))
                      else
                        _StatusBody(status: status),
                    ],
                  ),
      ),
    );
  }

  static EquipmentConnectionState _chipState(GuiderConnectionState? s) => switch (s) {
        GuiderConnectionState.connected => EquipmentConnectionState.connected,
        GuiderConnectionState.connecting => EquipmentConnectionState.connecting,
        GuiderConnectionState.error => EquipmentConnectionState.error,
        _ => EquipmentConnectionState.disconnected,
      };
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
        _row(context, 'State', guiderRuntimeLabel(status.runtimeState)),
        if (status.currentProfile != null) _row(context, 'Profile', status.currentProfile!),
        if (rms != null) _row(context, 'RMS', '${rms.toStringAsFixed(2)}"'),
      ],
    );
  }

  // Label left, value right — the mount / camera / focuser card rows.
  Widget _row(BuildContext context, String label, String value) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 2),
      child: Row(
        children: [
          Expanded(child: Text(label)),
          Text(value),
        ],
      ),
    );
  }

}
