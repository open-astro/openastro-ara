import 'dart:async';
import 'dart:developer' as developer;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'app_version.dart';
import 'screens/app_shell.dart';
import 'screens/first_run_screen.dart';
import 'screens/launch_profile_screen.dart';
import 'screens/offline_launch_screen.dart';
import 'widgets/plan_offline_button.dart';
import 'services/client_error_handlers.dart';
import 'services/client_error_log.dart';
import 'services/window_mode.dart';
import 'state/client_gps_state.dart';
import 'state/backup/backup_stream_state.dart';
import 'state/launch_gate_state.dart';
import 'state/sky_atlas/dso_catalog_state.dart';
import 'state/saved_server_state.dart';
import 'theme/ara_theme.dart';
import 'widgets/night_filter.dart';
import 'widgets/night_hotkey.dart';
import 'widgets/sky_atlas/linux_planetarium_overlay.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  // #1111 — the client's own error log. Installed before runApp so the very
  // first build failure is on record; a debug app launched via `open` has
  // no stderr, and a release build would otherwise leave no trace at all.
  final errorLog = ClientErrorLog();
  ClientErrorHandlers.install(errorLog);
  unawaited(_noteLaunch(errorLog));
  runApp(
    ProviderScope(
      overrides: [clientErrorLogProvider.overrideWithValue(errorLog)],
      child: const OpenAstroAraApp(),
    ),
  );
}

/// First entry of every run. The VM service URL is included when there is
/// one (debug/profile) — with it, a `flutter attach` or DevTools can reach a
/// debug app that was launched from Finder rather than a terminal.
Future<void> _noteLaunch(ClientErrorLog log) async {
  var vm = '';
  try {
    final info = await developer.Service.getInfo();
    if (info.serverUri != null) vm = ' vm service: ${info.serverUri}';
  } catch (_) {
    // Not available on this platform/mode — the launch line still lands.
  }
  await log.note('launch$vm');
}

/// The planetarium renders in the platform's native webview (`webview_all`), which
/// the OS tears down with the process — so there's no CEF/Chromium subprocess tree
/// to shut down on exit, and the app needs no exit-lifecycle hook.
///
/// Deliberately a [StatelessWidget] with nothing to watch (#1111): a rebuild
/// here rebuilds `MaterialApp` → `WidgetsApp` → Navigator with whatever theme
/// object it is handed, so the root must not depend on anything that changes
/// at runtime. Night mode is applied by [NightFilter] inside `builder`, the
/// hotkey by [NightHotkey] around `home`, and the GPS loop is kept alive from
/// `_RootRouter` with a listen, never a watch.
class OpenAstroAraApp extends StatelessWidget {
  /// [home] replaces the launch router; tests use it to put a plain page under
  /// the real root (theme, night filter, hotkey) without the router's providers.
  const OpenAstroAraApp({super.key, @visibleForTesting this.home});

  /// Test seam only; production always routes through `_RootRouter`.
  @visibleForTesting
  final Widget? home;

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'OpenAstro Ara',
      theme: araTheme,
      // The diagonal DEBUG ribbon overlaps top-right app-bar actions (e.g. the
      // first-run Rescan button); it adds nothing for users, so hide it.
      debugShowCheckedModeBanner: false,
      // The Linux planetarium overlay subscribes to this so the native GTK
      // webview hides when a route is pushed over the shell (no-op elsewhere).
      navigatorObservers: [planetariumRouteObserver],
      // Always the same widget type above the Navigator, on or off — see
      // NightFilter for why the Navigator's parent must never change.
      builder: (context, child) =>
          NightFilter(child: child ?? const SizedBox.shrink()),
      home: NightHotkey(child: home ?? const _RootRouter()),
    );
  }
}

/// §30.1 launch sequence: FirstRunScreen (no saved servers yet) → the
/// LaunchProfileScreen profile box (always shown, §30.2/§30.3) → AppShell
/// once the user clicks [Image] and the launch gate passes. "Plan offline"
/// (§2 — the client is a planning workstation, not a thin viewer) bypasses both
/// gates and enters the shell with no server for the session.
class _RootRouter extends ConsumerWidget {
  const _RootRouter();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    // Materialize the §44 backup-stream controller at the root so a
    // persisted-enabled stream resumes at launch without the user having to
    // open Settings → Storage first (listen, not watch — per-frame sync
    // counters must not rebuild the whole app).
    ref.listen(backupStreamProvider, (previous, next) {});
    // Native window title: "OpenAstro Ara 0.0.1a" — the runners' build-time
    // titles carry no version, so stamp it as soon as PackageInfo resolves.
    ref.listen(appDisplayVersionProvider, (previous, next) {
      final version = next.asData?.value;
      if (version != null) {
        unawaited(ref.read(windowModeProvider).setTitle('$kAppName $version'));
      }
    });
    // Materialize the DSO-catalog mirror sync (fetch-on-connect) at the root
    // so offline planning has the full catalog after any connected session.
    ref.listen(dsoCatalogSyncProvider, (previous, next) {});
    // §31 — keep the "GPS on this computer" loop alive for the app's lifetime
    // (a no-op while the pref is off). A LISTEN: it used to be a watch in the
    // app root, and every sync (busy on, busy off) rebuilt MaterialApp and
    // re-themed the whole tree (#1111).
    ref.listen(clientGpsProvider, (previous, next) {});
    final saved = ref.watch(savedServersProvider);
    final gatePassed = ref.watch(profileGatePassedProvider);
    final offline = ref.watch(offlineModeProvider);
    // §30 — the shell's Launchpad action asks for the SERVER chooser, not just
    // the profile box: with servers saved the flow would otherwise resume one
    // step in, and "back to the launchpad" should mean screen one.
    final serverChooserRequested = ref.watch(serverChooserRequestedProvider);
    final Widget routed = saved.when(
      data: (servers) => offline
          // Offline still gets a profile step: pick which CACHED profile to
          // plan with (seeding the settings notifiers) before the shell.
          ? (gatePassed ? const AppShell() : const OfflineLaunchScreen())
          : servers.isEmpty || serverChooserRequested
          ? const FirstRunScreen()
          : gatePassed
          ? const AppShell()
          : const LaunchProfileScreen(),
      loading: () =>
          const Scaffold(body: Center(child: CircularProgressIndicator())),
      error: (e, st) {
        // Log internal details for debug; UI shows a generic message so
        // exception text can't leak into the user-facing surface.
        developer.log(
          'Failed to load saved servers',
          name: 'openastroara.saved_servers',
          error: e,
          stackTrace: st,
        );
        // A storage-read failure must not dead-end the app — offline planning
        // stays reachable from here too (§2: offline is never blocked).
        return Scaffold(
          body: Center(
            child: Padding(
              padding: const EdgeInsets.all(24),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  const Text('Failed to load saved servers. Please try again.'),
                  const SizedBox(height: 12),
                  // Self-gated on a cached profile existing.
                  const PlanOfflineButton(),
                ],
              ),
            ),
          ),
        );
      },
    );
    // The launchpad (server connect + profile box) runs in a compact window;
    // the shell maximizes it. Derived from the WIDGET the router actually
    // chose — not a hand-copied predicate that could drift from the branches
    // above (review #846). Post-frame + idempotent, so it's a no-op until the
    // routed-to surface really changes.
    final inShell = routed is AppShell;
    final windowMode = ref.read(windowModeProvider);
    WidgetsBinding.instance.addPostFrameCallback(
      (_) => windowMode.set(
        inShell ? WindowMode.workstation : WindowMode.launchpad,
      ),
    );
    return routed;
  }
}
