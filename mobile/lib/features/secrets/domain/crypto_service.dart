import 'dart:convert';
import 'package:flutter/foundation.dart';
import 'package:pointycastle/export.dart';

class MacMismatchException implements Exception {
  final String message;
  MacMismatchException(this.message);
  @override
  String toString() => 'MacMismatchException: $message';
}

/// Top-level so [compute] can run it in a background isolate.
Uint8List _deriveKeyImpl(Map<String, Object> args) {
  final derivator = KeyDerivator('SHA-256/HMAC/PBKDF2')
    ..init(Pbkdf2Parameters(
      args['salt'] as Uint8List,
      CryptoService.pbkdf2Iterations,
      32,
    ));

  final pinBytes = Uint8List.fromList(utf8.encode(args['pin'] as String));
  final key = derivator.process(pinBytes);

  // Explicitly zero out the pinBytes array from memory
  for (var i = 0; i < pinBytes.length; i++) {
    pinBytes[i] = 0;
  }

  return key;
}

class CryptoService {
  static const int pbkdf2Iterations = 100000;

  /// Derives AES key using PBKDF2 with HMAC-SHA256.
  /// Synchronous — blocks the calling isolate for seconds. Only use in
  /// tests or already-background code; UI paths must use [deriveKeyAsync].
  Uint8List deriveKey(String pin, Uint8List salt) {
    return _deriveKeyImpl({'pin': pin, 'salt': salt});
  }

  /// Derives the AES key in a background isolate. 100k PBKDF2 iterations
  /// take seconds in pure Dart — running this on the main isolate freezes
  /// the UI (Choreographer "skipped frames").
  Future<Uint8List> deriveKeyAsync(String pin, Uint8List salt) {
    return compute(_deriveKeyImpl, {'pin': pin, 'salt': salt});
  }

  /// Explicitly zeroes out a byte array from memory
  void zeroKey(Uint8List key) {
    for (var i = 0; i < key.length; i++) {
      key[i] = 0;
    }
  }

  /// Encrypts plaintext producing an encrypted blob including the GCM MAC.
  Uint8List encrypt(Uint8List key, String plaintext, Uint8List iv) {
    final cipher = GCMBlockCipher(AESEngine())
      ..init(true, AEADParameters(KeyParameter(key), 128, iv, Uint8List(0)));
      
    final plaintextBytes = Uint8List.fromList(utf8.encode(plaintext));
    return cipher.process(plaintextBytes);
  }

  /// Decrypts an encrypted blob. Throws MacMismatchException if MAC validation fails.
  String decrypt(Uint8List key, Uint8List encryptedBlob, Uint8List iv) {
    try {
      final cipher = GCMBlockCipher(AESEngine())
        ..init(false, AEADParameters(KeyParameter(key), 128, iv, Uint8List(0)));
        
      final plaintextBytes = cipher.process(encryptedBlob);
      return utf8.decode(plaintextBytes);
    } catch (e) {
      if (e is InvalidCipherTextException || e.toString().contains('mac check in GCM failed')) {
        throw MacMismatchException('GCM Auth Tag mismatch - wrong PIN or corrupted data');
      }
      rethrow;
    }
  }
}
