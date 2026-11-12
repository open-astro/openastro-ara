import 'package:flutter/foundation.dart'
    show TargetPlatform, defaultTargetPlatform;
import 'package:flutter/widgets.dart'
    show WidgetsBindingObserver, WidgetsFlutterBinding;
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../services/simple_mode_prefs_service.dart';

/// The prefs file backing [simpleModeProvider]; overridden in tests.
final simpleModePrefsProvider = Provider<SimpleModePrefsService>(
  (ref) => SimpleModePrefsService(),
);

/// Persisted "Simple Mode" UI preference (off by default: the full UI stays
/// the desktop default). The root router shows `SimpleShell` instead of
/// `AppShell` while it is on; switching resets nothing on the daemon.
class SimpleModeController extends AsyncNotifier<bool> {
  /// A switch that landed before the initial prefs read finished, so the
  /// read's late result can't undo it (same race as NightModeController).
  bool? _pending;

  @override
  Future<bool> build() async {
    final loaded = await ref.read(simpleModePrefsProvider).load();
    return _pending ?? loaded;
  }

  Future<void> set(bool on) async {
    _pending = on;
    state = AsyncData(on);
    await ref.read(simpleModePrefsProvider).save(on);
  }
}

final simpleModeProvider = AsyncNotifierProvider<SimpleModeController, bool>(
  SimpleModeController.new,
);

/// The prefs file backing [simpleModeFeatureProvider]; overridden in tests.
final simpleModeFeaturePrefsProvider = Provider<SimpleModePrefsService>(
  (ref) => SimpleModePrefsService(fileName: 'simple_mode_feature.json'),
);

/// Experimental feature toggle (Settings → System → Experimental): Simple
/// Mode is only reachable while this is on. Off by default and per client;
/// turning it off drops back to the full UI without touching the mode pref.
class SimpleModeFeatureController extends AsyncNotifier<bool> {
  bool? _pending;

  @override
  Future<bool> build() async {
    final loaded = await ref.read(simpleModeFeaturePrefsProvider).load();
    return _pending ?? loaded;
  }

  Future<void> set(bool on) async {
    _pending = on;
    state = AsyncData(on);
    await ref.read(simpleModeFeaturePrefsProvider).save(on);
  }
}

final simpleModeFeatureProvider =
    AsyncNotifierProvider<SimpleModeFeatureController, bool>(
      SimpleModeFeatureController.new,
    );

/// The platform Companion Mode reads; overridden in tests.
final companionPlatformProvider = Provider<TargetPlatform>(
  (ref) => defaultTargetPlatform,
);

/// The view's shortest side in logical px; overridden in tests. Recomputed
/// whenever the view metrics change (split-screen, foldables, Slide Over). A
/// view that is missing or reports a zero size (Android and iOS can before the
/// first frame) is "unknown" and reads as infinity, so a tablet is never taken
/// for a phone.
final companionShortestSideProvider = Provider<double>((ref) {
  final binding = WidgetsFlutterBinding.ensureInitialized();
  final observer = _MetricsObserver(ref.invalidateSelf);
  binding.addObserver(observer);
  ref.onDispose(() => binding.removeObserver(observer));
  final view = binding.platformDispatcher.views.firstOrNull;
  if (view == null) return double.infinity;
  final logical = view.physicalSize / view.devicePixelRatio;
  final shortest = logical.shortestSide;
  return shortest > 0 ? shortest : double.infinity;
});

class _MetricsObserver with WidgetsBindingObserver {
  _MetricsObserver(this._onChange);
  final void Function() _onChange;
  @override
  void didChangeMetrics() => _onChange();
}

/// Companion Mode: a phone (iOS or Android, shortest side under 600 logical
/// px). Simple Mode is forced on and every way into the full UI is gone. It
/// reads no preference and writes none.
final companionModeProvider = Provider<bool>((ref) {
  final platform = ref.watch(companionPlatformProvider);
  final phone =
      platform == TargetPlatform.iOS || platform == TargetPlatform.android;
  return phone && ref.watch(companionShortestSideProvider) < 600;
});

/// Whether the Simple Mode shell shows: Companion Mode, or the feature is on
/// AND the client chose Simple Mode.
final simpleModeActiveProvider = Provider<bool>(
  (ref) =>
      ref.watch(companionModeProvider) ||
      ((ref.watch(simpleModeFeatureProvider).value ?? false) &&
          (ref.watch(simpleModeProvider).value ?? false)),
);

/// The prefs file backing [simplePermanentPierProvider]; overridden in tests.
final simplePermanentPierPrefsProvider = Provider<SimpleModePrefsService>(
  (ref) => SimpleModePrefsService(fileName: 'simple_permanent_pier.json'),
);

/// The mount stays polar aligned (a permanent pier), so Simple Mode's
/// Preflight skips polar alignment. Off by default.
// shortcut: saved on this client, not in the rig's profile; move it to the
// profile once the daemon carries the setting (it belongs to the rig).
class SimplePermanentPierController extends AsyncNotifier<bool> {
  bool? _pending;

  @override
  Future<bool> build() async {
    final loaded = await ref.read(simplePermanentPierPrefsProvider).load();
    return _pending ?? loaded;
  }

  Future<void> set(bool on) async {
    _pending = on;
    state = AsyncData(on);
    await ref.read(simplePermanentPierPrefsProvider).save(on);
  }
}

final simplePermanentPierProvider =
    AsyncNotifierProvider<SimplePermanentPierController, bool>(
      SimplePermanentPierController.new,
    );
