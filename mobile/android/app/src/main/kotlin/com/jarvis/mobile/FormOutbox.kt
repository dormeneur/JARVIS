package com.jarvis.mobile

import android.content.Context
import android.os.Build
import androidx.annotation.RequiresApi
import io.flutter.plugin.common.BinaryMessenger
import io.flutter.plugin.common.MethodChannel
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.time.OffsetDateTime
import java.time.ZoneOffset

/**
 * What the phone learned from forms but the PC hasn't heard yet. "Save to Jarvis" lands here first
 * (a file in the app's private storage, so it survives restarts and a dead PC); [fold] replays the
 * batches onto the server's form memory when the PC is reachable, and [ack] drops them only after
 * that write succeeded. Until then [JarvisAutofillService.memory] overlays them, so the phone
 * already offers what it just learned.
 * ponytail: FIFO file, no per-item ids; a flush racing another flush can count a use twice (a ranking nudge).
 */
@RequiresApi(Build.VERSION_CODES.O)
object FormOutbox {
    private const val NAME = "form_outbox.json"

    private fun file(context: Context) = File(context.filesDir, NAME)

    private fun read(context: Context): JSONArray =
        runCatching { JSONArray(file(context).readText()) }.getOrDefault(JSONArray())

    // Write-then-rename: a crash mid-write never leaves half a queue.
    private fun write(context: Context, batches: JSONArray) {
        val tmp = File(context.filesDir, "$NAME.tmp")
        tmp.writeText(batches.toString())
        check(tmp.renameTo(file(context))) { "couldn't save the outbox" }
    }

    @Synchronized
    fun add(context: Context, title: String, answers: List<Pair<String, String>>) {
        val batches = read(context)
        batches.put(
            JSONObject()
                .put("title", title)
                .put("at", OffsetDateTime.now(ZoneOffset.UTC).toString())
                .put("answers", JSONArray(answers.map { JSONArray(listOf(it.first, it.second)) })),
        )
        write(context, batches)
    }

    /** [json] with every waiting batch learned into it, and how many that was. */
    @Synchronized
    fun fold(context: Context, json: String?): Pair<String?, Int> {
        val batches = read(context)
        var merged = json
        for (i in 0 until batches.length()) {
            val batch = batches.getJSONObject(i)
            val answers = batch.getJSONArray("answers")
            merged = FormMemory.remember(
                merged,
                (0 until answers.length()).map { answers.getJSONArray(it).let { pair -> pair.getString(0) to pair.getString(1) } },
                batch.optString("title"),
                runCatching { OffsetDateTime.parse(batch.optString("at")) }.getOrDefault(OffsetDateTime.now(ZoneOffset.UTC)),
            )
        }
        return merged to batches.length()
    }

    /** The first [count] batches reached the PC: forget them (ones queued since stay). */
    @Synchronized
    fun ack(context: Context, count: Int) {
        val batches = read(context)
        write(context, JSONArray((count until batches.length()).map { batches.get(it) }))
    }

    /** One channel for both Flutter engines (the app and the autofill sheet). */
    fun register(messenger: BinaryMessenger, context: Context) {
        val app = context.applicationContext
        MethodChannel(messenger, "jarvis/form-outbox").setMethodCallHandler { call, result ->
            when (call.method) {
                "queue" -> {
                    val labels = call.argument<List<String>>("labels").orEmpty()
                    val values = call.argument<List<String?>>("values").orEmpty()
                    add(app, call.argument<String>("title").orEmpty(), labels.zip(values).mapNotNull { (l, v) -> v?.takeIf { it.isNotBlank() }?.let { l to it } })
                    result.success(null)
                }
                "count" -> result.success(read(app).length())
                "merge" -> {
                    val (json, count) = fold(app, call.argument<String>("json"))
                    result.success(mapOf("json" to json, "count" to count))
                }
                "ack" -> {
                    ack(app, call.argument<Int>("count") ?: 0)
                    result.success(null)
                }
                else -> result.notImplemented()
            }
        }
    }
}
