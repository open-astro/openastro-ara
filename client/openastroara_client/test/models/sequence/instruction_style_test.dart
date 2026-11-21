import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/instruction_catalog.dart';
import 'package:openastroara/models/sequence/instruction_style.dart';

void main() {
  test('every catalog category has a distinct accent hue', () {
    final colors = {
      for (final c in InstructionCategory.values) c: instructionCategoryColor(c),
    };
    // Total coverage (the switch is exhaustive by construction) and no two
    // NON-neutral categories share a hue.
    expect(colors.length, InstructionCategory.values.length);
    final nonContainer = colors.entries
        .where((e) => e.key != InstructionCategory.container)
        .map((e) => e.value)
        .toList();
    expect(nonContainer.toSet().length, nonContainer.length,
        reason: 'category hues must be distinguishable');
  });
}
