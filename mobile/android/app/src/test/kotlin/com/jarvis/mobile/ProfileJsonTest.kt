package com.jarvis.mobile

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class ProfileJsonTest {
    private val profile = """
        {"contact": {"first_name": "Aditya", "last_name": "B", "email": "a@example.com", "phone": "98765 43210", "phone_country": "91", "middle_name": ""},
         "address": {"city": "Pune", "country": "IN", "unknown_number": 7},
         "links": {"linkedin": "https://linkedin.com/in/x"},
         "background": {"notes": "never read"}}
    """.trimIndent()

    @Test
    fun contactAddressAndLinksBecomeFacts() {
        val facts = ProfileJson.facts(profile).toMap()
        assertEquals("a@example.com", facts["email"])
        assertEquals("Pune", facts["city"])
        assertEquals("https://linkedin.com/in/x", facts["linkedin"])
        assertTrue("background" !in facts.keys.joinToString())
    }

    @Test
    fun fullNameAndInternationalPhoneAreBuilt() {
        val facts = ProfileJson.facts(profile)
        assertTrue("name" to "Aditya B" in facts)
        assertTrue("phone" to "+91 98765 43210" in facts)
    }

    @Test
    fun brokenOrMissingFilesGiveNothing() {
        assertEquals(emptyList<Pair<String, String>>(), ProfileJson.facts(null))
        assertEquals(emptyList<Pair<String, String>>(), ProfileJson.facts("{ not json"))
    }
}
