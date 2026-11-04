package com.jarvis.mobile

import org.junit.Assert.assertEquals
import org.junit.Test

class FormMemoryTest {
    private val now = java.time.OffsetDateTime.parse("2026-11-04T10:00:00Z").toInstant().toEpochMilli()

    // The PC's file format (desktop FormMemory.ToJson), with made-up values.
    private val memory = FormMemory.parse(
        """
        {"version":1,"fields":{
          "name":{"questions":["Full name"],"values":[{"value":"Asha Rao","uses":3,"last":"2026-11-01T09:00:00.1234567+05:30","forms":[]}]},
          "phone":{"questions":["Phone number"],"values":[
            {"value":"5550100001","uses":1,"last":"2025-01-01T00:00:00Z","forms":[]},
            {"value":"5550100002","uses":1,"last":"2026-11-03T00:00:00Z","forms":[]}]},
          "notice period weeks":{"questions":["Notice period (weeks)"],"values":[{"value":"4","uses":1,"last":"2026-10-01T00:00:00Z","forms":[]}]},
          "why join":{"questions":["Why join?"],"values":[{"value":"Because\nreasons","uses":1,"last":"2026-10-01T00:00:00Z","forms":[]}]}
        }}
        """,
    )

    @Test
    fun matchesTheWaysAFieldIsAsked() {
        assertEquals("phone", FormMemory.keyOf("What's your mobile number?"))
        assertEquals(listOf("Asha Rao"), memory.suggestions("Your Name *", now))
        assertEquals(listOf("5550100002", "5550100001"), memory.suggestions("contact number", now)) // recent first
        assertEquals(listOf("4"), memory.suggestions("notice period in weeks", now)) // worded alike
        assertEquals(emptyList<String>(), memory.suggestions("company name", now)) // not the person's name
    }

    @Test
    fun splitsTheNameAndSkipsParagraphs() {
        assertEquals(listOf("Asha"), memory.suggestions("first name", now))
        assertEquals(listOf("Rao"), memory.suggestions("Last Name", now))
        assertEquals(emptyList<String>(), memory.suggestions("why join", now))
    }

    @Test
    fun learnsWhatWasSentUnderTheRightField_KeepingTheRest() {
        val original = """{"version":1,"fields":{"phone":{"questions":["Phone number"],"values":[{"value":"5550100001","uses":1,"last":"2025-01-01T00:00:00Z","forms":["Old form"],"source":"Profile/about.md"}]}}}"""
        val later = java.time.OffsetDateTime.parse("2026-11-04T10:00:00Z")

        val learned = FormMemory.remember(
            original,
            listOf("Mobile number" to "5550100002", "Contact number" to " 5550100001 ", "Given Name" to "Asha", "Notes" to "   "),
            "example.test",
            later,
        )
        val memory = FormMemory.parse(learned)

        assertEquals(listOf("5550100001", "5550100002"), memory.suggestions("phone", now)) // used twice now: first
        assertEquals(listOf("Asha"), memory.suggestions("given name", now))
        assertEquals(emptyList<String>(), memory.suggestions("notes", now))
        val phone = org.json.JSONObject(learned).getJSONObject("fields").getJSONObject("phone")
        assertEquals("Profile/about.md", phone.getJSONArray("values").getJSONObject(0).getString("source")) // kept
        assertEquals(3, phone.getJSONArray("questions").length())
    }

    @Test
    fun profileNotesAnswerWhatNoFormAskedYet_BelowWhatFormsUsed() {
        val notes = """
            # About
            - **Full name:** Asha Rao
            - Email: asha@example.test
            - LinkedIn: [my profile](https://linkedin.example/in/asha)
            - Phone: 5550100009

            ## Notice period?
            Four weeks.

            ## Why this role?
            Because it fits.
            And more.
        """.trimIndent()

        val known = memory.withFacts(FormMemory.facts(notes))

        assertEquals(listOf("asha@example.test"), known.suggestions("Email address", now))
        assertEquals(listOf("https://linkedin.example/in/asha"), known.suggestions("LinkedIn profile", now))
        assertEquals(listOf("Four weeks"), known.suggestions("notice period", now))
        assertEquals(emptyList<String>(), known.suggestions("why this role", now)) // two lines: not a one-liner
        assertEquals(listOf("5550100002", "5550100001", "5550100009"), known.suggestions("phone", now)) // forms first
    }

    @Test
    fun brokenFileMeansNoSuggestions() {
        assertEquals(emptyList<String>(), FormMemory.parse("{not json").suggestions("name", now))
        assertEquals(emptyList<String>(), FormMemory.parse(null).suggestions("name", now))
    }
}
