import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/library/frame_viewer.dart';
import '../../models/ws_event.dart';
import '../ws/ws_providers.dart';
import 'live_library_state.dart';

abstract final class FrameWsEvents {
  static const persistStarted = 'frame.persist_started';
  static const persistProgress = 'frame.persist_progress';
  static const complete = 'frame.complete';
  static const analysisStarted = 'frame.analysis_started';
  static const analyzed = 'frame.analyzed';
  static const previewStarted = 'frame.preview_started';
  static const previewReady = 'frame.preview_ready';
  static const previewReadyLegacy = 'frame.preview.ready';
  static const failed = 'frame.failed';
  static const quarantined = 'frame.quarantined';
}

class FrameLifecycleProgress {
  final String storageState;
  final double? storageProgress;
  final String analysisState;
  final String previewState;
  final String? failureStage;
  final String? failureCode;
  final String? failureMessage;
  final int lastEventSequence;

  const FrameLifecycleProgress({
    this.storageState = 'unknown',
    this.storageProgress,
    this.analysisState = 'unknown',
    this.previewState = 'unknown',
    this.failureStage,
    this.failureCode,
    this.failureMessage,
    this.lastEventSequence = -1,
  });

  factory FrameLifecycleProgress.fromMetadata(FrameMetadata metadata) =>
      FrameLifecycleProgress(
        storageState:
            metadata.storage?.state ??
            (metadata.sourceExists ? 'complete' : 'unknown'),
        storageProgress: metadata.storage?.state.toLowerCase() == 'complete'
            ? 1
            : null,
        analysisState: metadata.analysisState ?? 'unknown',
        previewState: metadata.previewState ?? 'unknown',
        failureStage: metadata.analysisFailureCode != null
            ? 'analysis'
            : metadata.previewFailureCode != null
            ? 'preview'
            : metadata.storage?.failureCode != null
            ? 'storage'
            : null,
        failureCode:
            metadata.analysisFailureCode ??
            metadata.previewFailureCode ??
            metadata.storage?.failureCode,
        failureMessage:
            metadata.analysisFailureMessage ??
            metadata.previewFailureMessage ??
            metadata.storage?.failureMessage,
      );

  FrameLifecycleProgress copyWith({
    String? storageState,
    double? Function()? storageProgress,
    String? analysisState,
    String? previewState,
    String? Function()? failureStage,
    String? Function()? failureCode,
    String? Function()? failureMessage,
    int? lastEventSequence,
  }) => FrameLifecycleProgress(
    storageState: storageState ?? this.storageState,
    storageProgress: storageProgress != null
        ? storageProgress()
        : this.storageProgress,
    analysisState: analysisState ?? this.analysisState,
    previewState: previewState ?? this.previewState,
    failureStage: failureStage != null ? failureStage() : this.failureStage,
    failureCode: failureCode != null ? failureCode() : this.failureCode,
    failureMessage: failureMessage != null
        ? failureMessage()
        : this.failureMessage,
    lastEventSequence: lastEventSequence ?? this.lastEventSequence,
  );
}

