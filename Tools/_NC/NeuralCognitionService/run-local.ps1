# SPDX-FileCopyrightText: 2026 Astro
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
# SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($env:OPENAI_API_KEY)) {
    throw 'Set OPENAI_API_KEY in this terminal before starting Neural Cognition Service.'
}

# This is an internal loopback bearer, not the OpenAI provider credential.
$env:NC_NEURAL_SERVICE_TOKEN = 'night-city-local-neural-dev'
& "$PSScriptRoot\.venv\Scripts\python.exe" -m uvicorn app.main:app --host 127.0.0.1 --port 8080
