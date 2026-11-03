package com.jarvis.mobile

import android.os.Build
import android.speech.SpeechRecognizer
import io.flutter.embedding.android.FlutterFragmentActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodChannel

// FlutterFragmentActivity (not FlutterActivity) is required by local_auth's
// BiometricPrompt for App Lock.
class MainActivity : FlutterFragmentActivity() {
    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        // Voice stays on the phone: the app only listens when Android has an on-device recognizer
        // (Android 12+ with the offline speech pack), never the online fallback.
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, "jarvis/speech").setMethodCallHandler { call, result ->
            when (call.method) {
                "onDeviceAvailable" -> result.success(
                    Build.VERSION.SDK_INT >= Build.VERSION_CODES.S &&
                        SpeechRecognizer.isOnDeviceRecognitionAvailable(this)
                )
                else -> result.notImplemented()
            }
        }
    }
}
