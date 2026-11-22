/// §38 sequence-editor working state — the bridge between the raw-body mutation
/// engine (`nina_dom.dart`) and the catalog (`instruction_catalog.dart`) on one
/// side, and the palette/tree/field-editor widgets (later slices) on the other.
///
/// Unlike `SequenceController` (which holds the lossy `SequenceNode` *display*
/// tree), this controller holds the RAW NINA body and edits it in place, so
/// Save round-trips faithfully. Every edit goes through `nina_dom`, which
/// returns a new body and never mutates the source — so the held body stays a
/// pure value and dirty-tracking is a deep compare against the loaded original.
library;

import 'dart:async';

import 'package:collection/collection.dart';
import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/condition_catalog.dart';
import '../../models/sequence/instruction_catalog.dart';
import '../../models/sequence/nina_dom.dart';
import '../../models/sequence/run_index_map.dart';
import '../../models/sequence/trigger_catalog.dart';
import '../../models/sequence/sequence_summary.dart';
import '../../services/sequence_api.dart';
import 'sequence_list_state.dart';

const DeepCollectionEquality _bodyEquality = DeepCollectionEquality();

/// Immutable snapshot of the editor: the working [body], the [originalBody] it
/// was loaded with (for dirty-tracking), and the currently-[selectedPath].
class SequenceEditorState {
  /// The id of the sequence being edited (so Save targets the right record).
  final String id;

  /// The working raw NINA body. Replaced wholesale by every edit (the
  /// `nina_dom` ops return a new root); never mutated in place.
  final Map<String, dynamic> body;

  /// The body as loaded (or last saved). [isDirty] compares [body] to this.
  final Map<String, dynamic> originalBody;

  /// The node currently selected in the tree, or null. A [NodePath] of child
  /// indices from the root (`[]` = the root container itself).
  final NodePath? selectedPath;

  /// The daemon's `modified_utc` for [originalBody] (#2168). A live setting
  /// edit sends it so the daemon can refuse positions from an older copy.
  final String? baseModified;

  /// Whether the working [body] differs from the loaded/saved [originalBody].
  /// Computed ONCE here (not per read) so widget rebuilds that watch the
  /// provider don't each pay an O(n) deep compare: an identity short-circuit
  /// makes the freshly-loaded / just-saved case (where the two alias the same
  /// map) O(1), and only an actually-edited body pays the deep compare — which
  /// still reports a revert-to-original as not dirty.
  final bool isDirty;

  SequenceEditorState({
    required this.id,
    required this.body,
    required this.originalBody,
    this.selectedPath,
    this.baseModified,
  }) : isDirty = !identical(body, originalBody) &&
            !_bodyEquality.equals(body, originalBody);

  SequenceEditorState _copyWith({
    Map<String, dynamic>? body,
    Map<String, dynamic>? originalBody,
    NodePath? selectedPath,
    bool clearSelection = false,
    String? baseModified,
  }) {
    assert(!(clearSelection && selectedPath != null),
        'pass either clearSelection or selectedPath, not both');
    return SequenceEditorState(
      id: id,
      body: body ?? this.body,
      originalBody: originalBody ?? this.originalBody,
      selectedPath: clearSelection ? null : (selectedPath ?? this.selectedPath),
      baseModified: baseModified ?? this.baseModified,
    );
  }
}

/// Holds the [SequenceEditorState] for the sequence open in the editor, or null
/// when nothing is loaded. Widgets call the mutation methods; each one runs a
/// `nina_dom` op and swaps in the new body.
class SequenceEditorController extends Notifier<SequenceEditorState?> {
  @override
  SequenceEditorState? build() => null;

  // §Run-redesign S12 — undo/redo. Bodies are immutable maps replaced
  // wholesale by every mutator, so a snapshot is an O(1) reference push.
  // Selection is NOT snapshotted: restoring a stale NodePath post-undo is a
  // correctness trap, so undo/redo clear it (matching removeNode's behavior).
  static const int undoCap = 50;
  final List<Map<String, dynamic>> _undoStack = [];
  final List<Map<String, dynamic>> _redoStack = [];
  // Consecutive edits to the SAME scalar field coalesce into one undo step
  // (per-keystroke snapshots would make ⌘Z a character-eraser).
  String? _lastCoalesceKey;

  // Undo works in live mood too (#2168): it only steps back the editor's own
  // unsaved edits, so a setting the daemon refuses can always be put back.
  bool get canUndo => _undoStack.isNotEmpty;
  bool get canRedo => _redoStack.isNotEmpty;

