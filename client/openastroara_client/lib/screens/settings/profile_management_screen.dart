import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/profile_list.dart';
import '../../models/profile_meta.dart';
import '../../models/profile_share_export.dart';
import '../../services/profile_share_file.dart';
import '../../state/profile_management_state.dart';
import '../../theme/ara_colors.dart';
import '../../widgets/profile/profile_import_flow.dart';
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
            onPressed: () => unawaited(runProfileImportFlow(context, ref)),
            icon: const Icon(Icons.file_download_outlined, size: 18),
            label: const Text('Import'),
          ),
          TextButton.icon(
            onPressed: () => _addProfile(context),
            icon: const Icon(Icons.add, size: 18),
            label: const Text('Add profile'),
          ),
          const SizedBox(width: 8),
        ],
      ),
      body: async.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => _ErrorState(
          message: friendlyDaemonError(e),
          onRetry: () => ref.read(profileManagementProvider.notifier).refresh(),
        ),
        data: (list) => _ProfileListView(list: list),
      ),
    );
  }

  Future<void> _addProfile(BuildContext context) async {
    // The wizard invalidates profileManagementProvider itself on a successful
    // save, so this list refreshes on its own — no onComplete/refresh needed here.
    // (A cancelled wizard never invalidates, so there's no needless re-fetch.)
    await Navigator.of(context).push<void>(
      MaterialPageRoute(builder: (_) => const WizardShell()),
    );
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
              // Export writes via FilePicker.saveFile(bytes:), which is only
              // reliable on the desktop targets — hide it elsewhere rather than
              // offer an action that would silently no-op.
              if (Platform.isMacOS || Platform.isLinux || Platform.isWindows)
                const PopupMenuItem(value: 'export', child: Text('Export…')),
              // Any profile is deletable: the daemon activates the newest
              // remaining profile when the active one goes, and re-seeds a
              // factory "Default" when the last one goes (fresh-install state).
              const PopupMenuItem(value: 'delete', child: Text('Delete')),
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
      case 'export':
        await _exportProfile(context, ref, p);
        break;
      case 'delete':
        final ok = await _confirmDelete(context, p.name,
            isActive: isActive, isLast: list.profiles.length == 1);
        if (ok) {
          await _run(messenger, () => notifier.delete(p.id), 'Couldn\'t delete profile');
        }
        break;
    }
  }

  /// §70 export — render the profile into a `profile-share-v1` template (paths /
  /// secrets / location / network already stripped by the daemon) and write it to
  /// a file the user picks. The recipient imports it as a starting template.
  Future<void> _exportProfile(
      BuildContext context, WidgetRef ref, ProfileMeta p) async {
    final messenger = ScaffoldMessenger.of(context);
    final api = ref.read(profileApiProvider);
    if (api == null) {
      messenger.showSnackBar(const SnackBar(
          content: Text('Connect to a daemon first to export a profile.')));
      return;
    }

    ProfileShareExport share;
    try {
      share = await runWithBlockingSpinner(context, () => api.exportProfile(p.id));
    } catch (e) {
      messenger.showSnackBar(SnackBar(
          content: Text(friendlyDaemonError(e, fallback: "Couldn't export that profile")),
          backgroundColor: AraColors.accentError));
      return;
    }

    if (!context.mounted) return;
    // Pretty-print so a shared file is human-readable. The manifest is already
    // stripped of paths / secrets / location / network by the daemon. saveFile
    // writes the bytes itself on the desktop targets and returns the saved path
    // (null if the user cancels).
    final jsonBytes = Uint8List.fromList(
        utf8.encode(const JsonEncoder.withIndent('  ').convert(share.manifest)));
    final String? saved;
    try {
      saved = await FilePicker.saveFile(
        dialogTitle: 'Save profile share',
        fileName: shareFileName(
            share.profileName.isEmpty ? p.name : share.profileName),
        type: FileType.any,
        bytes: jsonBytes,
      );
    } catch (e) {
      messenger.showSnackBar(SnackBar(
          content: Text(friendlyDaemonError(e, fallback: "Couldn't save the file")),
          backgroundColor: AraColors.accentError));
      return;
    }
    if (saved == null || !context.mounted) return; // cancelled / screen gone

    messenger.showSnackBar(SnackBar(
        content: Text('Exported "${p.name}" — share this file; the recipient '
            'imports it as a starting template.')));
  }

  Future<void> _run(ScaffoldMessengerState messenger, Future<void> Function() op,
      String fallback) async {
    try {
      await op();
    } catch (e) {
      messenger.showSnackBar(SnackBar(
        content: Text(friendlyDaemonError(e, fallback: fallback)),
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

Future<bool> _confirmDelete(BuildContext context, String name,
    {bool isActive = false, bool isLast = false}) async {
  final consequence = isLast
      ? ' This is the last profile — a factory "Default" profile will be '
          'created in its place.'
      : (isActive ? ' The most recent remaining profile becomes active.' : '');
  final ok = await showDialog<bool>(
    context: context,
    builder: (ctx) => AlertDialog(
      title: const Text('Delete profile?'),
      content: Text(
          '"$name" will be permanently removed. This can\'t be undone.$consequence'),
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
