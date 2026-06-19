import 'dart:convert';
import 'dart:typed_data';

import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/sequencer/sequence_list_state.dart';
import '../../theme/ara_colors.dart';

/// Pretty-printed NINA JSON for [body], with ARA's `schemaVersion` backfill
/// stripped so the exported file is pristine NINA (the daemon adds it on import;
/// NINA itself doesn't write it). Pure — unit-testable without the file picker.
String prepareNinaExportJson(Map<String, dynamic> body) {
  // Shallow copy so the source (a deeply-unmodifiable SequenceDetail.body) isn't
  // touched. Only the TOP-LEVEL schemaVersion is stripped — that's the only
  // place the daemon backfills it; NINA never nests a schemaVersion deeper, so a
  // shallow copy is correct.
  final out = Map<String, dynamic>.of(body)..remove('schemaVersion');
  return const JsonEncoder.withIndent('  ').convert(out);
}

/// A filesystem-safe `<name>.json` for the export's default filename.
String sequenceExportFileName(String name) {
  var base = name.trim();
  // Drop a trailing .json (case-insensitive) so a sequence already named
  // "M42.json" doesn't export as "M42.json.json".
  if (base.toLowerCase().endsWith('.json')) {
    base = base.substring(0, base.length - 5);
  }
  if (base.trim().isEmpty) base = 'sequence';
  final safe = base.trim().replaceAll(RegExp(r'[^A-Za-z0-9 _.-]'), '_');
  return '$safe.json';
}

/// §38.4 — export the sequence [id] to a `.json` file the user can open straight
/// back in NINA (faithful round-trip — the daemon stores the body verbatim, so
/// this is the original NINA DOM, only stripped of ARA's `schemaVersion`).
/// Fetches the raw detail, then writes via the OS save dialog. A cancelled save
/// is a no-op; a failure surfaces a SnackBar.
Future<void> exportSequence(
  BuildContext context,
  WidgetRef ref, {
  required String id,
  required String name,
}) async {
  final api = ref.read(sequenceApiProvider);
  if (api == null) return;
  final messenger = ScaffoldMessenger.of(context);

  Uint8List bytes;
  String exportName;
  try {
    final detail = await api.getSequenceDetail(id);
    exportName = detail.name.trim().isNotEmpty ? detail.name.trim() : name;
    bytes = Uint8List.fromList(utf8.encode(prepareNinaExportJson(detail.body)));
  } catch (e, st) {
    debugPrint('[sequencer] export load failed: $e\n$st');
    if (context.mounted) {
      messenger.showSnackBar(const SnackBar(
        content: Text("Couldn't load the sequence to export."),
        backgroundColor: AraColors.accentError,
      ));
    }
    return;
  }

  if (!context.mounted) return;
  String? saved;
  try {
    saved = await FilePicker.saveFile(
      dialogTitle: 'Export sequence for NINA',
      fileName: sequenceExportFileName(exportName),
      // Enforce the .json extension at the dialog so the user can't accidentally
      // save a name NINA won't load.
      type: FileType.custom,
      allowedExtensions: const ['json'],
      bytes: bytes,
    );
  } catch (e, st) {
    debugPrint('[sequencer] export save failed: $e\n$st');
    if (context.mounted) {
      messenger.showSnackBar(const SnackBar(
        content: Text("Couldn't save the file."),
        backgroundColor: AraColors.accentError,
      ));
    }
    return;
  }
  if (saved == null || !context.mounted) return; // cancelled / unmounted

  // Confirm with the saved filename (the user chose the folder, so the basename
  // is enough and avoids a very long path overflowing the SnackBar).
  final fileName = saved.split(RegExp(r'[/\\]')).last;
  messenger.showSnackBar(SnackBar(
    content: Text('Exported "$exportName" → $fileName'),
    duration: const Duration(seconds: 4),
  ));
}
