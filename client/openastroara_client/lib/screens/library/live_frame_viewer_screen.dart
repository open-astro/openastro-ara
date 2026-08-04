import 'dart:async';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/library/frame_viewer.dart';
import '../../models/library/live_library.dart';
import '../../state/library/frame_viewer_state.dart';
import '../../state/library/live_library_state.dart';
import '../../theme/ara_colors.dart';
import '../../util/stream_save_location.dart';

/// Full Rank 1 frame viewer. Pixels remain server-owned: every control selects
/// a non-destructive preview variant while metadata and long-operation state
/// flow through [frameViewerProvider].
class LiveFrameViewerScreen extends ConsumerStatefulWidget {
  final LibraryFrameItem frame;

  /// Test seam for the native directory picker.
  final Future<String?> Function(String dialogTitle, String suggestedName)?
  savePathPicker;

  const LiveFrameViewerScreen({
    super.key,
    required this.frame,
    this.savePathPicker,
  });

  @override
  ConsumerState<LiveFrameViewerScreen> createState() =>
      _LiveFrameViewerScreenState();
}

class _LiveFrameViewerScreenState extends ConsumerState<LiveFrameViewerScreen> {
  final _scaffoldKey = GlobalKey<ScaffoldState>();
  CancelToken? _downloadToken;
  bool _downloadBusy = false;

  @override
  void dispose() {
    _downloadToken?.cancel('frame viewer disposed');
    super.dispose();
  }

