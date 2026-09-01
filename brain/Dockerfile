# Build stage
FROM python:3.12-slim AS builder
WORKDIR /build
COPY requirements.txt .
# This network drops TLS handshakes to PyPI often enough that a plain
# `pip install` fails the build on an unlucky run. Retry instead of failing.
RUN pip install --no-cache-dir --retries 10 --timeout 60 -r requirements.txt

# Runtime stage
FROM python:3.12-slim
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    # Create a non-root user: containers must not run as root (06-Security-Model.md)
    && adduser --system --no-create-home --uid 1001 jarvis

COPY --from=builder /usr/local/lib/python3.12/site-packages /usr/local/lib/python3.12/site-packages
COPY --from=builder /usr/local/bin /usr/local/bin
COPY app/ ./app/

# history.db lives under /app/data which is a named volume in production,
# but in tests it needs to exist on the image. Create it and give jarvis
# ownership before dropping privileges.
RUN mkdir -p /app/data && chown jarvis /app/data

USER jarvis
EXPOSE 8001
CMD ["uvicorn", "app.api:app", "--host", "0.0.0.0", "--port", "8001"]
