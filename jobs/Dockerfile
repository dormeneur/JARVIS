# jv-jobs: Claude Code + everything the ai-job-search engine shells out to
# (bun for the portal CLIs, python3 + pypdf for tools/, TeX for the CV and
# cover letter, poppler for verify_layout's `pdftotext -bbox`).
FROM oven/bun:1.2.21 AS bun

FROM python:3.13-slim

ARG CLAUDE_VERSION=2.1.310

# TeX: lualatex (CV, moderncv + fontawesome5) and xelatex (cover.cls + fontspec).
# texlive-fonts-extra is the big one; it carries fontawesome5.
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates curl git poppler-utils bubblewrap socat ripgrep \
        texlive-luatex texlive-xetex texlive-latex-extra texlive-fonts-recommended texlive-fonts-extra lmodern \
    && rm -rf /var/lib/apt/lists/* \
    && adduser --system --uid 1001 --home /home/jarvis --shell /bin/bash jarvis \
    && mkdir -p /state /work /home/jarvis/.claude && chown jarvis /state /work /home/jarvis/.claude \
    # The workspace is a host bind mount owned by another uid.
    && git config --system safe.directory /work

COPY --from=bun /usr/local/bin/bun /usr/local/bin/bun

COPY requirements.txt /tmp/requirements.txt
RUN pip install --no-cache-dir --retries 10 --timeout 60 -r /tmp/requirements.txt

# Shell escape is the one way a LaTeX compile runs arbitrary commands, which is
# why the engine never pre-approves lualatex. Disable it twice: kpathsea reads
# `shell_escape` from the environment, and the shims refuse the CLI flags.
ENV shell_escape=f
COPY bin/texshim /opt/jv/bin/lualatex
COPY bin/texshim /opt/jv/bin/xelatex
RUN chmod 755 /opt/jv/bin/*

USER jarvis
RUN curl -fsSL https://claude.ai/install.sh | bash -s ${CLAUDE_VERSION}
ENV PATH=/opt/jv/bin:/home/jarvis/.local/bin:$PATH

WORKDIR /app
COPY --chown=jarvis app/ ./app/
EXPOSE 8002
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8002"]
