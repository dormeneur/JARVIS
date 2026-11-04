package com.jarvis.mobile

import android.os.Build
import androidx.annotation.RequiresApi
import org.json.JSONObject
import java.time.OffsetDateTime
import kotlin.math.exp
import kotlin.math.max

/**
 * Read-only port of the PC's form memory (desktop/src/Jarvis.Core/Vault/FormMemory.cs): the same
 * field keys, synonyms, likeness and ranking, so a form on the phone gets what forms on the PC
 * taught. The file is the phone's synced copy of Profile/form-memory.json; nothing leaves the phone.
 */
@RequiresApi(Build.VERSION_CODES.O)
class FormMemory private constructor(private val fields: Map<String, List<Entry>>) {
    class Entry(val value: String, val uses: Int, val lastMillis: Long)

    /** Remembered one-line answers for a field labelled [label], best first. */
    fun suggestions(label: String, nowMillis: Long = System.currentTimeMillis()): List<String> {
        val key = keyOf(label)
        val direct = values(key, nowMillis)
        if (direct.isNotEmpty()) return direct
        // Forms ask for the full name; split it for first/last name boxes.
        val names = values("name", nowMillis).map { it.split(' ').filter(String::isNotEmpty) }
        return when (key) {
            "first name", "given name" -> names.mapNotNull { it.firstOrNull() }
            "last name", "surname", "family name" -> names.filter { it.size > 1 }.map { it.last() }
            else -> emptyList()
        }.distinct()
    }

    private fun values(key: String, nowMillis: Long): List<String> =
        (find(key) ?: emptyList())
            // ponytail: paragraphs ("why us?") belong to one form; phone autofill offers one-liners only.
            .filter { '\n' !in it.value && it.value.length <= 120 }
            .sortedByDescending { score(it, nowMillis) }
            .map { it.value }
            .distinctBy { it.lowercase() }

    /** The field with this key, or the one worded most like it. */
    private fun find(key: String): List<Entry>? {
        fields[key]?.let { return it }
        val words = key.split(' ').toSet()
        val best = fields.entries.maxByOrNull { similarity(words, it.key.split(' ').toSet()) } ?: return null
        return if (similarity(words, best.key.split(' ').toSet()) >= SAME_FIELD) best.value else null
    }

    companion object {
        private const val SAME_FIELD = 0.75
        private val NOT_WORD = Regex("[^\\p{L}\\p{N}]+")

        private val SYNONYMS = mapOf(
            "name" to "name", "full name" to "name", "first and last name" to "name", "legal name" to "name", "candidate name" to "name", "applicant name" to "name",
            "email" to "email", "email address" to "email", "e mail" to "email", "e mail address" to "email", "mail" to "email",
            "phone" to "phone", "phone number" to "phone", "mobile" to "phone", "mobile number" to "phone", "contact number" to "phone", "telephone" to "phone", "cell" to "phone", "whatsapp number" to "phone",
            "country" to "country", "country of residence" to "country", "nationality" to "nationality",
            "city" to "city", "current city" to "city", "location" to "city", "current location" to "city",
            "address" to "address", "home address" to "address", "postal address" to "address",
            "linkedin" to "linkedin", "linkedin profile" to "linkedin", "linkedin url" to "linkedin",
            "github" to "github", "github profile" to "github", "portfolio" to "portfolio", "website" to "portfolio",
            "date of birth" to "birth date", "dob" to "birth date", "birthday" to "birth date",
        )

        private val FILLER = setOf(
            "what", "whats", "is", "are", "your", "you", "please", "enter", "provide", "the", "a", "an", "of", "do", "have", "in", "optional", "required", "current", "us",
        )

        /** The field a question is about: its words without filler, or a known common field. */
        fun keyOf(question: String): String {
            val words = question.lowercase().replace("'", "").split(NOT_WORD).filter { it.isNotEmpty() }
            val plain = words.joinToString(" ")
            SYNONYMS[plain]?.let { return it }
            val meaningful = words.filterNot { it in FILLER }.joinToString(" ")
            return SYNONYMS[meaningful] ?: meaningful.ifEmpty { plain }
        }

        /** A missing or hand-broken file means no suggestions, never a crash. */
        fun parse(json: String?): FormMemory {
            if (json.isNullOrBlank()) return FormMemory(emptyMap())
            return try {
                val saved = JSONObject(json).optJSONObject("fields") ?: return FormMemory(emptyMap())
                val fields = saved.keys().asSequence().associateWith { key ->
                    val values = saved.getJSONObject(key).optJSONArray("values")
                    (0 until (values?.length() ?: 0)).mapNotNull { i ->
                        val entry = values!!.getJSONObject(i)
                        val value = entry.optString("value").trim()
                        if (value.isEmpty()) null
                        else Entry(value, entry.optInt("uses"), runCatching { OffsetDateTime.parse(entry.optString("last")).toInstant().toEpochMilli() }.getOrDefault(0L))
                    }
                }
                FormMemory(fields)
            } catch (e: Exception) {
                FormMemory(emptyMap())
            }
        }

        /** Frequency plus recency, as on the PC: each use counts 1, being used lately adds up to 2. */
        private fun score(entry: Entry, nowMillis: Long): Double =
            entry.uses + 2 * exp(-max(0L, nowMillis - entry.lastMillis) / 86_400_000.0 / 30)

        private fun similarity(a: Set<String>, b: Set<String>): Double =
            if (a.isEmpty() && b.isEmpty()) 0.0 else a.intersect(b).size.toDouble() / a.union(b).size
    }
}