  Future<void> _download() async {
    final api = ref.read(libraryApiProvider);
    final viewer = ref.read(frameViewerProvider(widget.frame.id)).value;
    if (api == null ||
        _downloadBusy ||
        viewer?.metadata?.sourceExists != true) {
      return;
    }
    final pick = widget.savePathPicker ?? pickStreamSavePath;
    final format =
        viewer?.metadata?.imageFormat ?? viewer?.metadata?.storage?.imageFormat;
    final safeId = widget.frame.id.replaceAll(RegExp(r'[^A-Za-z0-9._-]'), '_');
    final savePath = await pick(
      'Choose where to save the original frame',
      'openastroara-$safeId.${_sourceExtension(format)}',
    );
    if (!mounted || savePath == null || savePath.trim().isEmpty) return;
    final token = CancelToken();
    _downloadToken = token;
    setState(() => _downloadBusy = true);
    try {
      await api.downloadFrameTo(widget.frame.id, savePath, cancelToken: token);
      if (!mounted) return;
      final name = _pathBasename(savePath);
      ScaffoldMessenger.of(
        context,
      ).showSnackBar(SnackBar(content: Text('Saved $name')));
    } on DioException catch (error) {
      if (!mounted) return;
      final message = error.type == DioExceptionType.cancel
          ? 'Download cancelled.'
          : 'Download failed: ${frameFailureMessage(error)}';
      ScaffoldMessenger.of(
        context,
      ).showSnackBar(SnackBar(content: Text(message)));
    } on Object catch (error) {
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text('Download failed: ${frameFailureMessage(error)}'),
        ),
      );
    } finally {
      if (mounted) setState(() => _downloadBusy = false);
      _downloadToken = null;
    }
  }

  void _cancelDownload() => _downloadToken?.cancel('cancelled by operator');

  Future<void> _addTag() async {
    final tag = await showDialog<String>(
      context: context,
      builder: (_) => const _TextValueDialog(
        title: 'Add tag',
        action: 'Add',
        hint: 'e.g. good seeing',
        maxLength: 64,
      ),
    );
    if (!mounted || tag == null || tag.isEmpty) return;
    await ref
        .read(frameViewerProvider(widget.frame.id).notifier)
        .editTag(add: tag);
  }

  Future<void> _toggleQuarantine(bool currentlyQuarantined) async {
    if (currentlyQuarantined) {
      final restore = await showDialog<bool>(
        context: context,
        builder: (context) => AlertDialog(
          title: const Text('Restore frame?'),
          content: const Text(
            'The source file was never deleted. Restore this frame to normal library use?',
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.pop(context, false),
              child: const Text('Cancel'),
            ),
            FilledButton(
              onPressed: () => Navigator.pop(context, true),
              child: const Text('Restore'),
            ),
          ],
        ),
      );
      if (!mounted || restore != true) return;
      await ref
          .read(frameViewerProvider(widget.frame.id).notifier)
          .setQuarantined(false);
      return;
    }

    final reason = await showDialog<String>(
      context: context,
      builder: (_) => const _TextValueDialog(
        title: 'Quarantine frame',
        action: 'Quarantine',
        hint: 'Reason (cloud, trail, tracking, …)',
        initialValue: 'Marked from WILMA frame viewer',
        maxLength: 256,
        destructive: true,
      ),
    );
    if (!mounted || reason == null) return;
    await ref
        .read(frameViewerProvider(widget.frame.id).notifier)
        .setQuarantined(true, reason: reason.isEmpty ? null : reason);
  }

  @override
  Widget build(BuildContext context) {
    final viewer = ref.watch(frameViewerProvider(widget.frame.id));
    final api = ref.watch(libraryApiProvider);
    final thumbnailUrl = api?.thumbnailUrl(widget.frame.id);
    final value = viewer.value;
    final frame = value?.metadata?.frame;
    final title = frame == null
        ? '${widget.frame.filterName ?? widget.frame.frameType} · ${_exposure(widget.frame.exposureSeconds)}'
        : '${frame.targetName} · ${frame.filterName ?? frame.frameType} · ${_exposure(frame.exposureSeconds)}';

    return Scaffold(
      key: _scaffoldKey,
      appBar: AppBar(
        title: Text(title, style: const TextStyle(fontSize: 14)),
        actions: [
          IconButton(
            tooltip: 'Frame metadata',
            onPressed: value?.metadata == null
                ? null
                : () => _scaffoldKey.currentState?.openEndDrawer(),
            icon: const Icon(Icons.info_outline),
          ),
          if (_downloadBusy)
            IconButton(
              tooltip: 'Cancel download',
              onPressed: _cancelDownload,
              icon: const Icon(Icons.stop_circle_outlined),
            )
          else
            IconButton(
              tooltip: 'Download original frame',
              onPressed: value?.metadata?.sourceExists == true
                  ? _download
                  : null,
              icon: const Icon(Icons.download_outlined),
            ),
        ],
      ),
      endDrawer: value?.metadata == null
          ? null
          : Drawer(
              child: SafeArea(
                child: _MetadataPanel(
                  metadata: value!.metadata!,
                  applied: value.preview?.applied,
                ),
              ),
            ),
      body: viewer.when(
        loading: () => _InitialLoading(thumbnailUrl: thumbnailUrl),
        error: (error, _) => _FatalError(
          message: frameFailureMessage(error),
          onRetry: () => ref.invalidate(frameViewerProvider(widget.frame.id)),
        ),
        data: (state) => LayoutBuilder(
          builder: (context, constraints) {
            final wide = constraints.maxWidth >= 900;
            final image = _ImagePane(
              state: state,
              thumbnailUrl: thumbnailUrl,
              onRetry: () => ref
                  .read(frameViewerProvider(widget.frame.id).notifier)
                  .retryPreview(),
            );
            final inspector = _Inspector(
              frameId: widget.frame.id,
              state: state,
              downloadBusy: _downloadBusy,
              onDownload: _download,
              onCancelDownload: _cancelDownload,
              onAddTag: _addTag,
              onToggleQuarantine: () => _toggleQuarantine(
                state.metadata?.frame.quarantinedUtc != null,
              ),
            );
            if (wide) {
              return Row(
                children: [
                  Expanded(child: image),
                  const VerticalDivider(width: 1),
                  SizedBox(width: 390, child: inspector),
                ],
              );
            }
            final inspectorHeight = (constraints.maxHeight * 0.46).clamp(
              250.0,
              430.0,
            );
            return Column(
              children: [
                Expanded(child: image),
                const Divider(height: 1),
                SizedBox(height: inspectorHeight, child: inspector),
              ],
            );
          },
        ),
      ),
    );
  }
}

class _InitialLoading extends StatelessWidget {
  final String? thumbnailUrl;
  const _InitialLoading({required this.thumbnailUrl});

  @override
  Widget build(BuildContext context) => Stack(
    children: [
      Positioned.fill(child: _PreviewImage(thumbnailUrl: thumbnailUrl)),
      const Positioned(
        top: 16,
        right: 16,
        child: CircularProgressIndicator(strokeWidth: 2),
      ),
    ],
  );
}

class _FatalError extends StatelessWidget {
  final String message;
  final VoidCallback onRetry;
  const _FatalError({required this.message, required this.onRetry});

  @override
  Widget build(BuildContext context) => Center(
    child: Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        const Icon(Icons.broken_image_outlined, size: 64),
        const SizedBox(height: 12),
        Text(message, textAlign: TextAlign.center),
        const SizedBox(height: 8),
        OutlinedButton(onPressed: onRetry, child: const Text('Retry')),
      ],
    ),
  );
}

