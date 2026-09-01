# ✅ Project Summary (Full Concept)

You are building a **Local-First AI Professional Life Manager**.

It is a **mobile app (Flutter)** connected to a **laptop-hosted server (FastAPI)** that runs a **local LLM (Ollama)** and accesses a **single private database** containing everything about the user:

* professional identity (resume, employment, projects, skills)
* personal info (name, age, blood group, marital info, etc)
* academics (uni, GPA, grades)
* health & fitness
* hobbies and interests
* personality traits + thought process

The app’s main job is to:
✅ maintain the user’s professional profile
✅ generate high-quality content using local AI **without fabricating facts**
✅ manage resumes as a **versioned repository** (Git-like history)
✅ tailor resumes instantly to a job description and store them as **new branches/categories**

**All data is stored locally on the laptop**, encrypted, and synced securely to the phone.

---

# ✅ PRD — Product Requirements Document

## 1) Product Name (Working)

**Local AI Professional OS** *(final name later)*

---

## 2) Vision

Create an app that becomes the user’s **personal career brain**, where all identity data lives in one place, and an AI can generate resumes, bios, answers, and professional writing **on demand**, privately, locally, and safely.

---

## 3) Goals

1. **Local-first privacy**

   * user data stays on laptop
   * AI runs locally (no cloud dependency)
2. **Single source of truth**

   * one database holds all validated facts
3. **Resume as code**

   * LaTeX + structured profile facts
   * version history + preview + branch/category system
4. **AI writing**

   * creates professional text using only stored facts + preferences + best practices
5. **Offline-first phone experience**

   * phone has synced copy for viewing/editing
   * laptop processes AI generation when online

---

## 4) Core Use Cases

### UC1 — Resume repo with version history

* user can create, edit, preview resume
* every major change becomes a version (commit)
* version history timeline + rollback
* compare versions

### UC2 — Auto-update fields over time

Example:

* graduation year changes
* “current year” changes
* duration of experiences
  System produces a suggested update and user approves → commit.

### UC3 — JD → Tailored Resume Branch

* user pastes job description
* AI generates a tailored resume draft
* new “category branch” created automatically:

  * “Flutter Dev Intern”
  * “Backend Intern”
  * “Company-specific resume branch”
* resume is stored under that category and can have multiple versions

### UC4 — Personal database

* store structured facts
* facts can be edited in the app
* this DB is the “truth”

### UC5 — AI Writer (safe & grounded)

* write LinkedIn/X posts
* write biography
* answer internship form questions
* reply drafts to messages (manual send only)
  **AI must never invent personal facts.**

### UC6 — Internship Google Forms helper (MVP safe)

* user provides questions
* AI generates responses using stored info
* user copy-pastes manually

---

## 5) MVP Scope (What we are building first)

### MVP Definition

A minimal working architecture proving the system:

✅ Flutter mobile app
✅ talks to a laptop local server
✅ laptop runs local AI (Ollama)
✅ laptop uses one local database
✅ phone can sync and view/edit data
✅ generate responses & resume drafts successfully

---

## 6) MVP Features (Detailed)

## 6.1 Mobile App (Flutter)

**Screens**

1. **Home**

   * quick actions: Resume, AI Writer, Sync status
2. **Chat / AI Assistant**

   * ask anything about yourself
   * generate bios, answers, posts
3. **Resume Manager**

   * list categories/branches:

     * “General Resume”
     * “Flutter Dev Intern”
     * “Company: Google”
   * open resume → preview PDF
   * edit LaTeX
4. **Commit History**

   * version list
   * commit message + time
   * preview any old version
5. **Personal Database**

   * structured sections + editable fields:

     * personal info
     * academics
     * employment
     * skills
     * projects
     * hobbies
     * health & fitness
     * personality traits
6. **Preferences**

   * edit `user-preference.md`
   * edit `best-practices.md`

---

## 6.2 Laptop Server (FastAPI)

Capabilities:

* secure device pairing
* encryption
* data sync API
* local AI processing via Ollama
* resume rendering pipeline
* versioning engine