  // ── §38.9 live mid-run editing ────────────────────────────────────────────
  // While the LOADED sequence has an active run, structural edits (insert /
  // remove / reorder) route to the daemon's live-edit endpoints instead of the
  // local body: the daemon mutates the EXECUTING plan, persists it, and returns
  // the updated detail, which reloads the editor (server-authoritative). Nodes
  // at/before the running position are locked; the daemon re-validates every
  // rule, so these client checks are UX (fail fast with a message), not the
  // enforcement. Field edits (#2168) stay LOCAL until Save or Resume, which
  // sends only the changed keys to the daemon's live `run/fields` op
  // ([applyLiveEdits]); adding or removing a loop rule or trigger mid-run is
  // refused with a notice in [_commitBody].

  /// Whether the loaded sequence currently has an active run (running or
  /// paused): Save and Resume send field edits live ([applyLiveEdits]).
  bool get liveRunActive => _liveRunActive;

  /// Whether the loaded sequence currently has an active run — live mood.
  bool get _liveRunActive {
    final s = state;
    if (s == null) return false;
    final run = ref.read(sequenceRunStateProvider).value;
    return run != null &&
        run.sequenceId == s.id &&
        (run.state?.isActive ?? false);
  }

  // Monotonic description-fallback matching, mirroring RunSpotlightNotifier
  // (review #872 r4): repeated identical labels (ten TakeExposures) must
  // advance instead of always matching the first occurrence. The notifier
  // itself can't be read from here — it WATCHES this editor provider, so
  // ref.read(runSpotlightProvider) is a circular dependency — hence the same
  // persisted-lastMatchedLeaf technique, reset per run.
  int? _liveLastMatchedLeaf;
  String? _liveLastRunId;

  /// The running leaf's [NodePath], resolved like the spotlight (including its
  /// monotonic fallback matching), or null when it can't be mapped confidently
  /// (the daemon still enforces every rule).
  NodePath? _liveCurrentPath(SequenceEditorState s) {
    final run = ref.read(sequenceRunStateProvider).value;
    if (run == null) return null;
    if (run.runId != _liveLastRunId) {
      _liveLastRunId = run.runId;
      _liveLastMatchedLeaf = null; // a fresh run starts the match over
    }
    final spotlight = resolveSpotlight(
      s.body,
      index: run.currentInstructionIndex,
      total: run.instructionsTotal == 0 ? null : run.instructionsTotal,
      description: run.currentInstructionDescription,
      lastMatchedLeaf: _liveLastMatchedLeaf,
    );
    _liveLastMatchedLeaf = spotlight.currentLeaf ?? _liveLastMatchedLeaf;
    return spotlight.currentPath;
  }

  /// Whether [path] addresses a slot strictly AFTER [current] in depth-first
  /// order. False for the current node itself, anything ordered before it, and
  /// any ancestor of it (an ancestor slot insert would shift the running item).
  static bool _isAfterInDfs(NodePath path, NodePath current) {
    final n = path.length < current.length ? path.length : current.length;
    for (var i = 0; i < n; i++) {
      if (path[i] != current[i]) return path[i] > current[i];
    }
    return false; // equal, prefix, or descendant of the running leaf — locked
  }

  /// Whether the node at [path] is editable in live mood (pending, strictly
  /// after the running item). The root (`[]`) is never editable as a node.
  bool isLiveEditable(NodePath path) {
    final s = state;
    if (s == null || !_liveRunActive) return true;
    if (path.isEmpty) return false;
    final current = _liveCurrentPath(s);
    if (current == null) return true; // unmappable — let the daemon decide
    return _isAfterInDfs(path, current);
  }

  void _notice(String message) =>
      ref.read(liveEditNoticeProvider.notifier).show(message);

  // §38.9 — one structural live edit at a time (review #872 r3): every op
  // addresses nodes by POSITIONAL path computed against the loaded body, so a
  // second op fired before the first one's reload would target pre-edit
  // indices — against a live telescope run. Refuse with a notice instead.
  bool _liveEditInFlight = false;

  bool _liveBusy() {
    if (_liveEditInFlight) {
      _notice(_busyNotice);
      return true;
    }
    return false;
  }

