#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.DragDrop;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §38.9 — live mid-run sequence editing: add / remove / reorder PENDING items
/// while the run executes. The engine's <c>SequentialStrategy</c> re-snapshots
/// its container's children at every instruction boundary and picks the first
/// CREATED item, so a lock-guarded mutation of pending items is picked up
/// naturally — what this partial adds on top is the safety envelope:
///
///  * positions are constrained to strictly AFTER the parent's last started
///    item (nothing may land at/above the running position);
///  * the currently-running item (or any node with a started descendant) is
///    never removable — the user skips it first;
///  * block lifecycle hooks fire for late-added items (the strategy only runs
///    them for items present when the block initialized/started);
///  * the run's leaf list is re-based so progress totals stay correct;
///  * the accepted tree mutation is persisted by re-serializing the live tree
///    over the stored body (file == executor by construction), then announced
///    via the <c>sequence.run_items_changed</c> WS event.
///
/// Concurrency: RunState.EditGate serializes competing edit requests; each
/// container's own lock serializes the list op against the advancing engine.
/// The status re-checks happen inside the gate, immediately before the
/// mutation, closing the check-then-act window to the engine's own boundary
/// race (documented best-effort: a remove that loses that race skips the item
/// via the same mechanism as skip-current).
/// </summary>
public sealed partial class SequencerService {

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The fragment's SequenceBlockInitialize/Started hooks run arbitrary item code (equipment/template types); any escape must roll the splice back and surface as a clean InvalidItem refusal instead of a raw 500 with the live tree mutated but unpersisted. CA1031's log-and-recover boundary applies.")]
    public async Task<SequenceLiveEditResult> AddRunItemAsync(Guid id, SequenceRunItemAddRequestDto request, CancellationToken ct) {
        _unattendedShutdown?.NotifyUserActivity("sequencer.live_edit");
        // Deserialize the fragment OUTSIDE the edit gate (it can be arbitrarily
        // large and touches no shared state). Only container nodes are accepted
        // in v1 — the "add another object mid-run" use case grafts a target
        // block; bare instructions still land through the container they ride in.
        if (!_deserializer.TryDeserialize(request.Item, out var fragment, out var fragmentError) || fragment is null) {
            return new(SequenceLiveEditOutcome.InvalidItem, fragmentError ?? "item is not a deserializable sequence node");
        }
        return await MutateRunAsync(id, "add", run => {
            var resolved = ResolveParent(run, request.ParentPath);
            if (resolved.Error is not null) return (resolved.Error, null);
            var parent = resolved.Container!;
            if (parent is not IDropContainer drop) {
                return (new SequenceLiveEditResult(SequenceLiveEditOutcome.InvalidPath,
                    "the addressed container does not accept inserted items"), null);
            }
            var children = ItemsOf(parent);
            var floor = LastStartedIndex(children) + 1;
            var index = request.Index ?? children.Count;
            if (index < floor || index > children.Count) {
                return (new SequenceLiveEditResult(SequenceLiveEditOutcome.InvalidPath,
                    $"insert index {index} is not in the allowed range [{floor}..{children.Count}] (positions at or before a started item are locked)"), null);
            }
            drop.InsertIntoSequenceBlocks(index, fragment);
            // The strategy ran SequenceBlockInitialize/Started for the items that
            // were present at those phases; a late arrival gets them here so it
            // executes with the same lifecycle as its siblings. Initialize is
            // always due once the run started; Started only once the parent
            // block itself is past its start hooks (RUNNING or later).
            // Hook code is arbitrary item code (equipment/template types can
            // throw) — a throw here must UNDO the splice and come back as a
            // clean refusal, never escape as a raw 500 with the tree mutated
            // but unpersisted (review #871). CA1031's log-and-recover boundary.
            try {
                fragment.SequenceBlockInitialize();
                if (parent.Status != SequenceEntityStatus.CREATED) {
                    fragment.SequenceBlockStarted();
                }
            } catch (Exception ex) {
                parent.Remove(fragment);
                LogLiveEditHookFailed(ex, id);
                return (new SequenceLiveEditResult(SequenceLiveEditOutcome.InvalidItem,
                    "the item could not be initialized for the running plan and was not added"), null);
            }
            return (null, fragment);
        }, ct);
    }

