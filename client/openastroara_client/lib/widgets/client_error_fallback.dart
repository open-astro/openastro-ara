import 'package:flutter/widgets.dart';

/// What a failed build shows in a release build instead of Flutter's grey
/// box (#1111). Compact, not red, and — the point — built from nothing that
/// looks up an inherited widget: no `Theme.of`, no `MediaQuery.of`, no
/// `Directionality.of`. The widget that failed may sit above all of those,
/// and a fallback that itself throws turns one red screen into a cascade.
///
/// Debug builds keep Flutter's own [ErrorWidget], whose message text is
/// what a developer wants to read.
class ClientErrorFallback extends StatelessWidget {
  const ClientErrorFallback({super.key, this.message = defaultMessage});

  static const defaultMessage =
      'Something went wrong drawing this part of the screen. '
      'Support → Save client log has the details.';

  final String message;

  @override
  Widget build(BuildContext context) {
    return Directionality(
      textDirection: TextDirection.ltr,
      child: ColoredBox(
        color: const Color(0xFF1E1E22),
        child: Center(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Text(
              message,
              textAlign: TextAlign.center,
              style: const TextStyle(
                color: Color(0xFFB0B0B8),
                fontSize: 13,
                decoration: TextDecoration.none,
              ),
            ),
          ),
        ),
      ),
    );
  }
}
