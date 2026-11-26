package com.jarvis.mobile

import org.json.JSONObject

/**
 * The typed career profile (Profile/profile.json, 14-Career-Copilot.md) as autofill facts: its
 * contact, address and links sections become "label: value" pairs FormMemory already understands
 * ("first name", "email", "city", "linkedin"…), plus the full name and the phone with its country
 * code. Ranked below what forms have actually used, like the Profile notes.
 */
object ProfileJson {
    private val SECTIONS = listOf("contact", "address", "links")

    fun facts(json: String?): List<Pair<String, String>> {
        if (json.isNullOrBlank()) return emptyList()
        val root = runCatching { JSONObject(json) }.getOrNull() ?: return emptyList()
        val facts = mutableListOf<Pair<String, String>>()
        for (section in SECTIONS) {
            val part = root.optJSONObject(section) ?: continue
            for (key in part.keys()) {
                val value = part.opt(key) as? String ?: continue
                if (value.isBlank() || key == "phone_country") continue
                facts += key.replace('_', ' ') to value.trim()
            }
        }
        val contact = root.optJSONObject("contact")
        if (contact != null) {
            val name = listOf("first_name", "middle_name", "last_name").mapNotNull { contact.optString(it).trim().takeIf(String::isNotEmpty) }
            if (name.isNotEmpty()) facts += "name" to name.joinToString(" ")
            val phone = contact.optString("phone").trim()
            val code = contact.optString("phone_country").trim()
            if (phone.isNotEmpty() && code.isNotEmpty() && !phone.startsWith("+")) facts += "phone" to "${if (code.startsWith("+")) code else "+$code"} $phone"
        }
        return facts.distinct()
    }
}