  /// A fresh sequential group holding [leaf] — the daemon's live-add accepts
  /// container nodes, so a bare instruction rides in one, named after itself.
  static Map<String, dynamic> _wrapInGroup(
      Map<String, dynamic> leaf, String label) {
    final group = instructionForType(sequentialContainerType)!.build();
    group['Name'] = label;
    return withChildren(group, [leaf]);
  }

  /// Shared live reorder: lock-check both the item and its destination slot,
  /// then send the daemon's same-parent move ([newIndex] is post-removal).
  void _liveMove(
      SequenceEditorState s, NodePath parentPath, int oldIndex, int newIndex) {
    if (_liveBusy()) return;
    final path = <int>[...parentPath, oldIndex];
    if (!isLiveEditable(path) ||
        !isLiveEditable(<int>[...parentPath, newIndex])) {
      _notice('Items at or before the running one are locked while it runs.');
      return;
    }
    if (oldIndex == newIndex) return;
    unawaited(_applyLive((api) => api.moveRunItem(s.id, path, newIndex)));
  }

  /// Run a live-edit API call: on success, reload the returned detail (which
  /// also clears undo history — live edits are server-authoritative and not
  /// locally undoable); on refusal, surface the daemon's reason.
  Future<void> _applyLive(
      Future<SequenceDetail> Function(SequenceClient api) send) async {
    final s = state;
    if (s == null) return;
    final api = ref.read(sequenceApiProvider);
    if (api == null) return;
    _liveEditInFlight = true;
    try {
      // Unsaved field edits go first, so the op's positions and the daemon's
      // copy agree.
      if (s.isDirty) {
        final why = await _pushLiveFields(api);
        if (why != null) {
          _notice(why);
          return;
        }
      }
      final detail = await send(api);
      // Stale guard: the editor may have moved to another sequence mid-flight.
      // A value typed while the op was in flight is moved onto the answer.
      if (state?.id == s.id) adopt(detail);
    } on DioException catch (e) {
      _liveRefused(e);
    } finally {
      _liveEditInFlight = false;
    }
  }

  void _liveRefused(DioException e) => _notice(_liveReason(e));

  /// #2168 — save the field edits made while the run is going (running or
  /// paused) to the live plan, NINA-style: the daemon changes the running
  /// items, so every step that starts after this uses the new values; the
  /// frame in hand and finished counts are kept. Only the changed keys are
  /// sent, never the whole body. True when there was nothing to send or the
  /// daemon took the change; false (edits kept, with a notice) on a refusal.
  Future<bool> applyLiveEdits() async {
    final why = await _sendLiveEdits();
    if (why != null) _notice(why);
    return why == null;
  }

  /// The same save for a Resume outside the Sequencer tab (#2168): the Simple
  /// plan panel, the "press Resume" message and the stop overlay. Sends the
  /// editor's unsaved settings when [sequenceId] is the one open in it. Null
  /// when there was nothing to send or the daemon took it; else the reason
  /// the run should stay paused, for the caller to show.
  Future<String?> saveLiveEditsBeforeResume(String sequenceId) async {
    final s = state;
    if (s == null || s.id != sequenceId || !s.isDirty || !_liveRunActive) {
      return null;
    }
    return _sendLiveEdits();
  }

  Future<String?> _sendLiveEdits() async {
    final s = state;
    if (s == null || !s.isDirty) return null;
    final api = ref.read(sequenceApiProvider);
    if (api == null) return _noConnectionNotice;
    if (_liveEditInFlight) return _busyNotice;
    _liveEditInFlight = true;
    try {
      return await _pushLiveFields(api);
    } finally {
      _liveEditInFlight = false;
    }
  }

  /// Send the editor's field edits and take the daemon's answer ([adopt]).
  /// Null on success (or nothing to send), else the reason. When the plan
  /// changed on the rig since this copy was read (409 plan changed), reload,
  /// move the edits onto the new copy and send once more.
  Future<String?> _pushLiveFields(SequenceClient api) async {
    for (var attempt = 0;; attempt++) {
      final s = state;
      if (s == null) return null;
      final changes = scalarFieldChanges(s.originalBody, s.body);
      if (changes == null) return _liveStructureNotice;
      if (changes.isEmpty) return null;
      try {
        final detail = await api.setRunFields(s.id, changes,
            baseModifiedUtc: s.baseModified);
        if (state?.id == s.id) adopt(detail);
        _notice('Saved. Frames that start now use the new settings.');
        return null;
      } on DioException catch (e) {
        if (attempt == 0 && isPlanChangedError(e)) {
          try {
            final fresh = await api.getSequenceDetail(s.id);
            if (state?.id != s.id) return null;
            adopt(fresh);
          } on DioException catch (e2) {
            return _liveReason(e2);
          }
          continue;
        }
        return _liveReason(e);
      }
    }
  }

