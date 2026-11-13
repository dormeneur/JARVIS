package com.jarvis.mobile

import android.content.ActivityNotFoundException
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.Settings
import android.speech.SpeechRecognizer
import android.view.autofill.AutofillManager
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
        // Forms learned while the PC was away, flushed from Dart once it's back (autofill_repository.dart).
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) FormOutbox.register(flutterEngine.dartExecutor.binaryMessenger, this)
        // JARVIS as the phone's autofill service (Android 8+): "on", "off" or "unsupported".
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, "jarvis/autofill").setMethodCallHandler { call, result ->
            val manager = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) getSystemService(AutofillManager::class.java) else null
            when (call.method) {
                "status" -> result.success(
                    when {
                        manager == null || !manager.isAutofillSupported -> "unsupported"
                        manager.hasEnabledAutofillServices() -> "on"
                        else -> "off"
                    }
                )
                "choose" -> {
                    // Android's own "use JARVIS for autofill?" prompt; some phones only have the settings page.
                    try {
                        startActivity(Intent(Settings.ACTION_REQUEST_SET_AUTOFILL_SERVICE, Uri.parse("package:$packageName")))
                    } catch (e: ActivityNotFoundException) {
                        startActivity(Intent(Settings.ACTION_SETTINGS))
                    }
                    result.success(null)
                }
                else -> result.notImplemented()
            }
        }
    }
}
