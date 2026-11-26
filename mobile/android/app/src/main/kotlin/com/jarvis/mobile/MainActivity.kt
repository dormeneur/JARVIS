package com.jarvis.mobile

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.provider.Settings
import android.speech.SpeechRecognizer
import android.view.autofill.AutofillManager
import androidx.core.app.NotificationCompat
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
        // "Done on your PC" notifications for jobs sent from the phone (relay_repository.dart).
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, "jarvis/notify").setMethodCallHandler { call, result ->
            when (call.method) {
                "ask" -> {
                    if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                        requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 41)
                    }
                    result.success(null)
                }
                "show" -> {
                    JobNotice.show(this, call.argument<String>("id") ?: "", call.argument<String>("title") ?: "Jarvis", call.argument<String>("body") ?: "")
                    result.success(null)
                }
                else -> result.notImplemented()
            }
        }
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

/** A job sent from the phone ended on the PC. Hidden on the lock screen: the result can be personal. */
object JobNotice {
    private const val CHANNEL = "pc-jobs"

    fun show(context: Context, id: String, title: String, body: String) {
        val manager = context.getSystemService(NotificationManager::class.java) ?: return
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            manager.createNotificationChannel(NotificationChannel(CHANNEL, "Jobs on your PC", NotificationManager.IMPORTANCE_DEFAULT))
        }
        if (Build.VERSION.SDK_INT >= 33 && context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) return
        val open = PendingIntent.getActivity(
            context, 0,
            Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
            PendingIntent.FLAG_IMMUTABLE,
        )
        val notice = NotificationCompat.Builder(context, CHANNEL)
            .setSmallIcon(R.mipmap.ic_launcher)
            .setContentTitle(title)
            .setContentText(body)
            .setStyle(NotificationCompat.BigTextStyle().bigText(body))
            .setVisibility(NotificationCompat.VISIBILITY_PRIVATE)
            .setContentIntent(open)
            .setAutoCancel(true)
            .build()
        manager.notify(id.hashCode(), notice)
    }
}
