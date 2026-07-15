import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/profile_list.dart';
import '../state/launch_gate_state.dart';
import '../state/profile_management_state.dart';
import '../state/saved_server_state.dart';
import '../theme/ara_colors.dart';
import '../widgets/profile/profile_import_flow.dart';
import 'wizard/wizard_shell.dart';

/// §30.2 launch profile box — shown on every launch after the server gate,
/// before the main shell. Dropdown of the daemon's profiles (pre-selecting the
/// active/last-used one per §30.3) + [Image] to enter the app, with
/// [Add a Profile] (→ §37 wizard) and [Import Profile] always visible.
class LaunchProfileScreen extends ConsumerStatefulWidget {
  const LaunchProfileScreen({super.key});

  @override
  ConsumerState<LaunchProfileScreen> createState() =>
      _LaunchProfileScreenState();
}

class _LaunchProfileScreenState extends ConsumerState<LaunchProfileScreen> {
  /// The dropdown's local pick. Null means "follow the daemon's active id" —
  /// it only becomes non-null when the user picks a different entry, so a
  /// background list refresh can't clobber an explicit choice.
  String? _pickedId;

  /// Guards [Image] against double-clicks while the select RPC is in flight.
  bool _entering = false;

  @override
  Widget build(BuildContext context) {
    final async = ref.watch(profileManagementProvider);
    final server = ref.watch(activeServerProvider);
    final serverLabel =
        server == null ? null : (server.mdnsName ?? server.hostname);

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
                  if (serverLabel != null) ...[
                    const SizedBox(height: 4),
                    Text('Connected to $serverLabel',
                        style: const TextStyle(color: AraColors.textSecondary)),
                  ],
                  const SizedBox(height: 24),
                  ...async.when(
                    loading: () => const [
                      Center(child: CircularProgressIndicator()),
                    ],
                    error: (e, _) => [
                      Text(
                        friendlyDaemonError(e,
                            fallback: "Couldn't load the profiles"),
                        textAlign: TextAlign.center,
                        style: const TextStyle(color: AraColors.textSecondary),
                      ),
                      const SizedBox(height: 12),
                      OutlinedButton.icon(
                        onPressed: () =>
                            ref.invalidate(profileManagementProvider),
                        icon: const Icon(Icons.refresh, size: 18),
                        label: const Text('Retry'),
                      ),
                    ],
                    data: (list) => _profileControls(list),
                  ),
                  const SizedBox(height: 24),
                  const Row(children: [
                    Expanded(child: Divider()),
                    Padding(
                      padding: EdgeInsets.symmetric(horizontal: 12),
                      child: Text('or',
                          style: TextStyle(color: AraColors.textSecondary)),
                    ),
                    Expanded(child: Divider()),
                  ]),
                  const SizedBox(height: 16),
                  // §30.2: Add / Import are ALWAYS visible — never gated behind
                  // picking an existing profile, so users can experiment freely.
                  Row(children: [
                    Expanded(
                      child: OutlinedButton.icon(
                        onPressed: _addProfile,
                        icon: const Icon(Icons.add, size: 18),
                        label: const Text('Add a Profile'),
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: OutlinedButton.icon(
                        onPressed: () =>
                            unawaited(runProfileImportFlow(context, ref)),
                        icon: const Icon(Icons.file_download_outlined, size: 18),
                        label: const Text('Import Profile'),
                      ),
                    ),
                  ]),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }

  /// The ≥1-profiles half of the box: dropdown + [Image]. Hidden entirely when
  /// no profiles exist yet (§30.2: Add / Import are then the only actions).
  List<Widget> _profileControls(ProfileList list) {
    if (list.profiles.isEmpty) {
      return const [
        Text('No profiles yet — add or import one to get started.',
            textAlign: TextAlign.center,
            style: TextStyle(color: AraColors.textSecondary)),
      ];
    }
    // Follow the daemon's active profile unless the user picked another entry
    // this session; fall back to the first profile if the daemon reports no
    // active id (shouldn't happen once ≥1 exists, but the dropdown needs a
    // valid value).
    final ids = {for (final p in list.profiles) p.id};
    final selected = (_pickedId != null && ids.contains(_pickedId))
        ? _pickedId!
        : (list.activeId != null && ids.contains(list.activeId)
            ? list.activeId!
            : list.profiles.first.id);
    return [
      Text('Active profile:',
          style: Theme.of(context).textTheme.titleSmall),
      const SizedBox(height: 8),
      DropdownButtonFormField<String>(
        // FormFields consume initialValue only on first mount, so key on the
        // daemon's active id: when the wizard returns with a new active
        // profile the dropdown remounts and actually shows it (review #844).
        key: ValueKey('launch-profile-dropdown-${list.activeId}'),
        initialValue: selected,
        items: [
          for (final p in list.profiles)
            DropdownMenuItem(value: p.id, child: Text(p.name)),
        ],
        onChanged: (v) => setState(() => _pickedId = v),
      ),
      const SizedBox(height: 16),
      FilledButton(
        onPressed: _entering ? null : () => unawaited(_enter(selected)),
        child: _entering
            ? const SizedBox(
                width: 18, height: 18,
                child: CircularProgressIndicator(strokeWidth: 2))
            : const Text('Image'),
      ),
    ];
  }

  /// [Image] — make the picked profile active on the daemon (when it isn't
  /// already), then pass the launch gate so the root router swaps in the shell.
  Future<void> _enter(String id) async {
    // Re-entrancy guard: two taps in the same frame both dispatch before the
    // disabled rebuild lands; the flag (not the button state) is authoritative.
    if (_entering) return;
    final messenger = ScaffoldMessenger.of(context);
    setState(() => _entering = true);
    try {
      final list = ref.read(profileManagementProvider).value;
      if (list != null && list.activeId != id) {
        await ref.read(profileManagementProvider.notifier).select(id);
      }
      ref.read(profileGatePassedProvider.notifier).pass();
    } catch (e) {
      messenger.showSnackBar(SnackBar(
        content: Text(
            friendlyDaemonError(e, fallback: "Couldn't switch profile")),
        backgroundColor: AraColors.accentError,
      ));
    } finally {
      if (mounted) setState(() => _entering = false);
    }
  }

  Future<void> _addProfile() async {
    // The wizard invalidates profileManagementProvider on a successful save and
    // the new profile becomes active on the daemon (§30.4), so on return the
    // dropdown pre-selects it — clear any stale manual pick so that shows.
    await Navigator.of(context).push<void>(
      MaterialPageRoute(builder: (_) => const WizardShell(), fullscreenDialog: true),
    );
    if (mounted) setState(() => _pickedId = null);
  }
}
