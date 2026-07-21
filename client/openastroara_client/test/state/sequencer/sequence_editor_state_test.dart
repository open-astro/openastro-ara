import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/condition_catalog.dart';
import 'package:openastroara/models/sequence/instruction_catalog.dart';
import 'package:openastroara/models/sequence/nina_dom.dart';
import 'package:openastroara/models/sequence/trigger_catalog.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/state/sequencer/sequence_editor_state.dart';

// A template-shaped body: a root SequentialContainer wrapping a SwitchFilter and
// a nested container that holds one TakeExposure.
SequenceDetail sampleDetail() => SequenceDetail(
      id: 'seq-1',
      name: 'M42',
      body: {
        'schemaVersion': 'openastroara-sequence-v1',
        r'$type':
            'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer',
        'Name': 'root',
        'Items': {
          r'$type': itemsWrapperType,
          r'$values': [
            {r'$type': 'X.SwitchFilter', 'Filter': 'L'},
            {
              r'$type': 'X.SequentialContainer',
              'Items': {
                r'$type': itemsWrapperType,
                r'$values': [
                  {r'$type': 'X.TakeExposure', 'ExposureTime': 60.0},
                ],
              },
            },
          ],
        },
      },
    );

InstructionDef takeExposure() => instructionForType(
    'OpenAstroAra.Sequencer.SequenceItem.Imaging.TakeExposure, OpenAstroAra.Sequencer')!;

InstructionDef sequentialContainer() => instructionForType(
    'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer')!;

