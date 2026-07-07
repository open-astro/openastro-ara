import 'dart:developer' as developer;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'screens/app_shell.dart';
import 'screens/first_run_screen.dart';
import 'state/backup/backup_stream_state.dart';
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

/// Decides between FirstRunScreen (no saved servers yet) and AppShell
/// (at least one server saved). Per playbook §30.
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
    return saved.when(
      data: (servers) =>
          servers.isEmpty ? const FirstRunScreen() : const AppShell(),
      loading: () => const Scaffold(
        body: Center(child: CircularProgressIndicator()),
      ),
      error: (e, st) {
        // Log internal details for debug; UI shows a generic message so
        // exception text can't leak into the user-facing surface.
        developer.log('Failed to load saved servers',
            name: 'openastroara.saved_servers', error: e, stackTrace: st);
        return const Scaffold(
          body: Center(
            child: Padding(
              padding: EdgeInsets.all(24),
              child: Text('Failed to load saved servers. Please try again.'),
            ),
          ),
        );
      },
    );
  }
}
