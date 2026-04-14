import 'dart:typed_data';
import 'package:flutter_test/flutter_test.dart';
import 'package:jarvis_mobile/features/secrets/domain/crypto_service.dart';

void main() {
  late CryptoService cryptoService;

  setUp(() {
    cryptoService = CryptoService();
  });

  group('CryptoService', () {
    test('PBKDF2 key derivation is deterministic', () {
      final pin = '1234';
      final salt = Uint8List.fromList([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);

      final key1 = cryptoService.deriveKey(pin, salt);
      final key2 = cryptoService.deriveKey(pin, salt);

      expect(key1, equals(key2));
      expect(key1.length, equals(32)); // 256-bit key
    });

    test('PBKDF2 keys differ with different salts or pins', () {
      final salt1 = Uint8List.fromList(List.generate(16, (i) => i));
      final salt2 = Uint8List.fromList(List.generate(16, (i) => 15 - i));
      
      final key1 = cryptoService.deriveKey('1234', salt1);
      final key2 = cryptoService.deriveKey('1234', salt2);
      final key3 = cryptoService.deriveKey('4321', salt1);

      expect(key1, isNot(equals(key2)));
      expect(key1, isNot(equals(key3)));
    });

    test('AES-256-GCM encrypt and decrypt roundtrip works correctly', () {
      final key = Uint8List.fromList(List.generate(32, (i) => i));
      final iv = Uint8List.fromList(List.generate(12, (i) => i));
      final plaintext = 'This is a secret message 🔐';

      final encrypted = cryptoService.encrypt(key, plaintext, iv);
      
      // Should not be the same as plaintext
      expect(encrypted, isNot(equals(Uint8List.fromList(plaintext.codeUnits))));

      final decrypted = cryptoService.decrypt(key, encrypted, iv);
      
      expect(decrypted, equals(plaintext));
    });

    test('Wrong PIN throws exactly MacMismatchException', () {
      final iv = Uint8List.fromList(List.generate(12, (i) => i));
      final salt = Uint8List.fromList(List.generate(16, (i) => i));
      final plaintext = 'Super secret';

      // Encrypt with correct PIN
      final correctKey = cryptoService.deriveKey('1234', salt);
      final encrypted = cryptoService.encrypt(correctKey, plaintext, iv);

      // Attempt to decrypt with wrong PIN
      final wrongKey = cryptoService.deriveKey('4321', salt);

      // Must throw MacMismatchException specifically
      expect(
        () => cryptoService.decrypt(wrongKey, encrypted, iv),
        throwsA(isA<MacMismatchException>()),
      );
      
      // Test explicit zeroing works and doesn't crash
      cryptoService.zeroKey(correctKey);
      expect(correctKey.every((byte) => byte == 0), isTrue);
    });
  });
}