/// Pure lifecycle fold. Irrelevant frames and duplicate/out-of-order events
/// return null, so reconnect replay cannot move the progress strip backward.
FrameLifecycleProgress? foldFrameLifecycle(
  FrameLifecycleProgress current,
  WsEvent event,
  String frameId,
) {
  if (event.payload['frame_id'] != frameId ||
      event.seq <= current.lastEventSequence) {
    return null;
  }
  final sequence = event.seq;
  final state = event.payload['state'] is String
      ? event.payload['state'] as String
      : null;
  switch (event.type) {
    case FrameWsEvents.persistStarted:
      return current.copyWith(
        storageState: state ?? 'accepted',
        storageProgress: () => _eventProgress(event) ?? 0,
        lastEventSequence: sequence,
      );
    case FrameWsEvents.persistProgress:
      return current.copyWith(
        storageState: state ?? current.storageState,
        storageProgress: () => _eventProgress(event),
        lastEventSequence: sequence,
      );
    case FrameWsEvents.complete:
      return current.copyWith(
        storageState: 'complete',
        storageProgress: () => 1,
        lastEventSequence: sequence,
      );
    case FrameWsEvents.analysisStarted:
      return current.copyWith(
        analysisState: state ?? 'analyzing',
        failureStage: () => null,
        failureCode: () => null,
        failureMessage: () => null,
        lastEventSequence: sequence,
      );
    case FrameWsEvents.analyzed:
      return current.copyWith(
        analysisState: state ?? 'ready',
        failureStage: () => null,
        failureCode: () => null,
        failureMessage: () => null,
        lastEventSequence: sequence,
      );
    case FrameWsEvents.previewStarted:
      return current.copyWith(
        previewState: state ?? 'rendering',
        failureStage: () => null,
        failureCode: () => null,
        failureMessage: () => null,
        lastEventSequence: sequence,
      );
    case FrameWsEvents.previewReady:
    case FrameWsEvents.previewReadyLegacy:
      return current.copyWith(
        previewState: state ?? 'ready',
        failureStage: () => null,
        failureCode: () => null,
        failureMessage: () => null,
        lastEventSequence: sequence,
      );
    case FrameWsEvents.failed:
      final stage = event.payload['stage'] is String
          ? event.payload['stage'] as String
          : 'unknown';
      return current.copyWith(
        analysisState: stage == 'analysis' ? 'failed' : null,
        previewState: stage == 'preview' ? 'failed' : null,
        storageState: stage == 'storage' ? 'failed' : null,
        failureStage: () => stage,
        failureCode: () => event.payload['code'] is String
            ? event.payload['code'] as String
            : 'operation_failed',
        failureMessage: () => event.payload['message'] is String
            ? event.payload['message'] as String
            : 'Frame operation failed.',
        lastEventSequence: sequence,
      );
    case FrameWsEvents.quarantined:
      return current.copyWith(lastEventSequence: sequence);
    default:
      return null;
  }
}

double? _eventProgress(WsEvent event) {
  final value = (event.payload['progress'] as num?)?.toDouble();
  return value == null || !value.isFinite ? null : value.clamp(0, 1);
}

enum FrameOperationKind { rebuildPreview, reanalyze }

class FrameJobPollingPolicy {
  final Duration interval;
  final int maxPolls;

  const FrameJobPollingPolicy({
    this.interval = const Duration(milliseconds: 750),
    this.maxPolls = 80,
  });
}

/// Testable bound for the WebSocket-fallback job monitor.
final frameJobPollingPolicyProvider = Provider<FrameJobPollingPolicy>(
  (ref) => const FrameJobPollingPolicy(),
);

class FrameViewerState {
  final FrameMetadata? metadata;
  final FramePreviewImage? preview;
  final FramePreviewOptions options;
  final FramePreviewOptions? renderedOptions;
  final FrameLifecycleProgress lifecycle;
  final bool previewLoading;
  final bool metadataRefreshing;
  final bool mutationBusy;
  final String? previewError;
  final String? metadataError;
  final String? actionError;
  final FrameJobStatus? operation;
  final FrameOperationKind? operationKind;
  final bool operationTimedOut;

  const FrameViewerState({
    this.metadata,
    this.preview,
    this.options = const FramePreviewOptions(),
    this.renderedOptions,
    this.lifecycle = const FrameLifecycleProgress(),
    this.previewLoading = false,
    this.metadataRefreshing = false,
    this.mutationBusy = false,
    this.previewError,
    this.metadataError,
    this.actionError,
    this.operation,
    this.operationKind,
    this.operationTimedOut = false,
  });

