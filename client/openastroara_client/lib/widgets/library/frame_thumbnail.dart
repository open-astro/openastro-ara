import 'package:flutter/material.dart';

import '../../theme/ara_colors.dart';

/// Per-frame thumbnail tile in the session grid per §40.4. Sized by its
/// parent (grid cell), Photos-style: the image fills a rounded tile, metadata
/// rides on a bottom scrim, and selection renders as the familiar blue
/// checkmark circle. Takes the plain wire fields (works for both live
/// `LibraryFrameItem`s and any legacy caller) and renders the capture-time
/// thumbnail via [imageUrl] when the server provides one, falling back to a
/// placeholder icon.
class FrameThumbnail extends StatefulWidget {
  final String filter;
  final double? hfr;
  final int rating;
  final String? imageUrl;
  final VoidCallback? onTap;
  final VoidCallback? onLongPress;
  final bool selected;
  final bool selectionMode;

  /// §44 tri-state backup badge: true = mirrored to the backup target,
  /// false = stream active but this frame not stored yet, null = no backup
  /// stream configured (no badge — don't imply frames are unprotected on
  /// rigs that never enabled the feature).
  final bool? synced;
  const FrameThumbnail({
    super.key,
    required this.filter,
    required this.hfr,
    required this.rating,
    this.imageUrl,
    this.onTap,
    this.onLongPress,
    this.selected = false,
    this.selectionMode = false,
    this.synced,
  });

  @override
  State<FrameThumbnail> createState() => _FrameThumbnailState();
}

class _FrameThumbnailState extends State<FrameThumbnail> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final selected = widget.selected;
    return Semantics(
      label: widget.hfr is double
          ? 'Frame ${widget.filter} HFR ${widget.hfr!.toStringAsFixed(2)}'
          : 'Frame ${widget.filter}',
      selected: selected,
      child: MouseRegion(
        onEnter: (_) => setState(() => _hovered = true),
        onExit: (_) => setState(() => _hovered = false),
        child: GestureDetector(
          onTap: widget.onTap,
          onLongPress: widget.onLongPress,
          onSecondaryTap: widget.onLongPress,
          child: AnimatedContainer(
            duration: const Duration(milliseconds: 120),
            // Selected tiles inset slightly inside a blue ring, like Photos.
            padding: EdgeInsets.all(selected ? 3 : 0),
            decoration: BoxDecoration(
              borderRadius: BorderRadius.circular(8),
              border: Border.all(
                color: selected ? AraColors.selectionBg : Colors.transparent,
                width: 2,
              ),
            ),
            child: ClipRRect(
              borderRadius: BorderRadius.circular(selected ? 4 : 6),
              child: Stack(
                fit: StackFit.expand,
                children: [
                  const ColoredBox(
                    color: AraColors.bgInput,
                    child: Icon(Icons.image_outlined,
                        color: AraColors.textDisabled, size: 28),
                  ),
                  if (widget.imageUrl != null)
                    Image.network(
                      widget.imageUrl!,
                      fit: BoxFit.cover,
                      // Fade in on arrival — thumbnails stream slowly off the
                      // rig, and popping in reads as glitchy.
                      frameBuilder: (context, child, frame, wasSync) =>
                          wasSync || frame != null
                              ? AnimatedOpacity(
                                  opacity: 1,
                                  duration: const Duration(milliseconds: 200),
                                  child: child)
                              : const SizedBox.shrink(),
                      // Keep the placeholder icon on 404/network failure —
                      // a thumbnail may not exist for recovered orphans.
                      errorBuilder: (_, _, _) => const SizedBox.shrink(),
                    ),
                  // Bottom scrim carrying the metadata, Photos-style.
                  Positioned(
                    left: 0,
                    right: 0,
                    bottom: 0,
                    child: Container(
                      padding: const EdgeInsets.fromLTRB(6, 10, 6, 4),
                      decoration: const BoxDecoration(
                        gradient: LinearGradient(
                          begin: Alignment.topCenter,
                          end: Alignment.bottomCenter,
                          colors: [Colors.transparent, Color(0xB3000000)],
                        ),
                      ),
                      child: Row(
                        children: [
                          Expanded(
                            child: Text(
                              widget.filter,
                              maxLines: 1,
                              overflow: TextOverflow.ellipsis,
                              style: Theme.of(context)
                                  .textTheme
                                  .labelSmall
                                  ?.copyWith(color: Colors.white),
                            ),
                          ),
                          // Defensive clamp — malformed payload could push
                          // rating outside 0..5 and overflow the tile.
                          if (widget.rating > 0)
                            for (var i = 0; i < widget.rating.clamp(0, 5); i++)
                              const Icon(Icons.star,
                                  size: 9, color: AraColors.accentBusy),
                        ],
                      ),
                    ),
                  ),
                  if (widget.synced != null)
                    Positioned(
                      right: 5,
                      top: 5,
                      child: Tooltip(
                        message: widget.synced!
                            ? 'Backed up to your desktop'
                            : 'Backup pending',
                        child: Icon(
                          widget.synced! ? Icons.cloud_done : Icons.cloud_queue,
                          size: 13,
                          color: widget.synced!
                              ? AraColors.accentConnected
                              : Colors.white70,
                        ),
                      ),
                    ),
                  // Selection circle: always visible in selection mode,
                  // hover-revealed otherwise as an affordance.
                  if (widget.selectionMode || _hovered)
                    Positioned(
                      left: 5,
                      bottom: widget.rating > 0 ? 22 : 5,
                      child: GestureDetector(
                        // Clicking the circle toggles selection even outside
                        // selection mode — the desktop affordance for
                        // entering it without a long-press.
                        onTap: widget.selectionMode
                            ? widget.onTap
                            : widget.onLongPress,
                        child: Container(
                          width: 20,
                          height: 20,
                          decoration: BoxDecoration(
                            color: selected
                                ? AraColors.selectionBg
                                : const Color(0x66000000),
                            shape: BoxShape.circle,
                            border: Border.all(color: Colors.white, width: 1.5),
                          ),
                          child: selected
                              ? const Icon(Icons.check,
                                  size: 14, color: Colors.white)
                              : null,
                        ),
                      ),
                    ),
                  if (_hovered && !selected)
                    const ColoredBox(color: Color(0x14FFFFFF)),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}