class _ImagePane extends StatelessWidget {
  final FrameViewerState state;
  final String? thumbnailUrl;
  final VoidCallback onRetry;

  const _ImagePane({
    required this.state,
    required this.thumbnailUrl,
    required this.onRetry,
  });

  @override
  Widget build(BuildContext context) {
    return Column(
      children: [
        _ProgressStrip(state: state),
        Expanded(
          child: Stack(
            children: [
              Positioned.fill(
                child: Semantics(
                  label:
                      'Astronomical frame preview. Pinch, scroll, or drag to inspect.',
                  image: true,
                  child: InteractiveViewer(
                    minScale: 0.5,
                    maxScale: 12,
                    boundaryMargin: const EdgeInsets.all(80),
                    child: Center(
                      child: _PreviewImage(
                        bytes: state.preview?.bytes,
                        thumbnailUrl: thumbnailUrl,
                      ),
                    ),
                  ),
                ),
              ),
              if (state.previewLoading)
                const Positioned(
                  top: 12,
                  right: 12,
                  child: DecoratedBox(
                    decoration: BoxDecoration(
                      color: AraColors.bgPanel,
                      shape: BoxShape.circle,
                    ),
                    child: Padding(
                      padding: EdgeInsets.all(8),
                      child: SizedBox(
                        width: 18,
                        height: 18,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      ),
                    ),
                  ),
                ),
              if (state.previewError != null)
                Positioned(
                  left: 12,
                  right: 12,
                  bottom: 12,
                  child: Material(
                    color: AraColors.bgPanel,
                    borderRadius: BorderRadius.circular(6),
                    child: Padding(
                      padding: const EdgeInsets.all(10),
                      child: Row(
                        children: [
                          const Icon(
                            Icons.warning_amber_rounded,
                            color: AraColors.accentBusy,
                            size: 18,
                          ),
                          const SizedBox(width: 8),
                          Expanded(child: Text(state.previewError!)),
                          TextButton(
                            onPressed: onRetry,
                            child: const Text('Retry'),
                          ),
                        ],
                      ),
                    ),
                  ),
                ),
            ],
          ),
        ),
      ],
    );
  }
}

class _PreviewImage extends StatelessWidget {
  final Uint8List? bytes;
  final String? thumbnailUrl;
  const _PreviewImage({this.bytes, this.thumbnailUrl});

  @override
  Widget build(BuildContext context) {
    final fallback = const Icon(
      Icons.image_outlined,
      size: 64,
      color: AraColors.textDisabled,
    );
    if (bytes != null) {
      return Image.memory(
        bytes!,
        fit: BoxFit.contain,
        gaplessPlayback: true,
        errorBuilder: (_, _, _) => fallback,
      );
    }
    if (thumbnailUrl != null) {
      return Image.network(
        thumbnailUrl!,
        fit: BoxFit.contain,
        errorBuilder: (_, _, _) => fallback,
      );
    }
    return fallback;
  }
}

class _ProgressStrip extends StatelessWidget {
  final FrameViewerState state;
  const _ProgressStrip({required this.state});

  @override
  Widget build(BuildContext context) {
    final lifecycle = state.lifecycle;
    final operation = state.operation;
    return Container(
      width: double.infinity,
      color: AraColors.bgPanel,
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Wrap(
            spacing: 8,
            runSpacing: 6,
            children: [
              _StageChip(
                label: 'Storage',
                state: lifecycle.storageState,
                progress: lifecycle.storageProgress,
              ),
              _StageChip(label: 'Analysis', state: lifecycle.analysisState),
              _StageChip(label: 'Preview', state: lifecycle.previewState),
              if (state.metadata?.frame.quarantinedUtc != null)
                const Chip(
                  avatar: Icon(Icons.inventory_2_outlined, size: 15),
                  label: Text('Quarantined'),
                  visualDensity: VisualDensity.compact,
                ),
            ],
          ),
          if (operation != null && !operation.isTerminal) ...[
            const SizedBox(height: 6),
            Row(
              children: [
                Expanded(
                  child: Text(
                    '${_operationLabel(state.operationKind)} · ${operation.state}',
                    style: Theme.of(context).textTheme.bodySmall,
                  ),
                ),
                Text(
                  '${operation.done}/${operation.total}',
                  style: Theme.of(context).textTheme.bodySmall,
                ),
              ],
            ),
            const SizedBox(height: 3),
            LinearProgressIndicator(value: operation.progress),
          ] else if (operation != null) ...[
            const SizedBox(height: 6),
            Text(
              '${_operationLabel(state.operationKind)} · ${operation.state}',
              style: Theme.of(context).textTheme.bodySmall,
            ),
          ],
        ],
      ),
    );
  }
}