void main() {
  late ProviderContainer container;
  SequenceEditorController ctrl() =>
      container.read(sequenceEditorProvider.notifier);
  SequenceEditorState? read() => container.read(sequenceEditorProvider);

  setUp(() => container = ProviderContainer());
  tearDown(() => container.dispose());

  test('starts empty', () {
    expect(read(), isNull);
  });

  group('load', () {
    test('seeds body + original, not dirty, nothing selected', () {
      ctrl().load(sampleDetail());
      final s = read()!;
      expect(s.id, 'seq-1');
      expect(s.isDirty, isFalse);
      expect(s.selectedPath, isNull);
      expect(childrenOf(s.body), hasLength(2));
    });

    test('clear resets to null', () {
      ctrl().load(sampleDetail());
      ctrl().clear();
      expect(read(), isNull);
    });
  });

  group('insertInstruction', () {
    test('inserts a built node, selects it, and goes dirty', () {
      ctrl().load(sampleDetail());
      ctrl().insertInstruction(const [], 1, takeExposure());
      final s = read()!;
      expect(s.isDirty, isTrue);
      expect(childrenOf(s.body), hasLength(3));
      // selected the new node at index 1
      expect(s.selectedPath, [1]);
      expect(nodeAt(s.body, [1])![r'$type'], contains('TakeExposure'));
      // it's a real, capturable node (base fields present)
      expect(nodeAt(s.body, [1])!['Attempts'], 1);
    });

    test('clamps the index and selects the landed slot', () {
      ctrl().load(sampleDetail());
      ctrl().insertInstruction(const [], 99, takeExposure());
      expect(read()!.selectedPath, [2]); // clamped to the end
    });

    test('inserts into a nested container', () {
      ctrl().load(sampleDetail());
      ctrl().insertInstruction(const [1], 0, takeExposure());
      final s = read()!;
      expect(childrenOf(nodeAt(s.body, [1])!), hasLength(2));
      expect(s.selectedPath, [1, 0]);
    });

    test('no-op for an unresolvable parent path', () {
      ctrl().load(sampleDetail());
      final before = read()!.body;
      ctrl().insertInstruction(const [9], 0, takeExposure());
      expect(read()!.body, same(before));
    });

    test('no-op when the parent path resolves to a leaf instruction', () {
      ctrl().load(sampleDetail());
      final before = read()!.body;
      // [0] is the SwitchFilter leaf — can't take children.
      ctrl().insertInstruction(const [0], 0, takeExposure());
      expect(read()!.body, same(before));
      expect(read()!.isDirty, isFalse);
    });
  });

  group('addInstruction (selection-relative target)', () {
    test('appends to the root when nothing is selected', () {
      ctrl().load(sampleDetail());
      ctrl().addInstruction(takeExposure());
      final s = read()!;
      expect(childrenOf(s.body), hasLength(3));
      expect(s.selectedPath, [2]); // appended at root end + selected
    });

    test('appends inside a selected container', () {
      ctrl().load(sampleDetail());
      ctrl().select(const [1]); // the nested container
      ctrl().addInstruction(takeExposure());
      final s = read()!;
      expect(childrenOf(nodeAt(s.body, [1])!), hasLength(2));
      expect(s.selectedPath, [1, 1]);
    });

    test('inserts after a selected leaf in its parent', () {
      ctrl().load(sampleDetail());
      ctrl().select(const [0]); // the SwitchFilter leaf at root
      ctrl().addInstruction(takeExposure());
      final s = read()!;
      expect(childrenOf(s.body), hasLength(3));
      expect(s.selectedPath, [1]); // landed right after the leaf
      expect(nodeAt(s.body, [1])![r'$type'], contains('TakeExposure'));
    });

    test('no-op when nothing is loaded', () {
      ctrl().addInstruction(takeExposure());
      expect(read(), isNull);
    });
  });

  group('containers (built from the catalog) nest like any container', () {
    test('a freshly-added container accepts children appended inside it', () {
      ctrl().load(sampleDetail());
      // Add a Sequential container at the root, then drop an exposure into it.
      ctrl().addInstruction(sequentialContainer());
      final addedPath = read()!.selectedPath!; // the new container, selected
      expect(childrenOf(read()!.body), hasLength(3));
      final added = nodeAt(read()!.body, addedPath)!;
      expect(isContainer(added), isTrue);
      expect(childrenOf(added), isEmpty); // starts empty

      // With the container selected, addInstruction nests inside it.
      ctrl().addInstruction(takeExposure());
      final s = read()!;
      final seqNode = nodeAt(s.body, addedPath)!;
      expect(childrenOf(seqNode), hasLength(1));
      expect(childrenOf(seqNode).single[r'$type'], contains('TakeExposure'));
      expect(s.selectedPath, [...addedPath, 0]);
    });

    test('the built container round-trips its full template shape', () {
      ctrl().load(sampleDetail());
      ctrl().addInstruction(sequentialContainer());
      final node = nodeAt(read()!.body, read()!.selectedPath!)!;
      expect(node['Name'], 'Sequential Instruction Set');
      expect((node['Strategy'] as Map)[r'$type'], contains('SequentialStrategy'));
      expect(node['Triggers'], isA<Map>());
      expect(node['Conditions'], isA<Map>());
    });
  });

  group('removeNode', () {
    test('removes and clears selection', () {
      ctrl().load(sampleDetail());
      ctrl().select(const [0]);
      ctrl().removeNode(const [0]);
      final s = read()!;
      expect(childrenOf(s.body), hasLength(1));
      expect(s.selectedPath, isNull);
      expect(s.isDirty, isTrue);
    });

    test('rejects the root and unresolvable paths (no-op)', () {
      ctrl().load(sampleDetail());
      final before = read()!.body;
      ctrl().removeNode(const []);
      ctrl().removeNode(const [9]);
      expect(read()!.body, same(before));
    });
  });

  group('reorder', () {
    test('reorders children and clears selection', () {
      ctrl().load(sampleDetail());
      ctrl().select(const [0]);
      // Flutter onReorder: drag child 0 to the end of a 2-item list → newIndex 2.
      ctrl().reorder(const [], 0, 2);
      final s = read()!;
      expect(childrenOf(s.body).map((n) => n[r'$type']),
          ['X.SequentialContainer', 'X.SwitchFilter']);
      expect(s.selectedPath, isNull);
    });

    test('no-op for an out-of-range oldIndex', () {
      ctrl().load(sampleDetail());
      final before = read()!.body;
      ctrl().reorder(const [], 9, 0);
      expect(read()!.body, same(before));
    });
  });

  group('moveNode', () {
    test('moves a child down and keeps it selected', () {
      ctrl().load(sampleDetail()); // root: [SwitchFilter, SequentialContainer]
      ctrl().select(const [0]);
      ctrl().moveNode(const [0], up: false);
      final s = read()!;
      expect(childrenOf(s.body).map((n) => n[r'$type']),
          ['X.SequentialContainer', 'X.SwitchFilter']);
      expect(s.selectedPath, [1]); // selection followed the node
      expect(s.isDirty, isTrue);
    });

    test('moves a child up and keeps it selected', () {
      ctrl().load(sampleDetail());
      ctrl().moveNode(const [1], up: true);
      final s = read()!;
      expect(childrenOf(s.body).map((n) => n[r'$type']),
          ['X.SequentialContainer', 'X.SwitchFilter']);
      expect(s.selectedPath, [0]);
    });

    test('no-op at the boundaries (first up / last down) and for the root', () {
      ctrl().load(sampleDetail());
      final before = read()!.body;
      ctrl().moveNode(const [0], up: true); // already first
      ctrl().moveNode(const [1], up: false); // already last
      ctrl().moveNode(const [], up: true); // root
      expect(read()!.body, same(before));
    });
  });

  group('moveNodeTo (drag-and-drop reparent)', () {
    test('reparents a leaf into a sibling container, clears selection, dirty', () {
      ctrl().load(sampleDetail()); // root: [SwitchFilter, container([TakeExposure])]
      ctrl().select(const [0]);
      ctrl().moveNodeTo(const [0], const [1], 0); // SwitchFilter into the container
      final s = read()!;
      expect(childrenOf(s.body), hasLength(1)); // root now just the container
      final container = nodeAt(s.body, [0])!;
      expect(childrenOf(container).map((n) => n[r'$type']),
          ['X.SwitchFilter', 'X.TakeExposure']);
      expect(s.selectedPath, isNull); // selection cleared (paths shifted)
      expect(s.isDirty, isTrue);
    });

    test('reparents a nested leaf out to the root container', () {
      ctrl().load(sampleDetail());
      ctrl().moveNodeTo(const [1, 0], const [], 0); // TakeExposure → root[0]
      final s = read()!;
      expect(childrenOf(s.body), hasLength(3));
      expect(childrenOf(s.body)[0][r'$type'], 'X.TakeExposure');
      expect(childrenOf(nodeAt(s.body, [2])!), isEmpty); // the emptied container
      expect(s.isDirty, isTrue);
    });

    test('no-op into a leaf, into self/descendant, or for the root', () {
      ctrl().load(sampleDetail());
      final before = read()!.body;
      ctrl().moveNodeTo(const [1], const [0], 0); // [0] is a leaf → not a container
      ctrl().moveNodeTo(const [1], const [1], 0); // into self
      ctrl().moveNodeTo(const [1], const [1, 0], 0); // into descendant
      ctrl().moveNodeTo(const [], const [1], 0); // moving the root
      expect(read()!.body, same(before));
    });
  });

  group('setNodeField', () {
    test('edits a field, keeps selection, goes dirty', () {
      ctrl().load(sampleDetail());
      ctrl().select(const [1, 0]);
      ctrl().setNodeField(const [1, 0], 'ExposureTime', 120.0);
      final s = read()!;
      expect(nodeAt(s.body, [1, 0])!['ExposureTime'], 120.0);
      expect(s.selectedPath, [1, 0]); // selection preserved
      expect(s.isDirty, isTrue);
    });

    test('no-op for an unresolvable path', () {
      ctrl().load(sampleDetail());
      final before = read()!.body;
      ctrl().setNodeField(const [9], 'x', 1);
      expect(read()!.body, same(before));
    });
  });

  group('conditions on a container', () {
    ConditionDef loopCondition() => conditionForType(
        'OpenAstroAra.Sequencer.Conditions.LoopCondition, OpenAstroAra.Sequencer')!;

    test('addConditionTo appends to a container, keeps selection, goes dirty', () {
      ctrl().load(sampleDetail());
      ctrl().select(const [1]); // the nested container
      ctrl().addConditionTo(const [1], loopCondition());
      final s = read()!;
      final container = nodeAt(s.body, [1])!;
      expect(conditionsOf(container), hasLength(1));
      expect(conditionsOf(container).single['Iterations'], 2);
      expect(s.selectedPath, [1]); // container stays selected
      expect(s.isDirty, isTrue);
    });

    test('addConditionTo is a no-op on a leaf (no spurious Conditions wrapper)', () {
      ctrl().load(sampleDetail());
      final before = read()!.body;
      ctrl().addConditionTo(const [0], loopCondition()); // [0] is a leaf
      expect(read()!.body, same(before));
    });

    test('setConditionFieldOn edits a condition in place', () {
      ctrl().load(sampleDetail());
      ctrl().addConditionTo(const [1], loopCondition());
      ctrl().setConditionFieldOn(const [1], 0, 'Iterations', 12);
      expect(conditionsOf(nodeAt(read()!.body, [1])!).single['Iterations'], 12);
    });

    test('removeConditionFrom drops one and bounds-checks (no-op out of range)', () {
      ctrl().load(sampleDetail());
      ctrl().addConditionTo(const [1], loopCondition());
      final afterAdd = read()!.body;
      ctrl().removeConditionFrom(const [1], 9); // out of range → no-op
      expect(read()!.body, same(afterAdd));
      ctrl().removeConditionFrom(const [1], 0);
      expect(conditionsOf(nodeAt(read()!.body, [1])!), isEmpty);
    });
  });

  group('triggers on a container', () {
    TriggerDef meridianFlip() => triggerForType(
        'OpenAstroAra.Sequencer.Trigger.MeridianFlip.MeridianFlipTrigger, OpenAstroAra.Sequencer')!;

    test('addTriggerTo appends to a container, keeps selection, goes dirty', () {
      ctrl().load(sampleDetail());
      ctrl().select(const [1]); // the nested container
      ctrl().addTriggerTo(const [1], meridianFlip());
      final s = read()!;
      final container = nodeAt(s.body, [1])!;
      expect(triggersOf(container), hasLength(1));
      expect(triggersOf(container).single['TriggerRunner'], isA<Map>());
      expect(s.selectedPath, [1]); // container stays selected
      expect(s.isDirty, isTrue);
    });

    test('addTriggerTo is a no-op on a leaf (no spurious Triggers wrapper)', () {
      ctrl().load(sampleDetail());
      final before = read()!.body;
      ctrl().addTriggerTo(const [0], meridianFlip()); // [0] is a leaf
      expect(read()!.body, same(before));
    });

    test('removeTriggerFrom drops one and bounds-checks (no-op out of range)', () {
      ctrl().load(sampleDetail());
      ctrl().addTriggerTo(const [1], meridianFlip());
      final afterAdd = read()!.body;
      ctrl().removeTriggerFrom(const [1], 9); // out of range → no-op
      expect(read()!.body, same(afterAdd));
      ctrl().removeTriggerFrom(const [1], 0);
      expect(triggersOf(nodeAt(read()!.body, [1])!), isEmpty);
    });

    test('setTriggerFieldOn edits a trigger in place, guards no-ops', () {
      ctrl().load(sampleDetail());
      ctrl().addTriggerTo(const [1], meridianFlip());
      ctrl().setTriggerFieldOn(const [1], 0, 'Parent', 'x');
      expect(triggersOf(nodeAt(read()!.body, [1])!).single['Parent'], 'x');
      final before = read()!.body;
      ctrl().setTriggerFieldOn(const [1], 9, 'Parent', 'y'); // out of range → no-op
      expect(read()!.body, same(before));
      ctrl().setTriggerFieldOn(const [0], 0, 'Parent', 'y'); // leaf → no triggers → no-op
      expect(read()!.body, same(before));
    });
  });

  group('select', () {
    test('sets and clears selection, ignoring a stale path', () {
      ctrl().load(sampleDetail());
      ctrl().select(const [1, 0]);
      expect(read()!.selectedPath, [1, 0]);
      ctrl().select(null);
      expect(read()!.selectedPath, isNull);
      // stale path is ignored (selection stays null)
      ctrl().select(const [5]);
      expect(read()!.selectedPath, isNull);
    });
  });

  group('dirty-tracking / markSaved', () {
    test('markSaved(sentBody) rebaselines so isDirty reads false until the next edit', () {
      ctrl().load(sampleDetail());
      ctrl().setNodeField(const [0], 'Filter', 'R');
      expect(read()!.isDirty, isTrue);
      ctrl().markSaved(read()!.body); // baseline = the just-saved body
      expect(read()!.isDirty, isFalse);
      ctrl().setNodeField(const [0], 'Filter', 'B');
      expect(read()!.isDirty, isTrue);
    });

    test('markSaved against a stale snapshot keeps a mid-save edit dirty', () {
      ctrl().load(sampleDetail());
      ctrl().setNodeField(const [0], 'Filter', 'R');
      final sent = read()!.body; // snapshot "sent" to the daemon
      // An edit lands while the (hypothetical) PATCH is in flight.
      ctrl().setNodeField(const [0], 'Filter', 'B');
      ctrl().markSaved(sent); // rebaseline to what was actually saved
      // The mid-flight edit ('B') is NOT the saved body → still dirty.
      expect(read()!.isDirty, isTrue);
    });

    test('an edit that restores the original value is not dirty', () {
      ctrl().load(sampleDetail());
      ctrl().setNodeField(const [0], 'Filter', 'R');
      ctrl().setNodeField(const [0], 'Filter', 'L'); // back to the original
      expect(read()!.isDirty, isFalse);
    });
  });

  group('undo/redo (S12)', () {
    test('every structural mutator is undoable; redo replays; edits clear redo',
        () {
      final n = ctrl();
      n.load(sampleDetail());
      final original = read()!.body;

      n.addInstruction(takeExposure());
      final afterAdd = read()!.body;
      expect(n.canUndo, isTrue);

      n.undo();
      expect(identical(read()!.body, original), isTrue);
      expect(read()!.selectedPath, isNull,
          reason: 'undo clears selection (stale paths are a trap)');
      expect(n.canRedo, isTrue);

      n.redo();
      expect(identical(read()!.body, afterAdd), isTrue);

      // A fresh edit clears the redo stack.
      n.undo();
      n.addInstruction(takeExposure());
      expect(n.canRedo, isFalse);
    });

    test('consecutive edits to the same field coalesce into one undo step', () {
      final n = ctrl();
      n.load(sampleDetail());
      final original = read()!.body;
      n.addInstruction(takeExposure());
      final sel = read()!.selectedPath!;
      n.setNodeField(sel, 'ExposureTime', 1.0);
      n.setNodeField(sel, 'ExposureTime', 12.0);
      n.setNodeField(sel, 'ExposureTime', 120.0);
      n.undo(); // one step back over ALL three keystroke edits
      final node = nodeAt(read()!.body, sel);
      expect(node!['ExposureTime'], isNot(120.0));
      n.undo(); // back over the insert
      expect(identical(read()!.body, original), isTrue);
      expect(n.canUndo, isFalse);
    });

    test('load resets history', () {
      final n = ctrl();
      n.load(sampleDetail());
      n.addInstruction(takeExposure());
      n.load(sampleDetail());
      expect(n.canUndo, isFalse);
      expect(n.canRedo, isFalse);
    });

    test('undo cap holds at 50 snapshots', () {
      final n = ctrl();
      n.load(sampleDetail());
      for (var i = 0; i < 60; i++) {
        n.addInstruction(takeExposure());
      }
      var undos = 0;
      while (n.canUndo) {
        n.undo();
        undos++;
      }
      expect(undos, SequenceEditorController.undoCap);
    });
  });

  group('selectAdjacent (arrow-key navigation)', () {
    // sampleDetail flattens depth-first to: [], [0], [1], [1,0].
    test('walks rows in tree order and stops at both ends', () {
      final c = ProviderContainer();
      addTearDown(c.dispose);
      final n = c.read(sequenceEditorProvider.notifier);
      n.load(sampleDetail());

      n.selectAdjacent(next: true); // nothing selected → first row (the root)
      expect(c.read(sequenceEditorProvider)!.selectedPath, isEmpty);
      n.selectAdjacent(next: true);
      expect(c.read(sequenceEditorProvider)!.selectedPath, [0]);
      n.selectAdjacent(next: true);
      expect(c.read(sequenceEditorProvider)!.selectedPath, [1]);
      n.selectAdjacent(next: true);
      expect(c.read(sequenceEditorProvider)!.selectedPath, [1, 0]);
      n.selectAdjacent(next: true); // last row — stays put
      expect(c.read(sequenceEditorProvider)!.selectedPath, [1, 0]);
      n.selectAdjacent(next: false);
      expect(c.read(sequenceEditorProvider)!.selectedPath, [1]);
    });

    test('Up with nothing selected lands on the last row; body untouched', () {
      final c = ProviderContainer();
      addTearDown(c.dispose);
      final n = c.read(sequenceEditorProvider.notifier);
      n.load(sampleDetail());
      final before = c.read(sequenceEditorProvider)!.body;
      n.selectAdjacent(next: false);
      final after = c.read(sequenceEditorProvider)!;
      expect(after.selectedPath, [1, 0]);
      expect(identical(after.body, before), isTrue,
          reason: 'navigation must never touch the body (no dirty, no undo)');
      expect(n.canUndo, isFalse);
    });
  });
}
