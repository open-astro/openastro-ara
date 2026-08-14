import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/equipment_device_status.dart';
import '../../../models/filter_wheel_status.dart';
import '../../../services/equipment_device_api.dart';
import '../../../state/equipment/filter_wheel_state.dart';
import '../../../state/settings/equipment_connection_state.dart';
import '../../../state/settings/filter_wheel_labels_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/equipment/equipment_connection_card.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';
import '../../../util/friendly_error.dart';

/// §37.4 Filter Wheel panel. Shows the connected wheel's live slots (the device's
/// own names + focus offsets) with a per-slot select, via the shared connection
/// card. Slot names are the device's, not local labels (§37.4 hydration).
class EquipmentFilterWheelPanel extends ConsumerWidget {
  const EquipmentFilterWheelPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connection = ref.watch(equipmentConnectionProvider);
    final connN = ref.read(equipmentConnectionProvider.notifier);
    final status = ref.watch(filterWheelProvider);
    final notifier = ref.read(filterWheelProvider.notifier);
    final labels = ref.watch(filterWheelLabelsProvider);
    final labelsN = ref.read(filterWheelLabelsProvider.notifier);
    // While a wheel is connected its own driver slot names are authoritative and
    // shown live above, so the local "Slot labels" section is pure duplication —
    // only surface it for offline sequence authoring. Show it ONLY on a resolved
    // status that is genuinely absent/disconnected; loading and (transient) error
    // keep it hidden so a single failed poll can't flash the editor in over a
    // still-registered wheel.
    final showSlotLabels = status.maybeWhen(
      data: (s) => s == null || !s.isConnected,
      orElse: () => false,
    );

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Connection'),
        EquipmentConnectionCard<FilterWheelStatus>(
          status: status,
          deviceType: EquipmentDeviceType.filterWheel,
          deviceTypeLabel: 'filter wheel',
          emptyLabel: 'No filter wheel connected.',
          onConnect: notifier.connect,
          onDisconnect: notifier.disconnect,
          onReconnect: notifier.reconnect,
          onRetry: notifier.refresh,
          connectedBody: (context, s) => _FilterWheelBody(status: s),
        ),
        SettingsSwitchRow(
          label: 'Auto-connect on boot',
          helpKey: 'eq.auto_connect_on_boot',
          value: connection.autoConnect(EquipmentDeviceType.filterWheel),
          onChanged: (v) =>
              connN.setAutoConnect(EquipmentDeviceType.filterWheel, v),
        ),
        // Local slot labels — the user's filter names used when authoring
        // sequences offline (the §38 editor reads `filterWheelLabelsProvider`),
        // independent of the connected wheel's own names shown live above. Hidden
        // while a wheel is connected (its driver names take over) to avoid duplication.
        if (showSlotLabels) ...[
          const SettingsSectionHeader('Slot labels (for sequences)'),
          for (var slot = 1; slot <= labels.slotCount; slot++)
            EditableTextRow(
              label: 'Slot $slot',
              helpKey: slot == 1 ? 'eq.filterwheel.slot_labels' : null,
              currentValue: labels.labelAt(slot),
              getCanonical: () =>
                  ref.read(filterWheelLabelsProvider).labelAt(slot),
              // Each committed row persists to the daemon (12h.2b round-trip);
              // a failure keeps the local edit and says so — offline authoring
              // still works, the labels just won't survive a daemon-side reload.
              parse: (s) {
                labelsN.setLabel(slot, s);
                unawaited(_persistLabels(context, ref));
              },
              hint: 'Empty = unused',
            ),
        ],
      ],
    );
  }
}

/// The connected wheel's live body: the current filter + a HIG-style slot
/// list. Each row is FULLY tappable (the trailing chevron marks a selectable
/// row, the green check the active slot). While a move is in flight — from
/// here, the Imaging picker, or a sequence — the pending row shows a spinner
/// and the rest are disabled: the same busy semantics as the Imaging picker,
/// so a wide-screen layout never requires aiming at a far-right button.
class _FilterWheelBody extends ConsumerStatefulWidget {
  final FilterWheelStatus status;
  const _FilterWheelBody({required this.status});

  @override
  ConsumerState<_FilterWheelBody> createState() => _FilterWheelBodyState();
}

class _FilterWheelBodyState extends ConsumerState<_FilterWheelBody> {
  /// The slot this panel commanded, while awaiting the landing — instant
  /// feedback without waiting for the 15 s live poll.
  int? _pendingTarget;
  bool _sawMove = false;
  Timer? _reconcileTimer;
  int _reconcileTicks = 0;
  Timer? _stallTimer;

