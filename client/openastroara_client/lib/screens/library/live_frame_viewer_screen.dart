import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/library/live_library.dart';
import '../../state/library/live_library_state.dart';
import '../../theme/ara_colors.dart';

/// §40.5 frame viewer over the live wire model. 12f.2 shipped the pinch-zoom
/// thumbnail + metadata; this slice adds the §65 stretched preview — a palette
/// picker fetches the full-resolution server-rendered JPEG (`POST
/// /frames/{id}/preview`), with the thumbnail as the instant first paint.
/// Manual stretch sliders (§65.9) and rating/tag editing are later slices.
class LiveFrameViewerScreen extends ConsumerStatefulWidget {
  final LibraryFrameItem frame;
  const LiveFrameViewerScreen({super.key, required this.frame});

  @override
  ConsumerState<LiveFrameViewerScreen> createState() =>
      _LiveFrameViewerScreenState();
}

/// Owns its TextEditingController so disposal happens with the dialog's own
/// State (disposing in the caller races the route's exit animation).
class _AddTagDialog extends StatefulWidget {
  const _AddTagDialog();

  @override
  State<_AddTagDialog> createState() => _AddTagDialogState();
}

class _AddTagDialogState extends State<_AddTagDialog> {
  final _controller = TextEditingController();

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: const Text('Add tag'),
      content: TextField(
        controller: _controller,
        autofocus: true,
        onSubmitted: (v) => Navigator.of(context).pop(v.trim()),
      ),
      actions: [
        TextButton(
            onPressed: () => Navigator.of(context).pop(),
            child: const Text('Cancel')),
        FilledButton(
            onPressed: () => Navigator.of(context).pop(_controller.text.trim()),
            child: const Text('Add')),
      ],
    );
  }
}

class _LiveFrameViewerScreenState extends ConsumerState<LiveFrameViewerScreen> {
  static const _palettes = [
    'auto_stf',
    'linear',
    'log',
    'asinh',
    'sqrt',
    'equalized',
  ];

  String _stretch = 'auto_stf';
  Uint8List? _preview;
  // The palette _preview was actually rendered with — on a failed re-fetch the
  // dropdown reverts to this so the picker never claims a render that didn't
  // happen (r1).
  String? _loadedStretch;
  bool _loading = false;
  String? _error;
  // Guards against a slow older fetch overwriting a newer palette choice.
  int _fetchGen = 0;
  // Local echo of the rating after an edit (the list item is immutable).
  late int _rating = widget.frame.rating;
  bool _ratingBusy = false;
  // Detail (tags + capture settings the list DTO lacks); null while loading.
  LibraryFrameDetail? _detail;
  bool _tagBusy = false;

  @override
  void initState() {
    super.initState();
    _load();
    _loadDetail();
  }

  Future<void> _loadDetail() async {
    final api = ref.read(libraryApiProvider);
    if (api == null) return;
    try {
      final detail = await api.frameDetail(widget.frame.id);
      if (!mounted) return;
      setState(() => _detail = detail);
    } on Exception {
      // Detail enriches the strip; the viewer stays functional without it.
    }
  }

  Future<void> _editTags({String? add, String? remove}) async {
    final api = ref.read(libraryApiProvider);
    final detail = _detail;
    if (api == null || detail == null) return;
    setState(() => _tagBusy = true);
    try {
      await api.bulkTag([widget.frame.id],
          addTags: [?add], removeTags: [?remove]);
      if (!mounted) return;
      final tags = [...detail.tags];
      if (remove != null) tags.remove(remove);
      if (add != null && !tags.contains(add)) tags.add(add);
      setState(() {
        _detail = LibraryFrameDetail(
          id: detail.id,
          gain: detail.gain,
          offset: detail.offset,
          temperatureC: detail.temperatureC,
          focuserPosition: detail.focuserPosition,
          width: detail.width,
          height: detail.height,
          tags: tags,
        );
        _tagBusy = false;
      });
    } on Exception catch (e) {
      if (!mounted) return;
      setState(() => _tagBusy = false);
      ScaffoldMessenger.of(context)
          .showSnackBar(SnackBar(content: Text('Tag update failed: $e')));
    }
  }

