import 'dart:async';
import 'dart:io';

import 'package:dio/dio.dart';
import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/profile_list.dart';
import '../../models/profile_meta.dart';
import '../../models/profile_share_import_preview.dart';
import '../../services/profile_share_file.dart';
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
            onPressed: () => unawaited(_importProfile(context, ref)),
            icon: const Icon(Icons.file_download_outlined, size: 18),
            label: const Text('Import'),
          ),
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

  /// §70 import — pick a shared profile-share file, preview what it'll create +
  /// what the recipient must re-enter, and on confirm commit it into a new
  /// (non-active) profile. The imported profile is a template: the user makes it
  /// active and fills in their own equipment via the wizard afterward.
  Future<void> _importProfile(BuildContext context, WidgetRef ref) async {
    final messenger = ScaffoldMessenger.of(context);
    final api = ref.read(profileApiProvider);
    if (api == null) {
      messenger.showSnackBar(const SnackBar(
          content: Text('Connect to a daemon first to import a profile.')));
      return;
    }

    // Pick metadata only (no withData) so we can size-check before reading the
    // file in — a profile share is a few KB of JSON, so anything large is a
    // mis-pick and we refuse rather than slurp it into memory. The picker itself
    // can throw (e.g. a macOS sandbox / denied-permission PlatformException), not
    // just return null — catch it so it can't become a silent unhandled rejection
    // under unawaited().
    final FilePickerResult? picked;
    try {
      picked = await FilePicker.pickFiles(
        type: FileType.any,
        dialogTitle: 'Choose a shared profile file',
      );
    } catch (e) {
      messenger.showSnackBar(SnackBar(
          content: Text(_friendly(e, fallback: "Couldn't open the file picker")),
          backgroundColor: AraColors.accentError));
      return;
    }
    if (picked == null || picked.files.isEmpty) return; // user cancelled
    final file = picked.files.single;
    const maxShareBytes = 1024 * 1024; // 1 MB ceiling — shares are a few KB
    if (file.size > maxShareBytes) {
      messenger.showSnackBar(const SnackBar(
          content: Text("That file is too large to be a profile share."),
          backgroundColor: AraColors.accentError));
      return;
    }
    final path = file.path;
    if (path == null) {
      messenger.showSnackBar(const SnackBar(
          content: Text("Couldn't read the selected file.")));
      return;
    }
    final List<int> bytes;
    try {
      // dart:io read — fine for the desktop targets (macOS / Linux / Windows);
      // this screen isn't built for web, where file.path is null and this branch
      // wouldn't be reached anyway.
      bytes = await File(path).readAsBytes();
    } catch (_) {
      messenger.showSnackBar(const SnackBar(
          content: Text("Couldn't read the selected file.")));
      return;
    }
    // Re-check the actual byte length: file.size is metadata read before this
    // gap (so it can be stale/0 on some backends, or the file could have grown).
    // This bounds what we parse + upload regardless of what file.size reported.
    if (bytes.length > maxShareBytes) {
      messenger.showSnackBar(const SnackBar(
          content: Text("That file is too large to be a profile share."),
          backgroundColor: AraColors.accentError));
      return;
    }

    if (!context.mounted) return;
    ProfileShareImportPreview preview;
    try {
      // Spinner over the preview RPC so a slow daemon doesn't look like a no-op.
      preview = await _runWithSpinner(
          context, () => api.importPreview(parseShareManifest(bytes)));
    } on FormatException catch (e) {
      messenger.showSnackBar(SnackBar(
          content: Text(e.message), backgroundColor: AraColors.accentError));
      return;
    } catch (e) {
      messenger.showSnackBar(SnackBar(
          content: Text(_friendly(e, fallback: "Couldn't read that share file")),
          backgroundColor: AraColors.accentError));
      return;
    }

    if (!context.mounted) return;
    final confirmed = await _confirmImport(context, preview);
    // Re-check after the dialog's async gap: the screen may have been popped
    // while it was open, which would make the post-commit ref.read/refresh run
    // against a disposed WidgetRef.
    if (!confirmed || !context.mounted) return;

    // Only a commit failure means the import didn't happen — keep it in its own
    // try so a later list-refresh failure can't masquerade as "couldn't import".
    try {
      await _runWithSpinner(context, () => api.importCommit(preview.importToken));
    } catch (e) {
      messenger.showSnackBar(SnackBar(
          content: Text(_friendly(e, fallback: "Couldn't import that profile")),
          backgroundColor: AraColors.accentError));
      return;
    }
    // The profile now exists on the daemon — confirm success regardless of the
    // refresh outcome (messenger is pre-captured, so safe even if unmounted).
    messenger.showSnackBar(SnackBar(
        content: Text('Imported "${preview.profileName}" — make it active, '
            'then set up your equipment in the wizard.')));
    // Best-effort reconcile of the list; only if still mounted (the commit was
    // another async gap), and a refresh failure is non-critical here.
    if (context.mounted) {
      try {
        await ref.read(profileManagementProvider.notifier).refresh();
      } catch (_) {}
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

/// Runs [op] behind a blocking, non-dismissible spinner so the import RPCs give
/// feedback instead of a silent hang on a slow daemon. Mirrors the wizard's
/// save-spinner pattern (same navigator via useRootNavigator:false; PopScope
/// blocks the system-back button). Always removes the spinner, then rethrows so
/// the caller handles success/failure.
Future<T> _runWithSpinner<T>(
    BuildContext context, Future<T> Function() op) async {
  final navigator = Navigator.of(context, rootNavigator: false);
  // Push an explicit spinner route we hold a handle to, then remove exactly that
  // route when done. showDialog()'s push is deferred a frame, so a synchronously
  // completing op could otherwise reach a bare Navigator.pop() before the spinner
  // is on the stack and pop the screen itself. removeRoute(spinner) targets the
  // specific route, so it's safe regardless of frame timing.
  final spinner = DialogRoute<void>(
    context: context,
    barrierDismissible: false,
    builder: (_) => const PopScope(
      canPop: false,
      child: Center(child: CircularProgressIndicator()),
    ),
  );
  unawaited(navigator.push(spinner));
  try {
    return await op();
  } finally {
    // Guard mounted so a screen popped mid-RPC doesn't touch a disposed navigator.
    if (context.mounted) navigator.removeRoute(spinner);
  }
}

Future<bool> _confirmImport(
    BuildContext context, ProfileShareImportPreview preview) async {
  final ok = await showDialog<bool>(
    context: context,
    builder: (ctx) => AlertDialog(
      title: Text('Import "${preview.profileName}"?'),
      content: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            // Fallback so the dialog is never blank (the daemon normally sends at
            // least one warning, but a sparse preview shouldn't show an empty box).
            if (preview.warnings.isEmpty && preview.droppedFields.isEmpty)
              const Text('Import this shared profile as a new template?'),
            for (final w in preview.warnings) ...[
              Text(w),
              const SizedBox(height: 8),
            ],
            if (preview.droppedFields.isNotEmpty) ...[
              const Text("You'll set these up yourself after importing:",
                  style: TextStyle(fontWeight: FontWeight.bold)),
              const SizedBox(height: 4),
              for (final d in preview.droppedFields)
                Padding(
                  padding: const EdgeInsets.only(left: 8, top: 2),
                  child: Text('• $d',
                      style: const TextStyle(color: AraColors.textSecondary)),
                ),
            ],
            if (shareExpiryNote(preview.expiresUtc) case final note?) ...[
              const SizedBox(height: 12),
              Text(note,
                  style: const TextStyle(
                      color: AraColors.textSecondary,
                      fontStyle: FontStyle.italic,
                      fontSize: 12)),
            ],
          ],
        ),
      ),
      actions: [
        TextButton(
            onPressed: () => Navigator.of(ctx).pop(false),
            child: const Text('Cancel')),
        FilledButton(
            onPressed: () => Navigator.of(ctx).pop(true),
            child: const Text('Import')),
      ],
    ),
  );
  return ok ?? false;
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
