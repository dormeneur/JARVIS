package com.jarvis.mobile

import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.IntentSender
import android.os.Build
import android.service.autofill.Dataset
import android.view.autofill.AutofillId
import android.view.autofill.AutofillManager
import android.view.autofill.AutofillValue
import androidx.annotation.RequiresApi
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.android.FlutterActivityLaunchConfigs
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodChannel
import java.time.OffsetDateTime
import java.time.ZoneOffset

/**
 * The Jarvis sheet over another app's form (lib/features/autofill/autofill_sheet.dart):
 *  - "fill": opened from "Fill this form with Jarvis"; shows Jarvis reading the form, asking the
 *    vault's brain on the PC about what memory doesn't know, and hands the answers to Android.
 *  - "save": opened from Android's "Save to Jarvis?"; learns what the user sent into the vault's
 *    form memory (the merge itself is FormMemory.remember, the same rules as the PC).
 *  - "text": "Autofill with Jarvis" in the text-selection menu ([JarvisTextActivity]), for any field
 *    the autofill list doesn't show on: the selected words are the question; the answer the user
 *    picks replaces them (or is copied, where the text can't be changed).
 */
@RequiresApi(Build.VERSION_CODES.O)
open class AutofillSheetActivity : FlutterActivity() {
    /** Opened from the text-selection menu: only the selected text is taken from the intent. */
    protected open val fromSelection = false

    override fun getDartEntrypointFunctionName() = "autofillMain"

    override fun getBackgroundMode() = FlutterActivityLaunchConfigs.BackgroundMode.transparent

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        // Anyone can open the selection menu's entry, so then nothing but the selected words is read.
        val selected = intent.getCharSequenceExtra(Intent.EXTRA_PROCESS_TEXT)?.toString()?.trim().orEmpty()
        val mode = if (fromSelection) "text" else intent.getStringExtra(EXTRA_MODE)
        val labels = if (fromSelection) listOf(selected) else intent.getStringArrayListExtra(EXTRA_LABELS).orEmpty()
        val values = if (fromSelection) emptyList() else intent.getStringArrayListExtra(EXTRA_VALUES).orEmpty()
        val title = if (fromSelection) "" else intent.getStringExtra(EXTRA_TITLE).orEmpty()
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, "jarvis/autofill-sheet").setMethodCallHandler { call, result ->
            when (call.method) {
                "request" -> result.success(
                    mapOf(
                        "mode" to mode,
                        "title" to title,
                        "labels" to labels,
                        "known" to if (fromSelection) listOf("") else intent.getStringArrayListExtra(EXTRA_KNOWN).orEmpty(), // "" where memory has nothing
                        "values" to values,
                        // The selection's question: everything memory has for it, and whether it can be replaced.
                        "choices" to if (fromSelection) JarvisAutofillService.memory(this).suggestions(selected) else emptyList(),
                        "readOnly" to intent.getBooleanExtra(Intent.EXTRA_PROCESS_TEXT_READONLY, false),
                    )
                )
                "merge" -> if (mode == "save") {
                    result.success(FormMemory.remember(call.argument<String>("json"), labels.zip(values), title, OffsetDateTime.now(ZoneOffset.UTC)))
                } else {
                    result.error("mode", "only a save learns", null)
                }
                "finish" -> {
                    finishWith(call.argument<List<String?>>("values").orEmpty())
                    result.success(null)
                }
                else -> result.notImplemented()
            }
        }
    }

    /** Hands Android the values to put in (none: nothing changes in the other app). */
    private fun finishWith(answers: List<String?>) {
        if (fromSelection) {
            val picked = answers.firstOrNull()
            if (picked.isNullOrBlank() || intent.getBooleanExtra(Intent.EXTRA_PROCESS_TEXT_READONLY, false)) setResult(RESULT_CANCELED)
            else setResult(RESULT_OK, Intent().putExtra(Intent.EXTRA_PROCESS_TEXT, picked))
            finish()
            return
        }
        @Suppress("DEPRECATION")
        val ids = intent.getParcelableArrayListExtra<AutofillId>(EXTRA_IDS).orEmpty()
        val filled = ids.zip(answers).filter { (_, value) -> !value.isNullOrBlank() }
        if (filled.isEmpty()) {
            setResult(RESULT_CANCELED)
        } else {
            val dataset = Dataset.Builder()
            for ((id, value) in filled) dataset.setValue(id, AutofillValue.forText(value), JarvisAutofillService.row(this, value!!))
            setResult(RESULT_OK, Intent().putExtra(AutofillManager.EXTRA_AUTHENTICATION_RESULT, dataset.build()))
        }
        finish()
    }

    companion object {
        private const val EXTRA_MODE = "mode"
        private const val EXTRA_TITLE = "title"
        private const val EXTRA_IDS = "ids"
        private const val EXTRA_LABELS = "labels"
        private const val EXTRA_KNOWN = "known"
        private const val EXTRA_VALUES = "values"
        private var nextRequest = 0

        fun fill(context: Context, title: String, fields: List<JarvisAutofillService.Field>): IntentSender = sender(
            context,
            Intent(context, AutofillSheetActivity::class.java)
                .putExtra(EXTRA_MODE, "fill")
                .putExtra(EXTRA_TITLE, title)
                .putParcelableArrayListExtra(EXTRA_IDS, ArrayList(fields.map { it.id }))
                .putStringArrayListExtra(EXTRA_LABELS, ArrayList(fields.map { it.label }))
                .putStringArrayListExtra(EXTRA_KNOWN, ArrayList(fields.map { it.suggestions.firstOrNull().orEmpty() })),
        )

        fun save(context: Context, title: String, answers: List<Pair<String, String>>): IntentSender = sender(
            context,
            Intent(context, AutofillSheetActivity::class.java)
                .putExtra(EXTRA_MODE, "save")
                .putExtra(EXTRA_TITLE, title)
                .putStringArrayListExtra(EXTRA_LABELS, ArrayList(answers.map { it.first }))
                .putStringArrayListExtra(EXTRA_VALUES, ArrayList(answers.map { it.second })),
        )

        // Android adds the form to the intent when it starts the sheet, so the PendingIntent is mutable.
        private fun sender(context: Context, intent: Intent): IntentSender = PendingIntent.getActivity(
            context,
            nextRequest++,
            intent,
            PendingIntent.FLAG_CANCEL_CURRENT or (if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) PendingIntent.FLAG_MUTABLE else 0),
        ).intentSender
    }
}

/** "Autofill with Jarvis" in Android's text-selection menu (next to Copy, Web search…). */
@RequiresApi(Build.VERSION_CODES.O)
class JarvisTextActivity : AutofillSheetActivity() {
    override val fromSelection = true
}