  /// #2168 — take a newer daemon copy of the open plan without dropping the
  /// editor's unsaved setting edits. Positions shift when an item is added,
  /// removed or moved elsewhere (Simple mode, another device), so each edit is
  /// moved onto the same item in [detail] ([rebaseFieldChanges]); an edit
  /// whose item is gone is dropped with a notice, never silently. With no
  /// unsaved edits this is [load]. Edits that change the layout can't be moved
  /// onto a live plan: the editor then keeps its own copy.
  void adopt(SequenceDetail detail) {
    final s = state;
    if (s == null || s.id != detail.id || !s.isDirty) {
      load(detail);
      return;
    }
    final changes = scalarFieldChanges(s.originalBody, s.body);
    if (changes == null) return;
    final moved =
        rebaseFieldChanges(s.originalBody, s.body, detail.body, changes);
    final selected = s.selectedPath;
    _resetHistory(); // the undo snapshots belong to the old copy
    state = SequenceEditorState(
      id: detail.id,
      body: applyFieldChanges(detail.body, moved.kept),
      originalBody: detail.body,
      baseModified: detail.modifiedUtc,
      selectedPath: selected == null
          ? null
          : mapNodePath(s.originalBody, detail.body, selected, edited: s.body),
    );
    if (moved.lost.isNotEmpty) {
      final keys = {for (final c in moved.lost) c.key}.join(', ');
      _notice('The plan changed on the rig and an item you edited is gone, '
          'so your change to $keys was not kept.');
    }
  }

  static String _liveReason(DioException e) {
    debugPrint('[sequencer] live edit refused/failed: $e');
    final data = e.response?.data;
    final detail = data is Map<String, dynamic>
        ? (data['detail'] ?? data['error'])
        : null;
    return detail is String && detail.isNotEmpty
        ? detail
        : "The running plan couldn't be changed. Check the connection and try again.";
  }

  static const String _noConnectionNotice =
      "The running plan couldn't be changed. Check the connection and try again.";
  static const String _busyNotice =
      'Hold on — the previous change is still being applied.';

  /// #2168 — while the run is going the daemon takes plain values (numbers,
  /// text, on/off, choices), a binning and a Switch Filter's filter. Other
  /// object settings (a target and its coordinates, a time source, lists) are
  /// read-only until the run ends: refused here with a notice, so the edit is
  /// never kept only to be refused at Save or Resume.
  bool _refuseLiveValue(String key, Object? old, Object? value) {
    if (!_liveRunActive) return false;
    bool settable(Object? v) {
      if (v is List) return false;
      if (v is! Map) return true;
      // A reference to a filter another item also uses: the new value says
      // what it becomes.
      if (v.containsKey(r'$ref')) return true;
      final type = v[r'$type'];
      return type is String &&
          (type.contains('.FilterInfo,') || type.contains('.BinningMode,'));
    }

    if (settable(old) && settable(value)) return false;
    _notice("$key can't be changed while the run is going. Stop the run to "
        'change it.');
    return true;
  }

  static const String _liveStructureNotice =
      'While the run is going you can change settings and add, remove or '
      'reorder upcoming items. Adding or removing a loop rule or trigger needs '
      'the run to finish.';

  /// Route for every body mutation: snapshot the pre-edit body, clear redo,
  /// then apply. [coalesceKey] marks a scalar-field edit; repeats on the same
  /// key reuse the existing snapshot.
  void _commitBody(SequenceEditorState prev, SequenceEditorState next,
      {String? coalesceKey, bool liveField = false}) {
    // §38.9 — every LOCAL body mutation funnels through here; in live mood the
    // structural ops route to the daemon before reaching this. A field edit
    // ([liveField]) is kept: it makes the editor dirty, and Save or Resume
    // sends it to the running plan (#2168). Anything else (adding or removing
    // a loop rule or trigger) would not join the run, so it is refused with a
    // notice instead of quietly forking the working copy from the live plan.
    if (_liveRunActive && !liveField) {
      _notice(_liveStructureNotice);
      return;
    }
    final coalesce = coalesceKey != null && coalesceKey == _lastCoalesceKey;
    if (!coalesce) {
      _undoStack.add(prev.body);
      if (_undoStack.length > undoCap) _undoStack.removeAt(0);
    }
    _lastCoalesceKey = coalesceKey;
    _redoStack.clear();
    state = next;
  }

