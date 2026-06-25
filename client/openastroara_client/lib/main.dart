import 'dart:developer' as developer;
import 'dart:ui' show AppExitResponse;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:webview_cef/webview_cef.dart';

import 'screens/app_shell.dart';
import 'screens/first_run_screen.dart';
import 'state/saved_server_state.dart';
import 'theme/ara_theme.dart';

void main() {
  runApp(const ProviderScope(child: OpenAstroAraApp()));
}

class OpenAstroAraApp extends StatefulWidget {
  const OpenAstroAraApp({super.key});

  @override
  State<OpenAstroAraApp> createState() => _OpenAstroAraAppState();
}

class _OpenAstroAraAppState extends State<OpenAstroAraApp> {
  AppLifecycleListener? _lifecycle;

  @override
  void initState() {
    super.initState();
    // Shut CEF down cleanly before the OS tears the process down. The Sky Atlas
    // webview (webview_cef) runs CEF multi-process (browser + helper subprocesses);
    // if exit() runs while CEF's GPU/renderer threads are still live, teardown
    // segfaults. onExitRequested intercepts the platform terminate request, lets us
    // quit CEF (CloseAllBrowsers + CefShutdown — a no-op if the atlas was never
    // opened), then allows exit. See the §36 Sky Atlas notes.
    _lifecycle = AppLifecycleListener(
      onExitRequested: () async {
        try {
          // Bound the wait: if CEF teardown ever hangs, a quit must not wedge the
          // whole app on exit. 3s is generous for CloseAllBrowsers + CefShutdown;
          // past that we log and exit anyway (the OS reclaims the process regardless).
          await WebviewManager().quit().timeout(const Duration(seconds: 3));
        } catch (e, st) {
          developer.log('CEF shutdown on exit failed or timed out',
              name: 'openastroara.webview', error: e, stackTrace: st);
        }
        return AppExitResponse.exit;
      },
    );
  }

  @override
  void dispose() {
    _lifecycle?.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'OpenAstro Ara WILMA',
      theme: buildAraTheme(),
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
