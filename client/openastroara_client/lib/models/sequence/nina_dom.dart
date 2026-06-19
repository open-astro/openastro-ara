/// §38 sequence-editor mutation engine — pure, structural operations over the
/// RAW NINA Json.NET DOM (the `$type`/`$values` map the daemon stores verbatim),
/// NOT the lossy display tree. Every op returns a NEW body; the input is never
/// mutated (the source `SequenceDetail.body` is deeply unmodifiable), so the
/// result round-trips faithfully on Save (`PATCH /{id}`).
///
/// A node is addressed by a [NodePath]: the list of child indices from the root
/// (root = `[]`, its first child = `[0]`, that child's second child = `[0,1]`).
library;

/// Child-index path from the root to a node. Empty = the root itself.
typedef NodePath = List<int>;

/// The ObservableCollection wrapper `$type` used for a container's `Items` when
/// the node being edited doesn't already carry one (e.g. a freshly-built
/// container). Matches the daemon's own templates so constructed bodies run.
const String itemsWrapperType =
    'System.Collections.ObjectModel.ObservableCollection`1[[OpenAstroAra.Sequencer.SequenceItem.ISequenceItem, OpenAstroAra.Sequencer]], System.ObjectModel';

/// The ObservableCollection wrapper `$type` for a container's `Conditions`
/// (loop/time/altitude conditions). Mirrors [itemsWrapperType] for the
/// `ISequenceCondition` element type the daemon's templates emit.
const String conditionsWrapperType =
    'System.Collections.ObjectModel.ObservableCollection`1[[OpenAstroAra.Sequencer.Conditions.ISequenceCondition, OpenAstroAra.Sequencer]], System.ObjectModel';

/// The ObservableCollection wrapper `$type` for a container's `Triggers`
/// (meridian flip, reconnect, …), for the `ISequenceTrigger` element type.
const String triggersWrapperType =
    'System.Collections.ObjectModel.ObservableCollection`1[[OpenAstroAra.Sequencer.Trigger.ISequenceTrigger, OpenAstroAra.Sequencer]], System.ObjectModel';

/// The child nodes of [node] — the daemon wraps `Items` as
/// `{ $type: ObservableCollection, $values: [...] }`, but tolerate a plain array
/// or a missing/!map `Items` (a leaf instruction) → empty list. The returned
/// list is a fresh, growable copy.
///
/// [Iterable.whereType] keeps only `Map` entries. Real Json.NET output never
/// puts a bare scalar in `$values` (a reference is emitted as the *object*
/// `{"$ref":"5"}`, which is a `Map` and passes through untouched), so in
/// practice nothing is dropped — the filter only guards against malformed input
/// and keeps index addressing within real child maps.
///
/// A `{"$ref":...}` handle inline in a container's `$values` would be addressed
/// as if it were a real node (and [setField] would write fields onto the handle,
/// producing invalid Json.NET). Tree-shaped NINA sequences never do this —
/// reference handles live on the referenced node as `$id`, not inline in a
/// child list — so a debug-only [assert] catches the assumption breaking early
/// without any release-build cost.
List<Map<String, dynamic>> childrenOf(Map<String, dynamic> node) {
  final items = node['Items'];
  final List raw;
  if (items is Map && items[r'$values'] is List) {
    raw = items[r'$values'] as List;
  } else if (items is List) {
    raw = items;
  } else {
    return <Map<String, dynamic>>[];
  }
  assert(
    raw.every((e) => e is! Map || !e.containsKey(r'$ref')),
    'childrenOf: a container\'s \$values holds a {"\$ref":...} reference handle; '
    'tree-shaped NINA sequences inline their nodes, so addressing this as a real '
    'child node would corrupt the body on edit.',
  );
  return raw.whereType<Map<String, dynamic>>().toList();
}

