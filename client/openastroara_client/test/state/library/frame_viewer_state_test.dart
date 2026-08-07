import 'dart:async';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/cursor_page.dart';
import 'package:openastroara/models/library/frame_viewer.dart';
import 'package:openastroara/models/library/live_library.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/services/library_api.dart';
import 'package:openastroara/state/library/frame_viewer_state.dart';
import 'package:openastroara/state/library/live_library_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';

class _FrameFake implements LibraryClient {
  int metadataCalls = 0;
  int previewCalls = 0;
  int rating = 2;
  List<String> tags = ['keeper'];
  DateTime? quarantinedUtc;
  String? quarantineReason;
  bool failRating = false;
  bool cancelledJob = false;
  bool failCancel = false;
  final Map<FrameStretch, Completer<FramePreviewImage>> heldPreviews = {};
  final List<FrameJobStatus> jobStates = [];
  int jobStatusCalls = 0;

  FrameMetadata metadata() => FrameMetadata(
    frame: FrameMetadataItem(
      id: 'f1',
      sessionId: 's1',
      targetName: 'M42',
      frameType: 'light',
      filterName: 'Ha',
      exposureSeconds: 300,
      gain: 100,
      offset: 10,
      temperatureC: -10,
      capturedUtc: DateTime.utc(2026, 8, 1),
      fileSizeBytes: 42,
      width: 4,
      height: 4,
      bitDepth: 16,
      hfr: 1.5,
      starCount: 100,
      eccentricity: 0.4,
      guidingRmsArcsec: 0.6,
      snrEstimate: 25,
      rating: rating,
      tags: [...tags],
      focuserPosition: 5000,
      analysisVersion: 'stars-v1',
      quarantinedUtc: quarantinedUtc,
      quarantineReason: quarantineReason,
    ),
    storage: FrameStorageMetadata(
      state: 'complete',
      acceptedUtc: DateTime.utc(2026, 8, 1),
      completedUtc: DateTime.utc(2026, 8, 1),
      byteCount: 42,
      checksumSha256: 'abc',
      imageFormat: 'fits',
      cfaPattern: 'RGGB',
      failureCode: null,
      failureMessage: null,
      updatedUtc: DateTime.utc(2026, 8, 1),
    ),
    sourceExists: true,
    sourceChecksumSha256: 'abc',
    imageFormat: 'fits',
    cfaPattern: 'RGGB',
    analysisState: 'ready',
    analysisFailureCode: null,
    analysisFailureMessage: null,
    previewState: 'ready',
    previewFailureCode: null,
    previewFailureMessage: null,
    previewChecksum: 'def',
    debayerMethod: 'super_pixel',
    previewVersion: 'schema-2',
  );

  FramePreviewImage preview(FramePreviewOptions options) => FramePreviewImage(
    bytes: Uint8List.fromList([options.stretch.index + 1]),
    applied: FramePreviewApplied(
      width: 4,
      height: 4,
      cacheStatus: 'miss',
      algorithm: options.stretch.wireName,
      blackPoint: options.blackPoint,
      midtonePoint: options.midtonePoint,
      whitePoint: options.whitePoint,
      asinhBeta: options.asinhBeta,
      linearClipLow: options.linearClipLow,
      linearClipHigh: options.linearClipHigh,
      debayerMode: options.applyDebayer ? 'super_pixel' : 'none',
      channelMode: options.channel.wireName,
      inverted: options.invert,
      saturation: options.saturation,
      annotated: options.annotateStars,
    ),
  );

  @override
  Future<FrameMetadata> frameMetadata(String frameId) async {
    metadataCalls++;
    return metadata();
  }

  @override
  Future<FramePreviewImage> fetchPreview(
    String frameId,
    FramePreviewOptions options, {
    CancelToken? cancelToken,
  }) async {
    previewCalls++;
    final held = heldPreviews[options.stretch];
    if (held == null) return preview(options);
    if (cancelToken == null) return held.future;
    return Future.any([
      held.future,
      cancelToken.whenCancel.then<FramePreviewImage>((error) => throw error),
    ]);
  }

  FrameOperationAccepted accepted(String type) => FrameOperationAccepted(
    operationId: 'j1',
    operationType: type,
    acceptedUtc: DateTime.utc(2026, 8, 1),
    idempotencyKey: 'key',
  );

