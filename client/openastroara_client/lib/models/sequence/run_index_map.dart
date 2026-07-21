import 'nina_dom.dart';
import 'node_display.dart';

/// §Run-redesign S3 — mapping the daemon's flat `current_instruction_index`
/// onto the editor tree's [NodePath] address space, so the tree can spotlight
/// the executing node.
///
/// The daemon reports execution progress as an ordinal over the sequence's
/// executable instructions; the editor addresses nodes structurally. The two
/// meet ONLY if we can reconstruct the daemon's ordering — assumed to be
/// depth-first leaf order (containers organise, leaves execute). Because that
/// assumption is unverified wire-contract territory, the mapping is
/// deliberately defensive:
///
///  * **Verified ordinal**: when the run state's `instructionsTotal` equals
///    the body's executable-leaf count, ordinal identity is trusted — the
///    spotlight lands on `leaves[index]`, and everything before it may be
///    tinted completed.
///  * **Description fallback**: on a count mismatch (or out-of-range index),
///    fall back to matching `currentInstructionDescription` against leaf
///    labels — monotonic (never behind [lastMatchedLeaf]) so repeated labels
///    (ten TakeExposures) advance instead of sticking to the first.
///  * **No match** → null: the header still shows description + N/M, and a
///    wrong node is never highlighted confidently.
class RunSpotlight {
  /// Executable-leaf-order paths for the whole body.
  final List<NodePath> leaves;

  /// The executing leaf's position in [leaves], or null when unmapped.
  final int? currentLeaf;

  /// True only when ordinal identity was verified by the count check —
  /// completed-tinting is allowed solely in this mode.
  final bool verified;

  const RunSpotlight(
      {required this.leaves, required this.currentLeaf, required this.verified});

  NodePath? get currentPath =>
      currentLeaf == null ? null : leaves[currentLeaf!];

  /// Paths safely known to be completed (ordinal-verified mode only).
  List<NodePath> get completedPaths =>
      verified && currentLeaf != null ? leaves.sublist(0, currentLeaf!) : const [];
}

/// Depth-first executable leaves (non-container nodes) of [body]'s tree.
List<NodePath> executableLeafPaths(Map<String, dynamic> body) {
  final out = <NodePath>[];
  void walk(Map<String, dynamic> node, NodePath path) {
    if (!isContainer(node)) {
      // The root is always a container; a non-container can only appear via
      // recursion, so path is never empty here.
      out.add(path);
      return;
    }
    final kids = childrenOf(node);
    for (var i = 0; i < kids.length; i++) {
      walk(kids[i], [...path, i]);
    }
  }

  walk(body, const []);
  return out;
}

/// Resolve the spotlight for [body] given the run state's [index], [total] and
/// [description]. [lastMatchedLeaf] carries the previous resolution so the
/// description fallback stays monotonic across identical labels.
RunSpotlight resolveSpotlight(
  Map<String, dynamic> body, {
  required int? index,
  required int? total,
  String? description,
  int? lastMatchedLeaf,
}) {
  final leaves = executableLeafPaths(body);
  if (leaves.isEmpty || index == null || index < 0) {
    return RunSpotlight(leaves: leaves, currentLeaf: null, verified: false);
  }
  final verified = total != null && total == leaves.length;
  if (verified && index < leaves.length) {
    return RunSpotlight(leaves: leaves, currentLeaf: index, verified: true);
  }
  // Fallback: description match, never moving backwards.
  final wanted = description?.trim().toLowerCase();
  if (wanted == null || wanted.isEmpty) {
    return RunSpotlight(leaves: leaves, currentLeaf: null, verified: false);
  }
  final from = (lastMatchedLeaf ?? -1) < 0 ? 0 : lastMatchedLeaf!;
  for (var i = from; i < leaves.length; i++) {
    final node = nodeAt(body, leaves[i]);
    if (node == null) continue;
    if (nodeLabel(node).trim().toLowerCase() == wanted) {
      return RunSpotlight(leaves: leaves, currentLeaf: i, verified: false);
    }
  }
  return RunSpotlight(leaves: leaves, currentLeaf: null, verified: false);
}
