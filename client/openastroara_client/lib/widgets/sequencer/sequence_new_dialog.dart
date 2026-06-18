import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/sequence_summary.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../theme/ara_colors.dart';

/// §38.7 "New sequence" picker. Lists the active daemon's starting-point
/// templates (from [sequenceTemplatesProvider]); the user picks one, names the
/// new sequence, and on Create it's instantiated server-side and selected (→ the
/// tab loads it into the tree). Handles loading / error / no-server / empty
/// states. Save/edit of the resulting sequence is a later slice.
class SequenceNewDialog extends ConsumerStatefulWidget {
  const SequenceNewDialog({super.key});

  /// Show the dialog. Returns the created sequence id, or null if dismissed.
  static Future<String?> show(BuildContext context) => showDialog<String>(
        context: context,
        builder: (_) => const SequenceNewDialog(),
      );

  @override
  ConsumerState<SequenceNewDialog> createState() => _SequenceNewDialogState();
}

class _SequenceNewDialogState extends ConsumerState<SequenceNewDialog> {
  final _nameController = TextEditingController();
  String? _selectedTemplate;
  bool _busy = false;

  @override
  void dispose() {
    _nameController.dispose();
    super.dispose();
  }

  void _select(SequenceTemplate t) {
    setState(() => _selectedTemplate = t.name);
    // Pre-fill the name from the template only when the user hasn't typed one,
    // so picking a template doesn't clobber a name they already entered.
    if (_nameController.text.trim().isEmpty) {
      _nameController.text = t.name;
    }
  }

  Future<void> _create() async {
    final templateName = _selectedTemplate;
    final newName = _nameController.text.trim();
    if (templateName == null || newName.isEmpty) return;
    final navigator = Navigator.of(context);
    setState(() => _busy = true);
    var created = false;
    try {
      final id = await createSequenceFromTemplate(context, ref,
          templateName: templateName, newName: newName);
      if (id != null && navigator.mounted) {
        navigator.pop(id);
        created = true;
      }
    } finally {
      // Only clear busy if we kept the dialog open (a successful create pops it).
      if (!created && mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final async = ref.watch(sequenceTemplatesProvider);
    final canCreate = !_busy &&
        _selectedTemplate != null &&
        _nameController.text.trim().isNotEmpty;
    return AlertDialog(
      title: const Text('New sequence'),
      content: SizedBox(
        width: 460,
        child: async.when(
          loading: () => const SizedBox(
            height: 120,
            child: Center(child: CircularProgressIndicator()),
          ),
          error: (e, st) {
            debugPrint('[sequencer] template list load failed: $e\n$st');
            return const _Message(
                'Couldn\'t load templates from the server. Check the connection and try again.');
          },
          data: (templates) {
            if (templates == null) {
              return const _Message('Connect to a daemon to start a new sequence.');
            }
            if (templates.isEmpty) {
              return const _Message('No sequence templates available.');
            }
            return Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                const Text('Start from a template:',
                    style: TextStyle(color: AraColors.textSecondary)),
                const SizedBox(height: 8),
                ConstrainedBox(
                  constraints: const BoxConstraints(maxHeight: 280),
                  child: RadioGroup<String>(
                    groupValue: _selectedTemplate,
                    // onChanged is non-nullable; ignore changes while busy rather
                    // than disabling (a create is brief and the Create button
                    // already shows a spinner).
                    onChanged: (name) {
                      if (_busy || name == null) return;
                      _select(templates.firstWhere((t) => t.name == name));
                    },
                    child: ListView.builder(
                      shrinkWrap: true,
                      itemCount: templates.length,
                      itemBuilder: (context, i) {
                        final t = templates[i];
                        return RadioListTile<String>(
                          value: t.name,
                          contentPadding: EdgeInsets.zero,
                          dense: true,
                          title: Text(t.name.isEmpty ? '(unnamed)' : t.name),
                          subtitle: _subtitle(t),
                        );
                      },
                    ),
                  ),
                ),
                const SizedBox(height: 12),
                TextField(
                  controller: _nameController,
                  enabled: !_busy,
                  decoration: const InputDecoration(
                    labelText: 'Name',
                    hintText: 'New sequence name',
                  ),
                  // Rebuild so the Create button enables/disables as the field
                  // changes (it gates on a non-empty name).
                  onChanged: (_) => setState(() {}),
                ),
              ],
            );
          },
        ),
      ),
      actions: [
        TextButton(
          onPressed: _busy ? null : () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: canCreate ? _create : null,
          child: _busy
              ? const SizedBox(
                  width: 18,
                  height: 18,
                  child: CircularProgressIndicator(strokeWidth: 2))
              : const Text('Create'),
        ),
      ],
    );
  }

  /// Category + description line for a template row; nothing when both are empty.
  Widget? _subtitle(SequenceTemplate t) {
    final parts = <String>[
      if (t.category.isNotEmpty) t.category,
      if (t.description != null && t.description!.trim().isNotEmpty)
        t.description!.trim(),
    ];
    if (parts.isEmpty) return null;
    return Text(parts.join(' · '),
        style: const TextStyle(color: AraColors.textSecondary));
  }
}

/// Create a new sequence from [templateName] named [newName]: instantiate it on
/// the daemon, refresh the list so it appears, and select it (→ the tab loads it
/// into the tree). Returns the created id, or null on failure (a SnackBar is
/// shown). Mirrors the import flow's mounted/ref ordering.
Future<String?> createSequenceFromTemplate(
  BuildContext context,
  WidgetRef ref, {
  required String templateName,
  required String newName,
}) async {
  final api = ref.read(sequenceApiProvider);
  if (api == null) return null;
  final messenger = ScaffoldMessenger.of(context);

  String id;
  try {
    id = await api.instantiateTemplate(templateName, newName);
  } catch (_) {
    if (context.mounted) {
      messenger.showSnackBar(const SnackBar(
        content: Text(
            "Couldn't create the sequence. Check the connection and try again."),
        backgroundColor: AraColors.accentError,
      ));
    }
    return null;
  }

  // Check mounted BEFORE touching ref: the sequence exists server-side now, but
  // if the widget was disposed during the await, ref.invalidate/ref.read would
  // throw on a defunct Ref.
  if (!context.mounted) return id;
  ref.invalidate(sequenceListProvider);
  ref.read(selectedSequenceIdProvider.notifier).select(id);
  messenger.showSnackBar(SnackBar(content: Text('Created "${newName.trim()}".')));
  return id;
}

class _Message extends StatelessWidget {
  const _Message(this.text);
  final String text;
  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 24),
        child: Text(text, style: const TextStyle(color: AraColors.textSecondary)),
      );
}
