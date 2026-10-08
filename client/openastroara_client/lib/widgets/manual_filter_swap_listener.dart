import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../services/equipment_device_api.dart';
import '../state/equipment/filter_wheel_state.dart';
import '../state/ws/ws_providers.dart';
import '../theme/ara_colors.dart';
import 'equipment/manual_filter_wheel_body.dart';

/// #1298 — the app-wide "install the X filter" prompt for the manual filter
/// wheel. Wraps the app shell's body, so the prompt appears on whatever screen
/// the user is on when a run (or the Imaging tab) asks for a filter.
///
/// Driven by the daemon's `equipment.filter_wheel.manual_swap` event, whose
/// payload carries the pending and installed filter names: it opens on a new
/// pending swap and closes itself when the swap is confirmed or cancelled
/// anywhere (another client, the Equipment card). One status read at launch
/// shows a swap raised while this client was closed. It deliberately does NOT
/// listen to the filter wheel status for the whole session: that would keep
/// the device's liveness poll running on every screen, which the equipment
/// notifier scopes to visible panels. "Later" closes the prompt; the Equipment
/// card and the run band keep the swap visible until it is resolved.
class ManualFilterSwapListener extends ConsumerStatefulWidget {
  final Widget child;

  const ManualFilterSwapListener({super.key, required this.child});

  @override
  ConsumerState<ManualFilterSwapListener> createState() =>
      _ManualFilterSwapListenerState();
}

class _ManualFilterSwapListenerState
    extends ConsumerState<ManualFilterSwapListener> {
  /// The pending slot the open dialog is about, or the last one the user
  /// closed with "Later" (so a repeat event does not reopen it).
  int? _shownFor;
  VoidCallback? _dismiss;

  @override
  void initState() {
    super.initState();
    ref.listenManual(wsEventsProvider, (previous, next) {
      final event = next.asData?.value;
      if (event?.type != 'equipment.filter_wheel.manual_swap') return;
      // Panels that are open re-read; with none open this is one GET.
      unawaited(ref.read(filterWheelProvider.notifier).refresh());
      final p = event!.payload;
      _onSwap(
        (p['pending_slot'] as num?)?.toInt(),
        p['pending_name'] as String?,
        p['current_name'] as String?,
      );
    });
    WidgetsBinding.instance.addPostFrameCallback((_) => _checkAtLaunch());
  }

  Future<void> _checkAtLaunch() async {
    try {
      final status = await ref.read(filterWheelProvider.future);
      if (!mounted ||
          status == null ||
          !status.manual ||
          !status.isAwaitingUser) {
        return;
      }
      _onSwap(status.pendingSlot, status.pending?.name, status.current?.name);
    } catch (_) {
      // No server or no wheel yet: the swap event will carry the prompt.
    }
  }

  void _onSwap(int? pending, String? target, String? current) {
    if (pending == null) {
      _shownFor = null;
      _dismiss?.call();
      return;
    }
    if (pending == _shownFor) return;
    _show(pending, target ?? 'Filter ${pending + 1}', current);
  }

  @override
  Widget build(BuildContext context) => widget.child;

  void _show(int position, String target, String? current) {
    _dismiss?.call();
    _shownFor = position;
    var shown = false;
    var open = false;
    var withdrawn = false;
    _dismiss = () {
      withdrawn = true;
      // Only ever pop OUR dialog: before it is on screen there is nothing to pop.
      if (shown && open && mounted) {
        open = false;
        Navigator.of(context, rootNavigator: true).pop();
      }
    };
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted || withdrawn) return;
      shown = true;
      open = true;
      unawaited(
        showDialog<void>(
          context: context,
          barrierDismissible: false,
          builder: (_) => _ManualSwapDialog(
            target: target,
            current: current,
            onDone: () => _answer(
              position,
              target,
              current,
              (n) => n.reportInstalled(position),
              "Couldn't confirm the filter",
            ),
            onCancel: () => _answer(
              position,
              target,
              current,
              (n) => n.cancelManualSwap(),
              "Couldn't cancel the swap",
            ),
          ),
        ).whenComplete(() {
          open = false;
          _dismiss = null;
        }),
      );
    });
  }

  Future<void> _answer(
    int position,
    String target,
    String? current,
    Future<bool> Function(FilterWheelNotifier n) action,
    String failure,
  ) async {
    _dismiss?.call();
    String? problem;
    try {
      final performed = await action(ref.read(filterWheelProvider.notifier));
      // Dropped by the notifier's one-action-at-a-time guard: nothing was sent.
      if (!performed) {
        problem = '$failure: another filter wheel action is still running';
      }
    } catch (e) {
      problem = '$failure: ${describeEquipmentError(e)}';
    }
    if (problem == null || !mounted) return;
    // Nothing reached the daemon, so the swap still stands: reopen it.
    _shownFor = null;
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text(problem), backgroundColor: AraColors.accentError),
    );
    _show(position, target, current);
  }
}

class _ManualSwapDialog extends StatelessWidget {
  final String target;
  final String? current;
  final VoidCallback onDone;
  final VoidCallback onCancel;

  const _ManualSwapDialog({
    required this.target,
    required this.current,
    required this.onDone,
    required this.onCancel,
  });

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      icon: const Icon(
        Icons.filter_alt_outlined,
        color: AraColors.accentWarning,
      ),
      title: Text('Install the $target filter'),
      content: Text(manualSwapDetail(target, current)),
      actions: [
        TextButton(onPressed: onCancel, child: const Text('Cancel swap')),
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Later'),
        ),
        FilledButton(onPressed: onDone, child: Text('$target is in')),
      ],
    );
  }
}
