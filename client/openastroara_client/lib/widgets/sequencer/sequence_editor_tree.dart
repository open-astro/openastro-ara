/// §38 sequence-editor tree view — renders the RAW NINA body held by
/// [sequenceEditorProvider] (not the lossy `SequenceNode` preview) as a flat,
/// indented, selectable list. Tapping a row selects that node (by [NodePath]),
/// which the field editor + palette react to; the selected row gets move-up /
/// move-down / delete actions. Rows are also drag sources (long-press) that drop
/// **onto a container** to move (reparent) into it, or into a thin gap **between
/// rows** to position at a precise index — including a trailing gap below a
/// container's last child to append at its end; the move-up/down buttons remain
/// for within-parent ordering.
library;

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/nina_dom.dart';
import '../../models/sequence/nina_sequence_parser.dart' show ninaParseMaxDepth;
import '../../models/sequence/instruction_style.dart';
import '../../models/sequence/node_display.dart';
import '../../state/sequencer/run_spotlight_state.dart';
import '../../state/sequencer/sequence_editor_state.dart';
import '../../state/sequencer/sequence_list_state.dart';
import 'run_state_badge.dart';
import '../../theme/ara_colors.dart';

/// One flattened tree row: the node, its address, and its indent depth.
class _Row {
  final NodePath path;
  final Map<String, dynamic> node;
  final int depth;
  const _Row(this.path, this.node, this.depth);
}

/// The move-up / move-down / delete buttons for the selected non-root [path].
/// Up/down disable at the sibling boundaries.
List<Widget> _rowActions(WidgetRef ref, Map<String, dynamic> body, NodePath path) {
  final index = path.last;
  final parent = nodeAt(body, path.sublist(0, path.length - 1));
  final siblingCount = parent == null ? 0 : childrenOf(parent).length;
  final notifier = ref.read(sequenceEditorProvider.notifier);
  return [
    _RowIcon(
      icon: Icons.arrow_upward,
      tooltip: 'Move up',
      onPressed: index > 0 ? () => notifier.moveNode(path, up: true) : null,
    ),
    _RowIcon(
      icon: Icons.arrow_downward,
      tooltip: 'Move down',
      onPressed:
          index < siblingCount - 1 ? () => notifier.moveNode(path, up: false) : null,
    ),
    _RowIcon(
      icon: Icons.delete_outline,
      tooltip: 'Delete',
      onPressed: () => notifier.removeNode(path),
    ),
  ];
}

/// Whether the subtree at [dragged] can be dropped INTO the container at
/// [target] (append). True iff [target] resolves to a container, isn't [dragged]
/// itself, isn't inside [dragged] (would orphan the subtree), and the move isn't
/// a pure no-op (dropping a node onto its own parent when it's already the last
/// child). Pure so the drag policy is unit-tested without a gesture harness.
@visibleForTesting
bool canReparentInto(Map<String, dynamic> body, NodePath dragged, NodePath target) {
  if (dragged.isEmpty) return false; // the root can't be dragged (`.last` would throw)
  final targetNode = nodeAt(body, target);
  if (targetNode == null || !isContainer(targetNode)) return false;
  if (listEquals(dragged, target)) return false; // onto itself
  if (isAncestorOrSelf(dragged, target)) return false; // into self/descendant
  // No-op: dragged is already the last child of target → dropping changes nothing.
  final isDirectChild =
      dragged.length == target.length + 1 && isAncestorOrSelf(target, dragged);
  if (isDirectChild && dragged.last == childrenOf(targetNode).length - 1) {
    return false;
  }
  return true;
}

