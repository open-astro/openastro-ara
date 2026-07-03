import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/library/live_library.dart';
import '../../state/library/live_library_state.dart';
import '../../theme/ara_colors.dart';

/// §40.5 frame viewer over the live wire model (12f.2): the capture-time
/// thumbnail (pinch-zoomable) plus the metadata the list endpoint carries.
/// Full-resolution stretched previews (§65 `POST /frames/{id}/preview`) and
/// rating/tag editing land in a later 12f slice.
class LiveFrameViewerScreen extends ConsumerWidget {
  final LibraryFrameItem frame;
  const LiveFrameViewerScreen({super.key, required this.frame});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final api = ref.watch(libraryApiProvider);
    final url = api?.thumbnailUrl(frame.id);
    final exposure = frame.exposureSeconds ==
            frame.exposureSeconds.roundToDouble()
        ? frame.exposureSeconds.toStringAsFixed(0)
        : frame.exposureSeconds.toString();
    final rows = <(String, String)>[
      ('Type', frame.frameType),
      ('Filter', frame.filterName ?? '—'),
      ('Exposure', '${exposure}s'),
      ('HFR', frame.hfr?.toStringAsFixed(2) ?? '—'),
      ('Stars', frame.starCount?.toString() ?? '—'),
      ('Rating', frame.rating > 0 ? '${frame.rating}/5' : '—'),
      ('Captured', frame.capturedUtc.toIso8601String()),
    ];

    return Scaffold(
      appBar: AppBar(
        title: Text('${frame.filterName ?? frame.frameType} · ${exposure}s',
            style: const TextStyle(fontSize: 14)),
      ),
      body: Column(
        children: [
          Expanded(
            child: url == null
                ? const Center(child: Icon(Icons.image_outlined, size: 64))
                : InteractiveViewer(
                    maxScale: 8,
                    child: Center(
                      child: Image.network(
                        url,
                        fit: BoxFit.contain,
                        errorBuilder: (_, _, _) => const Icon(
                            Icons.broken_image_outlined,
                            size: 64,
                            color: AraColors.textDisabled),
                      ),
                    ),
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
