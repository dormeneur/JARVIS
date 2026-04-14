import 'package:flutter_test/flutter_test.dart';

// Logic extracted from ChatScreen._saveChatPair
String truncateTitle(String query) {
  return query.length > 60 ? '${query.substring(0, 57)}...' : query;
}

void main() {
  group('Session Title Truncation', () {
    test('Should not truncate titles shorter than 60 characters', () {
      const shortQuery = 'Hello Jarvis, how are you today?';
      expect(truncateTitle(shortQuery), equals(shortQuery));
      expect(truncateTitle(shortQuery).length, lessThanOrEqualTo(60));
    });

    test('Should truncate titles exactly 60 characters (no ellipsis needed)', () {
      final exactQuery = 'A' * 60;
      expect(truncateTitle(exactQuery), equals(exactQuery));
    });

    test('Should truncate and add ellipsis for titles longer than 60 characters', () {
      final longQuery = 'This is a very long query that definitely exceeds sixty characters to test the truncation logic correctly.';
      final result = truncateTitle(longQuery);
      
      expect(result.length, equals(60));
      expect(result.endsWith('...'), isTrue);
      expect(result, equals('${longQuery.substring(0, 57)}...'));
    });

    test('Should handle multi-line queries by simple truncation', () {
      const multiLineQuery = 'Line 1\nLine 2';
      expect(truncateTitle(multiLineQuery), equals(multiLineQuery));
    });
  });
}