    public Task<SequenceLiveEditResult> RemoveRunItemAsync(Guid id, SequenceRunItemRemoveRequestDto request, CancellationToken ct) {
        _unattendedShutdown?.NotifyUserActivity("sequencer.live_edit");
        return MutateRunAsync(id, "remove", run => {
            var resolved = ResolveItem(run, request.Path, out var parent);
            if (resolved.Error is not null) return (resolved.Error, null);
            var item = resolved.Item!;
            if (HasStarted(item)) {
                return (new SequenceLiveEditResult(SequenceLiveEditOutcome.ItemAlreadyStarted,
                    "the item (or something inside it) has already started — skip the current item instead, or wait for it to finish"), null);
            }
            parent!.Remove(item);
            // Best-effort boundary race: the engine can pick this item as `next`
            // between the status check and the Remove. If it raced into RUNNING,
            // fold it away with the same mechanism as skip-current.
            if (item.Status == SequenceEntityStatus.RUNNING) {
                LogLiveEditRemoveRaced(id);
                run.Root?.SkipCurrentRunningItems();
            }
            return (null, item);
        }, ct);
    }

    public Task<SequenceLiveEditResult> MoveRunItemAsync(Guid id, SequenceRunItemMoveRequestDto request, CancellationToken ct) {
        _unattendedShutdown?.NotifyUserActivity("sequencer.live_edit");
        return MutateRunAsync(id, "move", run => {
            var resolved = ResolveItem(run, request.Path, out var parent);
            if (resolved.Error is not null) return (resolved.Error, null);
            var item = resolved.Item!;
            if (HasStarted(item)) {
                return (new SequenceLiveEditResult(SequenceLiveEditOutcome.ItemAlreadyStarted,
                    "a started item cannot be reordered"), null);
            }
            var children = ItemsOf(parent!);
            var currentIndex = children.IndexOf(item);
            var floor = LastStartedIndex(children) + 1;
            // The list shrinks by one when the item is lifted out, so the last
            // valid destination is Count - 1 (still expressed pre-removal).
            if (request.NewIndex < floor || request.NewIndex > children.Count - 1) {
                return (new SequenceLiveEditResult(SequenceLiveEditOutcome.InvalidPath,
                    $"destination index {request.NewIndex} is not in the allowed range [{floor}..{children.Count - 1}] (positions at or before a started item are locked)"), null);
            }
            if (request.NewIndex == currentIndex) {
                return (null, item); // no-op move — still Applied (idempotent)
            }
            if (parent is not IDropContainer drop) {
                return (new SequenceLiveEditResult(SequenceLiveEditOutcome.InvalidPath,
                    "the addressed container does not support reordering"), null);
            }
            // Atomic under the container's own lock — the item never detaches from
            // its parent mid-move (unlike a remove + insert pair).
            drop.MoveWithinIntoSequenceBlocks(currentIndex, request.NewIndex);
            return (null, item);
        }, ct);
    }

