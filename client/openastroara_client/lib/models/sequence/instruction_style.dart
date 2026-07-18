import 'package:flutter/material.dart';

import '../../theme/ara_colors.dart';
import 'instruction_catalog.dart';

/// §Run-redesign S7 — one accent hue per instruction category, used as the
/// icon tint in BOTH the palette and the tree so the sequence stops reading as
/// a wall of grey text and becomes scannable by kind. A sidecar (not fields on
/// the catalog) so the catalog stays a pure data model.
///
/// Hues are muted toward the app's dark surface — category identity, not
/// christmas lights; status accents (green/amber/red) keep their §25.2
/// meanings and are NOT reused here except blue-adjacent info tones.
Color instructionCategoryColor(InstructionCategory category) =>
    switch (category) {
      InstructionCategory.camera => const Color(0xFF64B5F6), // sky blue
      InstructionCategory.filterWheel => const Color(0xFFBA68C8), // violet
      InstructionCategory.focuser => const Color(0xFF4DB6AC), // teal
      InstructionCategory.telescope => const Color(0xFFFFD54F), // gold
      InstructionCategory.guider => const Color(0xFF81C784), // soft green
      InstructionCategory.switchDevice => const Color(0xFFFF8A65), // coral
      InstructionCategory.calibration => const Color(0xFFA1887F), // warm grey
      InstructionCategory.utility => const Color(0xFF90A4AE), // blue-grey
      InstructionCategory.container => AraColors.textSecondary,
    };

/// Category colour for a raw tree node: catalogued instructions take their
/// category hue; unknown leaves stay neutral; containers stay neutral (their
/// structure, not their colour, is their identity).
Color nodeAccentColor(Map<String, dynamic> node) {
  final type = node[r'$type'];
  if (type is String) {
    final def = instructionForType(type);
    if (def != null) return instructionCategoryColor(def.category);
  }
  return AraColors.textSecondary;
}