  @override
  Future<FrameOperationAccepted> rebuildPreview(
    String frameId,
    FramePreviewOptions options,
  ) async => accepted('frames.rebuild-preview');

  @override
  Future<FrameOperationAccepted> reanalyze(
    String frameId, {
    double? starSensitivity,
    int? starNoiseReduction,
  }) async => accepted('frames.reanalyze');

  @override
  Future<FrameJobStatus> jobStatus(String jobId) async {
    final index = jobStatusCalls++;
    if (jobStates.isEmpty) {
      return FrameJobStatus(
        jobId: jobId,
        jobType: 'frames.reanalyze:f1',
        state: 'running',
        done: 0,
        total: 1,
        startedUtc: DateTime.utc(2026, 8, 1),
        finishedUtc: null,
        errorMessage: null,
      );
    }
    return jobStates[index.clamp(0, jobStates.length - 1)];
  }

  @override
  Future<void> cancelJob(String jobId) async {
    cancelledJob = true;
    if (failCancel) throw StateError('cancel request failed');
  }

  @override
  Future<void> bulkRate(List<String> frameIds, int value) async {
    if (failRating) throw StateError('rating failed');
    rating = value;
  }

  @override
  Future<void> bulkTag(
    List<String> frameIds, {
    List<String> addTags = const [],
    List<String> removeTags = const [],
  }) async {
    tags.removeWhere(removeTags.contains);
    for (final tag in addTags) {
      if (!tags.contains(tag)) tags.add(tag);
    }
  }

  @override
  Future<FrameOperationAccepted> bulkQuarantine(
    List<String> frameIds, {
    required bool quarantined,
    String? reason,
  }) async {
    quarantinedUtc = quarantined ? DateTime.utc(2026, 8, 1, 1) : null;
    quarantineReason = quarantined ? reason : null;
    return accepted('frames.bulk-quarantine');
  }

  @override
  Future<CursorPage<LibrarySession>> listSessions({
    int limit = 200,
    String? cursor,
  }) async => const CursorPage(items: [], nextCursor: null, hasMore: false);

  @override
  Future<List<LibraryFrameItem>> sessionFrames(
    String sessionId, {
    int limit = 200,
  }) async => const [];

  @override
  String thumbnailUrl(String frameId) => 'http://test/$frameId';

  @override
  Future<LibraryFrameDetail> frameDetail(String frameId) async =>
      const LibraryFrameDetail(
        id: 'f1',
        gain: 100,
        offset: 10,
        temperatureC: -10,
        focuserPosition: 5000,
        width: 4,
        height: 4,
        tags: ['keeper'],
      );

  @override
  Future<String> downloadFrameTo(
    String frameId,
    String savePath, {
    CancelToken? cancelToken,
  }) async => 'frame.fits';

  @override
  Future<void> bulkDelete(
    List<String> frameIds, {
    bool deleteFromDisk = false,
  }) async {}

  @override
  Future<void> bulkMove(List<String> frameIds, String targetSessionId) async {}

  @override
  Future<(List<int>, String, int)> exportFrames(List<String> frameIds) async =>
      (const [1], 'frames.tar', frameIds.length);

  @override
  Future<String> resumeTarget(String sessionId) async => 'sequence';

  @override
  void close() {}
}

FrameJobStatus _job(String state) => FrameJobStatus(
  jobId: 'j1',
  jobType: 'frames.reanalyze:f1',
  state: state,
  done: state == 'complete' ? 1 : 0,
  total: 1,
  startedUtc: DateTime.utc(2026, 8, 1),
  finishedUtc: const {'complete', 'failed', 'cancelled'}.contains(state)
      ? DateTime.utc(2026, 8, 1, 0, 0, 1)
      : null,
  errorMessage: state == 'failed' ? 'analysis failed safely' : null,
);

(ProviderContainer, ProviderSubscription<AsyncValue<FrameViewerState>>) _host(
  _FrameFake fake, {
  Stream<WsEvent>? events,
  FrameJobPollingPolicy polling = const FrameJobPollingPolicy(
    interval: Duration(milliseconds: 1),
    maxPolls: 4,
  ),
}) {
  final container = ProviderContainer(
    overrides: [
      libraryApiProvider.overrideWithValue(fake),
      wsEventsProvider.overrideWith(
        (ref) => events ?? const Stream<WsEvent>.empty(),
      ),
      frameJobPollingPolicyProvider.overrideWithValue(polling),
    ],
  );
  final subscription = container.listen(frameViewerProvider('f1'), (_, _) {});
  return (container, subscription);
}

