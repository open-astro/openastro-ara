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
  bool _loading = false;
  String? _error;
  // Guards against a slow older fetch overwriting a newer palette choice.
  int _fetchGen = 0;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    final api = ref.read(libraryApiProvider);
    if (api == null) return;
    final gen = ++_fetchGen;
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final bytes =
          await api.fetchPreview(widget.frame.id, stretch: _stretch);
      if (!mounted || gen != _fetchGen) return;
      setState(() {
        _preview = Uint8List.fromList(bytes);
        _loading = false;
      });
    } on Exception catch (e) {
      if (!mounted || gen != _fetchGen) return;
      setState(() {
        _loading = false;
        _error = 'Preview unavailable: $e';
      });
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
    final rows = <(String, String)>[
      ('Type', f.frameType),
      ('Filter', f.filterName ?? '—'),
      ('Exposure', '${exposure}s'),
      ('HFR', f.hfr?.toStringAsFixed(2) ?? '—'),
      ('Stars', f.starCount?.toString() ?? '—'),
      ('Rating', f.rating > 0 ? '${f.rating}/5' : '—'),
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
                          ? Image.memory(_preview!, fit: BoxFit.contain, gaplessPlayback: true)
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
            child: Wrap(
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
          ),
        ],
      ),
    );
  }
}
