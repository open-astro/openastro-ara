import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../l10n/l10n.dart';
import '../state/launch_gate_state.dart';
import '../state/profile_cache_state.dart';
import '../theme/ara_colors.dart';
import '../widgets/adaptive_controls.dart';

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
                  Text(context.l10n.offlineTitle,
                      style: Theme.of(context).textTheme.headlineSmall),
                  const SizedBox(height: 4),
                  Text(context.l10n.offlineSubtitle,
                      style: const TextStyle(color: AraColors.textSecondary)),
                  const SizedBox(height: 24),
                  ...async.when(
                    loading: () => const [
                      Center(child: AdaptiveProgressIndicator()),
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
        Text(
          context.l10n.offlineNoProfiles,
          textAlign: TextAlign.center,
          style: const TextStyle(color: AraColors.textSecondary),
        ),
        const SizedBox(height: 16),
        FilledButton(
          onPressed: () => ref.read(offlineModeProvider.notifier).exit(),
          child: Text(context.l10n.offlineBackToSetup),
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
      Text(context.l10n.offlinePlanWithProfile, style: Theme.of(context).textTheme.titleSmall),
      const SizedBox(height: 8),
      DropdownButtonFormField<String>(
        key: ValueKey('offline-profile-dropdown-${cached.activeId}'),
        initialValue: selected,
        items: [
          for (final p in cached.profiles)
            DropdownMenuItem(
                value: p.id,
                child: Text(
                    p.name.isEmpty ? context.l10n.offlineUnnamedProfile : p.name)),
        ],
        onChanged: (v) => setState(() => _pickedId = v),
      ),
      if (!pick.hasSections) ...[
        const SizedBox(height: 8),
        Text(
          context.l10n.offlineNotCached,
          style: const TextStyle(color: AraColors.textSecondary, fontSize: 12),
        ),
      ] else if (pick.capturedUtc case final captured?) ...[
        const SizedBox(height: 8),
        Text(
          context.l10n.offlineGearCached(_relativeAge(context.l10n, captured)),
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
                child: AdaptiveProgressIndicator(strokeWidth: 2))
            : Text(context.l10n.offlinePlan),
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
String _relativeAge(AppLocalizations l10n, DateTime utc) {
  final age = DateTime.now().toUtc().difference(utc);
  if (age.inHours < 1) return l10n.offlineAgeJustNow;
  if (age.inHours < 48) return l10n.offlineAgeHours(age.inHours);
  return l10n.offlineAgeDays(age.inDays);
}