  @override
  void dispose() {
    _reconcileTimer?.cancel();
    _stallTimer?.cancel();
    super.dispose();
  }

  void _clearPending() {
    _reconcileTimer?.cancel();
    _stallTimer?.cancel();
    _pendingTarget = null;
    _sawMove = false;
  }

  /// Fast re-reads (1.5 s cadence, bounded) while a landing is pending, so
  /// the check/spinner moves within a couple of seconds instead of at the
  /// next 15 s live poll.
  void _startReconcile() {
    _reconcileTimer?.cancel();
    _reconcileTicks = 0;
    _reconcileTimer = Timer.periodic(const Duration(milliseconds: 1500), (_) {
      if (!mounted || _pendingTarget == null) {
        _reconcileTimer?.cancel();
        return;
      }
      if (_reconcileTicks >= 40 || !ref.read(filterWheelProvider).maybeWhen(
          data: (w) => w != null && w.isConnected, orElse: () => false)) {
        _reconcileTimer?.cancel();
        return;
      }
      _reconcileTicks++;
      ref.read(filterWheelProvider.notifier).refresh();
    });
  }

  /// Backstop: a command that never starts the wheel errors at 10 s; a
  /// started-but-unreported landing gets a hard cap before the spinner
  /// clears. Re-checks [_sawMove] at the 10 s mark — a slow driver may not
  /// have reported `moving` yet (the 1.5 s reconcile + the daemon's own poll
  /// cadence gate that transition), so only a genuinely-never-started move
  /// errors; a started one re-arms the long cap.
  void _startStall() {
    _stallTimer?.cancel();
    // All paths clear via setState so the row spinner + disabled list actually
    // rebuild — clearing the fields alone would leave the busy UI frozen until
    // some unrelated provider update happens.
    _stallTimer = Timer(const Duration(seconds: 10), () {
      if (!mounted || _pendingTarget == null) return;
      if (_sawMove) {
        _stallTimer = Timer(const Duration(seconds: 45), _clearPendingUi);
      } else {
        // Maybe it started but we haven't SEEN it yet — one more fast look
        // before declaring the command dead.
        ref.read(filterWheelProvider.notifier).refresh().then((_) {
          if (!mounted || _pendingTarget == null) return;
          if (_sawMove) {
            _stallTimer = Timer(const Duration(seconds: 45), _clearPendingUi);
          } else {
            _clearPendingUi();
          }
        });
      }
    });
  }

  /// [_clearPending] + a rebuild — see [_startStall].
  void _clearPendingUi() {
    if (!mounted) return;
    setState(_clearPending);
  }

  @override
  Widget build(BuildContext context) {
    // Reconcile the commanded landing, and pick up moves from other sources.
    ref.listen(filterWheelProvider, (prev, next) {
      final w = next.maybeWhen(data: (s) => s, orElse: () => null);
      final pending = _pendingTarget;
      if (w != null && w.isMoving) {
        _sawMove = true;
      } else if (pending != null && w != null && !w.isMoving) {
        // Parked: landed on the target, or settled elsewhere after moving.
        if (w.currentSlot == pending || _sawMove) {
          if (mounted) {
            setState(_clearPending);
          } else {
            _clearPending();
          }
        }
      } else if (w == null || !w.isConnected) {
        if (mounted) {
          setState(_clearPending);
        } else {
          _clearPending();
        }
      }
    });

    final status = widget.status;
    if (status.isConnecting) return const Text('Reading…');
    if (status.connectionState == EquipmentConnectionState.error) {
      return const Row(
        children: [
          Icon(Icons.error_outline, color: AraColors.accentError, size: 20),
          SizedBox(width: 8),
          Expanded(child: Text('Filter wheel read failed — check the device.')),
        ],
      );
    }
    final current = status.current;
    final pending = _pendingTarget;
    final busy = status.isMoving || pending != null;
    final currentText = pending != null
        ? 'Changing…'
        : (current != null
            ? '${current.name.isEmpty ? 'Slot ${current.position}' : current.name} '
                  '(slot ${current.position})'
            : (status.isMoving ? 'Changing…' : '—'));
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            const Expanded(child: Text('Current filter')),
            if (pending != null || status.isMoving)
              const Padding(
                padding: EdgeInsets.only(right: 6),
                child: SizedBox(
                  width: 14,
                  height: 14,
                  child: CircularProgressIndicator(strokeWidth: 2),
                ),
              ),
            Text(currentText),
          ],
        ),
        const Divider(height: 20, color: AraColors.border),
        if (status.slots.isEmpty)
          const Text('This filter wheel reports no slots.')
        else
          // Focus offsets are only meaningful when the driver actually reports
          // them; most wheels (e.g. ZWO EFW) report 0 for every slot, so hide the
          // column entirely rather than show a row of "focus offset 0".
          for (final slot in status.slots)
            _SlotRow(
              slot: slot,
              isCurrent: slot.position == status.currentSlot,
              busy: busy,
              pending: slot.position == pending,
              showOffset: status.slots.any((s) => s.focusOffset != 0),
              onSelect: () => _select(context, slot),
            ),
      ],
    );
  }

  Future<void> _select(BuildContext context, FilterSlot slot) async {
    final messenger = ScaffoldMessenger.of(context);
    // INSTANT busy feedback — the pending row spins and the rest disable
    // immediately, not when the first poll reports the wheel turning.
    setState(() {
      _pendingTarget = slot.position;
      _sawMove = false;
    });
    _startReconcile();
    _startStall();
    try {
      final performed = await ref
          .read(filterWheelProvider.notifier)
          .changeFilter(slot.position);
      if (!performed) {
        if (mounted) setState(_clearPending);
        messenger.showSnackBar(
          const SnackBar(content: Text('Another action is still in progress.')),
        );
        return;
      }
    } catch (e) {
      if (mounted) setState(_clearPending);
      messenger.showSnackBar(
        SnackBar(
          content: Text("Couldn't change filter: ${describeEquipmentError(e)}"),
          backgroundColor: AraColors.accentError,
        ),
      );
    }
  }
}

