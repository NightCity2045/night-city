# SPDX-FileCopyrightText: 2026 Astro
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
# SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

$ErrorActionPreference = 'Stop'

# The game server forwards its confidential nc_neural.api_key for each bounded request.
& "$PSScriptRoot\.venv\Scripts\python.exe" -m uvicorn app.main:app --host 127.0.0.1 --port 8080 `
    --reload --reload-dir "$PSScriptRoot\app"
