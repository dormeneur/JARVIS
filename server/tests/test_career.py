"""Career copilot: resume checks (14-Career-Copilot.md §4.3) and the extract route."""
from unittest.mock import patch

import httpx
import pytest

from app.config import settings
from app.routers import career
from app.services.resume import check, parse_date

RESUME = """Jane Q Doe
jane.doe@example.com | +91 98765 43210 | github.com/janedoe
12 MG Road, Bengaluru, Karnataka 560001, India

Experience
Backend Engineer, Acme Corp, Bengaluru    Jan 2023 - Present
- Built the payments API in Go serving 2M requests a day
Intern, Beta Labs                          06/2021 - 12/2021
- Wrote data pipelines in Python

Education
B.Tech in Computer Science, IIT Delhi, 2017 - 2021, CGPA 8.7

Skills: Go, Python, C++, C#, React, PostgreSQL, Docker, R
""" + "x " * 100


def _raw(**over):
    raw = {
        "contact": {"first_name": "Jane", "middle_name": "Q", "last_name": "Doe",
                    "email": "jane.doe@example.com", "phone": "+91 98765 43210"},
        "address": {"line1": "12 MG Road", "city": "Bengaluru", "postal": "560001",
                    "region_name": "Karnataka", "country_name": "India"},
        "summary": "",
        "links": {"github": "https://github.com/janedoe", "linkedin": "linkedin.com/in/made-up", "website": ""},
        "experience": [
            {"title": "Intern", "company": "Beta Labs", "location": "", "start": "06/2021", "end": "12/2021",
             "current": False, "bullets": ["Wrote data pipelines in Python"]},
            {"title": "Backend Engineer", "company": "Acme Corp", "location": "Bengaluru", "start": "Jan 2023",
             "end": "Present", "current": True,
             "bullets": ["Built the payments API in Go serving 2M requests a day", "Led a team of 40 engineers at Google"]},
            {"title": "CTO", "company": "Imaginary Inc", "location": "", "start": "2020", "end": "2021",
             "current": False, "bullets": []},
        ],
        "education": [{"degree": "B.Tech", "major": "Computer Science", "school": "IIT Delhi", "gpa": "8.7",
                       "honors": "", "start": "2017", "end": "2021", "enrolled": False}],
        "skills": [{"category": "languages", "items": ["Go", "Python", "C++", "C#", "R", "Rust"]},
                   {"category": "Backend & Data", "items": ["PostgreSQL", "python"]},
                   {"category": "Tools", "items": ["Docker"]}],
        "projects": [], "certificates": [],
    }
    raw.update(over)
    return raw


def test_only_facts_in_the_text_survive():
    result = check(_raw(), RESUME)
    p = result["profile"]
    assert [e["company"] for e in p["experience"]] == ["Acme Corp", "Beta Labs"]  # newest first, invented one dropped
    assert any("Imaginary Inc" in u for u in result["unverified"])
    assert p["experience"][0]["bullets"] == ["Built the payments API in Go serving 2M requests a day"]
    assert p["skills"]["Languages"] == ["Go", "Python", "C++", "C#", "R"]  # "Rust" isn't in the resume
    assert p["skills"]["Backend & Data"] == ["PostgreSQL"]  # "python" already taken, case-insensitively
    assert p["skills"]["Tools"] == ["Docker"]  # a new category the model named is kept
    assert result["counts"] == {"roles": 2, "schools": 1, "skills": 7}
    assert p["links"] == {"github": "https://github.com/janedoe", "linkedin": "", "website": ""}
    assert p["contact"]["phone"] == "+91 98765 43210" and p["contact"]["email"] == "jane.doe@example.com"
    assert p["education"][0]["gpa"] == "8.7" and p["education"][0]["school"] == "IIT Delhi"
    assert [e["id"] for e in p["experience"]] == ["e1", "e2"]


