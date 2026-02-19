import 'package:flutter_test/flutter_test.dart';
import 'package:jarvis_mobile/shared/utils/hash_utils.dart';
import 'package:jarvis_mobile/shared/utils/date_utils.dart';

void main() {
  group('SHA-256 hashing', () {
    test('sha256Hex produces correct prefix', () {
      final hash = sha256Hex([1, 2, 3]);
      expect(hash, startsWith('sha256:'));
    });

    test('sha256Hex is deterministic', () {
      final h1 = sha256Hex([10, 20, 30]);
      final h2 = sha256Hex([10, 20, 30]);
      expect(h1, equals(h2));
    });

    test('sha256String hashes UTF-8', () {
      final hash = sha256String('hello world');
      expect(hash, startsWith('sha256:'));
      expect(hash.length, greaterThan(10));
    });

    test('different inputs different hashes', () {
      expect(sha256String('a'), isNot(equals(sha256String('b'))));
    });
  });

  group('Date utils', () {
    test('toUtcIso8601 produces Z suffix', () {
      final dt = DateTime.utc(2026, 1, 15, 12, 0, 0);
      final result = toUtcIso8601(dt);
      expect(result, endsWith('Z'));
      expect(result, contains('2026'));
    });

    test('parseUtcIso8601 parses Z suffix', () {
      final dt = parseUtcIso8601('2026-01-15T12:00:00Z');
      expect(dt.year, 2026);
      expect(dt.month, 1);
      expect(dt.day, 15);
      expect(dt.isUtc, true);
    });

    test('nowUtcIso8601 returns Z suffix', () {
      final now = nowUtcIso8601();
      expect(now, endsWith('Z'));
    });

    test('formatFileSize handles various sizes', () {
      expect(formatFileSize(null), '');
      expect(formatFileSize(500), '500 B');
      expect(formatFileSize(2048), '2.0 KB');
      expect(formatFileSize(1048576), '1.0 MB');
    });
  });
}