/// Resolve a "drop [dragged] just BEFORE the row at [beforePath]" gesture (an
/// insert-between-rows gap) to the `(parent, index)` for
/// [SequenceEditorController.moveNodeTo] — i.e. drop it into `beforePath`'s
/// parent at `beforePath`'s slot — or null when invalid or a no-op. [index] is
/// the post-removal index `moveNodeTo` expects. Pure, so the gap policy is
/// unit-tested without a gesture harness.
@visibleForTesting
({NodePath parent, int index})? resolveDropBefore(
    Map<String, dynamic> body, NodePath dragged, NodePath beforePath) {
  // The root isn't movable, and there's no gap "before the root".
  if (dragged.isEmpty || beforePath.isEmpty) return null;
  // Can't drop a subtree before itself or before a row inside it (orphan/cycle).
  // This also covers the gap's parent: `parent` is a strict prefix of
  // `beforePath`, so a `dragged` that contains `parent` contains `beforePath`
  // too and is already rejected here.
  if (isAncestorOrSelf(dragged, beforePath)) return null;
  final parent = beforePath.sublist(0, beforePath.length - 1);
  final draggedParent = dragged.sublist(0, dragged.length - 1);
  var index = beforePath.last; // pre-removal slot of the target row
  if (listEquals(draggedParent, parent)) {
    // Same-parent reorder: dropping before itself, or before the row that
    // immediately follows it, lands it where it already is → no-op.
    if (beforePath.last == dragged.last || beforePath.last == dragged.last + 1) {
      return null;
    }
    // Pulling the node out shifts every later sibling (incl. the target) down one.
    if (dragged.last < beforePath.last) index -= 1;
  }
  return (parent: parent, index: index);
}

/// Resolve a "drop [dragged] at the END of the container at [container]" gesture
/// (a trailing append gap below a container's last child) to the
/// `(parent, index)` for [SequenceEditorController.moveNodeTo], or null when
/// invalid/no-op. Reuses [canReparentInto] for the validity rules (container,
/// not self/descendant, not an already-last-child no-op); [index] is the
/// post-removal append slot. Pure, for unit testing without a gesture harness.
@visibleForTesting
({NodePath parent, int index})? resolveDropInto(
    Map<String, dynamic> body, NodePath dragged, NodePath container) {
  if (!canReparentInto(body, dragged, container)) return null;
  final node = nodeAt(body, container)!; // canReparentInto proved it's a container
  final draggedParent = dragged.sublist(0, dragged.length - 1);
  // Pulling a same-parent source out first shrinks the child list by one, so the
  // append slot is one less than the current count.
  final sameParent = listEquals(draggedParent, container);
  final index = childrenOf(node).length - (sameParent ? 1 : 0);
  return (parent: container, index: index);
}

/// The (non-root) container paths whose subtree ENDS at the flattened row
/// [rowPath] — i.e. the next flattened row [nextPath] (or null at the list end)
/// is outside them. Each yields a trailing "append into this container" gap
/// below [rowPath], deepest-first so the gaps step out to match the indent. A
/// container with children contributes its gap at its last descendant, not at
/// its own row; an empty container contributes at its own row. Pure, for unit
/// testing. The root ([]) is never returned (append-to-root is the root row's
/// own drop target).
@visibleForTesting
List<NodePath> appendGapContainers(
    Map<String, dynamic> body, NodePath rowPath, NodePath? nextPath) {
  final out = <NodePath>[];
  for (var len = rowPath.length; len >= 1; len--) {
    final p = rowPath.sublist(0, len);
    // Once the next row is inside p, it's inside every shallower ancestor too —
    // none of them close here, so stop.
    if (nextPath != null && isAncestorOrSelf(p, nextPath)) break;
    final node = nodeAt(body, p);
    if (node != null && isContainer(node)) out.add(p);
  }
  return out;
}

/// Stable widget key for the "insert before [path]" gap (also the test handle).
@visibleForTesting
String gapBeforeKey(NodePath path) => 'gap_before_${path.join(".")}';

/// Stable widget key for the "append into [container]" trailing gap.
@visibleForTesting
String gapAppendKey(NodePath container) => 'gap_append_${container.join(".")}';

void _flatten(Map<String, dynamic> node, NodePath path, int depth, List<_Row> out) {
  out.add(_Row(path, node, depth));
  // Stop recursing past the same adversarial-depth cap the parser uses, so a
  // malformed/pathologically-nested body can't blow the stack.
  if (depth >= ninaParseMaxDepth) return;
  final kids = childrenOf(node);
  for (var i = 0; i < kids.length; i++) {
    _flatten(kids[i], <int>[...path, i], depth + 1, out);
  }
}

