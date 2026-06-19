/// §38 display metadata for a raw NINA node — pure mappings from a node map to
/// the label/icon the editor surfaces. Lives in the model layer so both the
/// tree view and the field panel (and future editor widgets) share one source
/// without coupling to each other.
library;

import 'package:flutter/material.dart' show IconData, Icons;

import 'instruction_catalog.dart';
import 'nina_dom.dart';

/// The label to show for [node]: the catalog instruction label when known,
/// else a container's `Name` (or short type), else the short `$type`.
String nodeLabel(Map<String, dynamic> node) {
  final type = node[r'$type'];
  if (type is String) {
    final def = instructionForType(type);
    if (def != null) return def.label;
  }
  if (isContainer(node)) {
    final name = node['Name'];
    if (name is String && name.isNotEmpty) return name;
  }
  return shortTypeName(type) ?? 'Unknown';
}

/// The icon for [node]: the catalog icon when known, a folder for an unknown
/// container, else a generic instruction glyph.
IconData nodeIcon(Map<String, dynamic> node) {
  final type = node[r'$type'];
  if (type is String) {
    final def = instructionForType(type);
    if (def != null) return def.icon;
  }
  return isContainer(node) ? Icons.account_tree_outlined : Icons.help_outline;
}

/// `'A.B.C, Asm'` → `'C'`; null/non-string/degenerate (empty or trailing-dot
/// like `'A., Asm'`) → null, so [nodeLabel] falls through to `'Unknown'`.
String? shortTypeName(Object? type) {
  if (type is! String || type.isEmpty) return null;
  final beforeComma = type.split(',').first.trim();
  final lastDot = beforeComma.lastIndexOf('.');
  final short = lastDot >= 0 ? beforeComma.substring(lastDot + 1) : beforeComma;
  return short.isEmpty ? null : short;
}