class _StageChip extends StatelessWidget {
  final String label;
  final String state;
  final double? progress;
  const _StageChip({required this.label, required this.state, this.progress});

  @override
  Widget build(BuildContext context) {
    final normalized = state.toLowerCase();
    final failed =
        normalized == 'failed' ||
        normalized == 'missing' ||
        normalized == 'partial' ||
        normalized == 'interrupted';
    final busy =
        normalized == 'accepted' ||
        normalized == 'exposing' ||
        normalized == 'downloading' ||
        normalized == 'persisting' ||
        normalized == 'analyzing' ||
        normalized == 'rendering';
    final complete =
        normalized == 'complete' ||
        normalized == 'ready' ||
        normalized == 'skipped';
    return Chip(
      avatar: Icon(
        failed
            ? Icons.error_outline
            : busy
            ? Icons.sync
            : complete
            ? Icons.check_circle_outline
            : Icons.help_outline,
        size: 15,
        color: failed
            ? AraColors.accentBusy
            : busy
            ? AraColors.accentBusy
            : complete
            ? AraColors.accentConnected
            : AraColors.textDisabled,
      ),
      label: Text(
        progress == null || !busy
            ? '$label: $state'
            : '$label: $state ${(progress! * 100).round()}%',
      ),
      visualDensity: VisualDensity.compact,
    );
  }
}

class _Inspector extends ConsumerWidget {
  final String frameId;
  final FrameViewerState state;
  final bool downloadBusy;
  final VoidCallback onDownload;
  final VoidCallback onCancelDownload;
  final VoidCallback onAddTag;
  final VoidCallback onToggleQuarantine;

  const _Inspector({
    required this.frameId,
    required this.state,
    required this.downloadBusy,
    required this.onDownload,
    required this.onCancelDownload,
    required this.onAddTag,
    required this.onToggleQuarantine,
  });

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final controller = ref.read(frameViewerProvider(frameId).notifier);
    final options = state.options;
    final metadata = state.metadata;
    final frame = metadata?.frame;
    final operationRunning =
        state.operation != null && !state.operation!.isTerminal;
    final controlsBusy = state.mutationBusy;