  void undo() {
    final s = state;
    if (s == null || _undoStack.isEmpty) return;
    _redoStack.add(s.body);
    _lastCoalesceKey = null;
    state = s._copyWith(body: _undoStack.removeLast(), clearSelection: true);
  }

  void redo() {
    final s = state;
    if (s == null || _redoStack.isEmpty) return;
    _undoStack.add(s.body);
    _lastCoalesceKey = null;
    state = s._copyWith(body: _redoStack.removeLast(), clearSelection: true);
  }

  void _resetHistory() {
    _undoStack.clear();
    _redoStack.clear();
    _lastCoalesceKey = null;
  }

  /// Load [detail] into the editor (discarding any current edits), with nothing
  /// selected. Seeds both [SequenceEditorState.body] and `originalBody` from the
  /// detail's raw body, so a freshly-loaded sequence is not dirty.
  ///
  /// `body` and `originalBody` intentionally alias the same map: `detail.body`
  /// is deeply unmodifiable and a refresh produces a NEW [SequenceDetail] (it
  /// never mutates an existing body), so there's no aliasing hazard — and the
  /// shared instance lets [SequenceEditorState.isDirty] short-circuit on
  /// identity until the first edit replaces `body` with a fresh map.
  void load(SequenceDetail detail) {
    _resetHistory();
    state = SequenceEditorState(
      id: detail.id,
      body: detail.body,
      originalBody: detail.body,
      baseModified: detail.modifiedUtc,
    );
  }

  /// Clear the editor (e.g. on disconnect or sequence deselect).
  void clear() {
    _resetHistory();
    state = null;
  }

  /// Select the node at [path], or clear selection with null. A non-null [path]
  /// that doesn't resolve to a node is ignored (stale selection guard).
  void select(NodePath? path) {
    final s = state;
    if (s == null) return;
    if (path != null && nodeAt(s.body, path) == null) return;
    state = path == null
        ? s._copyWith(clearSelection: true)
        : s._copyWith(selectedPath: path);
  }

  /// Move the selection one row up/down in depth-first (tree display) order —
  /// the arrow-key navigation (run-redesign S12). With nothing selected, Down
  /// selects the first row (the root) and Up the last; at either end the
  /// selection stays put. Pure navigation: never mutates the body.
  void selectAdjacent({required bool next}) {
    final s = state;
    if (s == null) return;
    final paths = <NodePath>[];
    // Same flattening order as the tree view; the parser's depth cap bounds a
    // pathological body the same way the tree's renderer does.
    void walk(Map<String, dynamic> node, NodePath path, int depth) {
      paths.add(path);
      if (depth >= 64) return;
      final kids = childrenOf(node);
      for (var i = 0; i < kids.length; i++) {
        walk(kids[i], <int>[...path, i], depth + 1);
      }
    }

    walk(s.body, const [], 0);
    if (paths.isEmpty) return;
    final sel = s.selectedPath;
    final current = sel == null
        ? -1
        : paths.indexWhere((p) => const ListEquality<int>().equals(p, sel));
    final target = current < 0
        ? (next ? 0 : paths.length - 1)
        : (next ? current + 1 : current - 1).clamp(0, paths.length - 1);
    state = s._copyWith(selectedPath: paths[target]);
  }

  /// Insert a fresh node built from [def] as a child of the container at
  /// [parentPath] at [index] (clamped), and select the new node. No-op if no
  /// sequence is loaded, or [parentPath] doesn't resolve to a container (a leaf
  /// instruction can't take children — inserting would graft a spurious `Items`
  /// wrapper onto it).
  void insertInstruction(NodePath parentPath, int index, InstructionDef def) {
    final s = state;
    if (s == null) return;
    final parent = nodeAt(s.body, parentPath);
    if (parent == null || !isContainer(parent)) return;
    final landed = index.clamp(0, childrenOf(parent).length);
    if (_liveRunActive) {
      if (_liveBusy()) return;
      // §38.9 — the daemon's live-add accepts CONTAINER nodes; a bare leaf
      // instruction rides inside a fresh sequential group so it can still be
      // added mid-run (and reads as "added live" in the tree).
      if (!isLiveEditable(<int>[...parentPath, landed])) {
        _notice('That spot has already run — add after the current item.');
        return;
      }
      final node =
          def.isContainer ? def.build() : _wrapInGroup(def.build(), def.label);
      unawaited(_applyLive((api) => api.addRunItem(s.id,
          parentPath: List<int>.of(parentPath), index: landed, item: node)));
      return;
    }
    final newBody = insertChild(s.body, parentPath, landed, def.build());
    _commitBody(
        s,
        s._copyWith(
          body: newBody,
          selectedPath: <int>[...parentPath, landed],
        ));
  }

