import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../state/equipment/camera_state.dart';
import '../state/equipment/dome_state.dart';
import '../state/equipment/filter_wheel_state.dart';
import '../state/equipment/flat_panel_state.dart';
import '../state/equipment/focuser_state.dart';
import '../state/equipment/mount_state.dart';
import '../state/equipment/rotator_state.dart';
import '../state/equipment/safety_monitor_state.dart';
import '../state/equipment/switch_state.dart';
import '../state/equipment/weather_state.dart';
import '../state/simple_mode_state.dart';

/// §41.3 — a phone's socket can sleep while the app is in the background, so
/// what was last shown may be stale. Re-reads every equipment status the app
/// holds from its REST endpoint. Overridable in tests.
final rigSnapshotRefreshProvider = Provider<Future<void> Function()>((ref) {
  return () async {
    final reads = <Future<void>>[];
    // Only providers already built: a snapshot of what the screen holds.
    void again(bool exists, Future<void> Function() refresh) {
      if (exists) reads.add(refresh().catchError((Object _) {}));
    }

    again(
      ref.read(cameraStatusProvider.exists),
      () => ref.read(cameraStatusProvider.notifier).refresh(),
    );
    again(
      ref.read(mountProvider.exists),
      () => ref.read(mountProvider.notifier).refresh(),
    );
    again(
      ref.read(focuserProvider.exists),
      () => ref.read(focuserProvider.notifier).refresh(),
    );
    again(
      ref.read(filterWheelProvider.exists),
      () => ref.read(filterWheelProvider.notifier).refresh(),
    );
    again(
      ref.read(rotatorProvider.exists),
      () => ref.read(rotatorProvider.notifier).refresh(),
    );
    again(
      ref.read(domeProvider.exists),
      () => ref.read(domeProvider.notifier).refresh(),
    );
    again(
      ref.read(flatPanelProvider.exists),
      () => ref.read(flatPanelProvider.notifier).refresh(),
    );
    again(
      ref.read(switchListProvider.exists),
      () => ref.read(switchListProvider.notifier).refresh(),
    );
    again(
      ref.read(weatherProvider.exists),
      () => ref.read(weatherProvider.notifier).refresh(),
    );
    again(
      ref.read(safetyMonitorProvider.exists),
      () => ref.read(safetyMonitorProvider.notifier).refresh(),
    );
    await Future.wait(reads);
  };
});

/// True from the app's return to the foreground until the snapshot read lands:
/// live values wait on it ("nothing stale is shown").
final rigResumingProvider = NotifierProvider<_ResumingNotifier, bool>(
  _ResumingNotifier.new,
);

class _ResumingNotifier extends Notifier<bool> {
  @override
  bool build() => false;
  void set(bool v) {
    if (ref.mounted) state = v;
  }
}

/// §41.3 — on `AppLifecycleState.resumed` in Companion Mode, refresh the rig
/// state from the REST snapshot.
class CompanionResumeRefreshListener extends ConsumerStatefulWidget {
  const CompanionResumeRefreshListener({super.key, required this.child});

  final Widget child;

  @override
  ConsumerState<CompanionResumeRefreshListener> createState() =>
      _CompanionResumeRefreshListenerState();
}

class _CompanionResumeRefreshListenerState
    extends ConsumerState<CompanionResumeRefreshListener>
    with WidgetsBindingObserver {
  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state != AppLifecycleState.resumed ||
        !ref.read(companionModeProvider)) {
      return;
    }
    unawaited(_refresh());
  }

  Future<void> _refresh() async {
    final resuming = ref.read(rigResumingProvider.notifier)..set(true);
    try {
      await ref.read(rigSnapshotRefreshProvider)();
    } finally {
      resuming.set(false);
    }
  }

  @override
  Widget build(BuildContext context) => widget.child;
}
