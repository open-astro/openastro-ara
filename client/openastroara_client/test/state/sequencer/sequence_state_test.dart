import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_node.dart';
import 'package:openastroara/state/sequencer/sequence_state.dart';

void main() {
  group('SequenceController', () {
    late ProviderContainer container;
    late SequenceController ctrl;

    setUp(() {
      container = ProviderContainer();
      ctrl = container.read(sequenceControllerProvider.notifier);
    });
    tearDown(() => container.dispose());

    test('demo sequence has a root node + one area + targets', () {
      final root = container.read(sequenceControllerProvider);
      expect(root.id, 'root');
      expect(root.kind, SequenceNodeKind.root);
      expect(root.children, isNotEmpty);
    });

    test('findNode locates root, deep child, and returns null on miss', () {
      final root = container.read(sequenceControllerProvider);
      expect(findNode(root, 'root'), same(root));
      expect(findNode(root, 'target-1')?.id, 'target-1');
      expect(findNode(root, 'instr-1-1')?.id, 'instr-1-1');
      expect(findNode(root, 'no-such-id'), isNull);
    });

    test('load swaps the root + clears selection', () {
      ctrl.addChild('target-1',
          kind: SequenceNodeKind.instruction, instructionType: 'X');
      final newId = container.read(selectedNodeIdProvider);
      expect(newId, isNotNull);
      ctrl.load(SequenceNode(
        id: 'fresh-root',
        kind: SequenceNodeKind.root,
        displayName: 'Fresh',
      ));
      expect(container.read(sequenceControllerProvider).id, 'fresh-root');
      expect(container.read(selectedNodeIdProvider), isNull);
    });

    test('addChild refuses instruction parents (single-root invariant guard)', () {
      // Phase 12 cleanup-2 contract.
      ctrl.addChild('instr-1-1',
          kind: SequenceNodeKind.instruction, instructionType: 'SubInstr');
      final inst = findNode(container.read(sequenceControllerProvider),
          'instr-1-1');
      expect(inst?.children, isEmpty);
    });

    test('addChild refuses SequenceNodeKind.root', () {
      ctrl.addChild('target-1', kind: SequenceNodeKind.root);
      final target = findNode(container.read(sequenceControllerProvider),
          'target-1')!;
      // No new child added.
      expect(target.children.any((c) => c.kind == SequenceNodeKind.root),
          isFalse);
    });

    test('addChild appends + auto-selects the new node', () {
      ctrl.addChild('target-1',
          kind: SequenceNodeKind.instruction, instructionType: 'NewInstr');
      final selected = container.read(selectedNodeIdProvider);
      expect(selected, isNotNull);
      final newNode = findNode(
          container.read(sequenceControllerProvider), selected!);
      expect(newNode, isNotNull);
      expect(newNode!.instructionType, 'NewInstr');
    });

    test('addSiblingAfter inserts at idx+1 + selects', () {
      // instr-1-1 is the first child of target-1; after this call the new
      // sibling should be at index 1.
      ctrl.addSiblingAfter('instr-1-1',
          kind: SequenceNodeKind.instruction, instructionType: 'Inserted');
      final newId = container.read(selectedNodeIdProvider)!;
      final target = findNode(
          container.read(sequenceControllerProvider), 'target-1')!;
      final idx = target.children.indexWhere((c) => c.id == newId);
      expect(idx, 1);
    });

    test('addSiblingAfter refuses root kind', () {
      final before = container.read(sequenceControllerProvider);
      ctrl.addSiblingAfter('target-1', kind: SequenceNodeKind.root);
      expect(container.read(sequenceControllerProvider), equals(before));
    });

    test('moveSelectedUp swaps with the previous sibling', () {
      // Move instr-1-2 (autofocus) above instr-1-1 (slew).
      container.read(selectedNodeIdProvider.notifier).select('instr-1-2');
      ctrl.moveSelectedUp();
      final target = findNode(
          container.read(sequenceControllerProvider), 'target-1')!;
      expect(target.children.first.id, 'instr-1-2');
      expect(target.children[1].id, 'instr-1-1');
    });

    test('moveSelectedUp on first sibling is a no-op', () {
      container.read(selectedNodeIdProvider.notifier).select('instr-1-1');
      final before = container.read(sequenceControllerProvider);
      ctrl.moveSelectedUp();
      // No change since instr-1-1 is already at index 0.
      expect(container.read(sequenceControllerProvider), equals(before));
    });

    test('moveSelectedDown swaps with the next sibling', () {
      container.read(selectedNodeIdProvider.notifier).select('instr-1-1');
      ctrl.moveSelectedDown();
      final target = findNode(
          container.read(sequenceControllerProvider), 'target-1')!;
      expect(target.children.first.id, 'instr-1-2');
      expect(target.children[1].id, 'instr-1-1');
    });

    test('deleteSelected removes node + clears selection', () {
      container.read(selectedNodeIdProvider.notifier).select('instr-1-1');
      ctrl.deleteSelected();
      expect(
        findNode(container.read(sequenceControllerProvider), 'instr-1-1'),
        isNull,
      );
      expect(container.read(selectedNodeIdProvider), isNull);
    });

    test('deleteSelected on root is a no-op', () {
      container.read(selectedNodeIdProvider.notifier).select('root');
      final before = container.read(sequenceControllerProvider);
      ctrl.deleteSelected();
      expect(container.read(sequenceControllerProvider), equals(before));
    });

    test('moveSelected with no selection is a no-op', () {
      container.read(selectedNodeIdProvider.notifier).select(null);
      final before = container.read(sequenceControllerProvider);
      ctrl.moveSelectedUp();
      ctrl.moveSelectedDown();
      expect(container.read(sequenceControllerProvider), equals(before));
    });
  });
}
