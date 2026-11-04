package com.jarvis.mobile

import android.app.assist.AssistStructure
import android.app.assist.AssistStructure.ViewNode
import android.content.pm.ApplicationInfo
import android.content.Context
import android.os.Build
import android.os.CancellationSignal
import android.service.autofill.AutofillService
import android.service.autofill.Dataset
import android.service.autofill.FillCallback
import android.service.autofill.FillRequest
import android.service.autofill.FillResponse
import android.service.autofill.SaveCallback
import android.service.autofill.SaveInfo
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
 * like, in any app or web form. Suggestions come from the phone's synced Profile/form-memory.json, so
 * they work offline; "Fill this form with Jarvis" also asks the vault's brain on the PC (never a cloud
 * model) through the sheet, and answers the user sends are learned back. Values reach the other app
 * only when the user picks them. Passwords, codes and card fields are never touched.
 */
@RequiresApi(Build.VERSION_CODES.O)
class JarvisAutofillService : AutofillService() {
    /** A text field on the other app's screen: what it's called, what Jarvis remembers for it, what's in it. */
    class Field(val id: AutofillId, val label: String, val suggestions: List<String>, val value: String?)

    override fun onFillRequest(request: FillRequest, cancellationSignal: CancellationSignal, callback: FillCallback) {
        val response = try {
            val structure = request.fillContexts.last().structure
            if (structure.activityComponent.packageName == packageName) null
            else {
                val memory = FormMemory.parse(memoryFile(this).takeIf { it.isFile }?.readText())
                responseFor(fieldsOf(structure, memory), titleOf(structure))
            }
        } catch (e: Exception) {
            Log.w(TAG, "autofill skipped", e) // never break the other app's form
            null
        }
        callback.onSuccess(response)
    }

    /**
     * The user tapped Save on Android's "Save to Jarvis?" after sending a form: what they typed is
     * learned (Android 9+, through the Jarvis sheet, which writes it to the vault on the PC).
     */
    override fun onSaveRequest(request: SaveRequest, callback: SaveCallback) {
        val sheet = try {
            val structure = request.fillContexts.last().structure
            val answers = fieldsOf(structure, FormMemory.parse(null))
                .mapNotNull { field -> field.value?.trim()?.takeIf { it.isNotEmpty() && '\n' !in it && it.length <= 120 }?.let { field.label to it } }
            if (answers.isEmpty() || Build.VERSION.SDK_INT < Build.VERSION_CODES.P) null
            else AutofillSheetActivity.save(this, titleOf(structure), answers)
        } catch (e: Exception) {
            Log.w(TAG, "save skipped", e)
            null
        }
        if (sheet != null && Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) callback.onSuccess(sheet) else callback.onSuccess()
    }

    private fun fieldsOf(structure: AssistStructure, memory: FormMemory): List<Field> {
        val fields = LinkedHashMap<AutofillId, Field>()
        val debug = applicationInfo.flags and ApplicationInfo.FLAG_DEBUGGABLE != 0
        fun visit(node: ViewNode) {
            val id = node.autofillId
            if (debug && id != null && node.autofillType != View.AUTOFILL_TYPE_NONE) {
                Log.i(TAG, "node class=${node.className} type=${node.autofillType} input=${node.inputType} hint=${node.hint != null} " +
                    "desc=${node.contentDescription != null} idEntry=${node.idEntry} hints=${node.autofillHints?.toList()} html=${node.htmlInfo?.tag} text=${node.text?.length}")
            }
            if (id != null && node.autofillType == View.AUTOFILL_TYPE_TEXT && !isSecret(node)) {
                val labels = labelsOf(node)
                val found = labels.firstNotNullOfOrNull { label -> memory.suggestions(label).takeIf { it.isNotEmpty() } }.orEmpty()
                if (labels.isNotEmpty()) fields[id] = Field(id, labels.first(), found, node.autofillValue?.takeIf { it.isText }?.textValue?.toString())
                // Debug builds: what each field is called and whether memory knew it (never the values).
                if (debug) Log.i(TAG, "field labels=$labels html=${node.htmlInfo?.attributes?.map { it.first }} suggestions=${found.size}")
            }
            for (i in 0 until node.childCount) visit(node.getChildAt(i))
        }
        for (i in 0 until structure.windowNodeCount) visit(structure.getWindowNodeAt(i).rootViewNode)
        return fields.values.toList()
    }

    /**
     * On a form (two or more named fields): "Fill this form with Jarvis" first, which opens the sheet
     * that also asks the vault's brain about fields memory doesn't know, and a Save prompt when the
     * form is sent. Then, on any field Jarvis remembers: its best value (filling every remembered
     * field at once) and up to two alternatives for that field alone.
     */
    private fun responseFor(fields: List<Field>, title: String): FillResponse? {
        val known = fields.filter { it.suggestions.isNotEmpty() }
        val isForm = fields.size >= 2
        if (known.isEmpty() && !isForm) return null
        val response = FillResponse.Builder()
        if (isForm) {
            val sheet = Dataset.Builder()
            fields.forEach { sheet.setValue(it.id, null, row("\u2726  Fill this form with Jarvis")) }
            sheet.setAuthentication(AutofillSheetActivity.fill(this, title, fields))
            response.addDataset(sheet.build())
            val ids = fields.map { it.id }.toTypedArray()
            val save = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O_MR1) SaveInfo.Builder(SaveInfo.SAVE_DATA_TYPE_GENERIC).setOptionalIds(ids)
            else SaveInfo.Builder(SaveInfo.SAVE_DATA_TYPE_GENERIC, arrayOf(ids[0])).setOptionalIds(ids.drop(1).toTypedArray())
            response.setSaveInfo(save.setFlags(SaveInfo.FLAG_SAVE_ON_ALL_VIEWS_INVISIBLE).build())
        }
        if (known.isNotEmpty()) {
            val all = Dataset.Builder()
            for (field in known) {
                all.setValue(field.id, AutofillValue.forText(field.suggestions[0]), row(if (known.size > 1) "${field.suggestions[0]}  \u00b7  fill all" else field.suggestions[0]))
            }
            response.addDataset(all.build())
            for (field in known) {
                for (value in field.suggestions.drop(1).take(MAX_ALTERNATIVES)) {
                    response.addDataset(Dataset.Builder().setValue(field.id, AutofillValue.forText(value), row(value)).build())
                }
            }
        }
        return response.build()
    }

    private fun row(text: String) = row(this, text)

    /** The website, or the app's name: what the form is remembered under. */
    private fun titleOf(structure: AssistStructure): String {
        fun domain(node: ViewNode): String? = node.webDomain ?: (0 until node.childCount).firstNotNullOfOrNull { domain(node.getChildAt(it)) }
        return (0 until structure.windowNodeCount).firstNotNullOfOrNull { domain(structure.getWindowNodeAt(it).rootViewNode) }
            ?: runCatching { packageManager.getApplicationLabel(packageManager.getApplicationInfo(structure.activityComponent.packageName, 0)).toString() }
                .getOrDefault(structure.activityComponent.packageName)
    }

    companion object {
        private const val TAG = "JarvisAutofill"
        private const val MAX_ALTERNATIVES = 2

        fun row(context: Context, text: String) = RemoteViews(context.packageName, android.R.layout.simple_list_item_1).apply {
            setTextViewText(android.R.id.text1, text)
        }

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