    /// <summary>
    /// The shared §38.9 mutation envelope: resolve the run, take the edit gate,
    /// run the op, re-base the leaves, persist the re-serialized tree, emit the
    /// WS event. <paramref name="mutate"/> returns (refusal, null) to reject or
    /// (null, touchedItem) after mutating the tree.
    /// </summary>
    private async Task<SequenceLiveEditResult> MutateRunAsync(
            Guid id, string op,
            Func<RunState, (SequenceLiveEditResult? Error, ISequenceItem? Item)> mutate,
            CancellationToken ct) {
        if (!_runs.TryGetValue(id, out var run) || IsTerminal(run.State)) {
            return new(SequenceLiveEditOutcome.NoActiveRun, "no active run for this sequence — edit it with the normal update endpoint");
        }
        var sequences = _sequencesResolver?.Invoke();
        if (sequences is null) {
            return new(SequenceLiveEditOutcome.PersistFailed, "sequence store unavailable");
        }

        // The edit lock is held across mutation, serialization AND persist
        // (review #871): releasing before the file write would let a slower
        // request's older tree snapshot land on disk after a newer one,
        // silently regressing the stored body relative to the executing plan.
        try {
            await run.EditLock.WaitAsync(ct);
        } catch (ObjectDisposedException) {
            // The terminal run was evicted while we queued — same answer as
            // finding it terminal below.
            return new(SequenceLiveEditOutcome.NoActiveRun, "no active run for this sequence — edit it with the normal update endpoint");
        }
        SequenceDto? updated;
        try {
            // Re-check under the lock: the run may have wound down while we
            // queued behind another edit. Mutable = a live run whose tree is
            // published and whose root hasn't reached its teardown phase (once
            // the root leaves RUNNING/CREATED the strategy no longer picks up
            // new items — an accepted edit would silently never execute).
            var state = run.State;
            if (IsTerminal(state) || state == SequenceRunState.Aborting) {
                return new(SequenceLiveEditOutcome.RunNotMutable, "the run is winding down — wait for it to finish");
            }
            var root = run.Root;
            var bodyTop = run.BodyTop;
            if (root is null || bodyTop is null) {
                return new(SequenceLiveEditOutcome.RunNotMutable, "the run is still starting — try again in a moment");
            }
            if (root.Status is not (SequenceEntityStatus.CREATED or SequenceEntityStatus.RUNNING)) {
                return new(SequenceLiveEditOutcome.RunNotMutable, "the run is in its teardown phase — new items would not execute");
            }

            var (error, _) = mutate(run);
            if (error is not null) {
                return error;
            }

            // Re-base the progress denominator onto the edited plan shape.
            run.SetLeaves(CollectLeaves(root));
            var leaves = run.Leaves;
            run.UpdateProgress(leaves.Count, CountTerminalLeaves(leaves), RunningLeafIndex(leaves));

            // Serialize the live tree under the lock so no competing edit can
            // interleave between mutation and snapshot. The engine only writes
            // item STATUS scalars mid-run (collection mutations all come through
            // this lock), so the enumeration is safe.
            JsonElement newBody;
            try {
                newBody = _deserializer.SerializeBody(bodyTop);
            } catch (Exception ex) when (ex is InvalidOperationException or JsonException or Newtonsoft.Json.JsonException) {
                LogLiveEditSerializeFailed(ex, id);
                return new(SequenceLiveEditOutcome.PersistFailed, "the edited plan could not be re-serialized; the change was not saved");
            }

            // A store failure leaves tree and file diverged until the next
            // accepted edit, so surface it loudly — the client re-fetches and
            // re-tries rather than us attempting a fragile inverse tree op
            // against a plan that kept executing.
            updated = await sequences.ReplaceRunBodyAsync(id, newBody, ct);
            if (updated is null) {
                LogLiveEditPersistFailed(id, op);
                return new(SequenceLiveEditOutcome.PersistFailed, "the live plan was updated but could not be saved to the sequence file");
            }
        } finally {
            run.EditLock.Release();
        }

        await EmitRunItemsChangedAsync(id, run, op);
        WriteCheckpointIfOwner(run, id);
        return new(SequenceLiveEditOutcome.Applied, Sequence: updated);
    }

    private sealed record ResolvedContainer(ISequenceContainer? Container, SequenceLiveEditResult? Error);
    private sealed record ResolvedItem(ISequenceItem? Item, SequenceLiveEditResult? Error);

    /// <summary>Walk a child-index path from the body's top container to a container node.</summary>
    private static ResolvedContainer ResolveParent(RunState run, IReadOnlyList<int>? path) {
        ISequenceContainer current = run.BodyTop!;
        if (path is null) return new(current, null);
        for (var depth = 0; depth < path.Count; depth++) {
            var children = ItemsOf(current);
            var idx = path[depth];
            if (idx < 0 || idx >= children.Count) {
                return new(null, new SequenceLiveEditResult(SequenceLiveEditOutcome.InvalidPath,
                    $"path index {idx} at depth {depth} is out of range (container has {children.Count} items)"));
            }
            if (children[idx] is not ISequenceContainer next) {
                return new(null, new SequenceLiveEditResult(SequenceLiveEditOutcome.InvalidPath,
                    $"path index {idx} at depth {depth} is not a container"));
            }
            current = next;
        }
        return new(current, null);
    }