/// Indented, selectable view of the editor's working sequence body.
class SequenceEditorTree extends ConsumerStatefulWidget {
  const SequenceEditorTree({super.key});

  @override
  ConsumerState<SequenceEditorTree> createState() => _SequenceEditorTreeState();
}

class _SequenceEditorTreeState extends ConsumerState<SequenceEditorTree> {
  final _scroll = ScrollController();
  // S9 — hover-revealed row actions (desktop): tracked by flattened row key so
  // one row at most shows its affordances; selection still shows them too (the
  // touch fallback).
  String? _hoveredKey;

  @override
  void dispose() {
    _scroll.dispose();
    super.dispose();
  }

  // §Run-redesign S4 — keep the executing row in view. Approximate scroll (row
  // heights vary with gap targets): proportional position by row index, only
  // fired when the SPOTLIGHTED row changes so a user browsing the tree isn't
  // fought for the scrollbar every WS tick.
  void _autoScrollTo(int rowIndex, int rowCount) {
    if (!_scroll.hasClients || rowCount <= 1) return;
    final target = _scroll.position.maxScrollExtent * rowIndex / (rowCount - 1);
    _scroll.animateTo(target,
        duration: const Duration(milliseconds: 300), curve: Curves.easeOut);
  }

  @override
  Widget build(BuildContext context) {
    final editor = ref.watch(sequenceEditorProvider);
    if (editor == null) {
      return const Center(
        child: Text('No sequence loaded',
            style: TextStyle(color: AraColors.textSecondary)),
      );
    }

    final rows = <_Row>[];
    _flatten(editor.body, const [], 0, rows);
    final selected = editor.selectedPath;

    // §Run-redesign S4 — live spotlight: the executing node gets an accent bar
    // + tint in the run-state colour; ordinal-verified completed leaves get a
    // quiet check tint. Null spotlight = render exactly as before.
    final spotlight = ref.watch(runSpotlightProvider);
    final runState = ref.watch(sequenceRunStateProvider).value?.state;
    final spotColor =
        runState == null ? AraColors.accentBusy : RunStateBadge.colorFor(runState);
    final currentKey = spotlight?.currentPath?.join('.');
    final completedKeys = <String>{
      for (final p in spotlight?.completedPaths ?? const <NodePath>[]) p.join('.'),
    };
    ref.listen(runSpotlightProvider, (prev, next) {
      final path = next?.currentPath;
      if (path == null) return;
      if (prev?.currentPath?.join('.') == path.join('.')) return;
      final idx = rows.indexWhere((r) => listEquals(r.path, path));
      if (idx >= 0) _autoScrollTo(idx, rows.length);
    });

    return ListView.builder(
      controller: _scroll,
      itemCount: rows.length,
      itemBuilder: (context, i) {
        final row = rows[i];
        final isSelected = selected != null && listEquals(row.path, selected);
        final rowKey = row.path.join('.');
        final isCurrent = currentKey != null && rowKey == currentKey;
        final isCompleted = completedKeys.contains(rowKey);
        final isContainerRow = isContainer(row.node);
        final conditionCount = isContainerRow ? conditionsOf(row.node).length : 0;
        final triggerCount = isContainerRow ? triggersOf(row.node).length : 0;
        final showActions =
            (isSelected || _hoveredKey == rowKey) && row.path.isNotEmpty;
        final content = ConstrainedBox(
          // S9 — rows breathe: 36px minimum instead of text-height + 12.
          constraints: const BoxConstraints(minHeight: 36),
          child: Padding(
          padding: EdgeInsets.only(
            left: 8 + row.depth * 20.0,
            right: 8,
            top: 6,
            bottom: 6,
          ),
          child: Row(
            children: [
              Icon(isCompleted ? Icons.check_circle_outline : nodeIcon(row.node),
                  size: 16,
                  color: isCurrent
                      ? spotColor
                      : isCompleted
                          ? AraColors.accentConnected.withValues(alpha: 0.7)
                          // S7 — category hue per instruction kind.
                          : nodeAccentColor(row.node)),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  nodeLabel(row.node),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                      color: AraColors.textPrimary,
                      fontSize: 13,
                      // Containers are structure — a touch of weight sets the
                      // grouping without another colour.
                      fontWeight:
                          isContainerRow ? FontWeight.w600 : FontWeight.w400),
                ),
              ),
              // S9 — a container's loops/triggers surface inline as quiet
              // chips instead of hiding in the inspector.
              if (conditionCount > 0)
                _MetaChip(icon: Icons.loop, count: conditionCount),
              if (triggerCount > 0)
                _MetaChip(icon: Icons.bolt_outlined, count: triggerCount),
              // Reorder + delete affordances on the selected row — never the
              // root, which can't move or be removed (it's the sequence
              // container itself).
              if (showActions) ..._rowActions(ref, editor.body, row.path),
            ],
          ),
          ),
        );
        // Every row (root included) is a drop target — dropping a dragged node
        // onto a container moves it inside; only non-root rows are drag sources.
        final rowTarget = DragTarget<NodePath>(
          onWillAcceptWithDetails: (d) =>
              canReparentInto(editor.body, d.data, row.path),
          onAcceptWithDetails: (d) {
            // Re-read current state at drop time rather than the build-time
            // snapshot, in case the body changed between onWillAccept and now.
            // (moveNodeTo re-validates and clamps too — this just keeps the
            // append index honest against the live child count.)
            final current = ref.read(sequenceEditorProvider);
            final target =
                current == null ? null : nodeAt(current.body, row.path);
            if (target == null) return;
            ref
                .read(sequenceEditorProvider.notifier)
                .moveNodeTo(d.data, row.path, childrenOf(target).length);
          },
          builder: (context, candidate, rejected) {
            final hovering = candidate.isNotEmpty;
            // Ink (not Container(color:)) so the InkWell splash paints ABOVE the
            // tint instead of being hidden by it.
            return Ink(
              // Hover > selection > live spotlight tint > container band.
              decoration: BoxDecoration(
                color: hovering
                    ? AraColors.accentInfo.withValues(alpha: 0.22)
                    : (isSelected
                        ? AraColors.selectionBg.withValues(alpha: 0.25)
                        : isCurrent
                            ? spotColor.withValues(alpha: 0.10)
                            : isContainerRow && row.path.isNotEmpty
                                ? AraColors.bgPanelAlt.withValues(alpha: 0.6)
                                : null),
                // The executing row carries an accent bar on its leading edge —
                // visible from across the room, unlike a text tint.
                border: isCurrent
                    ? Border(left: BorderSide(color: spotColor, width: 3))
                    : null,
              ),
              child: InkWell(
                onTap: () =>
                    ref.read(sequenceEditorProvider.notifier).select(row.path),
                child: row.path.isEmpty
                    ? content
                    : LongPressDraggable<NodePath>(
                        data: row.path,
                        dragAnchorStrategy: pointerDragAnchorStrategy,
                        feedback: _DragChip(label: nodeLabel(row.node)),
                        childWhenDragging:
                            Opacity(opacity: 0.4, child: content),
                        child: content,
                      ),
              ),
            );
          },
        );
        // A thin "insert before this row" gap above each non-root row, so a drag
        // can drop BETWEEN rows to position precisely (the row target above only
        // appends into a container). Indented to the row's depth. Below the row,
        // a trailing "append into" gap for every container whose subtree ends
        // here (so a drag can reach the end of a container without the
        // drop-onto-container gesture), indented one level past each container.
        final nextPath = i + 1 < rows.length ? rows[i + 1].path : null;
        final appendContainers =
            appendGapContainers(editor.body, row.path, nextPath);
        return Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (row.path.isNotEmpty)
              _GapTarget(
                key: ValueKey(gapBeforeKey(row.path)),
                resolve: (body, dragged) =>
                    resolveDropBefore(body, dragged, row.path),
                indent: 8 + row.depth * 20.0,
              ),
            MouseRegion(
              onEnter: (_) => setState(() => _hoveredKey = rowKey),
              onExit: (_) => setState(
                  () => _hoveredKey = _hoveredKey == rowKey ? null : _hoveredKey),
              child: rowTarget,
            ),
            for (final c in appendContainers)
              _GapTarget(
                key: ValueKey(gapAppendKey(c)),
                resolve: (body, dragged) => resolveDropInto(body, dragged, c),
                indent: 8 + (c.length + 1) * 20.0,
              ),
          ],
        );
      },
    );
  }
}