---

## 6.3 Resume Engine (LaTeX + Structured DB)

Resume stored as:

* **LaTeX source**
* PDF output
* metadata: category, version, commit message, tags, created_by (“human”/“ai”)

Auto-update uses:

* placeholders + template values (preferred approach)

---

## 6.4 Prompt Injection Rules (Core idea)

Every AI generation request must automatically include:

1. relevant profile facts from DB
2. `user-preference.md`
3. `best-practices.md`
4. strict “no hallucination” rule
5. output format constraints (resume in LaTeX, post in LinkedIn format, etc.)

---

## 7) Non-Goals (MVP)

❌ Public internet access from phone → laptop
❌ Auto-posting to LinkedIn / X
❌ Fully automated Google Form submission
❌ CRDT conflict-free realtime editing
❌ Multi-user teams

---

## 8) User Stories (MVP)

### Resume

* As a user, I want to manage multiple resumes by category (job type/company)
* As a user, I want a Git-style history for each resume
* As a user, I want to compare versions and revert
* As a user, I want JD → resume branch generated instantly

### AI Writer

* As a user, I want AI to write a LinkedIn post about my internship
* As a user, I want AI to answer internship form questions using my profile
* As a user, I want AI to write a biography about me
* As a user, I want draft replies written like me (manual send only)

### Database

* As a user, I want all my data stored locally
* As a user, I want the phone to sync and work offline for viewing/editing

---

## 9) Functional Requirements

## 9.1 Authentication / Pairing

* Pair phone to laptop via **QR code**
* Establish secure key-based communication
* Phone cannot access without pairing

## 9.2 Resume Versioning

* each commit stores:

  * latex
  * pdf
  * commit message
  * timestamp
  * created_by

## 9.3 Resume Categories / Branching

* categories are like branches
* each category can have many versions

## 9.4 AI Generation Safety Rules

* AI must never invent facts
* if missing info → either:

  * ask user
  * write neutral statement (no numbers)

## 9.5 Sync Requirements

* phone can sync user DB and resume metadata
* PDF sync optional (but recommended)
* local-first offline viewing/editing

---

## 10) Non-Functional Requirements

### Performance

* AI response time target: **< 10s** for most outputs (local)
* resume PDF compile: **< 3s** typical

### Security

* encrypted DB on laptop
* secure API keys
* no plain-text token storage

### Reliability

* if laptop offline:

  * phone works in offline mode
  * AI generation disabled with clear UI message

---

# ✅ System Architecture (Formal)

## Components

### 1) Flutter Mobile Client

* UI + local cache DB
* sync manager
* request builder for AI tasks

### 2) Laptop Server (FastAPI)

* auth + pairing
* sync controller
* AI orchestrator
* resume engine
* versioning service

### 3) Local AI Runtime (Ollama)

* model runs locally
* accessed only via laptop server
* never directly exposed to phone

### 4) Database (Laptop = source of truth)

* PostgreSQL + pgvector
* encrypted storage policy

### 5) Storage

* local file storage for PDFs
* structured paths by user/category/version

---

## Request Flow (AI generation)

1. Mobile sends request: `"Tailor resume for this JD"`
2. Laptop server:

   * validates pairing key
   * fetches facts from DB
   * loads `best-practices.md` + `user-preference.md`
   * crafts prompt + tool calls
3. Ollama generates output
4. server returns:

   * content
   * optionally new version commit created

---

# ✅ Tech Stack (Final, as chosen)

### Mobile

* Flutter + Dart

### Laptop backend

* FastAPI (Python)
* Docker (optional but recommended)

### Local AI

* Ollama
* Suggested model tier for your laptop:

  * **Qwen2.5 7B** (fast + good)
  * **Llama 3.1 8B** (balanced)
  * use **4-bit quantization** for speed

### Database

* PostgreSQL + pgvector

### Resume compilation

* Tectonic / pdflatex (sandboxed)

---

# ✅ Data Model (MVP Database Schema)

## Tables (core)

