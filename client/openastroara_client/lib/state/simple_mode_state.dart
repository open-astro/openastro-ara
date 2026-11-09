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

/// Whether the Simple Mode shell shows: the feature is on AND the client
/// chose Simple Mode.
final simpleModeActiveProvider = Provider<bool>(
  (ref) =>
      (ref.watch(simpleModeFeatureProvider).value ?? false) &&
      (ref.watch(simpleModeProvider).value ?? false),
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
