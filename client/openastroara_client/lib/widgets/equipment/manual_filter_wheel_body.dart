import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/filter_wheel_status.dart';
import '../../services/equipment_device_api.dart';
import '../../state/equipment/filter_wheel_state.dart';
import '../../theme/ara_colors.dart';

/// #1298 — the Equipment card body for the driverless manual filter wheel.
///
/// Nothing moves on its own, so the slot list answers one question: which
/// filter is in the train right now? Tapping a row reports it installed. A
/// hand swap the daemon is waiting for (a run's filter change, or one asked
/// for from the Imaging tab) shows as a card on top with a confirm button.
class ManualFilterWheelBody extends ConsumerStatefulWidget {
  final FilterWheelStatus status;

  const ManualFilterWheelBody({super.key, required this.status});

  @override
  ConsumerState<ManualFilterWheelBody> createState() =>
      _ManualFilterWheelBodyState();
}

class _ManualFilterWheelBodyState extends ConsumerState<ManualFilterWheelBody> {
  bool _busy = false;

  Future<void> _run(
    Future<bool> Function(FilterWheelNotifier n) action,
    String failure,
  ) async {
    final messenger = ScaffoldMessenger.of(context);
    setState(() => _busy = true);
    try {
      await action(ref.read(filterWheelProvider.notifier));
    } catch (e) {
      messenger.showSnackBar(
        SnackBar(
          content: Text('$failure: ${describeEquipmentError(e)}'),
          backgroundColor: AraColors.accentError,
        ),
      );
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final status = widget.status;
    final current = status.current;
    final pending = status.isAwaitingUser ? status.pending : null;
    final secondary = Theme.of(context).textTheme.bodySmall
        ?.copyWith(color: AraColors.textSecondary);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            const Expanded(child: Text('Installed filter')),
            Text(current?.name ?? 'Unknown'),
          ],
        ),
        if (pending != null) ...[
          const SizedBox(height: 12),
          ManualSwapCard(
            target: pending.name,
            current: current?.name,
            busy: _busy,
            onDone: () => _run(
              (n) => n.reportInstalled(pending.position),
              "Couldn't confirm the filter",
            ),
            onCancel: () =>
                _run((n) => n.cancelManualSwap(), "Couldn't cancel the swap"),
          ),
        ],
        const Divider(height: 20, color: AraColors.border),
        if (status.slots.isEmpty)
          const Text('No filters yet. Name them under Filters below.')
        else ...[
          Text(
            current == null
                ? 'Tap the filter that is in the train now.'
                : 'Swapped by hand? Tap the filter that is in now.',
            style: secondary,
          ),
          const SizedBox(height: 4),
          for (final slot in status.slots)
            ListTile(
              key: ValueKey('manual-slot-${slot.position}'),
              dense: true,
              contentPadding: const EdgeInsets.symmetric(horizontal: 12),
              title: Text(slot.name),
              trailing: slot.position == status.currentSlot
                  ? const Icon(
                      Icons.check_circle,
                      size: 20,
                      color: AraColors.accentConnected,
                    )
                  : null,
              enabled: !_busy,
              onTap: slot.position == status.currentSlot
                  ? null
                  : () => _run(
                      (n) => n.reportInstalled(slot.position),
                      "Couldn't record the filter",
                    ),
            ),
        ],
      ],
    );
  }
}

/// The "install the X filter" call to action, shared by the Equipment card
/// and the app-wide prompt.
class ManualSwapCard extends StatelessWidget {
  final String target;
  final String? current;
  final bool busy;
  final VoidCallback onDone;
  final VoidCallback onCancel;

  const ManualSwapCard({
    super.key,
    required this.target,
    required this.current,
    required this.busy,
    required this.onDone,
    required this.onCancel,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: AraColors.accentWarning.withValues(alpha: 0.12),
        borderRadius: BorderRadius.circular(8),
        border: Border.all(color: AraColors.accentWarning),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Install the $target filter',
            style: Theme.of(context).textTheme.titleSmall,
          ),
          const SizedBox(height: 4),
          Text(manualSwapDetail(target, current)),
          const SizedBox(height: 8),
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              FilledButton(
                onPressed: busy ? null : onDone,
                child: Text('$target is in'),
              ),
              TextButton(
                onPressed: busy ? null : onCancel,
                child: const Text('Cancel swap'),
              ),
            ],
          ),
        ],
      ),
    );
  }
}

/// One sentence explaining a pending hand swap.
String manualSwapDetail(String target, String? current) =>
    '${current == null ? 'Swap' : 'Take out $current and swap'} in $target, '
    'then confirm. A run waiting on it continues when you do.';