void main() {
  test('lifecycle fold rejects other frames and stale event sequences', () {
    const initial = FrameLifecycleProgress(lastEventSequence: 5);
    final other = foldFrameLifecycle(
      initial,
      WsEvent(
        type: FrameWsEvents.previewStarted,
        ts: DateTime.utc(2026),
        seq: 6,
        payload: const {'frame_id': 'other'},
      ),
      'f1',
    );
    final stale = foldFrameLifecycle(
      initial,
      WsEvent(
        type: FrameWsEvents.previewStarted,
        ts: DateTime.utc(2026),
        seq: 5,
        payload: const {'frame_id': 'f1'},
      ),
      'f1',
    );
    expect(other, isNull);
    expect(stale, isNull);
  });

  test('lifecycle fold tracks progress and safe failure details', () {
    const initial = FrameLifecycleProgress();
    final progress = foldFrameLifecycle(
      initial,
      WsEvent(
        type: FrameWsEvents.persistProgress,
        ts: DateTime.utc(2026),
        seq: 1,
        payload: const {
          'frame_id': 'f1',
          'state': 'persisting',
          'progress': 0.85,
        },
      ),
      'f1',
    )!;
    final failed = foldFrameLifecycle(
      progress,
      WsEvent(
        type: FrameWsEvents.failed,
        ts: DateTime.utc(2026),
        seq: 2,
        payload: const {
          'frame_id': 'f1',
          'stage': 'preview',
          'code': 'source_unavailable',
          'message': 'Source image is unavailable.',
        },
      ),
      'f1',
    )!;
    expect(progress.storageState, 'persisting');
    expect(progress.storageProgress, 0.85);
    expect(failed.previewState, 'failed');
    expect(failed.failureCode, 'source_unavailable');
  });

  test(
    'provider loads metadata and preview as separate durable state',
    () async {
      final fake = _FrameFake();
      final (container, subscription) = _host(fake);
      addTearDown(subscription.close);
      addTearDown(container.dispose);

      final value = await container.read(frameViewerProvider('f1').future);

      expect(value.metadata?.frame.targetName, 'M42');
      expect(value.preview?.bytes, [1]);
      expect(value.lifecycle.storageState, 'complete');
      expect(fake.metadataCalls, 1);
      expect(fake.previewCalls, 1);
    },
  );

  test(
    'last-issued preview wins and superseded request is cancelled',
    () async {
      final fake = _FrameFake();
      final (container, subscription) = _host(fake);
      addTearDown(subscription.close);
      addTearDown(container.dispose);
      await container.read(frameViewerProvider('f1').future);
      final controller = container.read(frameViewerProvider('f1').notifier);
      fake.heldPreviews[FrameStretch.asinh] = Completer<FramePreviewImage>();
      fake.heldPreviews[FrameStretch.log] = Completer<FramePreviewImage>();

      final first = controller.setOptions(
        const FramePreviewOptions(stretch: FrameStretch.asinh),
      );
      await Future<void>.delayed(Duration.zero);
      final second = controller.setOptions(
        const FramePreviewOptions(stretch: FrameStretch.log),
      );
      fake.heldPreviews[FrameStretch.log]!.complete(
        fake.preview(const FramePreviewOptions(stretch: FrameStretch.log)),
      );
      await Future.wait([first, second]);
      fake.heldPreviews[FrameStretch.asinh]!.complete(
        fake.preview(const FramePreviewOptions(stretch: FrameStretch.asinh)),
      );
      await Future<void>.delayed(Duration.zero);

      final value = container.read(frameViewerProvider('f1')).value!;
      expect(value.options.stretch, FrameStretch.log);
      expect(value.preview?.bytes, [FrameStretch.log.index + 1]);
    },
  );

  test(
    'failed preview preserves last good pixels and restores controls',
    () async {
      final fake = _FrameFake();
      final (container, subscription) = _host(fake);
      addTearDown(subscription.close);
      addTearDown(container.dispose);
      final initial = await container.read(frameViewerProvider('f1').future);
      final controller = container.read(frameViewerProvider('f1').notifier);
      final failure = Completer<FramePreviewImage>();
      fake.heldPreviews[FrameStretch.asinh] = failure;

      final request = controller.setOptions(
        const FramePreviewOptions(stretch: FrameStretch.asinh),
      );
      failure.completeError(StateError('fixture failed'));
      await request;

      final value = container.read(frameViewerProvider('f1')).value!;
      expect(value.preview?.bytes, initial.preview?.bytes);
      expect(value.options.stretch, FrameStretch.autoStf);
      expect(value.previewError, isNotNull);
    },
  );

  test(
    'debounced controls supersede an in-flight preview immediately',
    () async {
      final fake = _FrameFake();
      final (container, subscription) = _host(fake);
      addTearDown(subscription.close);
      addTearDown(container.dispose);
      await container.read(frameViewerProvider('f1').future);
      final controller = container.read(frameViewerProvider('f1').notifier);
      fake.heldPreviews[FrameStretch.asinh] = Completer<FramePreviewImage>();

      final slow = controller.setOptions(
        const FramePreviewOptions(stretch: FrameStretch.asinh),
      );
      await Future<void>.delayed(Duration.zero);
      await controller.setOptions(
        const FramePreviewOptions(stretch: FrameStretch.log),
        debounce: true,
      );
      await slow;
      await Future<void>.delayed(const Duration(milliseconds: 250));

      final value = container.read(frameViewerProvider('f1')).value!;
      expect(value.options.stretch, FrameStretch.log);
      expect(value.preview?.bytes, [FrameStretch.log.index + 1]);
    },
  );

  test(
    'terminal frame event refreshes metadata; stale event cannot regress',
    () async {
      final fake = _FrameFake();
      final events = StreamController<WsEvent>.broadcast();
      final (container, subscription) = _host(fake, events: events.stream);
      addTearDown(subscription.close);
      addTearDown(container.dispose);
      addTearDown(events.close);
      await container.read(frameViewerProvider('f1').future);

      events.add(
        WsEvent(
          type: FrameWsEvents.previewReady,
          ts: DateTime.utc(2026),
          seq: 10,
          payload: const {'frame_id': 'f1', 'state': 'ready'},
        ),
      );
      await Future<void>.delayed(const Duration(milliseconds: 150));
      expect(fake.metadataCalls, 2);
      expect(
        container.read(frameViewerProvider('f1')).value!.lifecycle.previewState,
        'ready',
      );

      events.add(
        WsEvent(
          type: FrameWsEvents.previewStarted,
          ts: DateTime.utc(2026),
          seq: 9,
          payload: const {'frame_id': 'f1', 'state': 'rendering'},
        ),
      );
      await Future<void>.delayed(Duration.zero);
      expect(
        container.read(frameViewerProvider('f1')).value!.lifecycle.previewState,
        'ready',
      );
    },
  );

  test(
    'reanalysis uses bounded job fallback and refreshes terminal state',
    () async {
      final fake = _FrameFake()
        ..jobStates.addAll([_job('running'), _job('complete')]);
      final (container, subscription) = _host(fake);
      addTearDown(subscription.close);
      addTearDown(container.dispose);
      await container.read(frameViewerProvider('f1').future);

      await container.read(frameViewerProvider('f1').notifier).reanalyze();
      await Future<void>.delayed(const Duration(milliseconds: 20));

      final value = container.read(frameViewerProvider('f1')).value!;
      expect(value.operation?.state, 'complete');
      expect(value.operationKind, FrameOperationKind.reanalyze);
      expect(fake.jobStatusCalls, 2);
      expect(fake.metadataCalls, greaterThan(1));
    },
  );

  test(
    'job monitor stops at bound, reports unknown, and remains cancellable',
    () async {
      final fake = _FrameFake();
      final (container, subscription) = _host(
        fake,
        polling: const FrameJobPollingPolicy(
          interval: Duration(milliseconds: 1),
          maxPolls: 2,
        ),
      );
      addTearDown(subscription.close);
      addTearDown(container.dispose);
      await container.read(frameViewerProvider('f1').future);
      final controller = container.read(frameViewerProvider('f1').notifier);

      await controller.reanalyze();
      await Future<void>.delayed(const Duration(milliseconds: 20));
      expect(fake.jobStatusCalls, 2);
      expect(
        container.read(frameViewerProvider('f1')).value!.operationTimedOut,
        isTrue,
      );

      fake.jobStates.add(_job('cancelled'));
      await controller.cancelOperation();
      expect(fake.cancelledJob, isTrue);
      expect(
        container.read(frameViewerProvider('f1')).value!.operation?.state,
        'cancelled',
      );
    },
  );

  test(
    'cancel race reports daemon completion instead of false cancellation',
    () async {
      final fake = _FrameFake();
      final (container, subscription) = _host(
        fake,
        polling: const FrameJobPollingPolicy(
          interval: Duration(milliseconds: 1),
          maxPolls: 1,
        ),
      );
      addTearDown(subscription.close);
      addTearDown(container.dispose);
      await container.read(frameViewerProvider('f1').future);
      final controller = container.read(frameViewerProvider('f1').notifier);

      await controller.reanalyze();
      await Future<void>.delayed(const Duration(milliseconds: 5));
      fake.jobStates.add(_job('complete'));
      await controller.cancelOperation();

      expect(fake.cancelledJob, isTrue);
      expect(
        container.read(frameViewerProvider('f1')).value!.operation?.state,
        'complete',
      );
    },
  );

  test('failed cancel request resumes authoritative job monitoring', () async {
    final fake = _FrameFake();
    final (container, subscription) = _host(
      fake,
      polling: const FrameJobPollingPolicy(
        interval: Duration(milliseconds: 1),
        maxPolls: 1,
      ),
    );
    addTearDown(subscription.close);
    addTearDown(container.dispose);
    await container.read(frameViewerProvider('f1').future);
    final controller = container.read(frameViewerProvider('f1').notifier);

    await controller.reanalyze();
    await Future<void>.delayed(const Duration(milliseconds: 5));
    fake
      ..failCancel = true
      ..jobStates.add(_job('complete'));
    await controller.cancelOperation();
    await Future<void>.delayed(const Duration(milliseconds: 5));

    final value = container.read(frameViewerProvider('f1')).value!;
    expect(fake.cancelledJob, isTrue);
    expect(value.operation?.state, 'complete');
    expect(value.actionError, isNull);
  });

  test('failed optimistic rating restores server state', () async {
    final fake = _FrameFake()..failRating = true;
    final (container, subscription) = _host(fake);
    addTearDown(subscription.close);
    addTearDown(container.dispose);
    await container.read(frameViewerProvider('f1').future);

    await container.read(frameViewerProvider('f1').notifier).setRating(5);

    final value = container.read(frameViewerProvider('f1')).value!;
    expect(value.metadata?.frame.rating, 2);
    expect(value.actionError, isNotNull);
  });

  test('quarantine round-trip is reversible and source-preserving', () async {
    final fake = _FrameFake();
    final (container, subscription) = _host(fake);
    addTearDown(subscription.close);
    addTearDown(container.dispose);
    await container.read(frameViewerProvider('f1').future);
    final controller = container.read(frameViewerProvider('f1').notifier);

    await controller.setQuarantined(true, reason: 'cloud');
    expect(
      container
          .read(frameViewerProvider('f1'))
          .value!
          .metadata
          ?.frame
          .quarantinedUtc,
      isNotNull,
    );
    expect(
      container.read(frameViewerProvider('f1')).value!.metadata?.sourceExists,
      isTrue,
    );

    await controller.setQuarantined(false);
    expect(
      container
          .read(frameViewerProvider('f1'))
          .value!
          .metadata
          ?.frame
          .quarantinedUtc,
      isNull,
    );
  });

  test('ProblemDetails detail is preferred; paths are not dumped', () {
    final error = DioException.badResponse(
      statusCode: 409,
      requestOptions: RequestOptions(path: '/secret/path'),
      response: Response(
        requestOptions: RequestOptions(path: '/secret/path'),
        statusCode: 409,
        data: {'title': 'Conflict', 'detail': 'Another operation is running.'},
      ),
    );
    expect(frameFailureMessage(error), 'Another operation is running.');
    expect(
      frameFailureMessage(StateError('/private/source/file.fits')),
      isNot(contains('/private/source')),
    );
  });
}