    /// <summary>Resolve a path to the addressed item + its parent container.</summary>
    private static ResolvedItem ResolveItem(RunState run, IReadOnlyList<int>? path, out ISequenceContainer? parent) {
        parent = null;
        if (path is null || path.Count == 0) {
            return new(null, new SequenceLiveEditResult(SequenceLiveEditOutcome.InvalidPath,
                "path must address an item (the body's top container itself cannot be removed or moved)"));
        }
        var parentResult = ResolveParent(run, path.Take(path.Count - 1).ToList());
        if (parentResult.Error is not null) return new(null, parentResult.Error);
        parent = parentResult.Container;
        var children = ItemsOf(parent!);
        var leafIdx = path[^1];
        if (leafIdx < 0 || leafIdx >= children.Count) {
            return new(null, new SequenceLiveEditResult(SequenceLiveEditOutcome.InvalidPath,
                $"path index {leafIdx} at depth {path.Count - 1} is out of range (container has {children.Count} items)"));
        }
        return new(children[leafIdx], null);
    }

    private static List<ISequenceItem> ItemsOf(ISequenceContainer container) =>
        [.. container.GetItemsSnapshot()];

    /// <summary>The last child index that has started (or finished, failed, was
    /// skipped) — insert/move destinations must be strictly after it. DISABLED
    /// items never execute, so they don't raise the floor.</summary>
    private static int LastStartedIndex(List<ISequenceItem> children) {
        for (var i = children.Count - 1; i >= 0; i--) {
            if (children[i].Status is SequenceEntityStatus.RUNNING or SequenceEntityStatus.FINISHED
                                   or SequenceEntityStatus.FAILED or SequenceEntityStatus.SKIPPED) {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Whether the node — or anything inside it — has left CREATED
    /// (DISABLED excepted: a disabled node never runs, so it stays removable).</summary>
    private static bool HasStarted(ISequenceItem item) {
        if (item.Status is not (SequenceEntityStatus.CREATED or SequenceEntityStatus.DISABLED)) {
            return true;
        }
        if (item is ISequenceContainer container) {
            foreach (var child in container.GetItemsSnapshot()) {
                if (HasStarted(child)) return true;
            }
        }
        return false;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "WS publish is best-effort; a broadcaster fault must not fail an already-applied edit. Same boundary as EmitAsync.")]
    private async Task EmitRunItemsChangedAsync(Guid sequenceId, RunState run, string op) {
        if (_ws is null) return;
        try {
            var payload = new JsonObject {
                ["sequence_id"] = sequenceId.ToString(),
                ["run_id"] = run.RunId.ToString(),
                ["op"] = op,
                ["instructions_completed"] = run.InstructionsCompleted,
                ["instructions_total"] = run.InstructionCount,
            };
            using var doc = JsonDocument.Parse(payload.ToJsonString());
            await _ws.PublishAsync(WsEventCatalog.SequenceRunItemsChanged, doc.RootElement.Clone(), CancellationToken.None);
        } catch (Exception) {
            // Best-effort; the applied edit is already persisted.
        }
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "§38.9 live remove on run {SequenceId} raced the engine — the item started mid-remove and was folded away via skip-current")]
    private partial void LogLiveEditRemoveRaced(Guid sequenceId);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "§38.9 live add on run {SequenceId}: the fragment's block lifecycle hooks threw — splice rolled back, add refused")]
    private partial void LogLiveEditHookFailed(Exception ex, Guid sequenceId);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "§38.9 live edit on run {SequenceId} could not re-serialize the edited plan")]
    private partial void LogLiveEditSerializeFailed(Exception ex, Guid sequenceId);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "§38.9 live {Op} on run {SequenceId} was applied to the executing plan but failed to persist — file and run diverge until the next accepted edit")]
    private partial void LogLiveEditPersistFailed(Guid sequenceId, string op);
}