    return SingleChildScrollView(
      padding: const EdgeInsets.all(12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          if (state.actionError != null) ...[
            _InlineError(message: state.actionError!),
            const SizedBox(height: 8),
          ],
          if (state.metadataError != null) ...[
            _InlineError(message: state.metadataError!),
            TextButton.icon(
              onPressed: controller.refreshMetadata,
              icon: const Icon(Icons.refresh, size: 16),
              label: const Text('Retry metadata'),
            ),
          ],
          Text('Display', style: Theme.of(context).textTheme.titleSmall),
          const SizedBox(height: 8),
          InputDecorator(
            key: const Key('frame-stretch-picker'),
            decoration: const InputDecoration(
              labelText: 'Stretch',
              isDense: true,
              border: OutlineInputBorder(),
            ),
            child: DropdownButtonHideUnderline(
              child: DropdownButton<FrameStretch>(
                value: options.stretch,
                isDense: true,
                isExpanded: true,
                items: [
                  for (final value in FrameStretch.values)
                    DropdownMenuItem(value: value, child: Text(value.label)),
                ],
                onChanged: (value) {
                  if (value != null) {
                    controller.setOptions(options.copyWith(stretch: value));
                  }
                },
              ),
            ),
          ),
          const SizedBox(height: 10),
          InputDecorator(
            key: const Key('frame-channel-picker'),
            decoration: const InputDecoration(
              labelText: 'Channel',
              isDense: true,
              border: OutlineInputBorder(),
            ),
            child: DropdownButtonHideUnderline(
              child: DropdownButton<FrameChannel>(
                value: options.channel,
                isDense: true,
                isExpanded: true,
                items: [
                  for (final value in FrameChannel.values)
                    DropdownMenuItem(
                      value: value,
                      enabled:
                          options.applyDebayer ||
                          value == FrameChannel.luminance,
                      child: Text(value.label),
                    ),
                ],
                onChanged: (value) {
                  if (value != null) {
                    controller.setOptions(options.copyWith(channel: value));
                  }
                },
              ),
            ),
          ),
          SwitchListTile.adaptive(
            contentPadding: EdgeInsets.zero,
            dense: true,
            title: const Text('Debayer OSC'),
            subtitle: const Text('Display only; raw CFA stays untouched.'),
            value: options.applyDebayer,
            onChanged: (value) => controller.setOptions(
              options.copyWith(
                applyDebayer: value,
                channel: value ? FrameChannel.rgb : FrameChannel.luminance,
              ),
            ),
          ),
          SwitchListTile.adaptive(
            contentPadding: EdgeInsets.zero,
            dense: true,
            title: const Text('Star annotation'),
            value: options.annotateStars,
            onChanged: (value) =>
                controller.setOptions(options.copyWith(annotateStars: value)),
          ),
          SwitchListTile.adaptive(
            contentPadding: EdgeInsets.zero,
            dense: true,
            title: const Text('Invert'),
            value: options.invert,
            onChanged: (value) =>
                controller.setOptions(options.copyWith(invert: value)),
          ),
          _LabeledSlider(
            key: const Key('frame-saturation-slider'),
            label: 'Saturation',
            value: options.saturation,
            min: 0,
            max: 2,
            onChanged: (value) => controller.setOptions(
              options.copyWith(saturation: value),
              debounce: true,
            ),
          ),
          if (options.stretch == FrameStretch.manual) ...[
            _LabeledSlider(
              key: const Key('frame-black-slider'),
              label: 'Black',
              value: options.blackPoint,
              onChanged: (value) => controller.setOptions(
                options.copyWith(
                  blackPoint: value.clamp(0, options.whitePoint - 0.001),
                ),
                debounce: true,
              ),
            ),
            _LabeledSlider(
              key: const Key('frame-midtone-slider'),
              label: 'Midtone',
              value: options.midtonePoint,
              onChanged: (value) => controller.setOptions(
                options.copyWith(midtonePoint: value),
                debounce: true,
              ),
            ),
            _LabeledSlider(
              key: const Key('frame-white-slider'),
              label: 'White',
              value: options.whitePoint,
              onChanged: (value) => controller.setOptions(
                options.copyWith(
                  whitePoint: value.clamp(options.blackPoint + 0.001, 1),
                ),
                debounce: true,
              ),
            ),
          ],
          if (options.stretch == FrameStretch.asinh)
            _LabeledSlider(
              key: const Key('frame-asinh-slider'),
              label: 'Asinh beta',
              value: options.asinhBeta,
              min: 0.1,
              max: 20,
              fractionDigits: 1,
              onChanged: (value) => controller.setOptions(
                options.copyWith(asinhBeta: value),
                debounce: true,
              ),
            ),
          if (options.stretch == FrameStretch.linear) ...[
            _LabeledSlider(
              label: 'Clip low',
              value: options.linearClipLow,
              onChanged: (value) => controller.setOptions(
                options.copyWith(
                  linearClipLow: value.clamp(0, options.linearClipHigh - 0.001),
                ),
                debounce: true,
              ),
            ),
            _LabeledSlider(
              label: 'Clip high',
              value: options.linearClipHigh,
              onChanged: (value) => controller.setOptions(
                options.copyWith(
                  linearClipHigh: value.clamp(options.linearClipLow + 0.001, 1),
                ),
                debounce: true,
              ),
            ),
          ],
          Row(
            children: [
              TextButton.icon(
                onPressed: controller.resetPreview,
                icon: const Icon(Icons.restart_alt, size: 16),
                label: const Text('Reset display'),
              ),
              if (state.metadataRefreshing)
                const Padding(
                  padding: EdgeInsets.only(left: 8),
                  child: SizedBox(
                    width: 14,
                    height: 14,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  ),
                ),
            ],
          ),
          if (state.preview?.applied case final applied?)
            _AppliedSummary(applied: applied),
          const Divider(height: 24),
          Text('Review', style: Theme.of(context).textTheme.titleSmall),
          const SizedBox(height: 6),
          if (frame != null) ...[
            Row(
              children: [
                for (var star = 1; star <= 5; star++)
                  IconButton(
                    tooltip: '$star stars',
                    visualDensity: VisualDensity.compact,
                    onPressed: controlsBusy
                        ? null
                        : () => controller.setRating(
                            star == frame.rating ? 0 : star,
                          ),
                    icon: Icon(
                      star <= frame.rating ? Icons.star : Icons.star_border,
                    ),
                    color: star <= frame.rating
                        ? AraColors.accentBusy
                        : AraColors.textSecondary,
                  ),
              ],
            ),
            Wrap(
              spacing: 6,
              runSpacing: 4,
              children: [
                for (final tag in frame.tags)
                  InputChip(
                    label: Text(tag),
                    visualDensity: VisualDensity.compact,
                    onDeleted: controlsBusy
                        ? null
                        : () => controller.editTag(remove: tag),
                  ),
                ActionChip(
                  avatar: const Icon(Icons.add, size: 14),
                  label: const Text('tag'),
                  visualDensity: VisualDensity.compact,
                  onPressed: controlsBusy ? null : onAddTag,
                ),
              ],
            ),
            const SizedBox(height: 8),
            Text(
              '${frame.width}×${frame.height} · ${frame.bitDepth}-bit · '
              'HFR ${frame.hfr?.toStringAsFixed(2) ?? '—'} · '
              '${frame.starCount?.toString() ?? '—'} stars',
              style: Theme.of(
                context,
              ).textTheme.bodySmall?.copyWith(color: AraColors.textSecondary),
            ),
          ],
          const Divider(height: 24),
          Text('Actions', style: Theme.of(context).textTheme.titleSmall),
          const SizedBox(height: 6),
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              OutlinedButton.icon(
                onPressed: downloadBusy
                    ? onCancelDownload
                    : metadata?.sourceExists == true
                    ? onDownload
                    : null,
                icon: Icon(
                  downloadBusy
                      ? Icons.stop_circle_outlined
                      : Icons.download_outlined,
                ),
                label: Text(
                  downloadBusy ? 'Cancel download' : 'Download original',
                ),
              ),
              OutlinedButton.icon(
                onPressed: controlsBusy || operationRunning
                    ? null
                    : controller.reanalyze,
                icon: const Icon(Icons.analytics_outlined),
                label: const Text('Reanalyze'),
              ),
              OutlinedButton.icon(
                onPressed: controlsBusy || operationRunning
                    ? null
                    : controller.rebuildPreview,
                icon: const Icon(Icons.refresh_outlined),
                label: const Text('Rebuild preview'),
              ),
              OutlinedButton.icon(
                style: frame?.quarantinedUtc == null
                    ? OutlinedButton.styleFrom(
                        foregroundColor: AraColors.accentBusy,
                      )
                    : null,
                onPressed: controlsBusy || operationRunning
                    ? null
                    : onToggleQuarantine,
                icon: Icon(
                  frame?.quarantinedUtc == null
                      ? Icons.inventory_2_outlined
                      : Icons.restore,
                ),
                label: Text(
                  frame?.quarantinedUtc == null
                      ? 'Quarantine'
                      : 'Restore frame',
                ),
              ),
            ],
          ),
          if (operationRunning) ...[
            const SizedBox(height: 8),
            TextButton.icon(
              onPressed: controlsBusy || state.operation?.state == 'cancelling'
                  ? null
                  : controller.cancelOperation,
              icon: const Icon(Icons.cancel_outlined),
              label: Text(
                state.operation?.state == 'cancelling'
                    ? 'Cancelling operation'
                    : 'Cancel operation',
              ),
            ),
          ],
          if (state.operationTimedOut) ...[
            const SizedBox(height: 8),
            TextButton.icon(
              onPressed: controller.retryOperationStatus,
              icon: const Icon(Icons.sync),
              label: const Text('Check operation status'),
            ),
          ],
        ],
      ),
    );
  }
}