  FrameViewerState copyWith({
    FrameMetadata? Function()? metadata,
    FramePreviewImage? Function()? preview,
    FramePreviewOptions? options,
    FramePreviewOptions? Function()? renderedOptions,
    FrameLifecycleProgress? lifecycle,
    bool? previewLoading,
    bool? metadataRefreshing,
    bool? mutationBusy,
    String? Function()? previewError,
    String? Function()? metadataError,
    String? Function()? actionError,
    FrameJobStatus? Function()? operation,
    FrameOperationKind? Function()? operationKind,
    bool? operationTimedOut,
  }) => FrameViewerState(
    metadata: metadata != null ? metadata() : this.metadata,
    preview: preview != null ? preview() : this.preview,
    options: options ?? this.options,
    renderedOptions: renderedOptions != null
        ? renderedOptions()
        : this.renderedOptions,
    lifecycle: lifecycle ?? this.lifecycle,
    previewLoading: previewLoading ?? this.previewLoading,
    metadataRefreshing: metadataRefreshing ?? this.metadataRefreshing,
    mutationBusy: mutationBusy ?? this.mutationBusy,
    previewError: previewError != null ? previewError() : this.previewError,
    metadataError: metadataError != null ? metadataError() : this.metadataError,
    actionError: actionError != null ? actionError() : this.actionError,
    operation: operation != null ? operation() : this.operation,
    operationKind: operationKind != null ? operationKind() : this.operationKind,
    operationTimedOut: operationTimedOut ?? this.operationTimedOut,
  );
}

class FrameViewerController extends AsyncNotifier<FrameViewerState> {
  static const _renderDebounce = Duration(milliseconds: 200);

  final String frameId;
  int _previewGeneration = 0;
  int _metadataGeneration = 0;
  int _monitorGeneration = 0;
  Timer? _renderTimer;
  Timer? _metadataRefreshTimer;
  CancelToken? _previewCancelToken;

  FrameViewerController(this.frameId);

  @override
  Future<FrameViewerState> build() async {
    // Rebuild on active-server changes and bind this provider's lifecycle to
    // the active socket. libraryApiProvider owns/cleans the Dio client.
    final api = ref.watch(libraryApiProvider);
    ref.watch(wsEventStreamProvider);
    ref.listen(wsEventsProvider, (previous, next) {
      final event = next.asData?.value;
      if (event != null) _applyEvent(event);
    });
    ref.onDispose(() {
      _renderTimer?.cancel();
      _metadataRefreshTimer?.cancel();
      _previewCancelToken?.cancel('frame viewer disposed');
      _monitorGeneration++;
    });

    if (api == null) {
      return const FrameViewerState(
        metadataError: 'Connect to a server to inspect this frame.',
        previewError: 'Connect to a server to load this preview.',
      );
    }

    final options = const FramePreviewOptions();
    final token = CancelToken();
    _previewCancelToken = token;
    final results = await Future.wait<Object?>([
      _attempt(() => api.frameMetadata(frameId)),
      _attempt(() => api.fetchPreview(frameId, options, cancelToken: token)),
    ]);
    final metadataAttempt = results[0] as _Attempt<FrameMetadata>;
    final previewAttempt = results[1] as _Attempt<FramePreviewImage>;
    final metadata = metadataAttempt.value;
    return FrameViewerState(
      metadata: metadata,
      preview: previewAttempt.value,
      options: options,
      renderedOptions: previewAttempt.value == null ? null : options,
      lifecycle: metadata == null
          ? const FrameLifecycleProgress()
          : FrameLifecycleProgress.fromMetadata(metadata),
      metadataError: metadataAttempt.error == null
          ? null
          : frameFailureMessage(metadataAttempt.error!),
      previewError: previewAttempt.error == null
          ? null
          : frameFailureMessage(previewAttempt.error!),
    );
  }

  Future<void> setOptions(
    FramePreviewOptions options, {
    bool debounce = false,
  }) async {
    final normalized = _normalizeOptions(options);
    final current = state.value;
    if (current == null) return;
    state = AsyncData(
      current.copyWith(options: normalized, actionError: () => null),
    );
    _renderTimer?.cancel();
    if (debounce) {
      // A slider edit supersedes an in-flight render immediately, even though
      // its replacement waits for the debounce window. This prevents a slow
      // older response from snapping the visible controls backward.
      _previewGeneration++;
      _previewCancelToken?.cancel('superseded preview controls');
      _renderTimer = Timer(_renderDebounce, () => _render(normalized).ignore());
      return;
    }
    await _render(normalized);
  }

  Future<void> retryPreview() =>
      _render(state.value?.options ?? const FramePreviewOptions());

