/// One page of a cursor-paged list endpoint plus the token for the next.
class CursorPage<T> {
  final List<T> items;
  final String? nextCursor;
  final bool hasMore;
  const CursorPage(
      {required this.items, required this.nextCursor, required this.hasMore});
}
