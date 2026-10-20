import 'dart:async';

import 'package:flutter/material.dart';

import '../../models/guider_twin.dart';
import '../../theme/ara_colors.dart';

/// What the user chose in [showDeleteProfileDialog].
class DeleteProfileChoice {
  /// Also delete the profile's OpenAstro Guider profile (calibration and
  /// dark library with it).
  final bool deleteGuiderProfile;
  const DeleteProfileChoice({required this.deleteGuiderProfile});
}

/// §63.4 — the one "Delete profile?" dialog both the launch screen and
/// Settings → Profiles use. It spells out the daemon's fallback for the
/// active / last profile, then — once [preview] answers — offers to delete
/// the profile's OpenAstro Guider profile too:
///
/// - on by default (no orphaned dark libraries), off when another Ara
///   profile shares the twin;
/// - hidden when the guider has no such profile;
/// - with a note when the delete will be queued (no guider connected, or the
///   guider is busy).
///
/// Returns null on Cancel.
Future<DeleteProfileChoice?> showDeleteProfileDialog(
  BuildContext context, {
  required String name,
  required bool isActive,
  required bool isLast,
  required Future<GuiderTwinPreview?> Function() preview,
}) {
  return showDialog<DeleteProfileChoice>(
    context: context,
    builder: (ctx) => DeleteProfileDialog(
      name: name,
      isActive: isActive,
      isLast: isLast,
      preview: preview,
    ),
  );
}

class DeleteProfileDialog extends StatefulWidget {
  const DeleteProfileDialog({
    super.key,
    required this.name,
    required this.isActive,
    required this.isLast,
    required this.preview,
  });

  final String name;
  final bool isActive;
  final bool isLast;
  final Future<GuiderTwinPreview?> Function() preview;

  @override
  State<DeleteProfileDialog> createState() => _DeleteProfileDialogState();
}

class _DeleteProfileDialogState extends State<DeleteProfileDialog> {
  GuiderTwinPreview? _twin;
  bool _loading = true;
  bool _deleteGuider = false;

  @override
  void initState() {
    super.initState();
    unawaited(_load());
  }

  Future<void> _load() async {
    GuiderTwinPreview? twin;
    try {
      twin = await widget.preview();
    } catch (_) {
      twin = null; // no answer → nothing offered about the guider
    }
    if (!mounted) return;
    setState(() {
      _twin = twin;
      _loading = false;
      // Default on: the old intent was never to leave an orphaned dark
      // library behind. Shared twins default off — the survivor needs it.
      _deleteGuider = twin != null && twin.exists != false && !twin.shared;
    });
  }

  /// The checkbox shows whenever the twin may exist (unknown counts).
  bool get _offerGuider => _twin != null && _twin!.exists != false;

  @override
  Widget build(BuildContext context) {
    final consequence = widget.isLast
        ? " This is the last profile — you'll be taken back to profile setup."
        : (widget.isActive
            ? ' The most recent remaining profile becomes active.'
            : '');
    final twin = _twin;
    return AlertDialog(
      title: const Text('Delete profile?'),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text('"${widget.name}" will be permanently removed. This can\'t be '
              'undone.$consequence'),
          if (_loading)
            const Padding(
              padding: EdgeInsets.only(top: 12),
              child: Text('Checking the guider…',
                  style: TextStyle(color: AraColors.textSecondary)),
            ),
          if (!_loading && twin != null && _offerGuider) ...[
            const SizedBox(height: 12),
            CheckboxListTile(
              key: const ValueKey('delete-guider-profile'),
              value: _deleteGuider,
              onChanged: (v) => setState(() => _deleteGuider = v ?? false),
              controlAffinity: ListTileControlAffinity.leading,
              contentPadding: EdgeInsets.zero,
              dense: true,
              title: Text(
                  'Also delete its OpenAstro Guider profile "${twin.displayName}"'),
              subtitle: Text(_guiderNote(twin),
                  style: const TextStyle(color: AraColors.textSecondary)),
            ),
          ],
        ],
      ),
      actions: [
        TextButton(
            onPressed: () => Navigator.of(context).pop(),
            child: const Text('Cancel')),
        FilledButton(
          style: FilledButton.styleFrom(backgroundColor: AraColors.accentError),
          // Delete waits for the preview: a tap before it lands would silently
          // keep the guider profile the box is about to offer (default on).
          onPressed: _loading
              ? null
              : () => Navigator.of(context).pop(DeleteProfileChoice(
                  deleteGuiderProfile: _offerGuider && _deleteGuider)),
          child: const Text('Delete'),
        ),
      ],
    );
  }

  static String _guiderNote(GuiderTwinPreview twin) {
    final lines = <String>[
      'Its guide calibration and dark library are deleted too.'
    ];
    if (twin.shared) {
      lines.add('Another Ara profile, "${twin.sharedWith ?? twin.displayName}", '
          'uses this guider profile.');
    }
    // The daemon's sentence wins when it has one ("No guider is connected.
    // It will be deleted the next time the guider connects.", "The guider is
    // guiding…", or the fault that kept it from answering); the next-connect
    // line is only the fallback for an unknown twin with no reason given.
    if (!twin.canDeleteNow && twin.reason != null && twin.reason!.isNotEmpty) {
      lines.add(twin.reason!);
    } else if (twin.exists == null) {
      lines.add('It will be deleted the next time the guider connects.');
    }
    return lines.join(' ');
  }
}

/// Show the outcome line for a delete (nothing when the guider was not
/// asked about); failures use the error colour.
void showGuiderTwinOutcome(
    ScaffoldMessengerState messenger, ProfileDeleteResult result) {
  final message = result.message;
  if (message == null) return;
  messenger.showSnackBar(SnackBar(
    content: Text(message),
    backgroundColor: result.isFailure ? AraColors.accentError : null,
  ));
}
