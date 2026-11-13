package com.jarvis.mobile

import android.os.Build
import androidx.annotation.RequiresApi
import org.json.JSONArray
import org.json.JSONObject
import java.time.OffsetDateTime
import kotlin.math.exp
import kotlin.math.max

/**
 * Read-only port of the PC's form memory (desktop/src/Jarvis.Core/Vault/FormMemory.cs): the same
 * field keys, synonyms, likeness and ranking, so a form on the phone gets what forms on the PC
 * taught. The file is the phone's synced copy of Profile/form-memory.json; nothing leaves the phone.
 * Facts from the synced Profile notes ([withFacts]) answer what no form has asked yet, with no AI.
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
            .filter { '\n' !in it.value && it.value.length <= 120 && fits(key, it.value) }
            .sortedByDescending { score(it, nowMillis) }
            .map { it.value }
            .distinctBy { it.lowercase() }

    private fun find(key: String): List<Entry>? = findKey(fields.keys, key)?.let { fields[it] }

    /**
     * This memory plus facts from the Profile notes ("- Email: …", or a "## Question" with a one-line
     * answer), ranked below anything a form has used, so what the user actually gave forms wins.
     */
    fun withFacts(facts: List<Pair<String, String>>): FormMemory {
        val merged = fields.mapValues { it.value.toMutableList() }.toMutableMap()
        for ((label, value) in facts) {
            val key = keyOf(label)
            if (key.isEmpty() || value.isEmpty()) continue
            val entries = merged.getOrPut(findKey(merged.keys, key) ?: key) { mutableListOf() }
            if (entries.none { same(it.value, value) }) entries += Entry(value, 0, 0L)
        }
        return FormMemory(merged)
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

        /**
         * Label-value facts in a Profile note, the way interview mode writes them: bullets like
         * "- **Email:** a@b.c" (a Markdown link gives its address), and a "## Question" heading whose
         * answer is one short line (standard-answers.md).
         */
        fun facts(markdown: String): List<Pair<String, String>> {
            val found = mutableListOf<Pair<String, String>>()
            var question: String? = null
            val answer = mutableListOf<String>()
            fun endQuestion() {
                val text = answer.singleOrNull()?.let(::cleanFact)
                if (question != null && text != null && text.isNotEmpty() && text.length <= 120) found += question!! to text
                question = null
                answer.clear()
            }
            for (raw in markdown.replace("\r\n", "\n").split('\n')) {
                val line = raw.trim()
                when {
                    line.startsWith("## ") -> {
                        endQuestion()
                        question = line.removePrefix("## ").trim().trimEnd('*').trim()
                    }
                    line.startsWith("#") -> endQuestion()
                    else -> {
                        FACT.matchEntire(line)?.let { match -> found += cleanFact(match.groupValues[1]) to cleanFact(match.groupValues[2]) }
                        if (question != null && line.isNotEmpty() && !line.startsWith("-") && !line.startsWith("*")) answer += line
                    }
                }
            }
            endQuestion()
            return found.filter { (label, value) -> label.isNotEmpty() && value.isNotEmpty() }
        }

        private val DATE = Regex("^\\d{1,4}[/.-]\\d{1,2}[/.-]\\d{1,4}$")

        /**
         * Whether [value] can be what a field called [label] holds. Apps give a date box a phone keypad,
         * so the input type alone says "phone" for a birthday; a value that can't be that field's
         * (no @ in an email, a date in a phone) isn't learned under it or offered for it.
         */
        fun fits(label: String, value: String): Boolean {
            val v = value.trim()
            return when (keyOf(label)) {
                "phone" -> v.count(Char::isDigit) in 7..15 && !DATE.matches(v) && v.none { it.isLetter() }
                "email" -> '@' in v && ' ' !in v
                "birth date" -> DATE.matches(v) || (v.any(Char::isLetter) && v.any(Char::isDigit))
                else -> true
            }
        }

        private val FACT = Regex("^[-*+]\\s+(?:\\*\\*)?([^:*]{1,40}?)(?:\\*\\*)?\\s*:\\s*(.+)$")
        private val LINK = Regex("\\[([^\\]]*)]\\(([^)]+)\\)")

        private fun cleanFact(text: String) =
            LINK.find(text)?.groupValues?.get(2)?.trim() ?: text.replace("**", "").replace("`", "").trim().trimEnd('.').trim()

        /** A missing or hand-broken file means no suggestions, never a crash. */
        fun parse(json: String?): FormMemory {
            if (json.isNullOrBlank()) return FormMemory(emptyMap())
            return try {
                val saved = JSONObject(json).optJSONObject("fields") ?: return FormMemory(emptyMap())
                val fields = saved.keys().asSequence().associateWith { key ->
                    val values = saved.getJSONObject(key).optJSONArray("values")
                    (0 until (values?.length() ?: 0)).mapNotNull { i ->
                        entryOf(values!!.getJSONObject(i)).takeIf { it.value.isNotEmpty() }
                    }
                }
                FormMemory(fields)
            } catch (e: Exception) {
                FormMemory(emptyMap())
            }
        }

        /**
         * The memory file with these answers learned, as the PC's FormMemory.Remember(used: true)
         * does it: each goes under its field (however it was worded), a value already there is used
         * once more, a new one is added, and past 20 values the least useful goes. Everything else in
         * the file is kept as it was.
         */
        fun remember(json: String?, answers: List<Pair<String, String>>, formTitle: String, now: OffsetDateTime): String {
            val root = runCatching { JSONObject(json ?: "") }.getOrDefault(JSONObject())
            if (!root.has("version")) root.put("version", 1)
            val fields = root.optJSONObject("fields") ?: JSONObject().also { root.put("fields", it) }
            val nowMillis = now.toInstant().toEpochMilli()
            for ((question, raw) in answers) {
                val value = raw.trim()
                if (value.isEmpty() || !fits(question, value)) continue
                val key = keyOf(question)
                val field = fields.optJSONObject(findKey(fields.keys().asSequence().toList(), key) ?: key)
                    ?: JSONObject().put("questions", JSONArray()).put("values", JSONArray()).also { fields.put(key, it) }
                val questions = field.optJSONArray("questions") ?: JSONArray().also { field.put("questions", it) }
                if ((0 until questions.length()).none { questions.optString(it).equals(question, ignoreCase = true) } && questions.length() < MAX_QUESTIONS) {
                    questions.put(question)
                }
                val values = field.optJSONArray("values") ?: JSONArray().also { field.put("values", it) }
                val entry = (0 until values.length()).map { values.getJSONObject(it) }.firstOrNull { same(it.optString("value"), value) }
                    ?: JSONObject().put("value", value).put("uses", 0).put("source", FROM_YOU).put("forms", JSONArray()).also { values.put(it) }
                entry.put("uses", entry.optInt("uses") + 1).put("last", now.toString()).put("value", value)
                val forms = entry.optJSONArray("forms") ?: JSONArray().also { entry.put("forms", it) }
                if (formTitle.isNotEmpty() && (0 until forms.length()).none { forms.optString(it).equals(formTitle, ignoreCase = true) }) {
                    forms.put(formTitle)
                    if (forms.length() > MAX_FORMS) forms.remove(0)
                }
                if (values.length() > MAX_VALUES) {
                    values.remove((0 until values.length()).minBy { score(entryOf(values.getJSONObject(it)), nowMillis) })
                }
            }
            return root.toString(2)
        }

        private const val MAX_VALUES = 20
        private const val MAX_QUESTIONS = 10
        private const val MAX_FORMS = 10
        private const val FROM_YOU = "You" // as on the PC: the user gave it

        private fun entryOf(json: JSONObject) = Entry(
            json.optString("value").trim(),
            json.optInt("uses"),
            runCatching { OffsetDateTime.parse(json.optString("last")).toInstant().toEpochMilli() }.getOrDefault(0L),
        )

        private fun same(a: String, b: String) =
            a.trim().split(SPACES).joinToString(" ").equals(b.trim().split(SPACES).joinToString(" "), ignoreCase = true)

        private val SPACES = Regex("\\s+")

        /** The field with this key, or the one worded most like it. */
        private fun findKey(keys: Collection<String>, key: String): String? {
            if (key in keys) return key
            val words = key.split(' ').toSet()
            val best = keys.maxByOrNull { similarity(words, it.split(' ').toSet()) } ?: return null
            return if (similarity(words, best.split(' ').toSet()) >= SAME_FIELD) best else null
        }

        /** Frequency plus recency, as on the PC: each use counts 1, being used lately adds up to 2. */
        private fun score(entry: Entry, nowMillis: Long): Double =
            entry.uses + 2 * exp(-max(0L, nowMillis - entry.lastMillis) / 86_400_000.0 / 30)

        private fun similarity(a: Set<String>, b: Set<String>): Double =
            if (a.isEmpty() && b.isEmpty()) 0.0 else a.intersect(b).size.toDouble() / a.union(b).size
    }
}
