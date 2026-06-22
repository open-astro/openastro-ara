/// Format an instant as `YYYY-MM-DD HH:MM UTC`. Shared by the equipment panels —
/// astronomy sessions are conventionally logged in UTC, and an unmarked local time
/// misleads remote/observatory users.
String formatUtcMinute(DateTime dt) {
  final u = dt.toUtc();
  String two(int n) => n.toString().padLeft(2, '0');
  return '${u.year}-${two(u.month)}-${two(u.day)} '
      '${two(u.hour)}:${two(u.minute)} UTC';
}