/// Resolves a drop of [dragged] against the live body to the `(parent, index)`
/// for [SequenceEditorController.moveNodeTo], or null when invalid/no-op.
typedef _GapResolver = ({NodePath parent, int index})? Function(
    Map<String, dynamic> body, NodePath dragged);

/// A thin drop zone between/after rows: dropping a dragged node here moves it to
/// the slot [resolve] yields against the live body — an insert-between reorder
/// ([resolveDropBefore]) or an append into a container ([resolveDropInto]).
/// Shows a highlight line while a valid drop hovers. The box height is fixed so
/// a hover never reflows the list (which could bounce the pointer in/out of the
/// gap); only the highlight line toggles.
class _GapTarget extends ConsumerWidget {
  const _GapTarget({super.key, required this.resolve, required this.indent});

  final _GapResolver resolve;
  final double indent;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    bool resolves(NodePath dragged) {
      final body = ref.read(sequenceEditorProvider)?.body;
      return body != null && resolve(body, dragged) != null;
    }

    return DragTarget<NodePath>(
      onWillAcceptWithDetails: (d) => resolves(d.data),
      onAcceptWithDetails: (d) {
        final body = ref.read(sequenceEditorProvider)?.body;
        if (body == null) return;
        final r = resolve(body, d.data);
        if (r == null) return;
        ref.read(sequenceEditorProvider.notifier).moveNodeTo(d.data, r.parent, r.index);
      },
      builder: (context, candidate, rejected) {
        final hovering = candidate.isNotEmpty;
        return Container(
          height: 8,
          padding: EdgeInsets.only(left: indent, right: 8),
          alignment: Alignment.center,
          child: hovering
              ? Container(height: 2, color: AraColors.accentInfo)
              : const SizedBox.shrink(),
        );
      },
    );
  }
}