/// A copy of [node] whose `Items` holds [children] in the ObservableCollection
/// wrapper shape. The existing wrapper map is shallow-copied and only its
/// `$values` is replaced, so the wrapper's `$type` (keeping a NINA-namespaced
/// imported body's namespace) AND any other Json.NET metadata it carried —
/// e.g. a `$id` reference handle on the collection — survive the round-trip.
/// A node with no map wrapper (a leaf, or a plain-array `Items`) gets a fresh
/// wrapper typed [itemsWrapperType].
Map<String, dynamic> withChildren(
    Map<String, dynamic> node, List<Map<String, dynamic>> children) {
  final existing = node['Items'];
  final wrapper = existing is Map
      ? Map<String, dynamic>.from(existing.cast<String, dynamic>())
      : <String, dynamic>{};
  wrapper[r'$values'] = children;
  wrapper.putIfAbsent(r'$type', () => itemsWrapperType);
  return Map<String, dynamic>.of(node)..['Items'] = wrapper;
}

/// The loop conditions of [node] — a container wraps `Conditions` as
/// `{ $type: ObservableCollection, $values: [...] }` exactly like `Items`.
/// Tolerates a plain array or a missing/!map `Conditions` (a leaf, or a
/// condition-less container) → empty list. The returned list is a fresh,
/// growable copy. Mirrors [childrenOf] for the parallel collection.
List<Map<String, dynamic>> conditionsOf(Map<String, dynamic> node) {
  final conditions = node['Conditions'];
  final List raw;
  if (conditions is Map && conditions[r'$values'] is List) {
    raw = conditions[r'$values'] as List;
  } else if (conditions is List) {
    raw = conditions;
  } else {
    return <Map<String, dynamic>>[];
  }
  return raw.whereType<Map<String, dynamic>>().toList();
}

/// A copy of [node] whose `Conditions` holds [conditions] in the
/// ObservableCollection wrapper shape. The existing wrapper map is shallow-copied
/// and only its `$values` replaced (so its `$type` / any `$id` survive); a node
/// with no map wrapper gets a fresh one typed [conditionsWrapperType]. Mirrors
/// [withChildren].
Map<String, dynamic> withConditions(
    Map<String, dynamic> node, List<Map<String, dynamic>> conditions) {
  final existing = node['Conditions'];
  final wrapper = existing is Map
      ? Map<String, dynamic>.from(existing.cast<String, dynamic>())
      : <String, dynamic>{};
  wrapper[r'$values'] = conditions;
  wrapper.putIfAbsent(r'$type', () => conditionsWrapperType);
  return Map<String, dynamic>.of(node)..['Conditions'] = wrapper;
}

/// Append [condition] to the `Conditions` of the container at [containerPath];
/// returns a new root. Parallels [insertChild] for the conditions collection.
Map<String, dynamic> addCondition(Map<String, dynamic> root,
        NodePath containerPath, Map<String, dynamic> condition) =>
    _rebuild(root, containerPath, (container) {
      final conditions = conditionsOf(container)..add(condition);
      return withConditions(container, conditions);
    });

/// Remove the condition at [index] from the container at [containerPath];
/// returns a new root. Throws [RangeError] if [index] is out of range.
Map<String, dynamic> removeConditionAt(
        Map<String, dynamic> root, NodePath containerPath, int index) =>
    _rebuild(root, containerPath, (container) {
      final conditions = conditionsOf(container);
      if (index < 0 || index >= conditions.length) {
        throw RangeError('removeConditionAt index $index out of range');
      }
      conditions.removeAt(index);
      return withConditions(container, conditions);
    });

/// Set scalar field [key] to [value] on the condition at [index] of the
/// container at [containerPath]; returns a new root. Throws [RangeError] if
/// [index] is out of range.
Map<String, dynamic> setConditionField(Map<String, dynamic> root,
        NodePath containerPath, int index, String key, Object? value) =>
    _rebuild(root, containerPath, (container) {
      final conditions = conditionsOf(container);
      if (index < 0 || index >= conditions.length) {
        throw RangeError('setConditionField index $index out of range');
      }
      conditions[index] = Map<String, dynamic>.of(conditions[index])..[key] = value;
      return withConditions(container, conditions);
    });

/// Whether [node] can hold child instructions (a container) rather than being a
/// leaf instruction. Containers carry an `Items` collection and/or a
/// `.Container.` `$type` (true for both the native OpenAstroAra and imported
/// NINA namespaces); leaf instructions have neither. Used to reject inserting a
/// child into a leaf, which would otherwise graft a spurious `Items` wrapper
/// onto an instruction node.
bool isContainer(Map<String, dynamic> node) {
  if (node['Items'] != null) return true;
  final type = node[r'$type'];
  return type is String && type.contains('.Container.');
}