  Future<void> resetPreview() => setOptions(const FramePreviewOptions());

  Future<void> _render(FramePreviewOptions options) async {
    final api = ref.read(libraryApiProvider);
    final current = state.value;
    if (api == null || current == null) return;
    final generation = ++_previewGeneration;
    _previewCancelToken?.cancel('superseded preview request');
    final token = CancelToken();
    _previewCancelToken = token;
    state = AsyncData(
      current.copyWith(
        options: options,
        previewLoading: true,
        previewError: () => null,
      ),
    );
    try {
      final preview = await api.fetchPreview(
        frameId,
        options,
        cancelToken: token,
      );
      if (!ref.mounted || generation != _previewGeneration) return;
      final appliedOptions = _optionsFromApplied(options, preview.applied);
      final latest = state.value;
      if (latest == null) return;
      state = AsyncData(
        latest.copyWith(
          preview: () => preview,
          options: appliedOptions,
          renderedOptions: () => appliedOptions,
          previewLoading: false,
          previewError: () => null,
        ),
      );
    } on Object catch (error) {
      if (!ref.mounted || generation != _previewGeneration) return;
      final latest = state.value;
      if (latest == null) return;
      // A superseded request is expected and the newer request owns state.
      if (error is DioException && error.type == DioExceptionType.cancel) {
        return;
      }
      state = AsyncData(
        latest.copyWith(
          options: latest.renderedOptions ?? options,
          previewLoading: false,
          previewError: () => frameFailureMessage(error),
        ),
      );
    }
  }

  Future<void> refreshMetadata() async {
    final api = ref.read(libraryApiProvider);
    final current = state.value;
    if (api == null || current == null) return;
    final generation = ++_metadataGeneration;
    state = AsyncData(
      current.copyWith(metadataRefreshing: true, metadataError: () => null),
    );
    try {
      final metadata = await api.frameMetadata(frameId);
      if (!ref.mounted || generation != _metadataGeneration) return;
      final latest = state.value;
      if (latest == null) return;
      final fromMetadata = FrameLifecycleProgress.fromMetadata(metadata);
      state = AsyncData(
        latest.copyWith(
          metadata: () => metadata,
          lifecycle: fromMetadata.copyWith(
            lastEventSequence: latest.lifecycle.lastEventSequence,
          ),
          metadataRefreshing: false,
        ),
      );
    } on Object catch (error) {
      if (!ref.mounted || generation != _metadataGeneration) return;
      final latest = state.value;
      if (latest == null) return;
      state = AsyncData(
        latest.copyWith(
          metadataRefreshing: false,
          metadataError: () => frameFailureMessage(error),
        ),
      );
    }
  }

  Future<void> setRating(int rating) async {
    if (rating < 0 || rating > 5) return;
    final api = ref.read(libraryApiProvider);
    final current = state.value;
    final metadata = current?.metadata;
    if (api == null ||
        current == null ||
        metadata == null ||
        current.mutationBusy) {
      return;
    }
    final optimistic = metadata.copyWith(
      frame: metadata.frame.copyWith(rating: rating),
    );
    state = AsyncData(
      current.copyWith(
        metadata: () => optimistic,
        mutationBusy: true,
        actionError: () => null,
      ),
    );
    try {
      await api.bulkRate([frameId], rating);
      if (!ref.mounted) return;
      final latest = state.value;
      if (latest != null) {
        state = AsyncData(latest.copyWith(mutationBusy: false));
      }
      ref.invalidate(sessionFramesProvider(metadata.frame.sessionId));
    } on Object catch (error) {
      if (!ref.mounted) return;
      final latest = state.value;
      if (latest != null) {
        state = AsyncData(
          latest.copyWith(
            metadata: () => metadata,
            mutationBusy: false,
            actionError: () => frameFailureMessage(error),
          ),
        );
      }
    }
  }

