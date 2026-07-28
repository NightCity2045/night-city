# License Scope

This document identifies which licenses apply to different parts of the
Night City 2045 repository.

## 1. Upstream code

Code inherited from Space Station 14, RobustToolbox, or another upstream
project remains under its original license.

Unmodified upstream code must not be marked as original Night City 2045 code.

The MIT License used by upstream components is available at:

```
LICENSE-MIT.txt
```

## 2. Original Night City 2045 code

Unless an individual file states otherwise, original files created for
Night City 2045 in the following directories are licensed under the
PolyForm Noncommercial License 1.0.0:

```
Content.Client/_NC/
Content.Server/_NC/
Content.Shared/_NC/
Resources/Prototypes/_NC/
```

Original Night City 2045 files outside these directories are covered by
PolyForm Noncommercial 1.0.0 only when they contain the following notice:

```
SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
```

The Community Funding Additional Permission also applies to those files.

## 3. Mixed files

When a file contains upstream code and original Night City 2045
modifications:

1. the upstream portions remain under their original license;
2. only the original Night City 2045 portions may be covered by PolyForm
   Noncommercial 1.0.0;
3. the origin and license of both portions must be documented clearly.

Substantial original Night City 2045 systems should be placed in separate
_NC files whenever reasonably possible.

## 4. Assets

Every asset must retain its own author and license information in the
applicable metadata file.

An asset is not covered by PolyForm Noncommercial merely because it is stored
inside an _NC directory. PolyForm applies to an asset only when its metadata
expressly says so.

## 5. Required notice

Required Notice: Copyright © 2026 Astro. Original Night City 2045 code is licensed under the PolyForm Noncommercial License 1.0.0, subject to the Community Funding Additional Permission.