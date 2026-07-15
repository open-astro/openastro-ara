import 'dart:developer' as developer;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'screens/app_shell.dart';
import 'screens/first_run_screen.dart';
import 'screens/launch_profile_screen.dart';
import 'screens/offline_launch_screen.dart';
import 'services/window_mode.dart';
import 'state/backup/backup_stream_state.dart';
import 'state/launch_gate_state.dart';
import 'state/saved_server_state.dart';
import 'theme/ara_theme.dart';
import 'widgets/sky_atlas/linux_planetarium_overlay.dart';

void main() {
  runApp(const ProviderScope(child: OpenAstroAraApp()));
}

/// The planetarium renders in the platform's native webview (`webview_all`), which
/// the OS tears down with the process — so there's no CEF/Chromium subprocess tree
/// to shut down on exit, and the app needs no exit-lifecycle hook.
class OpenAstroAraApp extends StatelessWidget {
  const OpenAstroAraApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'OpenAstro Ara WILMA',
      theme: buildAraTheme(),
      // The diagonal DEBUG ribbon overlaps top-right app-bar actions (e.g. the
      // first-run Rescan button); it adds nothing for users, so hide it.
      debugShowCheckedModeBanner: false,
      // The Linux planetarium overlay subscribes to this so the native GTK
      // webview hides when a route is pushed over the shell (no-op elsewhere).
      navigatorObservers: [planetariumRouteObserver],
      home: const _RootRouter(),
    );
  }
}

/// §30.1 launch sequence: FirstRunScreen (no saved servers yet) → the
/// LaunchProfileScreen profile box (always shown, §30.2/§30.3) → AppShell
/// once the user clicks [Image] and the launch gate passes. "Plan offline"
/// (§2 — WILMA is a planning workstation, not a thin client) bypasses both
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
    final saved = ref.watch(savedServersProvider);
    final gatePassed = ref.watch(profileGatePassedProvider);
    final offline = ref.watch(offlineModeProvider);
    // The launchpad (server connect + profile box) runs in a compact window;
    // entering the shell maximizes it. Post-frame + idempotent, so this is a
    // no-op until the routed-to surface actually changes.
    final inShell = gatePassed &&
        (offline || (saved.asData?.value.isNotEmpty ?? false));
    final windowMode = ref.read(windowModeProvider);
    WidgetsBinding.instance.addPostFrameCallback((_) => windowMode
        .set(inShell ? WindowMode.workstation : WindowMode.launchpad));
    return saved.when(
      data: (servers) => offline
          // Offline still gets a profile step: pick which CACHED profile to
          // plan with (seeding the settings notifiers) before the shell.
          ? (gatePassed ? const AppShell() : const OfflineLaunchScreen())
          : servers.isEmpty
              ? const FirstRunScreen()
              : gatePassed
                  ? const AppShell()
                  : const LaunchProfileScreen(),
      loading: () => const Scaffold(
        body: Center(child: CircularProgressIndicator()),
      ),
      error: (e, st) {
        // Log internal details for debug; UI shows a generic message so
        // exception text can't leak into the user-facing surface.
        developer.log('Failed to load saved servers',
            name: 'openastroara.saved_servers', error: e, stackTrace: st);
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
                  TextButton.icon(
                    onPressed: () =>
                        ref.read(offlineModeProvider.notifier).enter(),
                    icon: const Icon(Icons.cloud_off_outlined, size: 18),
                    label: const Text('Plan offline'),
                  ),
                ],
              ),
            ),
          ),
        );
      },
    );
  }
}
