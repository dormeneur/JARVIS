package com.jarvis.mobile

import android.app.assist.AssistStructure.ViewNode
import android.content.Context
import android.os.Build
import android.os.CancellationSignal
import android.service.autofill.AutofillService
import android.service.autofill.Dataset
import android.service.autofill.FillCallback
import android.service.autofill.FillRequest
import android.service.autofill.FillResponse
import android.service.autofill.SaveCallback
import android.service.autofill.SaveRequest
import android.text.InputType
import android.util.Log
import android.view.View
import android.view.autofill.AutofillId
import android.view.autofill.AutofillValue
import android.widget.RemoteViews
import androidx.annotation.RequiresApi
import java.io.File

/**
 * Android autofill from what JARVIS remembers of past forms: name, email, phone, city and the
 * like, in any app or (with the browser's "autofill using another service" on) any web form. Reads
 * the phone's synced Profile/form-memory.json, so it works offline; values reach the other app only
 * when the user picks one. Passwords and card fields are never touched.
 */
@RequiresApi(Build.VERSION_CODES.O)
class JarvisAutofillService : AutofillService() {
    override fun onFillRequest(request: FillRequest, cancellationSignal: CancellationSignal, callback: FillCallback) {
        val response = try {
            val structure = request.fillContexts.last().structure
            if (structure.activityComponent.packageName == packageName) null
            else {
                val memory = FormMemory.parse(memoryFile(this).takeIf { it.isFile }?.readText())
                val fields = LinkedHashMap<AutofillId, List<String>>()
                for (i in 0 until structure.windowNodeCount) collect(structure.getWindowNodeAt(i).rootViewNode, memory, fields)
                responseFor(fields)
            }
        } catch (e: Exception) {
            Log.w(TAG, "autofill skipped", e) // never break the other app's form
            null
        }
        callback.onSuccess(response)
    }

    // ponytail: no saving back yet; forms filled on the PC teach the memory. Add a SaveInfo when phone forms should too.
    override fun onSaveRequest(request: SaveRequest, callback: SaveCallback) = callback.onSuccess()

    private fun collect(node: ViewNode, memory: FormMemory, fields: MutableMap<AutofillId, List<String>>) {
        val id = node.autofillId
        if (id != null && node.autofillType == View.AUTOFILL_TYPE_TEXT && !isSecret(node)) {
            val labels = labelsOf(node)
            val found = labels.firstNotNullOfOrNull { label -> memory.suggestions(label).takeIf { it.isNotEmpty() } }
            found?.let { fields[id] = it }
            // Debug builds: what each field is called and whether memory knew it (never the values).
            if (applicationInfo.flags and android.content.pm.ApplicationInfo.FLAG_DEBUGGABLE != 0) {
                Log.d(TAG, "field labels=$labels html=${node.htmlInfo?.attributes?.map { it.first }} suggestions=${found?.size ?: 0}")
            }
        }
        for (i in 0 until node.childCount) collect(node.getChildAt(i), memory, fields)
    }

    /** One dataset fills every recognised field with its best value; the others swap in one alternative. */
    private fun responseFor(fields: Map<AutofillId, List<String>>): FillResponse? {
        if (fields.isEmpty()) return null
        val response = FillResponse.Builder()
        val all = Dataset.Builder()
        for ((id, values) in fields) {
            all.setValue(id, AutofillValue.forText(values[0]), row(if (fields.size > 1) "${values[0]}  ·  fill all" else values[0]))
        }
        response.addDataset(all.build())
        for ((id, values) in fields) {
            for (value in values.drop(1).take(MAX_ALTERNATIVES)) {
                response.addDataset(Dataset.Builder().setValue(id, AutofillValue.forText(value), row(value)).build())
            }
        }
        return response.build()
    }

    private fun row(text: String) = RemoteViews(packageName, android.R.layout.simple_list_item_1).apply {
        setTextViewText(android.R.id.text1, text)
    }

    companion object {
        private const val TAG = "JarvisAutofill"
        private const val MAX_ALTERNATIVES = 2

        /** The sync mirror Flutter keeps (path_provider's documents dir is app_flutter/). */
        fun memoryFile(context: Context) = File(context.getDir("flutter", Context.MODE_PRIVATE), "jarvis_mirror/Profile/form-memory.json")

        /** Android's own hints and HTML autocomplete tokens, in the memory's words. */
        private val HINTS = mapOf(
            "emailAddress" to "email", "email" to "email",
            "name" to "name", "personName" to "name",
            "personGivenName" to "first name", "given-name" to "first name",
            "personFamilyName" to "last name", "family-name" to "last name",
            "phone" to "phone", "phoneNumber" to "phone", "phoneNational" to "phone", "tel" to "phone", "tel-national" to "phone",
            "postalAddress" to "address", "streetAddress" to "address", "street-address" to "address", "address-line1" to "address",
            "addressLocality" to "city", "address-level2" to "city",
            "addressCountry" to "country", "country" to "country", "country-name" to "country",
            "birthDateFull" to "birth date", "bday" to "birth date",
            "url" to "portfolio",
            // Chrome's own guesses (its "ua-autofill-hints" attribute).
            "NAME_FULL" to "name", "NAME_FIRST" to "first name", "NAME_LAST" to "last name", "EMAIL_ADDRESS" to "email",
            "PHONE_HOME_WHOLE_NUMBER" to "phone", "PHONE_HOME_CITY_AND_NUMBER" to "phone", "ADDRESS_HOME_STREET_ADDRESS" to "address",
            "ADDRESS_HOME_LINE1" to "address", "ADDRESS_HOME_CITY" to "city", "ADDRESS_HOME_COUNTRY" to "country",
        )

        private val PASSWORD_TYPES = setOf(
            InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD,
            InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_WEB_PASSWORD,
            InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD,
            InputType.TYPE_CLASS_NUMBER or InputType.TYPE_NUMBER_VARIATION_PASSWORD,
        )

        /** Passwords, one-time codes, PINs and card fields. */
        private fun isSecret(node: ViewNode): Boolean {
            val html = node.htmlInfo?.attributes.orEmpty().associate { it.first.lowercase() to it.second.orEmpty().lowercase() }
            val hints = (node.autofillHints.orEmpty().toList() + listOfNotNull(html["autocomplete"], html["ua-autofill-hints"]))
                .flatMap { it.lowercase().split(' ', ',') }
            return (node.inputType and (InputType.TYPE_MASK_CLASS or InputType.TYPE_MASK_VARIATION)) in PASSWORD_TYPES ||
                html["type"] == "password" ||
                hints.any { it.contains("password") || it.contains("creditcard") || it.contains("otp") || it.startsWith("cc-") || it.startsWith("credit_card") || it in setOf("one-time-code", "pin", "cvc") }
        }

        /** What the field is called, most reliable first: hints, then the web page's words, then the app's. */
        private fun labelsOf(node: ViewNode): List<String> {
            val html = node.htmlInfo?.attributes.orEmpty().associate { it.first.lowercase() to it.second.orEmpty() }
            val hints = node.autofillHints.orEmpty().toList() + listOfNotNull(html["autocomplete"], html["ua-autofill-hints"]).flatMap { it.split(' ', ',') }
            return (hints.mapNotNull { HINTS[it] } +
                listOfNotNull(html["label"], html["aria-label"], html["placeholder"], html["name"], html["id"]) +
                listOfNotNull(node.hint, node.idEntry, node.contentDescription?.toString()))
                .map { it.replace(CAMEL, "$1 $2") }
                .filter { it.isNotBlank() }
        }

        private val CAMEL = Regex("([a-z])([A-Z])")
    }
}
