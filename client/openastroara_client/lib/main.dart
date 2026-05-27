import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'screens/app_shell.dart';
import 'screens/first_run_screen.dart';
import 'state/saved_server_state.dart';
import 'theme/ara_theme.dart';

void main() {
  runApp(const ProviderScope(child: OpenAstroAraApp()));
}

class OpenAstroAraApp extends StatelessWidget {
  const OpenAstroAraApp({super.key});

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
      error: (e, _) => Scaffold(
        body: Center(
          child: Padding(
            padding: const EdgeInsets.all(24),
            child: Text('Failed to load saved servers: $e'),
          ),
        ),
      ),
    );
  }
}