  Future<void> _promptAddTag() async {
    final tag = await showDialog<String>(
      context: context,
      builder: (_) => const _AddTagDialog(),
    );
    if (tag == null || tag.isEmpty || !mounted) return;
    await _editTags(add: tag);
  }

  Future<void> _load() async {
    final api = ref.read(libraryApiProvider);
    if (api == null) return;
    final gen = ++_fetchGen;
    setState(() {
      _loading = true;
      _error = null;
    });
    final requested = _stretch;
    try {
      final bytes =
          await api.fetchPreview(widget.frame.id, stretch: requested);
      if (!mounted || gen != _fetchGen) return;
      setState(() {
        // Dio's ResponseType.bytes already yields a Uint8List — avoid copying
        // a full-resolution image on every palette switch (r1).
        _preview = bytes is Uint8List ? bytes : Uint8List.fromList(bytes);
        _loadedStretch = requested;
        _loading = false;
      });
    } on Exception catch (e) {
      if (!mounted || gen != _fetchGen) return;
      setState(() {
        _loading = false;
        _error = 'Preview unavailable: $e';
        // Keep the last good render on screen, but snap the picker back to
        // the palette it was actually rendered with (r1: the dropdown must
        // never read as if the failed palette succeeded).
        if (_loadedStretch != null) {
          _stretch = _loadedStretch!;
        }
      });
    }
  }

  Future<void> _setRating(int rating) async {
    final api = ref.read(libraryApiProvider);
    if (api == null) return;
    final previous = _rating;
    setState(() {
      _rating = rating; // optimistic — reverted on failure
      _ratingBusy = true;
    });
    try {
      await api.bulkRate([widget.frame.id], rating);
      if (!mounted) return;
      setState(() => _ratingBusy = false);
      // The list item behind this viewer is stale now — refresh the strips.
      ref.invalidate(sessionFramesProvider);
    } on Exception catch (e) {
      if (!mounted) return;
      setState(() {
        _rating = previous;
        _ratingBusy = false;
      });
      ScaffoldMessenger.of(context)
          .showSnackBar(SnackBar(content: Text('Rating failed: $e')));
    }
  }

