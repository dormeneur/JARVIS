"""Writes mobile/assets/geo/countries.json: every ISO 3166-1 country with its calling code and its
top-level ISO 3166-2 subdivisions (states, provinces...), for the career wizard's dropdowns
(14-Career-Copilot.md §6.2). Run by hand when the data needs a refresh; the output is committed.

    pip install pycountry phonenumbers   # dev only, never an app dependency
    python scripts/gen_geo.py
"""
import json
import re
import unicodedata
from pathlib import Path

import phonenumbers
import pycountry

OUT = Path(__file__).resolve().parent.parent / "mobile" / "assets" / "geo" / "countries.json"


def plain(name: str) -> str:
    """How forms spell it: "Bihār" -> "Bihar", "Wales [Cymru GB-CYM]" -> "Wales"."""
    name = re.sub(r"\s*\[.*?\]", "", name)
    return "".join(ch for ch in unicodedata.normalize("NFKD", name) if not unicodedata.combining(ch))


def main() -> None:
    by_country: dict[str, list] = {}
    for sub in pycountry.subdivisions:
        if getattr(sub, "parent_code", None) is None:  # states, not their districts
            by_country.setdefault(sub.country_code, []).append([sub.code, plain(sub.name)])
    countries = []
    for c in pycountry.countries:
        code = phonenumbers.country_code_for_region(c.alpha_2)
        name = getattr(c, "common_name", None) or c.name
        regions = sorted(by_country.get(c.alpha_2, []), key=lambda r: r[1])
        countries.append({"code": c.alpha_2, "name": plain(name), "dial": f"+{code}" if code else "", "regions": regions})
    countries.sort(key=lambda c: c["name"])
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(countries, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    print(f"{len(countries)} countries, {sum(len(c['regions']) for c in countries)} regions -> {OUT}")


if __name__ == "__main__":
    main()
