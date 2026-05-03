import 'dart:async';
import 'dart:convert';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';
import 'package:jarvis_mobile/features/secrets/data/secrets_repository.dart';
import 'package:jarvis_mobile/features/secrets/domain/crypto_service.dart';
import 'package:jarvis_mobile/core/storage/app_database.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:uuid/uuid.dart';
import 'dart:math';

class SecretsState {
  final bool isUnlocked;
  final bool hasPin;
  final List<SecretEntry> secrets;
  final String? error;
  final bool isLoading;

  SecretsState({
    this.isUnlocked = false,
    this.hasPin = false,
    this.secrets = const [],
    this.error,
    this.isLoading = false,
  });

  SecretsState copyWith({
    bool? isUnlocked,
    bool? hasPin,
    List<SecretEntry>? secrets,
    String? error,
    bool? isLoading,
  }) {
    return SecretsState(
      isUnlocked: isUnlocked ?? this.isUnlocked,
      hasPin: hasPin ?? this.hasPin,
      secrets: secrets ?? this.secrets,
      error: error,
      isLoading: isLoading ?? this.isLoading,
    );
  }
}

final secretsProvider = StateNotifierProvider<SecretsNotifier, SecretsState>((ref) {
  final db = ref.watch(appDatabaseProvider);
  final crypto = CryptoService();
  final apiClient = ref.watch(apiClientProvider);
  final repo = SecretsRepository(db: db, cryptoService: crypto, apiClient: apiClient);
  return SecretsNotifier(repo, crypto);
});

class SecretsNotifier extends StateNotifier<SecretsState> {
  final SecretsRepository _repository;
  final CryptoService _cryptoService;
  
  Uint8List? _derivedKey;
  Timer? _lockTimer;
  Timer? _clipboardTimer;
  
  static const _idleTimeout = Duration(minutes: 5);
  static const _clipboardTimeout = Duration(seconds: 30);
  
  static const _saltKey = 'secrets_salt';
  static const _validatorKey = 'secrets_validator';
  static const _validatorIvKey = 'secrets_validator_iv';
  static const _validatorPlaintext = 'JARVIS_VERIFIED';

  SecretsNotifier(this._repository, this._cryptoService) : super(SecretsState()) {
    _init();
  }

  Future<void> _init() async {
    final prefs = await SharedPreferences.getInstance();
    final hasPin = prefs.containsKey(_saltKey);
    state = state.copyWith(hasPin: hasPin);
    if (hasPin) {
      await refreshSecrets();
    }
  }

  Future<void> refreshSecrets() async {
    final secrets = await _repository.getAllSecrets();
    state = state.copyWith(secrets: secrets);
  }

  Future<void> setupPin(String pin) async {
    state = state.copyWith(isLoading: true, error: null);
    try {
      final prefs = await SharedPreferences.getInstance();
      
      // Generate 16-byte salt
      final salt = Uint8List.fromList(List.generate(16, (_) => Random.secure().nextInt(256)));
      final iv = Uint8List.fromList(List.generate(12, (_) => Random.secure().nextInt(256)));
      
      final key = _cryptoService.deriveKey(pin, salt);
      final encryptedValidator = _cryptoService.encrypt(key, _validatorPlaintext, iv);
      
      await prefs.setString(_saltKey, base64Encode(salt));
      await prefs.setString(_validatorIvKey, base64Encode(iv));
      await prefs.setString(_validatorKey, base64Encode(encryptedValidator));
      
      _derivedKey = key;
      state = state.copyWith(isUnlocked: true, hasPin: true, isLoading: false);
      _startLockTimer();
    } catch (e) {
      state = state.copyWith(error: 'Failed to set up PIN: $e', isLoading: false);
    }
  }