class _AppliedSummary extends StatelessWidget {
  final FramePreviewApplied applied;
  const _AppliedSummary({required this.applied});

  @override
  Widget build(BuildContext context) {
    String number(double? value) => value?.toStringAsFixed(4) ?? '—';
    String percent(double? value) =>
        value == null ? '—' : '${(value * 100).toStringAsFixed(3)}%';
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.all(8),
      decoration: BoxDecoration(
        color: AraColors.bgPanelAlt,
        borderRadius: BorderRadius.circular(6),
      ),
      child: Text(
        'Applied ${applied.algorithm} · ${applied.width}×${applied.height}\n'
        'B ${number(applied.blackPoint)}  M ${number(applied.midtonePoint)}  '
        'W ${number(applied.whitePoint)}\n'
        'Clip ${percent(applied.linearClipLow)}–'
        '${percent(applied.linearClipHigh)} · '
        'Asinh β ${number(applied.asinhBeta)}\n'
        '${applied.debayerMode} · ${applied.channelMode} · '
        '${applied.inverted ? 'inverted' : 'normal'} · '
        'saturation ${applied.saturation.toStringAsFixed(2)}\n'
        '${applied.annotated ? '${applied.annotationCount} annotations' : 'no annotations'} · '
        '${applied.rejectedAnnotationCount} rejected · '
        'cache ${applied.cacheStatus}',
        style: Theme.of(
          context,
        ).textTheme.bodySmall?.copyWith(color: AraColors.textSecondary),
      ),
    );
  }
}