  Future<void> editTag({String? add, String? remove}) async {
    final api = ref.read(libraryApiProvider);
    final current = state.value;
    final metadata = current?.metadata;
    if (api == null ||
        current == null ||
        metadata == null ||
        current.mutationBusy) {
      return;
    }
    final tags = [...metadata.frame.tags];
    if (remove != null) tags.remove(remove);
    if (add != null && add.isNotEmpty && !tags.contains(add)) tags.add(add);
    final optimistic = metadata.copyWith(
      frame: metadata.frame.copyWith(tags: tags),
    );
    state = AsyncData(
      current.copyWith(
        metadata: () => optimistic,
        mutationBusy: true,
        actionError: () => null,
      ),
    );
    try {
      await api.bulkTag([frameId], addTags: [?add], removeTags: [?remove]);
      if (!ref.mounted) return;
      final latest = state.value;
      if (latest != null) {
        state = AsyncData(latest.copyWith(mutationBusy: false));
      }
    } on Object catch (error) {
      if (!ref.mounted) return;
      final latest = state.value;
      if (latest != null) {
        state = AsyncData(
          latest.copyWith(
            metadata: () => metadata,
            mutationBusy: false,
            actionError: () => frameFailureMessage(error),
          ),
        );
      }
    }
  }

  Future<void> setQuarantined(bool quarantined, {String? reason}) async {
    final api = ref.read(libraryApiProvider);
    final current = state.value;
    if (api == null || current == null || current.mutationBusy) return;
    state = AsyncData(
      current.copyWith(mutationBusy: true, actionError: () => null),
    );
    try {
      await api.bulkQuarantine(
        [frameId],
        quarantined: quarantined,
        reason: reason,
      );
      if (!ref.mounted) return;
      final latest = state.value;
      if (latest != null) {
        state = AsyncData(latest.copyWith(mutationBusy: false));
      }
      ref.invalidate(sessionFramesProvider);
      await refreshMetadata();
    } on Object catch (error) {
      if (!ref.mounted) return;
      final latest = state.value;
      if (latest != null) {
        state = AsyncData(
          latest.copyWith(
            mutationBusy: false,
            actionError: () => frameFailureMessage(error),
          ),
        );
      }
    }
  }

  Future<void> rebuildPreview() async {
    await _startOperation(FrameOperationKind.rebuildPreview);
  }

  Future<void> reanalyze() async {
    await _startOperation(FrameOperationKind.reanalyze);
  }

  Future<void> _startOperation(FrameOperationKind kind) async {
    final api = ref.read(libraryApiProvider);
    final current = state.value;
    if (api == null ||
        current == null ||
        (current.operation != null && !current.operation!.isTerminal)) {
      return;
    }
    state = AsyncData(
      current.copyWith(
        mutationBusy: true,
        actionError: () => null,
        operation: () => null,
        operationKind: () => kind,
        operationTimedOut: false,
      ),
    );
    try {
      final accepted = kind == FrameOperationKind.rebuildPreview
          ? await api.rebuildPreview(frameId, current.options)
          : await api.reanalyze(frameId);
      if (!ref.mounted) return;
      final queued = FrameJobStatus(
        jobId: accepted.operationId,
        jobType: accepted.operationType,
        state: 'queued',
        done: 0,
        total: 1,
        startedUtc: accepted.acceptedUtc,
        finishedUtc: null,
        errorMessage: null,
      );
      final latest = state.value;
      if (latest == null) return;
      state = AsyncData(
        latest.copyWith(
          mutationBusy: false,
          operation: () => queued,
          operationKind: () => kind,
        ),
      );
      _monitorJob(queued, kind).ignore();
    } on Object catch (error) {
      if (!ref.mounted) return;
      final latest = state.value;
      if (latest != null) {
        state = AsyncData(
          latest.copyWith(
            mutationBusy: false,
            operationKind: () => null,
            actionError: () => frameFailureMessage(error),
          ),
        );
      }
    }
  }

  Future<void> retryOperationStatus() async {
    final current = state.value;
    final operation = current?.operation;
    final kind = current?.operationKind;
    if (operation == null || kind == null || operation.isTerminal) return;
    state = AsyncData(current!.copyWith(operationTimedOut: false));
    await _monitorJob(operation, kind);
  }