/// A compact icon button for the selected-row actions; greyed when [onPressed]
/// is null (boundary).
class _RowIcon extends StatelessWidget {
  const _RowIcon({required this.icon, required this.tooltip, required this.onPressed});
  final IconData icon;
  final String tooltip;
  final VoidCallback? onPressed;

  @override
  Widget build(BuildContext context) => IconButton(
        icon: Icon(icon, size: 16),
        color: AraColors.textSecondary,
        disabledColor: AraColors.textDisabled,
        visualDensity: VisualDensity.compact,
        padding: EdgeInsets.zero,
        constraints: const BoxConstraints.tightFor(width: 28, height: 28),
        tooltip: tooltip,
        onPressed: onPressed,
      );
}

/// The floating label shown under the pointer while a row is being dragged.
class _DragChip extends StatelessWidget {
  const _DragChip({required this.label});
  final String label;

  @override
  Widget build(BuildContext context) => Material(
        color: Colors.transparent,
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
          decoration: BoxDecoration(
            color: AraColors.bgPanelAlt,
            borderRadius: BorderRadius.circular(6),
            border: Border.all(color: AraColors.selectionBg),
          ),
          child: Text(label,
              style: const TextStyle(color: AraColors.textPrimary, fontSize: 13)),
        ),
      );
}


/// Quiet count chip for a container's loops/triggers (S9).
class _MetaChip extends StatelessWidget {
  final IconData icon;
  final int count;
  const _MetaChip({required this.icon, required this.count});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(left: 6),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 5, vertical: 1),
        decoration: BoxDecoration(
          color: AraColors.bgInput,
          borderRadius: BorderRadius.circular(8),
        ),
        child: Row(mainAxisSize: MainAxisSize.min, children: [
          Icon(icon, size: 11, color: AraColors.textSecondary),
          const SizedBox(width: 3),
          Text('$count',
              style: const TextStyle(
                  fontSize: 10.5, color: AraColors.textSecondary)),
        ]),
      ),
    );
  }
}