class _MetadataPanel extends StatelessWidget {
  final FrameMetadata metadata;
  final FramePreviewApplied? applied;
  const _MetadataPanel({required this.metadata, required this.applied});

  @override
  Widget build(BuildContext context) {
    final frame = metadata.frame;
    final rows = <(String, String)>[
      ('Frame ID', frame.id),
      ('Session ID', frame.sessionId),
      ('Target', frame.targetName),
      ('Type', frame.frameType),
      ('Filter', frame.filterName ?? '—'),
      ('Exposure', _exposure(frame.exposureSeconds)),
      ('Gain', frame.gain?.toString() ?? '—'),
      ('Offset', frame.offset?.toString() ?? '—'),
      (
        'Sensor',
        frame.temperatureC == null
            ? '—'
            : '${frame.temperatureC!.toStringAsFixed(1)}°C',
      ),
      (
        'Focus',
        frame.focuserPosition == null ? '—' : '${frame.focuserPosition} steps',
      ),
      ('Dimensions', '${frame.width}×${frame.height}'),
      ('Bit depth', '${frame.bitDepth}'),
      ('Source size', _humanBytes(frame.fileSizeBytes)),
      ('Captured UTC', frame.capturedUtc.toIso8601String()),
      ('HFR', frame.hfr?.toStringAsFixed(3) ?? '—'),
      ('Stars', frame.starCount?.toString() ?? '—'),
      ('Eccentricity', frame.eccentricity?.toStringAsFixed(3) ?? '—'),
      ('SNR estimate', frame.snrEstimate?.toStringAsFixed(2) ?? '—'),
      (
        'Guiding RMS',
        frame.guidingRmsArcsec == null
            ? '—'
            : '${frame.guidingRmsArcsec!.toStringAsFixed(2)}″',
      ),
      ('Source exists', metadata.sourceExists ? 'yes' : 'no'),
      ('Format', metadata.imageFormat ?? '—'),
      ('CFA pattern', metadata.cfaPattern ?? '—'),
      ('Storage', metadata.storage?.state ?? '—'),
      ('Analysis', metadata.analysisState ?? '—'),
      ('Analysis version', frame.analysisVersion ?? '—'),
      ('Preview', metadata.previewState ?? '—'),
      ('Preview version', metadata.previewVersion ?? '—'),
      ('Debayer', metadata.debayerMethod ?? applied?.debayerMode ?? '—'),
      ('Source SHA-256', metadata.sourceChecksumSha256 ?? '—'),
      if (frame.quarantinedUtc != null)
        ('Quarantined UTC', frame.quarantinedUtc!.toIso8601String()),
      if (frame.quarantineReason != null)
        ('Quarantine reason', frame.quarantineReason!),
      if (metadata.storage?.failureCode != null)
        (
          'Storage failure',
          '${metadata.storage!.failureCode}: ${metadata.storage!.failureMessage ?? ''}',
        ),
      if (metadata.analysisFailureCode != null)
        (
          'Analysis failure',
          '${metadata.analysisFailureCode}: ${metadata.analysisFailureMessage ?? ''}',
        ),
      if (metadata.previewFailureCode != null)
        (
          'Preview failure',
          '${metadata.previewFailureCode}: ${metadata.previewFailureMessage ?? ''}',
        ),
    ];
    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        Row(
          children: [
            Expanded(
              child: Text(
                'Frame metadata',
                style: Theme.of(context).textTheme.titleMedium,
              ),
            ),
            IconButton(
              tooltip: 'Close',
              onPressed: () => Navigator.maybePop(context),
              icon: const Icon(Icons.close),
            ),
          ],
        ),
        const Divider(),
        for (final (label, value) in rows)
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 5),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  label,
                  style: Theme.of(context).textTheme.labelSmall?.copyWith(
                    color: AraColors.textSecondary,
                  ),
                ),
                const SizedBox(height: 2),
                SelectableText(value),
              ],
            ),
          ),
      ],
    );
  }
}