  /// Add [def] relative to the current selection — the palette's tap-to-add
  /// target resolution, so the user doesn't have to think about paths:
  /// - nothing selected → appended to the root container;
  /// - a container selected → appended inside it;
  /// - a leaf instruction selected → inserted right after it in its parent.
  /// The new node is selected (via [insertInstruction]). No-op if no sequence
  /// is loaded.
  void addInstruction(InstructionDef def) {
    final s = state;
    if (s == null) return;
    final sel = s.selectedPath;
    if (sel == null) {
      insertInstruction(const [], childrenOf(s.body).length, def);
      return;
    }
    final node = nodeAt(s.body, sel);
    if (node != null && isContainer(node)) {
      insertInstruction(sel, childrenOf(node).length, def);
    } else {
      // Selected leaf → insert right after it in its parent. (A structurally
      // stale selection can't normally occur — structural edits clear it — but
      // if it did, insertInstruction's parent/clamp guards keep this safe: it
      // either lands in the still-valid parent or no-ops.)
      insertInstruction(sel.sublist(0, sel.length - 1), sel.last + 1, def);
    }
  }

  /// Remove the node at [path] and clear selection (the selected node may have
  /// been removed or had its path shifted). No-op for the root (`[]`) or an
  /// unresolvable path.
  void removeNode(NodePath path) {
    final s = state;
    if (s == null || path.isEmpty) return;
    if (nodeAt(s.body, path) == null) return;
    if (_liveRunActive) {
      if (_liveBusy()) return;
      if (!isLiveEditable(path)) {
        _notice('That item has already started — use Skip to drop the current '
            'one, or wait for it to finish.');
        return;
      }
      unawaited(
          _applyLive((api) => api.removeRunItem(s.id, List<int>.of(path))));
      return;
    }
    _commitBody(s, s._copyWith(body: removeAt(s.body, path), clearSelection: true));
  }

  /// Reorder a child of the container at [parentPath]. [oldIndex]/[newIndex]
  /// follow `nina_dom.reorderChild`'s Flutter `onReorder` convention; [oldIndex]
  /// is bounds-checked here (no-op when out of range) while [newIndex] is
  /// clamped downstream by `reorderChild` (so a drop at/after the end appends).
  /// Clears selection (sibling paths shift). No-op if [parentPath] is not a
  /// resolvable node.
  void reorder(NodePath parentPath, int oldIndex, int newIndex) {
    final s = state;
    if (s == null) return;
    final parent = nodeAt(s.body, parentPath);
    if (parent == null) return;
    if (oldIndex < 0 || oldIndex >= childrenOf(parent).length) return;
    if (_liveRunActive) {
      _liveMove(s, parentPath, oldIndex,
          // reorderChild's Flutter onReorder convention is pre-removal; the
          // daemon's move takes a post-removal destination.
          newIndex > oldIndex ? newIndex - 1 : newIndex);
      return;
    }
    _commitBody(
        s,
        s._copyWith(
          body: reorderChild(s.body, parentPath, oldIndex, newIndex),
          clearSelection: true,
        ));
  }

  /// Move the node at [path] one slot up or down among its siblings, keeping it
  /// selected (selection follows the node to its new position). No-op at a
  /// boundary (already first when [up], already last when not), for the root, or
  /// an unresolvable path. The reorder goes through `nina_dom.reorderChild`,
  /// whose Flutter `onReorder` (pre-removal) convention is handled here.
  void moveNode(NodePath path, {required bool up}) {
    final s = state;
    if (s == null || path.isEmpty) return;
    final parentPath = path.sublist(0, path.length - 1);
    final parent = nodeAt(s.body, parentPath);
    if (parent == null) return;
    final i = path.last;
    final count = childrenOf(parent).length;
    if (up ? i <= 0 : i >= count - 1) return; // already at the boundary
    if (_liveRunActive) {
      _liveMove(state!, parentPath, i, up ? i - 1 : i + 1);
      return;
    }
    final newIndex = up ? i - 1 : i + 1; // destination sibling index
    // reorderChild takes a pre-removal newIndex: moving down, the slot is one
    // past the destination; moving up it's the destination itself.
    final preRemoval = up ? newIndex : newIndex + 1;
    _commitBody(
        s,
        s._copyWith(
          body: reorderChild(s.body, parentPath, i, preRemoval),
          selectedPath: <int>[...parentPath, newIndex],
        ));
  }

