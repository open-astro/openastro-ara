import 'dart:typed_data';

import 'package:flutter/material.dart';

import '../../theme/ara_colors.dart';

/// A rendered focus frame (JPEG from the daemon, auto-stretched with star
/// rings). Fits the box; tap for a zoomable full-size view. [caption] sits in
/// the corner (e.g. "at 14817 · HFR 1.42"); [badge] is the LIVE / BEST tag.
class FocusFrameView extends StatelessWidget {
  final Uint8List? frame;
  final String? caption;
  final String? badge;
  final Color badgeColor;
  final String emptyText;
  final double height;

  const FocusFrameView({
    super.key,
    required this.frame,
    this.caption,
    this.badge,
    this.badgeColor = AraColors.accentBusy,
    this.emptyText = 'No frame yet.',
    this.height = 220,
  });

  @override
  Widget build(BuildContext context) {
    final jpeg = frame;
    return ClipRRect(
      borderRadius: BorderRadius.circular(6),
      child: Container(
        height: height,
        width: double.infinity,
        color: AraColors.bgPanelAlt,
        child: Stack(
          children: [
            Positioned.fill(
              child: jpeg == null
                  ? Center(
                      child: Text(
                        emptyText,
                        style: Theme.of(context)
                            .textTheme
                            .bodySmall
                            ?.copyWith(color: AraColors.textSecondary),
                        textAlign: TextAlign.center,
                      ),
                    )
                  : GestureDetector(
                      onTap: () => _openZoom(context, jpeg),
                      child: Image.memory(
                        jpeg,
                        gaplessPlayback: true,
                        fit: BoxFit.contain,
                        filterQuality: FilterQuality.medium,
                      ),
                    ),
            ),
            if (badge != null && jpeg != null)
              Positioned(
                top: 8,
                left: 8,
                child: Container(
                  padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
                  decoration: BoxDecoration(
                    color: badgeColor,
                    borderRadius: BorderRadius.circular(4),
                  ),
                  child: Text(
                    badge!,
                    style: const TextStyle(
                      color: Colors.black,
                      fontSize: 11,
                      fontWeight: FontWeight.bold,
                    ),
                  ),
                ),
              ),
            if (caption != null && jpeg != null)
              Positioned(
                bottom: 6,
                right: 8,
                child: Container(
                  padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                  decoration: BoxDecoration(
                    color: AraColors.bgPrimary.withValues(alpha: 0.7),
                    borderRadius: BorderRadius.circular(4),
                  ),
                  child: Text(
                    caption!,
                    style: Theme.of(context)
                        .textTheme
                        .labelSmall
                        ?.copyWith(color: AraColors.textPrimary),
                  ),
                ),
              ),
          ],
        ),
      ),
    );
  }

  void _openZoom(BuildContext context, Uint8List jpeg) {
    showDialog<void>(
      context: context,
      barrierColor: Colors.black87,
      builder: (ctx) => Dialog.fullscreen(
        backgroundColor: Colors.black,
        child: Stack(
          children: [
            Positioned.fill(
              child: InteractiveViewer(
                minScale: 0.5,
                maxScale: 8,
                child: Center(child: Image.memory(jpeg, gaplessPlayback: true)),
              ),
            ),
            Positioned(
              top: 12,
              right: 12,
              child: IconButton.filledTonal(
                tooltip: 'Close',
                onPressed: () => Navigator.of(ctx).pop(),
                icon: const Icon(Icons.close),
              ),
            ),
            if (caption != null)
              Positioned(
                bottom: 16,
                left: 16,
                child: Text(caption!,
                    style: const TextStyle(color: AraColors.textSecondary)),
              ),
          ],
        ),
      ),
    );
  }
}
