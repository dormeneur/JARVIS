import 'dart:convert';
import 'dart:typed_data';
import 'package:pointycastle/export.dart';

class MacMismatchException implements Exception {
  final String message;
  MacMismatchException(this.message);
  @override
  String toString() => 'MacMismatchException: $message';
}

class CryptoService {
  static const int _pbkdf2Iterations = 100000;

  /// Derives AES key using PBKDF2 with HMAC-SHA256
  Uint8List deriveKey(String pin, Uint8List salt) {
    final derivator = KeyDerivator('SHA-256/HMAC/PBKDF2')
      ..init(Pbkdf2Parameters(salt, _pbkdf2Iterations, 32));
    
    final pinBytes = Uint8List.fromList(utf8.encode(pin));
    final key = derivator.process(pinBytes);
    
    // Explicitly zero out the pinBytes array from memory
    for (var i = 0; i < pinBytes.length; i++) {
      pinBytes[i] = 0;
    }
    
    return key;
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