  /// Move the subtree at [fromPath] into the container at [toParentPath] at
  /// [toIndex] (a post-removal index, clamped) — the drag-and-drop reparent.
  /// Clears selection (paths shift wholesale). No-op if no sequence is loaded,
  /// [fromPath] is the root or unresolvable, [toParentPath] isn't a container, or
  /// the destination is the node itself or one of its descendants (which would
  /// orphan it). Goes through `nina_dom.moveSubtree`.
  void moveNodeTo(NodePath fromPath, NodePath toParentPath, int toIndex) {
    final s = state;
    if (s == null || fromPath.isEmpty) return;
    if (nodeAt(s.body, fromPath) == null) return;
    final dest = nodeAt(s.body, toParentPath);
    if (dest == null || !isContainer(dest)) return;
    // A leaf can't take children, and a node can't move inside itself.
    if (isAncestorOrSelf(fromPath, toParentPath)) return;
    if (_liveRunActive) {
      final sameParent = const ListEquality<int>()
          .equals(fromPath.sublist(0, fromPath.length - 1), toParentPath);
      if (!sameParent) {
        // v1 live moves are same-parent only (the daemon's move op contract);
        // reparenting mid-run stays a compose-mood edit.
        _notice("While running, items can be reordered within their group — "
            "moving into a different group needs the run to finish.");
        return;
      }
      _liveMove(s, toParentPath, fromPath.last,
          toIndex.clamp(0, childrenOf(dest).length - 1));
      return;
    }
    _commitBody(
        s,
        s._copyWith(
          body: moveSubtree(s.body, fromPath, toParentPath, toIndex),
          clearSelection: true,
        ));
  }

  /// Set scalar field [key] to [value] on the node at [path], keeping selection
  /// (structure is unchanged). No-op for an unresolvable path.
  void setNodeField(NodePath path, String key, Object? value) {
    final s = state;
    final node = s == null ? null : nodeAt(s.body, path);
    if (s == null || node == null) return;
    if (_refuseLiveValue(key, node[key], value)) return;
    _commitBody(s, s._copyWith(body: setField(s.body, path, key, value)),
        coalesceKey: 'f:${path.join('.')}/$key', liveField: true);
  }

  /// Append a fresh condition built from [def] to the container at
  /// [containerPath]; keeps selection (the container stays selected — a
  /// condition isn't a tree node and Item paths don't shift). No-op if no
  /// sequence is loaded, or [containerPath] doesn't resolve to a container (a
  /// leaf can't carry conditions — adding would graft a spurious `Conditions`
  /// wrapper onto it).
  void addConditionTo(NodePath containerPath, ConditionDef def) {
    final s = state;
    if (s == null) return;
    final container = nodeAt(s.body, containerPath);
    if (container == null || !isContainer(container)) return;
    _commitBody(
        s, s._copyWith(body: addCondition(s.body, containerPath, def.build())));
  }

  /// Remove the condition at [index] from the container at [containerPath];
  /// keeps selection. No-op for an unresolvable container or an out-of-range
  /// [index].
  void removeConditionFrom(NodePath containerPath, int index) {
    final s = state;
    if (s == null) return;
    final container = nodeAt(s.body, containerPath);
    if (container == null) return;
    if (index < 0 || index >= conditionsOf(container).length) return;
    _commitBody(
        s, s._copyWith(body: removeConditionAt(s.body, containerPath, index)));
  }

  /// Set scalar field [key] to [value] on the condition at [index] of the
  /// container at [containerPath]; keeps selection. No-op for an unresolvable
  /// container or an out-of-range [index].
  void setConditionFieldOn(
      NodePath containerPath, int index, String key, Object? value) {
    final s = state;
    if (s == null) return;
    final container = nodeAt(s.body, containerPath);
    if (container == null) return;
    if (index < 0 || index >= conditionsOf(container).length) return;
    if (_refuseLiveValue(key, conditionsOf(container)[index][key], value)) {
      return;
    }
    _commitBody(
        s,
        s._copyWith(
            body: setConditionField(s.body, containerPath, index, key, value)),
        coalesceKey: 'c:${containerPath.join('.')}/$index/$key',
        liveField: true);
  }