def test_dates_and_current():
    p = check(_raw(), RESUME)["profile"]
    acme, beta = p["experience"]
    assert (acme["start"], acme["end"], acme["current"]) == ("2023-01", None, True)
    assert (beta["start"], beta["end"], beta["current"]) == ("2021-06", "2021-12", False)
    assert parse_date("Present") == (None, True)
    assert parse_date("2019") == ("2019", False)
    assert parse_date("2022-03") == ("2022-03", False)
    assert parse_date("sometime") == (None, False)


def test_short_skill_needs_a_whole_word():
    raw = _raw(skills=[{"category": "Languages", "items": ["C", "Ruby"]}])
    assert check(raw, RESUME)["profile"]["skills"]["Languages"] == []  # "C" only appears inside C++/C#


def test_duplicates_merged_and_garbage_tolerated():
    dup = _raw()["experience"][1]
    result = check(_raw(experience=[dup, dict(dup), "junk"], education=None, skills=[{"items": None}]), RESUME)
    assert result["counts"]["roles"] == 1 and result["counts"]["schools"] == 0
    assert check({}, RESUME)["counts"] == {"roles": 0, "schools": 0, "skills": 0}
    assert check("not a dict", "")["counts"]["roles"] == 0


@pytest.fixture
def calls():
    seen = []

    async def fake_post(url, body, timeout, headers=None):
        seen.append(url)
        if url.endswith("/resume-text"):
            return {"text": RESUME}
        if url.endswith("/extract"):
            if seen and getattr(fake_post, "claude_down", False):
                raise httpx.ConnectError("down")
            return {"data": _raw()}
        return {"data": _raw(experience=[])}  # the local model

    with patch.object(career, "_post", fake_post), patch.object(settings, "jobs_secret", "s"):
        yield seen, fake_post


def test_extract_uses_claude_then_checks(client, auth_headers, calls):
    seen, _ = calls
    resp = client.post("/career/resume/extract", json={"path": "Profile/resume/cv.pdf"}, headers=auth_headers)
    assert resp.status_code == 200
    body = resp.json()
    assert body["engine"] == "claude" and body["counts"]["roles"] == 2
    assert not any(u.endswith("/extract-local") for u in seen)


def test_extract_falls_back_to_local_model(client, auth_headers, calls):
    seen, fake = calls
    fake.claude_down = True
    resp = client.post("/career/resume/extract", json={"path": "Profile/resume/cv.pdf"}, headers=auth_headers)
    assert resp.status_code == 200 and resp.json()["engine"] == "local"
    assert seen[-1].endswith("/brain/career/extract-local")


def test_extract_refuses_empty_text(client, auth_headers):
    async def empty(url, body, timeout, headers=None):
        return {"text": "  "}

    with patch.object(career, "_post", empty):
        resp = client.post("/career/resume/extract", json={"path": "Profile/resume/scan.pdf"}, headers=auth_headers)
    assert resp.status_code == 422


def test_guest_cannot_extract(client, auth_headers, calls):
    guest = client.post("/auth/register/device", json={"device_name": "guest"}, headers=auth_headers)
    headers = {"Authorization": f"Bearer {guest.json()['access_token']}"}
    assert client.post("/career/resume/extract", json={"path": "x.pdf"}, headers=headers).status_code == 403


def test_eval_score_counts_roles_dates_and_skills():
    from app.evals.resume import score

    profile = check(_raw(), RESUME)["profile"]
    expected = {"roles": [{"title": "Backend Engineer", "company": "Acme Corp", "start": "2023-01", "end": None},
                          {"title": "Intern", "company": "Beta Labs", "start": "2021-07", "end": "2021-12"},
                          {"title": "Lead", "company": "Gamma", "start": "2019", "end": "2020"}],
                "schools": ["IIT Delhi"], "skills": ["Go", "Python", "Docker", "React"]}
    s = score(expected, profile)
    assert s["roles_found"] == "2/3" and s["role_dates_exact"] == "1/2" and s["extra_roles"] == 0
    assert s["schools_found"] == "1/1" and s["skills_recall"] == 0.75
