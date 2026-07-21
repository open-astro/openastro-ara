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

/// One-line "what this does" per instruction label (S8 palette cards).
/// Keyed by catalog LABEL (stable product copy, no wire types); an absent
/// entry renders a single-line tile — additions are one line each.
const Map<String, String> instructionDescriptions = {
  'Take Exposure': 'Capture one frame with the current settings.',
  'Smart Exposure': 'Exposure loop with dither + filter handling built in.',
  'Cool Camera': 'Ramp the sensor down to the target temperature.',
  'Warm Camera': 'Ramp the sensor safely back up before shutdown.',
  'Switch Filter': 'Rotate the wheel to a chosen filter.',
  'Flat Panel Flats': 'Automated flats against the flat panel.',
  'Sky Flats': 'Twilight-sky flats with auto exposure.',
  'Run Autofocus': 'Focus sweep and return to best HFR position.',
  'Slew to RA/Dec': 'Point the mount at fixed coordinates.',
  'Center and Rotate': 'Plate-solve, centre precisely, match rotation.',
  'Set Tracking': 'Turn sidereal tracking on or off.',
  'Park Scope': 'Send the mount to its park position.',
  'Unpark Scope': 'Release the mount from park.',
  'Start Guiding': 'Begin PHD2 guiding (calibrates if needed).',
  'Stop Guiding': 'Stop PHD2 guiding.',
  'Dither': 'Nudge pointing between frames to break up pattern noise.',
  'Set Switch Value': 'Drive a power/dew port on a switch hub.',
  'Wait (duration)': 'Pause the sequence for a fixed time.',
  'Wait (until time)': 'Hold until a clock time (e.g. astro dark).',
  'Annotation': 'A note to yourself inside the plan.',
  'Wait for User':
      'Pauses the run until you press Resume — for manual filter swaps and '
          'other hands-on steps.',
  'Sequential Instruction Set': 'Run children in order, with loops + triggers.',
  'Parallel Instruction Set': 'Run children at the same time.',
  'Deep Sky Object': 'A target block: coordinates + its own instructions.',
};
