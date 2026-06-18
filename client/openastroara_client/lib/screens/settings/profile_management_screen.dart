import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/profile_list.dart';
import '../../models/profile_meta.dart';
import '../../state/profile_management_state.dart';
import '../../theme/ara_colors.dart';
import '../wizard/wizard_shell.dart';

/// §37/§30 multi-profile management. Lists the daemon's known profiles with the
/// active one badged, and offers Select / Rename / Delete per profile plus an
/// "Add profile" action that runs the §37 wizard. Deleting the active or the
/// last-remaining profile is refused by the daemon (409); the message is shown.
class ProfileManagementScreen extends ConsumerWidget {
  const ProfileManagementScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(profileManagementProvider);
    return Scaffold(
      appBar: AppBar(
        title: const Text('Profiles'),
        actions: [
          TextButton.icon(
            onPressed: () => _addProfile(context, ref),
            icon: const Icon(Icons.add, size: 18),
            label: const Text('Add profile'),
          ),
          const SizedBox(width: 8),
        ],
      ),
      body: async.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => _ErrorState(
          message: _friendly(e),
          onRetry: () => ref.read(profileManagementProvider.notifier).refresh(),
        ),
        data: (list) => _ProfileListView(list: list),
      ),
    );
  }

  Future<void> _addProfile(BuildContext context, WidgetRef ref) async {
    // The wizard fires onComplete only on Save (not on a back/cancel); refresh
    // only then, so a cancelled wizard doesn't trigger a needless list re-fetch.
    var created = false;
    await Navigator.of(context).push<void>(
      MaterialPageRoute(
        builder: (_) => WizardShell(onComplete: (_) => created = true)),
    );
    if (created && context.mounted) {
      await ref.read(profileManagementProvider.notifier).refresh();
    }
  }
}

class _ProfileListView extends ConsumerWidget {
  const _ProfileListView({required this.list});
  final ProfileList list;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    if (list.profiles.isEmpty) {
      return const Center(
        child: Text('No profiles yet — use "Add profile" to create one.',
            style: TextStyle(color: AraColors.textSecondary)),
      );
    }
    final onlyOne = list.profiles.length == 1;
    return ListView.separated(
      itemCount: list.profiles.length,
      separatorBuilder: (_, _) =>
          const Divider(height: 1, color: AraColors.border),
      itemBuilder: (context, i) {
        final p = list.profiles[i];
        final isActive = p.id == list.activeId;
        return ListTile(
          leading: Icon(
            isActive ? Icons.radio_button_checked : Icons.radio_button_unchecked,
            color: isActive ? AraColors.accentConnected : AraColors.textSecondary,
          ),
          title: Text(p.name),
          subtitle: isActive
              ? const Text('Active', style: TextStyle(color: AraColors.accentConnected))
              : null,
          trailing: PopupMenuButton<String>(
            onSelected: (action) =>
                unawaited(_onAction(context, ref, action, p, isActive)),
            itemBuilder: (_) => [
              if (!isActive)
                const PopupMenuItem(value: 'select', child: Text('Make active')),
              const PopupMenuItem(value: 'rename', child: Text('Rename')),
              PopupMenuItem(
                value: 'delete',
                // The daemon refuses deleting the active or last-remaining
                // profile; disable here too so the action reads as unavailable.
                enabled: !isActive && !onlyOne,
                child: const Text('Delete'),
              ),
            ],
          ),
        );
      },
    );
  }

  Future<void> _onAction(BuildContext context, WidgetRef ref, String action,
      ProfileMeta p, bool isActive) async {
    final messenger = ScaffoldMessenger.of(context);
    final notifier = ref.read(profileManagementProvider.notifier);
    switch (action) {
      case 'select':
        await _run(messenger, () => notifier.select(p.id), 'Couldn\'t switch profile');
        break;
      case 'rename':
        final name = await _promptName(context, initial: p.name);
        if (name != null && name != p.name) {
          await _run(messenger, () => notifier.rename(p.id, name), 'Couldn\'t rename profile');
        }
        break;
      case 'delete':
        final ok = await _confirmDelete(context, p.name);
        if (ok) {
          await _run(messenger, () => notifier.delete(p.id), 'Couldn\'t delete profile');
        }
        break;
    }
  }

  Future<void> _run(ScaffoldMessengerState messenger, Future<void> Function() op,
      String fallback) async {
    try {
      await op();
    } catch (e) {
      messenger.showSnackBar(SnackBar(
        content: Text(_friendly(e, fallback: fallback)),
        backgroundColor: AraColors.accentError,
      ));
    }
  }
}

Future<String?> _promptName(BuildContext context, {required String initial}) {
  final controller = TextEditingController(text: initial);
  // Dispose the controller once the dialog future settles — controllers hold
  // resources Flutter won't reclaim automatically.
  return showDialog<String>(
    context: context,
    builder: (ctx) => AlertDialog(
      title: const Text('Rename profile'),
      content: TextField(
        controller: controller,
        autofocus: true,
        decoration: const InputDecoration(labelText: 'Profile name'),
        // Match the Rename button: an empty value returns null (no rename), so a
        // cleared field + Enter can't push an empty name to the daemon.
        onSubmitted: (v) {
          final t = v.trim();
          Navigator.of(ctx).pop(t.isEmpty ? null : t);
        },
      ),
      actions: [
        TextButton(onPressed: () => Navigator.of(ctx).pop(), child: const Text('Cancel')),
        FilledButton(
          onPressed: () {
            final v = controller.text.trim();
            Navigator.of(ctx).pop(v.isEmpty ? null : v);
          },
          child: const Text('Rename'),
        ),
      ],
    ),
  ).whenComplete(controller.dispose);
}

Future<bool> _confirmDelete(BuildContext context, String name) async {
  final ok = await showDialog<bool>(
    context: context,
    builder: (ctx) => AlertDialog(
      title: const Text('Delete profile?'),
      content: Text('"$name" will be permanently removed. This can\'t be undone.'),
      actions: [
        TextButton(onPressed: () => Navigator.of(ctx).pop(false), child: const Text('Cancel')),
        FilledButton(
          style: FilledButton.styleFrom(backgroundColor: AraColors.accentError),
          onPressed: () => Navigator.of(ctx).pop(true),
          child: const Text('Delete'),
        ),
      ],
    ),
  );
  return ok ?? false;
}

/// Turn a transport error into a user-facing line — prefer the daemon's
/// ProblemDetails `detail` (e.g. the 409 "select another profile first").
String _friendly(Object e, {String fallback = 'Something went wrong'}) {
  if (e is DioException) {
    final data = e.response?.data;
    if (data is Map && data['detail'] is String) return data['detail'] as String;
    return '$fallback: ${e.message ?? 'network error'}';
  }
  if (e is StateError) return e.message;
  return fallback;
}

class _ErrorState extends StatelessWidget {
  const _ErrorState({required this.message, required this.onRetry});
  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(message,
              textAlign: TextAlign.center,
              style: const TextStyle(color: AraColors.textSecondary)),
          const SizedBox(height: 12),
          OutlinedButton.icon(
            onPressed: onRetry,
            icon: const Icon(Icons.refresh, size: 18),
            label: const Text('Retry'),
          ),
        ],
      ),
    );
  }
}
