import 'package:dio/dio.dart';

import '../models/cursor_page.dart';
import '../models/server.dart';

/// How loud a notification is. Ordered least → most urgent so the UI can sort
/// and compare with `index`.
enum NotificationSeverity { info, warning, error, critical }

/// What part of the night a notification came from (§46.3 wire tokens).
enum NotificationCategory { equipment, sequence, storage, software, safety, alarm }

T _parseEnum<T extends Enum>(List<T> values, Object? wire, T fallback) {
  final name = wire is String ? wire.toLowerCase() : '';
  for (final v in values) {
    if (v.name == name) return v;
  }
  return fallback;
}

/// One entry in the inbox.
class AraNotification {
  const AraNotification({
    required this.id,
    required this.postedUtc,
    required this.severity,
    required this.category,
    required this.title,
    required this.message,
    required this.read,
    required this.dismissed,
  });

  final String id;
  final DateTime postedUtc;
  final NotificationSeverity severity;
  final NotificationCategory category;
  final String title;
  final String message;
  final bool read;
  final bool dismissed;

  AraNotification copyWith({bool? read, bool? dismissed}) => AraNotification(
        id: id,
        postedUtc: postedUtc,
        severity: severity,
        category: category,
        title: title,
        message: message,
        read: read ?? this.read,
        dismissed: dismissed ?? this.dismissed,
      );

  factory AraNotification.fromJson(Map<String, dynamic> json) =>
      AraNotification(
        id: json['id'] as String? ?? '',
        postedUtc:
            DateTime.tryParse(json['posted_utc'] as String? ?? '')?.toLocal() ??
                DateTime.fromMillisecondsSinceEpoch(0),
        severity: _parseEnum(NotificationSeverity.values, json['severity'],
            NotificationSeverity.info),
        category: _parseEnum(NotificationCategory.values, json['category'],
            NotificationCategory.software),
        title: json['title'] as String? ?? '',
        message: json['message'] as String? ?? '',
        read: json['read'] == true,
        dismissed: json['dismissed'] == true,
      );
}

/// Client for `/api/v1/notifications` (§46). Interface first so widget tests
/// can fake it, matching [FaultsClient].
abstract interface class NotificationsClient {
  /// One newest-first page. [unreadOnly] asks the server to filter.
  Future<CursorPage<AraNotification>> list({
    int limit,
    String? cursor,
    bool? unreadOnly,
  });

  /// Mark one as read. Returns the updated entry, or null if it's gone (404).
  Future<AraNotification?> markRead(String id);

  /// Dismiss one — it leaves the inbox. Returns null if it's gone (404).
  Future<AraNotification?> dismiss(String id, {String? reason});

  void close();
}

class NotificationsApi implements NotificationsClient {
  NotificationsApi(AraServer server, {Dio? dio})
      : _dio = dio ??
            Dio(BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              sendTimeout: const Duration(seconds: 5),
              receiveTimeout: const Duration(seconds: 12),
            ));

  final Dio _dio;

  @override
  Future<CursorPage<AraNotification>> list({
    int limit = 50,
    String? cursor,
    bool? unreadOnly,
  }) async {
    final res = await _dio.get<dynamic>(
      '/api/v1/notifications',
      queryParameters: <String, dynamic>{
        'limit': limit,
        'cursor': ?cursor,
        'unreadOnly': ?unreadOnly,
      },
    );
    final data = res.data;
    if (data is! Map<String, dynamic> || data['items'] is! List) {
      throw FormatException(
          'notifications list returned an unexpected body (${data.runtimeType})');
    }
    final items = (data['items'] as List)
        .whereType<Map<String, dynamic>>()
        .map(AraNotification.fromJson)
        .where((n) => n.id.isNotEmpty)
        .toList(growable: false);
    final next = data['next_cursor'];
    return CursorPage(
      items: items,
      nextCursor: next is String && next.isNotEmpty ? next : null,
      hasMore: data['has_more'] == true,
    );
  }

  @override
  Future<AraNotification?> markRead(String id) =>
      _post('/api/v1/notifications/$id/mark-read');

  @override
  Future<AraNotification?> dismiss(String id, {String? reason}) => _post(
        '/api/v1/notifications/$id/dismiss',
        body: <String, dynamic>{'reason': ?reason},
      );

  Future<AraNotification?> _post(String path, {Map<String, dynamic>? body}) async {
    try {
      final res = await _dio.post<dynamic>(path, data: body ?? const {});
      final data = res.data;
      return data is Map<String, dynamic> ? AraNotification.fromJson(data) : null;
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) return null;
      rethrow;
    }
  }

  @override
  void close() => _dio.close();
}