  Future<void> unlock(String pin) async {
    state = state.copyWith(isLoading: true, error: null);
    try {
      final prefs = await SharedPreferences.getInstance();
      final saltBase64 = prefs.getString(_saltKey);
      final ivBase64 = prefs.getString(_validatorIvKey);
      final validatorBase64 = prefs.getString(_validatorKey);
      
      if (saltBase64 == null || ivBase64 == null || validatorBase64 == null) {
        state = state.copyWith(hasPin: false, isLoading: false);
        return;
      }
      
      final salt = base64Decode(saltBase64);
      final iv = base64Decode(ivBase64);
      final validator = base64Decode(validatorBase64);
      
      final key = _cryptoService.deriveKey(pin, Uint8List.fromList(salt));
      
      try {
        final decrypted = _cryptoService.decrypt(key, Uint8List.fromList(validator), Uint8List.fromList(iv));
        if (decrypted == _validatorPlaintext) {
          _derivedKey = key;
          state = state.copyWith(isUnlocked: true, isLoading: false);
          _startLockTimer();
          await refreshSecrets();
        } else {
          _cryptoService.zeroKey(key);
          state = state.copyWith(error: 'Invalid PIN', isLoading: false);
        }
      } on MacMismatchException {
        _cryptoService.zeroKey(key);
        state = state.copyWith(error: 'Invalid PIN', isLoading: false);
      } catch (e) {
        _cryptoService.zeroKey(key);
        state = state.copyWith(error: 'Decryption error: $e', isLoading: false);
      }
    } catch (e) {
      state = state.copyWith(error: 'Unlock failed: $e', isLoading: false);
    }
  }

  void lock() {
    if (_derivedKey != null) {
      _cryptoService.zeroKey(_derivedKey!);
      _derivedKey = null;
    }
    _lockTimer?.cancel();
    state = state.copyWith(isUnlocked: false);
  }

  void resetLockTimer() {
    if (state.isUnlocked) {
      _startLockTimer();
    }
  }

  void _startLockTimer() {
    _lockTimer?.cancel();
    _lockTimer = Timer(_idleTimeout, () {
      lock();
    });
  }

  Future<void> addSecret(String label, String value) async {
    if (_derivedKey == null) return;
    
    state = state.copyWith(isLoading: true);
    try {
      final id = const Uuid().v4();
      final salt = Uint8List.fromList(List.generate(16, (_) => Random.secure().nextInt(256)));
      final iv = Uint8List.fromList(List.generate(12, (_) => Random.secure().nextInt(256)));
      
      await _repository.saveSecret(
        id: id,
        label: label,
        value: value,
        derivedKey: _derivedKey!,
        salt: salt,
        iv: iv,
      );
      
      await refreshSecrets();
      state = state.copyWith(isLoading: false);
    } catch (e) {
      state = state.copyWith(error: 'Failed to add secret: $e', isLoading: false);
    }
  }

  Future<void> deleteSecret(String id) async {
    state = state.copyWith(isLoading: true);
    try {
      await _repository.deleteSecret(id);
      await refreshSecrets();
      state = state.copyWith(isLoading: false);
    } catch (e) {
      state = state.copyWith(error: 'Failed to delete secret: $e', isLoading: false);
    }
  }

  String decryptValue(SecretEntry secret) {
    if (_derivedKey == null) return '********';
    try {
      final encryptedBlob = base64Decode(secret.encryptedBlob);
      final iv = base64Decode(secret.iv);
      return _cryptoService.decrypt(_derivedKey!, Uint8List.fromList(encryptedBlob), Uint8List.fromList(iv));
    } catch (e) {
      return 'Decryption Error';
    }
  }

  Future<void> copyToClipboard(String text) async {
    await Clipboard.setData(ClipboardData(text: text));
    _clipboardTimer?.cancel();
    _clipboardTimer = Timer(_clipboardTimeout, () {
      Clipboard.setData(const ClipboardData(text: ''));
    });
  }

  @override
  void dispose() {
    lock();
    _clipboardTimer?.cancel();
    super.dispose();
  }
}
