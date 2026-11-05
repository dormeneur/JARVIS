"""Answers common form fields straight from Profile/profile.json (14-Career-Copilot.md §7.2).

Name, email, phone, address, links, work authorization, preferences and voluntary
self-identification questions are answered here without a model call: exact values, with the
JSON path as evidence. Anything this table doesn't know, or can't map onto the offered options,
falls through to the model (routers/forms.py), which may still answer it from the vault.
"""
from __future__ import annotations

import json
import logging
import re
from typing import Callable, Optional

from app.services.tools import ToolError, safe_path

logger = logging.getLogger(__name__)

PROFILE_JSON = "Profile/profile.json"

# Names forms use for countries that profile.json stores by ISO code (plus its own "name").
ALIASES = {
    "US": ["united states", "united states of america", "usa", "u s", "u s a", "us", "america"],
    "GB": ["united kingdom", "uk", "u k", "great britain", "britain", "england"],
    "AE": ["united arab emirates", "uae"],
    "IN": ["india"], "TH": ["thailand"], "SG": ["singapore"], "CA": ["canada"], "DE": ["germany"],
    "AU": ["australia"], "NL": ["netherlands", "the netherlands"], "IE": ["ireland"],
}
DECLINE = ("decline", "prefer not", "not wish", "don t wish", "do not wish", "not to say", "not to answer",
           "rather not", "choose not")
THIS_COUNTRY = ("this country", "the country", "country where", "the location", "this location")


def load() -> dict:
    try:
        path = safe_path(PROFILE_JSON)
    except ToolError:
        return {}
    if not path.is_file():
        return {}
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        logger.warning("profile.json unreadable: %s", exc)
        return {}
    return data if isinstance(data, dict) else {}


def fold(text: str) -> str:
    return " ".join(re.findall(r"[a-z0-9+#]+", (text or "").casefold()))


def label_of(question: str) -> str:
    """The question as a bare label: no asterisks, (required)/(optional), or trailing punctuation."""
    text = re.sub(r"\((?:required|optional|mandatory)\)", " ", question or "", flags=re.I)
    return fold(text)


def _get(profile: dict, path: str):
    value = profile
    for part in path.split("."):
        if not isinstance(value, dict):
            return None
        value = value.get(part)
    return value


def _str(profile: dict, path: str) -> Optional[str]:
    value = _get(profile, path)
    return value.strip() if isinstance(value, str) and value.strip() else None


def _yes_no(value) -> Optional[str]:
    return ("Yes" if value else "No") if isinstance(value, bool) else None


class Ask:
    """One question, with what the caller knows about the application."""

    def __init__(self, text: str, country: str = "", company: str = "") -> None:
        self.text = text
        self.label = label_of(text)
        self.country = (country or "").upper()
        self.company = company or ""


def country_in(profile: dict, text: str) -> Optional[str]:
    """The ISO code of a country the text names, among those the profile knows."""
    words = f" {fold(text)} "
    known = (_get(profile, "work_auth.by_country") or {})
    for code, entry in known.items():
        names = list(ALIASES.get(code.upper(), []))
        if isinstance(entry, dict) and entry.get("name"):
            names.append(fold(entry["name"]))
        if any(f" {name} " in words for name in names):
            return code.upper()
    return None


def _auth_country(profile: dict, ask: Ask) -> Optional[str]:
    """The country an authorization question is about: named in it, else the application's."""
    named = country_in(profile, ask.text)
    if named:
        return named
    m = re.search(r"\b(?:work|employment) (?:in|for) (?:the )?(.+)$", ask.label)
    phrase = m.group(1) if m else ""
    if phrase and not any(p in f"the {phrase}" for p in THIS_COUNTRY):
        return None  # names a country we have no answer for: the user decides
    return ask.country or None


def _auth(key: str) -> Callable[[dict, Ask], Optional[tuple[str, str]]]:
    def resolve(profile: dict, ask: Ask):
        code = _auth_country(profile, ask)
        if not code:
            return None
        entry = (_get(profile, "work_auth.by_country") or {}).get(code)
        answer = _yes_no(entry.get(key)) if isinstance(entry, dict) else None
        return (answer, f"work_auth.by_country.{code}.{key}") if answer else None
    return resolve


def _path(path: str, yes_no: bool = False):
    def resolve(profile: dict, _ask: Ask):
        value = _yes_no(_get(profile, path)) if yes_no else _str(profile, path)
        return (value, path) if value else None
    return resolve


def _full_name(profile: dict, _ask: Ask):
    parts = [_str(profile, f"contact.{k}") for k in ("first_name", "middle_name", "last_name")]
    if not parts[0] or not parts[2]:
        return None
    return " ".join(p for p in parts if p), "contact"


def _preferred(profile: dict, _ask: Ask):
    value = _str(profile, "contact.preferred_name") or _str(profile, "contact.first_name")
    return (value, "contact.preferred_name") if value else None


def _phone(profile: dict, ask: Ask):
    number = _str(profile, "contact.phone")
    if not number:
        return None
    code = _str(profile, "contact.phone_country") or ""
    if "without" in ask.label or number.startswith("+") or not code:
        return number, "contact.phone"
    return f"{code} {number}", "contact.phone"


def _worked_here(profile: dict, ask: Ask):
    companies = _get(profile, "work_auth.worked_at")
    if not ask.company or not isinstance(companies, list) or "auth" not in (_get(profile, "setup.completed") or []):
        return None
    mine = fold(ask.company)
    about_them = f" {ask.label} "
    if mine not in about_them and not any(w in about_them for w in (" us ", " here ", "this company", "our company", "this organi")):
        return None  # "ever worked remotely?" is not about this employer
    return ("Yes" if any(fold(c) == mine for c in companies if isinstance(c, str)) else "No"), "work_auth.worked_at"