/// One HIG-style row: the whole row is the tap target, the active slot gets a
/// green check, selectable rows a trailing chevron, and the pending row a
/// spinner (with the rest disabled while a move is in flight).
class _SlotRow extends StatelessWidget {
  final FilterSlot slot;
  final bool isCurrent;
  final bool busy;
  final bool pending;
  // Whether to show the focus-offset column (any slot has a non-zero offset).
  final bool showOffset;
  final VoidCallback onSelect;
  const _SlotRow({
    required this.slot,
    required this.isCurrent,
    required this.busy,
    required this.pending,
    required this.showOffset,
    required this.onSelect,
  });

  @override
  Widget build(BuildContext context) {
    final secondary = Theme.of(
      context,
    ).textTheme.bodySmall?.copyWith(color: AraColors.textSecondary);
    // Same empty-name fallback the "Current filter" header uses, so an unnamed
    // slot reads consistently in both places.
    final name = slot.name.isEmpty ? 'Slot ${slot.position}' : slot.name;
    final selectable = !busy && !isCurrent;
    // transparent (not null): Material's canvas default would paint the
    // theme's canvasColor (#1A1A1A) instead of blending into the card's
    // bgPanel (#262626) behind every non-pending row.
    return Material(
      color: pending
          ? AraColors.accentConnected.withValues(alpha: 0.08)
          : Colors.transparent,
      child: InkWell(
        onTap: selectable ? onSelect : null,
        borderRadius: BorderRadius.circular(8),
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
          child: Row(
            children: [
              // The driver's own slot number (0-indexed, matching "Current
              // filter (slot N)").
              SizedBox(
                width: 26,
                child: Text(
                  '${slot.position}',
                  textAlign: TextAlign.center,
                  style: secondary?.copyWith(
                    color: isCurrent
                        ? AraColors.accentConnected
                        : AraColors.textSecondary,
                  ),
                ),
              ),
              const SizedBox(width: 10),
              Expanded(child: Text(name)),
              if (showOffset)
                Padding(
                  padding: const EdgeInsets.only(right: 12),
                  child: Text('focus offset ${slot.focusOffset}',
                      style: secondary),
                ),
              // Trailing affordance: spinner on the pending row, green check
              // on the active row, chevron on selectable rows.
              if (pending)
                const SizedBox(
                  width: 18,
                  height: 18,
                  child: CircularProgressIndicator(strokeWidth: 2),
                )
              else if (isCurrent)
                const Icon(Icons.check_circle,
                    size: 20, color: AraColors.accentConnected)
              else
                Icon(
                  Icons.chevron_right,
                  size: 20,
                  color: busy
                      ? AraColors.textDisabled
                      : AraColors.textSecondary,
                ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Persist the slot labels to the daemon, surfacing a failure as a SnackBar
/// (the local edit is kept either way — offline authoring keeps working).
Future<void> _persistLabels(BuildContext context, WidgetRef ref) async {
  try {
    await ref.read(filterWheelLabelsProvider.notifier).persistToServer();
  } catch (e) {
    if (context.mounted) {
      ScaffoldMessenger.of(
        context,
      ).showSnackBar(SnackBar(content: Text(friendlyError(e, action: 'save the slot labels'))));
    }
  }
}