  Future<void> cancelOperation() async {
    final api = ref.read(libraryApiProvider);
    final current = state.value;
    final operation = current?.operation;
    final kind = current?.operationKind;
    if (api == null ||
        current == null ||
        operation == null ||
        kind == null ||
        operation.state == 'cancelling' ||
        operation.isTerminal) {
      return;
    }
    final generation = ++_monitorGeneration;
    state = AsyncData(current.copyWith(mutationBusy: true));
    try {
      await api.cancelJob(operation.jobId);
      if (!ref.mounted || generation != _monitorGeneration) return;
      // DELETE only requests cancellation. The worker may still finish or win
      // the race, so keep this non-terminal until GET /jobs reports truth.
      final cancelling = FrameJobStatus(
        jobId: operation.jobId,
        jobType: operation.jobType,
        state: 'cancelling',
        done: operation.done,
        total: operation.total,
        startedUtc: operation.startedUtc,
        finishedUtc: null,
        errorMessage: null,
      );
      final latest = state.value;
      if (latest != null) {
        state = AsyncData(
          latest.copyWith(mutationBusy: false, operation: () => cancelling),
        );
      }
      await _monitorJob(cancelling, kind);
    } on Object catch (error) {
      if (!ref.mounted || generation != _monitorGeneration) return;
      final latest = state.value;
      if (latest != null) {
        state = AsyncData(
          latest.copyWith(
            mutationBusy: false,
            actionError: () => frameFailureMessage(error),
          ),
        );
      }
      // cancelOperation invalidated the previous monitor before sending the
      // DELETE. If that request fails, resume authoritative GET polling so a
      // job that completed meanwhile cannot remain stuck as "running".
      _monitorJob(operation, kind).ignore();
    }
  }

  Future<void> _monitorJob(
    FrameJobStatus initial,
    FrameOperationKind kind,
  ) async {
    final api = ref.read(libraryApiProvider);
    if (api == null) return;
    final generation = ++_monitorGeneration;
    final polling = ref.read(frameJobPollingPolicyProvider);
    FrameJobStatus latestJob = initial;
    Object? lastError;
    for (var attempt = 0; attempt < polling.maxPolls; attempt++) {
      if (!ref.mounted || generation != _monitorGeneration) return;
      if (attempt > 0) await Future<void>.delayed(polling.interval);
      if (!ref.mounted || generation != _monitorGeneration) return;
      try {
        latestJob = await api.jobStatus(initial.jobId);
        lastError = null;
      } on Object catch (error) {
        lastError = error;
        continue;
      }
      final current = state.value;
      if (current == null) return;
      state = AsyncData(
        current.copyWith(operation: () => latestJob, actionError: () => null),
      );
      if (!latestJob.isTerminal) continue;

      if (latestJob.state == 'complete') {
        await refreshMetadata();
        if (kind == FrameOperationKind.rebuildPreview) {
          await _render(state.value?.options ?? const FramePreviewOptions());
        } else {
          final sessionId = state.value?.metadata?.frame.sessionId;
          if (sessionId != null) {
            ref.invalidate(sessionFramesProvider(sessionId));
          }
        }
      } else if (latestJob.state == 'failed') {
        final currentAfter = state.value;
        if (currentAfter != null) {
          state = AsyncData(
            currentAfter.copyWith(
              actionError: () =>
                  latestJob.errorMessage ??
                  'Frame operation failed without a server message.',
            ),
          );
        }
        await refreshMetadata();
      } else {
        await refreshMetadata();
      }
      return;
    }

    if (!ref.mounted || generation != _monitorGeneration) return;
    final current = state.value;
    if (current != null) {
      state = AsyncData(
        current.copyWith(
          operationTimedOut: true,
          actionError: () => lastError == null
              ? 'Operation still running; status monitoring timed out.'
              : 'Operation status unavailable: ${frameFailureMessage(lastError)}',
        ),
      );
    }
  }

