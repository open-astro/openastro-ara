import 'dart:convert';
import 'dart:io';

import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/sequence_summary.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../theme/ara_colors.dart';

/// Upper bound on a picked NINA sequence file. Real exports are KBs; 32 MiB is
/// far above any plausible sequence while still bounding memory before decode.
const int _maxSequenceFileBytes = 32 * 1024 * 1024;

/// §38.4 — let the user pick a NINA sequence `.json` and import it. A cancelled
/// pick is a no-op; an unreadable / non-JSON file surfaces a SnackBar. On a valid
/// file it delegates to [importSequenceFromJson].
///
/// Returns true only when a sequence was actually imported, so the caller can
/// keep the Load dialog open on a cancel/error (and close it only on success).
Future<bool> pickAndImportSequence(BuildContext context, WidgetRef ref) async {
  final messenger = ScaffoldMessenger.of(context);
  final picked = await FilePicker.pickFiles(
    type: FileType.custom,
    allowedExtensions: ['json'],
  );
  // `path` is populated on desktop/mobile (ARA's targets); Flutter Web surfaces
  // file content via `bytes` instead, so a `bytes` path would be needed before
  // web is supported (a null path here reads as a cancel).
  final path = picked?.files.single.path;
  if (path == null) return false; // cancelled

  Map<String, dynamic> nina;
  String fileName;
  try {
    final file = File(path);
    // NINA sequences are KBs; cap before reading so an accidental huge file
    // can't spike memory in jsonDecode.
    if (await file.length() > _maxSequenceFileBytes) {
      throw const FormatException('sequence file is too large');
    }
    final decoded = jsonDecode(await file.readAsString());
    if (decoded is! Map<String, dynamic>) {
      throw const FormatException('sequence file is not a JSON object');
    }
    nina = decoded;
    final base = path.split(Platform.pathSeparator).last;
    fileName = base.toLowerCase().endsWith('.json')
        ? base.substring(0, base.length - 5)
        : base;
  } catch (e, st) {
    debugPrint('[sequencer] NINA file read/parse failed: $e\n$st');
    if (context.mounted) {
      messenger.showSnackBar(const SnackBar(
        content: Text("That file isn't a valid sequence JSON."),
        backgroundColor: AraColors.accentError,
      ));
    }
    return false;
  }

  if (!context.mounted) return false;
  // Prefer the NINA root's own name; fall back to the file name.
  final rootName = nina['Name'];
  final name = (rootName is String && rootName.trim().isNotEmpty)
      ? rootName.trim()
      : fileName;
  final id = await importSequenceFromJson(context, ref, name: name, ninaJson: nina);
  return id != null;
}

/// Import an already-decoded NINA sequence body. On success: refresh the list so
/// the new sequence appears, select it (→ the tab loads it into the tree), and
/// surface any translation warnings (or a brief confirmation). On failure: a
/// SnackBar. Returns the created sequence id, or null on failure.
Future<String?> importSequenceFromJson(
  BuildContext context,
  WidgetRef ref, {
  required String name,
  required Map<String, dynamic> ninaJson,
}) async {
  final api = ref.read(sequenceApiProvider);
  if (api == null) return null;
  final messenger = ScaffoldMessenger.of(context);

  SequenceImportResult result;
  try {
    result = await api.importNina(name, ninaJson);
  } catch (_) {
    if (context.mounted) {
      messenger.showSnackBar(const SnackBar(
        content: Text("Couldn't import the sequence. Check the file and connection."),
        backgroundColor: AraColors.accentError,
      ));
    }
    return null;
  }

  // Surface the new sequence: invalidate the list so it re-fetches (and shows the
  // import) next time it's watched, and select it so the tab loads it now.
  // invalidate (not notifier.refresh) so this works even when the autoDispose
  // list provider currently has no listeners.
  ref.invalidate(sequenceListProvider);
  final id = result.createdSequenceId;
  if (id != null && id.isNotEmpty) {
    ref.read(selectedSequenceIdProvider.notifier).select(id);
  }

  final hasWarnings = result.lossyTranslation ||
      result.warnings.isNotEmpty ||
      result.droppedInstructionTypes.isNotEmpty;
  if (!context.mounted) {
    // The widget went away before we could surface the warnings dialog. Don't
    // let a lossy translation vanish silently — at least record it (a lossy
    // NINA import can change what the rig actually does).
    if (hasWarnings) {
      debugPrint('[sequencer] import warnings not shown (context unmounted): '
          'lossy=${result.lossyTranslation} '
          'dropped=${result.droppedInstructionTypes} warnings=${result.warnings}');
    }
    return id;
  }
  if (hasWarnings) {
    await _showImportWarnings(context, result, name);
  } else {
    messenger.showSnackBar(SnackBar(
        content: Text('Imported "${result.name.isEmpty ? name : result.name}".')));
  }
  return id;
}

Future<void> _showImportWarnings(
    BuildContext context, SequenceImportResult result, String fallbackName) {
  final title = result.name.isEmpty ? fallbackName : result.name;
  return showDialog<void>(
    context: context,
    builder: (ctx) => AlertDialog(
      title: Text('Imported "$title" with warnings'),
      content: SizedBox(
        width: 420,
        child: SingleChildScrollView(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: [
              if (result.lossyTranslation)
                const Text(
                  'Some of this NINA sequence couldn\'t be translated exactly — '
                  'review it before running.',
                  style: TextStyle(color: AraColors.textSecondary),
                ),
              if (result.droppedInstructionTypes.isNotEmpty) ...[
                const SizedBox(height: 12),
                const Text('Dropped instruction types:'),
                ...result.droppedInstructionTypes.map((t) => Text('  • $t',
                    style: const TextStyle(color: AraColors.textSecondary))),
              ],
              if (result.warnings.isNotEmpty) ...[
                const SizedBox(height: 12),
                const Text('Warnings:'),
                ...result.warnings.map((w) => Text('  • $w',
                    style: const TextStyle(color: AraColors.textSecondary))),
              ],
            ],
          ),
        ),
      ),
      actions: [
        TextButton(
            onPressed: () => Navigator.of(ctx).pop(), child: const Text('OK')),
      ],
    ),
  );
}
