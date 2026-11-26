package com.jarvis.mobile

import android.content.Intent
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.android.FlutterActivityLaunchConfigs
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodChannel

/**
 * Jarvis for text in any app (lib/features/selection/selection_sheet.dart), a sheet over that app:
 *  - "Explain with Jarvis" / "Save to Jarvis" in Android's text-selection toolbar (PROCESS_TEXT,
 *    two activity-aliases of this one, told apart by which alias started it);
 *  - "Jarvis" in the share sheet for shared text or a link (SEND text/plain).
 * Anyone can start it, so nothing but the text (and the sending app's name) is read from the intent.
 */
class JarvisSelectionActivity : FlutterActivity() {
    override fun getDartEntrypointFunctionName() = "selectionMain"

    override fun getBackgroundMode() = FlutterActivityLaunchConfigs.BackgroundMode.transparent

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        val alias = intent.component?.className.orEmpty()
        val action = when {
            alias.endsWith("SelectionExplain") -> "explain"
            alias.endsWith("SelectionSave") -> "save"
            else -> "menu"
        }
        val text = when (intent.action) {
            Intent.ACTION_SEND -> listOfNotNull(intent.getStringExtra(Intent.EXTRA_SUBJECT), intent.getStringExtra(Intent.EXTRA_TEXT))
                .filter { it.isNotBlank() }.distinct().joinToString("\n\n")
            else -> intent.getCharSequenceExtra(Intent.EXTRA_PROCESS_TEXT)?.toString().orEmpty()
        }.trim().take(MAX_TEXT)
        val url = intent.getStringExtra(Intent.EXTRA_TEXT)?.trim()?.takeIf { it.startsWith("http://") || it.startsWith("https://") }
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, "jarvis/selection").setMethodCallHandler { call, result ->
            when (call.method) {
                "request" -> result.success(mapOf("action" to action, "text" to text, "source" to sourceName(), "url" to url))
                else -> result.notImplemented()
            }
        }
    }

    /** The app the text came from, by name ("Chrome"), for where the note says it came from. */
    private fun sourceName(): String {
        val pkg = callingActivity?.packageName ?: referrer?.host ?: return ""
        if (pkg == packageName) return ""
        return runCatching { packageManager.getApplicationLabel(packageManager.getApplicationInfo(pkg, 0)).toString() }.getOrDefault("")
    }

    companion object {
        private const val MAX_TEXT = 20000
    }
}