  void _applyEvent(WsEvent event) {
    final current = state.value;
    if (current == null) return;
    final lifecycle = foldFrameLifecycle(current.lifecycle, event, frameId);
    if (lifecycle == null) return;
    state = AsyncData(
      current.copyWith(
        lifecycle: lifecycle,
        actionError: event.type == FrameWsEvents.failed
            ? () => lifecycle.failureMessage ?? 'Frame operation failed.'
            : null,
      ),
    );

    if (event.type == FrameWsEvents.analyzed ||
        event.type == FrameWsEvents.previewReady ||
        event.type == FrameWsEvents.previewReadyLegacy ||
        event.type == FrameWsEvents.quarantined ||
        event.type == FrameWsEvents.failed) {
      _metadataRefreshTimer?.cancel();
      _metadataRefreshTimer = Timer(const Duration(milliseconds: 100), () {
        refreshMetadata().ignore();
        if (event.type == FrameWsEvents.analyzed) {
          final sessionId = state.value?.metadata?.frame.sessionId;
          if (sessionId != null) {
            ref.invalidate(sessionFramesProvider(sessionId));
          }
        }
      });
    }
  }

  static FramePreviewOptions _normalizeOptions(FramePreviewOptions options) {
    var channel = options.channel;
    if (!options.applyDebayer && channel != FrameChannel.luminance) {
      channel = FrameChannel.luminance;
    }
    final black = options.blackPoint.clamp(0.0, 0.99);
    final white = options.whitePoint.clamp(black + 0.001, 1.0);
    final clipLow = options.linearClipLow.clamp(0.0, 0.999);
    return options.copyWith(
      channel: channel,
      saturation: options.saturation.clamp(0, 2),
      blackPoint: black,
      midtonePoint: options.midtonePoint.clamp(0, 1),
      whitePoint: white,
      asinhBeta: options.asinhBeta.clamp(0.01, 1000000),
      linearClipLow: clipLow,
      linearClipHigh: options.linearClipHigh.clamp(clipLow + 0.001, 1.0),
      maxDimensionPx: options.maxDimensionPx.clamp(1, 4096),
    );
  }

  static FramePreviewOptions _optionsFromApplied(
    FramePreviewOptions requested,
    FramePreviewApplied applied,
  ) {
    final stretch = FrameStretch.values
        .where((value) => value.wireName == applied.algorithm)
        .firstOrNull;
    return requested.copyWith(
      stretch: stretch,
      blackPoint: applied.blackPoint,
      midtonePoint: applied.midtonePoint,
      whitePoint: applied.whitePoint,
      asinhBeta: applied.asinhBeta,
      linearClipLow: applied.linearClipLow,
      linearClipHigh: applied.linearClipHigh,
      saturation: applied.saturation,
      invert: applied.inverted,
      annotateStars: applied.annotated,
    );
  }
}

final frameViewerProvider = AsyncNotifierProvider.autoDispose
    .family<FrameViewerController, FrameViewerState, String>(
      FrameViewerController.new,
    );

Future<_Attempt<T>> _attempt<T>(Future<T> Function() action) async {
  try {
    return _Attempt<T>(value: await action());
  } on Object catch (error) {
    return _Attempt<T>(error: error);
  }
}

class _Attempt<T> {
  final T? value;
  final Object? error;
  const _Attempt({this.value, this.error});
}

/// Stable, path-safe user message. RFC 7807 detail/title wins; transport
/// failures are summarized without dumping a URI, stack, or local path.
String frameFailureMessage(Object error) {
  if (error is DioException) {
    if (error.type == DioExceptionType.cancel) return 'Operation cancelled.';
    final data = error.response?.data;
    if (data is Map<String, dynamic>) {
      final detail = data['detail'];
      if (detail is String && detail.trim().isNotEmpty) return detail.trim();
      final title = data['title'];
      if (title is String && title.trim().isNotEmpty) return title.trim();
    }
    return switch (error.type) {
      DioExceptionType.connectionTimeout ||
      DioExceptionType.sendTimeout ||
      DioExceptionType.receiveTimeout => 'The daemon did not respond in time.',
      DioExceptionType.connectionError => 'Could not reach the daemon.',
      DioExceptionType.badResponse =>
        'The daemon rejected the request (${error.response?.statusCode ?? 'unknown status'}).',
      _ => 'The frame request failed.',
    };
  }
  if (error is FormatException) return error.message;
  return 'The frame request failed.';
}