class _InlineError extends StatelessWidget {
  final String message;
  const _InlineError({required this.message});

  @override
  Widget build(BuildContext context) => Container(
    width: double.infinity,
    padding: const EdgeInsets.all(8),
    decoration: BoxDecoration(
      color: AraColors.bgPanelAlt,
      border: Border.all(color: AraColors.accentBusy),
      borderRadius: BorderRadius.circular(6),
    ),
    child: Text(
      message,
      style: Theme.of(
        context,
      ).textTheme.bodySmall?.copyWith(color: AraColors.accentBusy),
    ),
  );
}

class _LabeledSlider extends StatelessWidget {
  final String label;
  final double value;
  final double min;
  final double max;
  final int fractionDigits;
  final ValueChanged<double> onChanged;

  const _LabeledSlider({
    super.key,
    required this.label,
    required this.value,
    this.min = 0,
    this.max = 1,
    this.fractionDigits = 3,
    required this.onChanged,
  });

  @override
  Widget build(BuildContext context) => Row(
    children: [
      SizedBox(
        width: 80,
        child: Text(label, style: Theme.of(context).textTheme.bodySmall),
      ),
      Expanded(
        child: Slider(
          value: value.clamp(min, max),
          min: min,
          max: max,
          onChanged: onChanged,
        ),
      ),
      SizedBox(
        width: 52,
        child: Text(
          value.toStringAsFixed(fractionDigits),
          textAlign: TextAlign.right,
          style: Theme.of(
            context,
          ).textTheme.bodySmall?.copyWith(color: AraColors.textSecondary),
        ),
      ),
    ],
  );
}

class _TextValueDialog extends StatefulWidget {
  final String title;
  final String action;
  final String hint;
  final String initialValue;
  final int maxLength;
  final bool destructive;

  const _TextValueDialog({
    required this.title,
    required this.action,
    required this.hint,
    this.initialValue = '',
    required this.maxLength,
    this.destructive = false,
  });

  @override
  State<_TextValueDialog> createState() => _TextValueDialogState();
}

class _TextValueDialogState extends State<_TextValueDialog> {
  late final TextEditingController _controller = TextEditingController(
    text: widget.initialValue,
  );

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => AlertDialog(
    title: Text(widget.title),
    content: TextField(
      controller: _controller,
      autofocus: true,
      maxLength: widget.maxLength,
      decoration: InputDecoration(hintText: widget.hint),
      onSubmitted: (value) => Navigator.pop(context, value.trim()),
    ),
    actions: [
      TextButton(
        onPressed: () => Navigator.pop(context),
        child: const Text('Cancel'),
      ),
      FilledButton(
        style: widget.destructive
            ? FilledButton.styleFrom(backgroundColor: AraColors.accentBusy)
            : null,
        onPressed: () => Navigator.pop(context, _controller.text.trim()),
        child: Text(widget.action),
      ),
    ],
  );
}

String _exposure(double seconds) {
  final value = seconds == seconds.roundToDouble()
      ? seconds.toStringAsFixed(0)
      : seconds.toStringAsFixed(3).replaceFirst(RegExp(r'0+$'), '');
  return '${value}s';
}

String _humanBytes(int bytes) {
  if (bytes < 1024) return '$bytes B';
  if (bytes < 1024 * 1024) return '${(bytes / 1024).toStringAsFixed(1)} KiB';
  if (bytes < 1024 * 1024 * 1024) {
    return '${(bytes / (1024 * 1024)).toStringAsFixed(1)} MiB';
  }
  return '${(bytes / (1024 * 1024 * 1024)).toStringAsFixed(2)} GiB';
}

String _sourceExtension(String? format) =>
    switch (format?.trim().toLowerCase()) {
      'xisf' => 'xisf',
      'cr2' ||
      'cr3' ||
      'nef' ||
      'arw' ||
      'dng' ||
      'raf' ||
      'orf' ||
      'rw2' => format!.trim().toLowerCase(),
      _ => 'fits',
    };

String _pathBasename(String path) {
  final name = path.split(RegExp(r'[/\\]')).last.trim();
  return name.isEmpty ? 'frame' : name;
}

String _operationLabel(FrameOperationKind? kind) => switch (kind) {
  FrameOperationKind.rebuildPreview => 'Preview rebuild',
  FrameOperationKind.reanalyze => 'Reanalysis',
  null => 'Frame operation',
};
