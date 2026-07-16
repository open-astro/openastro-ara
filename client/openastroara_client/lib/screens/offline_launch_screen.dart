import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../state/launch_gate_state.dart';
import '../state/profile_cache_state.dart';
import '../theme/ara_colors.dart';

/// §2 offline planning — the profile choice for a server-less session. Shows
/// the profiles cached from past connected sessions; picking one seeds the
/// planning settings notifiers (optics, imaging defaults, autofocus, site,
/// filter set) with that profile's last-known gear so offline drafts are built
/// from the user's real settings, then passes the launch gate into the shell.
///
/// A machine that never connected has an empty cache — planning proceeds with
/// client defaults and says so, rather than blocking the session.
class OfflineLaunchScreen extends ConsumerStatefulWidget {
  const OfflineLaunchScreen({super.key});

  @override
  ConsumerState<OfflineLaunchScreen> createState() =>
      _OfflineLaunchScreenState();
}

class _OfflineLaunchScreenState extends ConsumerState<OfflineLaunchScreen> {
  /// Local pick; null = follow the cached active id.
  String? _pickedId;
  bool _entering = false;

  @override
  Widget build(BuildContext context) {
    final async = ref.watch(cachedProfilesProvider);
    return Scaffold(
      body: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 420),
          child: Card(
            child: Padding(
              padding: const EdgeInsets.all(24),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Text('OpenAstro Ara',
                      style: Theme.of(context).textTheme.headlineSmall),
                  const SizedBox(height: 4),
                  const Text('Planning offline — no server connected',
                      style: TextStyle(color: AraColors.textSecondary)),
                  const SizedBox(height: 24),
                  ...async.when(
                    loading: () => const [
                      Center(child: CircularProgressIndicator()),
                    ],
                    // A cache read never throws (the service degrades to
                    // empty), but the branch is required — treat it as empty.
                    error: (_, _) => _emptyCache(),
                    data: (cached) => cached.profiles.isEmpty
                        ? _emptyCache()
                        : _picker(cached),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }

  // Near-unreachable: every "Plan offline" entry is gated on a cached profile
  // existing (PlanOfflineButton). Kept as a safety net for a cache that was
  // deleted between the gate check and this screen — offline planning without
  // a profile is deliberately NOT offered (maintainer call), only the way back.
  List<Widget> _emptyCache() => [
        const Text(
          'No profiles are cached on this device — offline planning needs '
          'one. Connect to your server once and set up a profile first.',
          textAlign: TextAlign.center,
          style: TextStyle(color: AraColors.textSecondary),
        ),
        const SizedBox(height: 16),
        FilledButton(
          onPressed: () => ref.read(offlineModeProvider.notifier).exit(),
          child: const Text('Back to server setup'),
        ),
      ];

  List<Widget> _picker(CachedProfileList cached) {
    final ids = {for (final p in cached.profiles) p.id};
    final selected = (_pickedId != null && ids.contains(_pickedId))
        ? _pickedId!
        : (cached.activeId != null && ids.contains(cached.activeId)
            ? cached.activeId!
            : cached.profiles.first.id);
    final pick = cached.profiles.firstWhere((p) => p.id == selected);
    return [
      Text('Plan with profile:', style: Theme.of(context).textTheme.titleSmall),
      const SizedBox(height: 8),
      DropdownButtonFormField<String>(
        key: ValueKey('offline-profile-dropdown-${cached.activeId}'),
        initialValue: selected,
        items: [
          for (final p in cached.profiles)
            DropdownMenuItem(
                value: p.id,
                child: Text(p.name.isEmpty ? '(unnamed profile)' : p.name)),
        ],
        onChanged: (v) => setState(() => _pickedId = v),
      ),
      if (!pick.hasSections) ...[
        const SizedBox(height: 8),
        const Text(
          'This profile\'s gear settings haven\'t been cached yet (it was '
          'never active while connected) — planning will use defaults.',
          style: TextStyle(color: AraColors.textSecondary, fontSize: 12),
        ),
      ] else if (pick.capturedUtc case final captured?) ...[
        const SizedBox(height: 8),
        Text(
          'Gear cached ${_relativeAge(captured)} — reconnect to refresh if '
          'your setup changed.',
          style: const TextStyle(color: AraColors.textSecondary, fontSize: 12),
        ),
      ],
      const SizedBox(height: 16),
      FilledButton(
        onPressed: _entering ? null : () => unawaited(_plan(selected)),
        child: _entering
            ? const SizedBox(
                width: 18,
                height: 18,
                child: CircularProgressIndicator(strokeWidth: 2))
            : const Text('Plan'),
      ),
    ];
  }

  Future<void> _plan(String id) async {
    if (_entering) return; // same-frame double-tap guard
    setState(() => _entering = true);
    try {
      // Seed BEFORE entering the shell so the first offline draft is already
      // built from the cached gear. A missing snapshot just keeps defaults.
      final container = ProviderScope.containerOf(context, listen: false);
      await seedPlanningFromCache(container, id);
      ref.read(profileGatePassedProvider.notifier).pass();
    } finally {
      if (mounted) setState(() => _entering = false);
    }
  }
}

/// "just now" / "N hours ago" / "N days ago" — coarse on purpose; the point
/// is flagging WEEKS-old gear, not precision.
String _relativeAge(DateTime utc) {
  final age = DateTime.now().toUtc().difference(utc);
  if (age.inHours < 1) return 'just now';
  if (age.inHours < 48) return '${age.inHours} h ago';
  return '${age.inDays} days ago';
}