  @override
  Widget build(BuildContext context) {
    final api = ref.watch(libraryApiProvider);
    final thumbUrl = api?.thumbnailUrl(widget.frame.id);
    final f = widget.frame;
    final exposure = f.exposureSeconds == f.exposureSeconds.roundToDouble()
        ? f.exposureSeconds.toStringAsFixed(0)
        : f.exposureSeconds.toString();
    final d = _detail;
    final rows = <(String, String)>[
      ('Type', f.frameType),
      ('Filter', f.filterName ?? '—'),
      ('Exposure', '${exposure}s'),
      if (d != null) ...[
        ('Gain', d.gain?.toString() ?? '—'),
        ('Offset', d.offset?.toString() ?? '—'),
        // 0.0 may be the uncooled-camera sentinel (see LibraryFrameDetail) —
        // rendered as-is until the server-side nullable pass lands.
        ('Sensor', d.temperatureC != null ? '${d.temperatureC!.toStringAsFixed(1)}°C' : '—'),
        if (d.focuserPosition != null) ('Focus', '${d.focuserPosition} steps'),
        if (d.width > 0) ('Size', '${d.width}×${d.height}'),
      ],
      ('HFR', f.hfr?.toStringAsFixed(2) ?? '—'),
      ('Stars', f.starCount?.toString() ?? '—'),
      ('Captured', f.capturedUtc.toIso8601String()),
    ];

    return Scaffold(
      appBar: AppBar(
        title: Text('${f.filterName ?? f.frameType} · ${exposure}s',
            style: const TextStyle(fontSize: 14)),
        actions: [
          // §65 stretch picker — server-side render per palette.
          DropdownButton<String>(
            value: _stretch,
            underline: const SizedBox.shrink(),
            items: [
              for (final p in _palettes)
                DropdownMenuItem(value: p, child: Text(p)),
            ],
            onChanged: _loading
                ? null
                : (v) {
                    if (v == null || v == _stretch) return;
                    setState(() => _stretch = v);
                    _load();
                  },
          ),
          const SizedBox(width: 8),
        ],
      ),
      body: Column(
        children: [
          Expanded(
            child: Stack(
              children: [
                Positioned.fill(
                  child: InteractiveViewer(
                    maxScale: 8,
                    child: Center(
                      child: _preview != null
                          ? Image.memory(
                              _preview!,
                              fit: BoxFit.contain,
                              gaplessPlayback: true,
                              // Undecodable bytes (truncated response) degrade
                              // like the thumbnail path instead of a render error.
                              errorBuilder: (_, _, _) => const Icon(
                                  Icons.broken_image_outlined,
                                  size: 64,
                                  color: AraColors.textDisabled),
                            )
                          : thumbUrl != null
                              ? Image.network(
                                  thumbUrl,
                                  fit: BoxFit.contain,
                                  errorBuilder: (_, _, _) => const Icon(
                                      Icons.broken_image_outlined,
                                      size: 64,
                                      color: AraColors.textDisabled),
                                )
                              : const Icon(Icons.image_outlined, size: 64),
                    ),
                  ),
                ),
                if (_loading)
                  const Positioned(
                    top: 12,
                    right: 12,
                    child: SizedBox(
                        width: 18, height: 18, child: CircularProgressIndicator(strokeWidth: 2)),
                  ),
                if (_error != null)
                  Positioned(
                    left: 12,
                    bottom: 12,
                    child: Container(
                      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                      color: AraColors.bgPanel,
                      child: Text(_error!,
                          style: Theme.of(context)
                              .textTheme
                              .bodySmall
                              ?.copyWith(color: AraColors.accentBusy)),
                    ),
                  ),
              ],
            ),
          ),
          Container(
            width: double.infinity,
            color: AraColors.bgPanel,
            padding: const EdgeInsets.all(12),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                // §40.5 rating editor — reuses the §40.8 bulk endpoint with a
                // single id; tapping the current rating clears it.
                Row(children: [
                  for (var star = 1; star <= 5; star++)
                    InkWell(
                      onTap: _ratingBusy || api == null
                          ? null
                          : () => _setRating(star == _rating ? 0 : star),
                      child: Padding(
                        padding: const EdgeInsets.symmetric(horizontal: 2),
                        child: Icon(
                          star <= _rating ? Icons.star : Icons.star_border,
                          size: 18,
                          color: star <= _rating
                              ? AraColors.accentBusy
                              : AraColors.textSecondary,
                        ),
                      ),
                    ),
                  if (_ratingBusy)
                    const Padding(
                      padding: EdgeInsets.only(left: 8),
                      child: SizedBox(
                          width: 12, height: 12, child: CircularProgressIndicator(strokeWidth: 2)),
                    ),
                ]),
                const SizedBox(height: 8),
                // §40.5 tag editor — chips delete individual tags; the + chip
                // adds one. Same single-id reuse of the §40.8 bulk endpoint.
                if (d != null)
                  Padding(
                    padding: const EdgeInsets.only(bottom: 8),
                    child: Wrap(
                      spacing: 6,
                      runSpacing: 4,
                      children: [
                        for (final tag in d.tags)
                          InputChip(
                            label: Text(tag),
                            visualDensity: VisualDensity.compact,
                            onDeleted: _tagBusy
                                ? null
                                : () => _editTags(remove: tag),
                          ),
                        ActionChip(
                          avatar: const Icon(Icons.add, size: 14),
                          label: const Text('tag'),
                          visualDensity: VisualDensity.compact,
                          onPressed: _tagBusy ? null : _promptAddTag,
                        ),
                      ],
                    ),
                  ),
                Wrap(
                  spacing: 24,
                  runSpacing: 6,
                  children: [
                    for (final (label, value) in rows)
                      Text('$label: $value',
                          style: Theme.of(context)
                              .textTheme
                              .bodySmall
                              ?.copyWith(color: AraColors.textSecondary)),
                  ],
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}