### users

* id
* name
* created_at

### devices

* id
* user_id
* device_name
* public_key
* paired_at
* last_seen

### profile_facts

* id
* user_id
* category (personal/academic/health/etc)
* key
* value
* updated_at
* confidence (manual/ai_suggested)

### resume_categories

* id
* user_id
* name (ex: “Flutter Dev Intern”, “Company: Amazon”)
* created_at

### resume_versions

* id
* category_id
* version_number
* latex_source
* pdf_path
* commit_message
* created_by (human/ai/auto)
* created_at
* tags (json)

### job_applications

* id
* user_id
* category_id
* company
* role
* job_description_text
* created_at

### preferences_files

* id
* user_id
* file_type (“user-preference”, “best-practices”)
* content
* updated_at

---

# ✅ API Contract (FastAPI)

## Pairing

* `POST /pair/start` → creates pairing session
* `GET /pair/qr` → QR payload for phone
* `POST /pair/confirm` → exchange keys, finalize pairing

## Sync

* `GET /sync/pull` → phone pulls updated facts + resume metadata
* `POST /sync/push` → phone pushes new edits made offline

## Profile

* `GET /profile/all`
* `PATCH /profile/update`

## Preferences

* `GET /prefs/user-preference`
* `PUT /prefs/user-preference`
* `GET /prefs/best-practices`
* `PUT /prefs/best-practices`

## Resume

* `GET /resume/categories`
* `POST /resume/category/create`
* `POST /resume/version/commit`
* `GET /resume/version/history`
* `GET /resume/version/pdf`

## AI Requests

* `POST /ai/generate`
  Body:
* task_type: resume_update | resume_new | linkedin_post | bio | form_answer | message_reply
* context: optional text (JD / question / prompt)
* target_category_id (optional)
* strict_mode: true

Returns:

* generated_text
* created_resume_version_id (optional)
* warnings (if info missing)

---

# ✅ Prompt Rules (Mandatory)

Every generation prompt must include:

### 1) Safety header

* “Never invent facts.”
* “Only use facts provided.”
* “If missing, ask or write neutral.”

### 2) User preferences injection

Loaded from `user-preference.md` (editable)

### 3) Best practices injection

Loaded from `best-practices.md` (editable)

### 4) User facts injection

Fetched from database

### 5) Output format constraint

* LaTeX for resume
* bullet style rules
* length constraints

---

# ✅ MVP Milestones (Build Plan)

## Milestone 1 — Laptop server skeleton

* FastAPI running
* basic routes + health check
* local DB connection

## Milestone 2 — Pairing + secure client auth

* QR pairing
* stored key
* protected endpoints

## Milestone 3 — Personal database CRUD + sync

* edit fields in mobile
* sync push/pull works

## Milestone 4 — Local AI orchestration

* connect to Ollama
* `/ai/generate` works using injected profile context

## Milestone 5 — Resume system

* LaTeX editor + preview
* compilation pipeline
* commit history

## Milestone 6 — JD → new category branch generator

* paste JD
* create category
* generate LaTeX resume draft
* commit it with message automatically

---

# ✅ Acceptance Criteria (MVP “Done” Checklist)

✅ Phone pairs securely with laptop
✅ Phone can sync profile info both ways
✅ AI can generate a biography based on your DB
✅ AI can answer form questions using your stored info
✅ Resume manager can:

* create category
* edit LaTeX
* compile PDF
* commit version
* view history
  ✅ JD paste creates a new resume category and generates draft
  ✅ No hallucination: AI never invents experience or facts

---

# ✅ Open Issues / Risks (You should know)

1. **LinkedIn/X reading your profile**

* scraping is risky/blocked
* MVP should rely on user-provided content import (manual)

2. **AI always-on availability**

* laptop must be running to generate
* offline mode should clearly disable AI actions

3. **Data sensitivity**

* storing health/marital/blood group is okay if user wants,
  but security must be strict (encryption + pairing)

4. **LaTeX compilation security**

* must sandbox compile to prevent injection

---