  /// Append a fresh trigger built from [def] to the container at [containerPath];
  /// keeps selection. No-op if no sequence is loaded, or [containerPath] doesn't
  /// resolve to a container (a leaf can't carry triggers — adding would graft a
  /// spurious `Triggers` wrapper onto it). Mirrors [addConditionTo].
  void addTriggerTo(NodePath containerPath, TriggerDef def) {
    final s = state;
    if (s == null) return;
    final container = nodeAt(s.body, containerPath);
    if (container == null || !isContainer(container)) return;
    _commitBody(
        s, s._copyWith(body: addTrigger(s.body, containerPath, def.build())));
  }

  /// Remove the trigger at [index] from the container at [containerPath]; keeps
  /// selection. No-op for an unresolvable container or an out-of-range [index].
  void removeTriggerFrom(NodePath containerPath, int index) {
    final s = state;
    if (s == null) return;
    final container = nodeAt(s.body, containerPath);
    if (container == null) return;
    if (index < 0 || index >= triggersOf(container).length) return;
    _commitBody(
        s, s._copyWith(body: removeTriggerAt(s.body, containerPath, index)));
  }

  /// Set scalar field [key] to [value] on the trigger at [index] of the container
  /// at [containerPath]; keeps selection. No-op for an unresolvable container or
  /// an out-of-range [index].
  void setTriggerFieldOn(
      NodePath containerPath, int index, String key, Object? value) {
    final s = state;
    if (s == null) return;
    final container = nodeAt(s.body, containerPath);
    if (container == null) return;
    if (index < 0 || index >= triggersOf(container).length) return;
    if (_refuseLiveValue(key, triggersOf(container)[index][key], value)) {
      return;
    }
    _commitBody(
        s,
        s._copyWith(
            body: setTriggerField(s.body, containerPath, index, key, value)),
        coalesceKey: 't:${containerPath.join('.')}/$index/$key',
        liveField: true);
  }

  /// Rebaseline dirty-tracking to [savedBody] — the exact body that was just
  /// persisted (call after a successful Save). Passing the *sent* snapshot
  /// (not re-reading live state) is deliberate: if an edit landed while the
  /// PATCH was in flight, the working body now differs from [savedBody], so
  /// [SequenceEditorState.isDirty] correctly stays true and that edit isn't
  /// silently marked clean. No-op if nothing is loaded.
  ///
  /// [modifiedUtc] is the saved record's new `modified_utc` (#2168), the copy
  /// a later live setting edit is based on.
  void markSaved(Map<String, dynamic> savedBody, {String? modifiedUtc}) {
    final s = state;
    if (s == null) return;
    state = s._copyWith(originalBody: savedBody, baseModified: modifiedUtc);
  }
}

/// #2168 — Resume from any button (the Simple plan panel, the "press Resume"
/// message, the stop overlay): the Sequencer editor's unsaved settings for
/// [id] join the run first, so the run never resumes on values the screen no
/// longer shows. When the daemon refuses them the run stays paused and this
/// throws a [StateError] with the reason, which the caller shows.
Future<String> resumeWithEdits(
    SequenceEditorController editor, SequenceClient api, String id,
    {bool recenter = true, bool refocus = false}) async {
  final why = await editor.saveLiveEditsBeforeResume(id);
  if (why != null) throw StateError(why);
  return api.resume(id, recenter: recenter, refocus: refocus);
}

/// The editor working-state for the open sequence (null when none is loaded).
final sequenceEditorProvider =
    NotifierProvider<SequenceEditorController, SequenceEditorState?>(
        SequenceEditorController.new);

/// §38.9 — transient user-facing message from a refused/failed live edit; the
/// Run tab listens and surfaces it as a SnackBar, then clears it. (Riverpod
/// 3.x removed StateProvider, so this is a minimal Notifier.)
class LiveEditNotice extends Notifier<String?> {
  @override
  String? build() => null;
  void show(String message) => state = message;
  void clear() => state = null;
}

final liveEditNoticeProvider =
    NotifierProvider<LiveEditNotice, String?>(LiveEditNotice.new);
