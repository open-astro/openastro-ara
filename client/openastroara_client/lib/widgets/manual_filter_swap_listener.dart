import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/filter_wheel_status.dart';
import '../services/equipment_device_api.dart';
import '../state/equipment/filter_wheel_state.dart';
import '../state/ws/ws_providers.dart';
import '../theme/ara_colors.dart';
import 'equipment/manual_filter_wheel_body.dart';

/// #1298 — the app-wide "install the X filter" prompt for the manual filter
/// wheel. Wraps the app shell's body, so the prompt appears on whatever screen
/// the user is on when a run (or the Imaging tab) asks for a filter.
///
/// The daemon's `equipment.filter_wheel.manual_swap` event triggers a re-read;
/// the dialog follows the wheel's `pending_slot`: it opens when a new swap is
/// pending and closes itself when the swap is confirmed or cancelled anywhere
/// (another client, the Equipment card). "Later" closes it without an answer;
/// the Equipment card keeps the swap visible until it is resolved.
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
  /// closed with "Later" (so a re-read does not reopen it).
  int? _shownFor;
  VoidCallback? _dismiss;

  @override
  void initState() {
    super.initState();
    ref.listenManual(wsEventsProvider, (previous, next) {
      final event = next.asData?.value;
      if (event?.type != 'equipment.filter_wheel.manual_swap') return;
      unawaited(ref.read(filterWheelProvider.notifier).refresh());
    });
    // fireImmediately: a swap raised while this client was closed is shown on
    // the first read after launch.
    ref.listenManual<AsyncValue<FilterWheelStatus?>>(
      filterWheelProvider,
      (previous, next) => _onStatus(next.asData?.value),
      fireImmediately: true,
    );
  }

  void _onStatus(FilterWheelStatus? status) {
    final pending = status != null && status.manual && status.isAwaitingUser
        ? status.pendingSlot
        : null;
    if (pending == null) {
      _shownFor = null;
      _dismiss?.call();
      return;
    }
    if (pending == _shownFor) return;
    _show(status!);
  }

  @override
  Widget build(BuildContext context) => widget.child;

  void _show(FilterWheelStatus status) {
    _dismiss?.call();
    final target = status.pending;
    if (target == null) return;
    _shownFor = target.position;
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
    // After this frame: the listener can fire during build.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted || withdrawn) return;
      shown = true;
      open = true;
      unawaited(
        showDialog<void>(
          context: context,
          barrierDismissible: false,
          builder: (_) => _ManualSwapDialog(
            target: target.name,
            current: status.current?.name,
            onDone: () => _answer(
              (n) => n.reportInstalled(target.position),
              "Couldn't confirm the filter",
            ),
            onCancel: () => _answer(
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
    Future<bool> Function(FilterWheelNotifier n) action,
    String failure,
  ) async {
    _dismiss?.call();
    try {
      await action(ref.read(filterWheelProvider.notifier));
    } catch (e) {
      if (!mounted) return;
      _shownFor = null; // let the next read reopen it
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text('$failure: ${describeEquipmentError(e)}'),
          backgroundColor: AraColors.accentError,
        ),
      );
    }
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