def _start(profile: dict, _ask: Ask):
    if _get(profile, "preferences.start_now") is True:
        return "Immediately", "preferences.start_now"
    return _path("preferences.earliest_start")(profile, _ask)


def _notice(profile: dict, _ask: Ask):
    if _get(profile, "preferences.start_now") is True:
        return "None, I can start right away", "preferences.start_now"
    days = _get(profile, "preferences.notice_days")
    return (f"{days} days", "preferences.notice_days") if isinstance(days, int) and days > 0 else None


def _salary(profile: dict, _ask: Ask):
    salary = _get(profile, "preferences.salary") or {}
    amount = salary.get("amount") if isinstance(salary, dict) else None
    if not isinstance(amount, (int, float)) or amount <= 0:
        return None
    per = {"year": "per year", "month": "per month", "hour": "per hour"}.get(salary.get("period"), "")
    return f"{amount:,.0f} {salary.get('currency') or ''} {per}".replace("  ", " ").strip(), "preferences.salary"


def _self_id(key: str):
    def resolve(profile: dict, _ask: Ask):
        value = _str(profile, f"background.{key}")
        return (value, f"background.{key}") if value else None
    return resolve


# (regex on the bare label, resolver). First match wins; ^…$ patterns are whole labels only.
FIELDS: list[tuple[str, Callable]] = [
    (r"^(?:legal |your )?(?:first|given) name$|^forename$", _path("contact.first_name")),
    (r"^middle name$", _path("contact.middle_name")),
    (r"^(?:legal |your )?(?:last|family) name$|^surname$", _path("contact.last_name")),
    (r"^preferred (?:first )?name$|^nickname$", _preferred),
    (r"^(?:your |full |legal |full legal )?name$", _full_name),
    (r"^(?:your )?e ?mail(?: address)?$", _path("contact.email")),
    (r"^(?:phone|mobile|cell|telephone)(?: phone)?(?: number)?(?: without country code)?$", _phone),
    (r"^(?:phone |country )?(?:country|dialing) code$|^country phone code$", _path("contact.phone_country")),
    (r"^(?:street )?address(?: line)?(?: 1)?$|^street(?: address)?$", _path("address.line1")),
    (r"^address(?: line)? 2$|^apartment.*", _path("address.line2")),
    (r"^(?:city|town|city town|city or town)$", _path("address.city")),
    (r"^(?:zip|postal|post|pin)(?: code)?$|^zip postal code$|^postcode$|^pincode$", _path("address.postal")),
    (r"^country(?: of residence)?$", _path("address.country_name")),
    (r"^(?:state|province|region|state province|state or province|county)$", _path("address.region_name")),
    (r"^(?:your )?linkedin(?: profile)?(?: url| link)?$", _path("links.linkedin")),
    (r"^(?:your )?github(?: profile)?(?: url| link)?$", _path("links.github")),
    (r"^(?:personal )?(?:website|portfolio)(?: url| link)?$", _path("links.website")),
    (r"authori[sz]ed to work|eligible to work|right to work|legally (?:able|permitted) to work", _auth("authorized")),
    (r"sponsorship", _auth("sponsorship")),
    (r"security clearance", _path("work_auth.clearance", yes_no=True)),
    (r"(?:ties|relationship|related|relative|connection|worked|employed|served|hold|held).*(?:government|public office|public official)"
     r"|public office|public official", _path("work_auth.government_ties", yes_no=True)),
    (r"(?:previously|ever|before).*(?:employed|worked)|(?:employed|worked).*before|worked here", _worked_here),
    (r"notice period", _notice),
    (r"(?:when|earliest|available).*start|^start date$|^availability$", _start),
    (r"(?:expected|desired|salary) (?:salary|compensation|ctc|pay)|salary expectation|expected ctc", _salary),
    (r"relocat", _path("preferences.relocate", yes_no=True)),
    (r"on ?site|in (?:the )?office|in person", _path("preferences.onsite_ok", yes_no=True)),
    (r"reliable (?:means of )?transportation", _path("preferences.transport", yes_no=True)),
    (r"accommodation", _path("preferences.accommodations", yes_no=True)),
    (r"^gender|gender identity", _self_id("gender")),
    (r"\brace\b|ethnic", _self_id("ethnicity")),
    (r"veteran", _self_id("veteran")),
    (r"disabilit", _self_id("disability")),
    (r"pronoun", _self_id("pronouns")),
]


def pick_option(answer: str, options: list[str]) -> Optional[str]:
    """The offered option that means `answer`, or None."""
    if not options:
        return answer
    want = fold(answer)
    for option in options:
        if fold(option) == want:
            return option
    if want in ("yes", "no"):
        return next((o for o in options if fold(o).split(" ", 1)[0] == want), None)
    if want == "decline":
        return next((o for o in options if any(d in f" {fold(o)} " for d in DECLINE)), None)
    starts = [o for o in options if fold(o).startswith(want) or want.startswith(fold(o))]
    return starts[0] if len(starts) == 1 else None


def answer(question_text: str, options: list[str], profile: dict, country: str = "", company: str = "") -> Optional[tuple[str, str]]:
    """(answer, evidence path) from profile.json, or None to leave the question to the model."""
    if not profile:
        return None
    ask = Ask(question_text, country, company)
    for pattern, resolve in FIELDS:
        if re.search(pattern, ask.label):
            found = resolve(profile, ask)
            if not found:
                return None
            value, path = found
            if value == "decline" and not options:
                value = "Prefer not to say"
            picked = pick_option(value, options)
            return (picked, f"{PROFILE_JSON}#{path}") if picked else None
    return None
