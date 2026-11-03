import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_tts/flutter_tts.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:speech_to_text/speech_recognition_error.dart';
import 'package:speech_to_text/speech_to_text.dart';

/// Speaking to JARVIS and hearing it back, both on the phone: the on-device speech recognizer
/// (never the online fallback) and the phone's own text-to-speech voice. Nothing about the
/// audio leaves the phone; only the recognised text is sent, like a typed question.
class VoiceService {
  VoiceService({SpeechToText? speech, FlutterTts? tts, MethodChannel? channel})
      : _speech = speech ?? SpeechToText(),
        _tts = tts ?? FlutterTts(),
        _channel = channel ?? const MethodChannel('jarvis/speech');

  final SpeechToText _speech;
  final FlutterTts _tts;
  final MethodChannel _channel;
  void Function(String message)? _onError;

  bool get isListening => _speech.isListening;

  /// Why the phone can't listen right now, in words for the user; null when it can.
  Future<String?> unavailableReason() async {
    final onDevice = await _channel.invokeMethod<bool>('onDeviceAvailable') ?? false;
    if (!onDevice) {
      return 'This phone has no on-device speech recogniser, so JARVIS won\'t listen (it would '
          'send your voice online). On Android 12 or later, download the offline speech pack: '
          'Settings → System → Languages → Speech, or the Google app → Settings → Voice → Offline speech recognition.';
    }

    final ready = await _speech.initialize(
      onError: (SpeechRecognitionError error) => _onError?.call(_describe(error.errorMsg)),
    );
    return ready ? null : 'JARVIS needs the microphone permission to hear you.';
  }

  /// Listens until the user pauses (or [stop]); [onWords] gets the words so far, then the final ones with done = true.
  Future<void> listen({
    required void Function(String words, bool done) onWords,
    void Function(String message)? onError,
  }) async {
    _onError = onError;
    await _tts.stop();
    await _speech.listen(
      onResult: (result) => onWords(result.recognizedWords, result.finalResult),
      listenOptions: SpeechListenOptions(
        onDevice: true,
        partialResults: true,
        listenMode: ListenMode.dictation,
        cancelOnError: true,
        autoPunctuation: true,
        pauseFor: const Duration(seconds: 3),
      ),
    );
  }

  Future<void> stop() => _speech.stop();

  /// Reads an answer aloud, without its Markdown, in the phone's own voice.
  Future<void> speak(String text) async {
    final plain = spokenText(text);
    if (plain.isEmpty) return;
    await _tts.stop();
    await _tts.speak(plain);
  }

  Future<void> stopSpeaking() => _tts.stop();

  /// An answer as it should sound: no Markdown symbols, code blocks or links' addresses.
  static String spokenText(String markdown) => markdown
      .replaceAll(RegExp(r'```[\s\S]*?```'), ' ')
      .replaceAllMapped(RegExp(r'\[([^\]]+)\]\([^)]+\)'), (match) => match.group(1)!)
      .replaceAll(RegExp(r'[*_`#>|~]'), '')
      .replaceAll(RegExp(r'\s+'), ' ')
      .trim();

  static String _describe(String code) => switch (code) {
        'error_no_match' || 'error_speech_timeout' => 'Didn\'t catch that. Try again.',
        'error_language_unavailable' || 'error_language_not_supported' =>
          'The offline speech pack for your language isn\'t installed on this phone.',
        'error_insufficient_permissions' || 'error_permission' => 'JARVIS needs the microphone permission to hear you.',
        'error_busy' => 'The speech recogniser is busy. Try again in a moment.',
        _ => 'Speech recognition stopped ($code).',
      };
}

final voiceServiceProvider = Provider<VoiceService>((ref) => VoiceService());

/// Whether answers to spoken questions are read aloud.
final speakAnswersProvider = StateNotifierProvider<SpeakAnswersNotifier, bool>((ref) => SpeakAnswersNotifier());

class SpeakAnswersNotifier extends StateNotifier<bool> {
  static const _key = 'jarvis_speak_answers';

  SpeakAnswersNotifier() : super(true) {
    _load();
  }

  Future<void> _load() async {
    final prefs = await SharedPreferences.getInstance();
    state = prefs.getBool(_key) ?? true;
  }

  Future<void> set(bool value) async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.setBool(_key, value);
    state = value;
  }
}
