// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

/// <summary>Immutable bytes. Possession of a digest is not permission to read a project or package.</summary>
[Table("nc_meta_blob"), Index(nameof(CreatedAt))]
public sealed class NCMetaBlob
{
    [Key, MaxLength(64)] public string Digest { get; set; } = string.Empty;
    public int ByteCount { get; set; }
    public byte[] Bytes { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public long AdmittedUntilUtcTicks { get; set; }
}

public abstract partial class ServerDbContext
{
    public DbSet<NCMetaBlob> NCMetaBlobs => Set<NCMetaBlob>();
}