/// The node at [path], or null if any index is out of range. [path] `[]` → root.
Map<String, dynamic>? nodeAt(Map<String, dynamic> root, NodePath path) {
  var node = root;
  for (final i in path) {
    final kids = childrenOf(node);
    if (i < 0 || i >= kids.length) return null;
    node = kids[i];
  }
  return node;
}

/// Rebuild the spine from [node] to [path], applying [op] to the addressed node
/// and re-threading new parent maps back up. Untouched subtrees are shared (not
/// copied). Throws [RangeError] if [path] addresses a missing child. [depth] is
/// the recursion cursor into [path] (an index instead of repeated
/// `sublist` allocation — O(depth) total, not O(depth²)).
Map<String, dynamic> _rebuild(Map<String, dynamic> node, NodePath path,
    Map<String, dynamic> Function(Map<String, dynamic>) op,
    [int depth = 0]) {
  if (depth == path.length) return op(node);
  final kids = childrenOf(node);
  final i = path[depth];
  if (i < 0 || i >= kids.length) {
    throw RangeError('node path index $i out of range (${kids.length} children)');
  }
  kids[i] = _rebuild(kids[i], path, op, depth + 1);
  return withChildren(node, kids);
}

/// Set scalar field [key] to [value] on the node at [path]; returns a new root.
Map<String, dynamic> setField(
        Map<String, dynamic> root, NodePath path, String key, Object? value) =>
    _rebuild(root, path, (n) => Map<String, dynamic>.of(n)..[key] = value);

/// Insert [newNode] as a child of the container at [parentPath] at [index];
/// returns a new root. [index] is a *destination* slot, so it's clamped to
/// `0..childCount` (a buggy out-of-range index lands at the nearest end rather
/// than throwing) — consistent with [reorderChild]'s `newIndex`.
Map<String, dynamic> insertChild(Map<String, dynamic> root, NodePath parentPath,
        int index, Map<String, dynamic> newNode) =>
    _rebuild(root, parentPath, (parent) {
      final kids = childrenOf(parent);
      kids.insert(index.clamp(0, kids.length), newNode);
      return withChildren(parent, kids);
    });

/// Remove the node at [path] from its parent; returns a new root. [path] must be
/// non-empty (the root can't be removed).
Map<String, dynamic> removeAt(Map<String, dynamic> root, NodePath path) {
  if (path.isEmpty) {
    throw ArgumentError('removeAt cannot remove the root (empty path)');
  }
  final parentPath = path.sublist(0, path.length - 1);
  final index = path.last;
  return _rebuild(root, parentPath, (parent) {
    final kids = childrenOf(parent);
    if (index < 0 || index >= kids.length) {
      throw RangeError('removeAt index $index out of range');
    }
    kids.removeAt(index);
    return withChildren(parent, kids);
  });
}

/// Reorder a child of the container at [parentPath] from [oldIndex] to
/// [newIndex] (the common drag-to-reorder case); returns a new root.
///
/// [newIndex] follows Flutter's `ReorderableListView.onReorder` **pre-removal**
/// convention — the slot in the *original* list the item is dropped before — so
/// the UI can wire `onReorder` straight through with no off-by-one adjustment.
/// The standard `if (oldIndex < newIndex) newIndex--` normalisation is absorbed
/// here, then [newIndex] is clamped to `0..N` (post-removal count), so a drop at
/// the end lands last. [oldIndex] *selects an existing child* and throws
/// [RangeError] when out of range.
Map<String, dynamic> reorderChild(Map<String, dynamic> root, NodePath parentPath,
        int oldIndex, int newIndex) =>
    _rebuild(root, parentPath, (parent) {
      final kids = childrenOf(parent);
      if (oldIndex < 0 || oldIndex >= kids.length) {
        throw RangeError('reorderChild oldIndex $oldIndex out of range');
      }
      // Flutter delivers newIndex against the pre-removal list; once oldIndex is
      // pulled out, every slot after it shifts down by one.
      final target = oldIndex < newIndex ? newIndex - 1 : newIndex;
      final node = kids.removeAt(oldIndex);
      kids.insert(target.clamp(0, kids.length), node);
      return withChildren(parent, kids);
    });
