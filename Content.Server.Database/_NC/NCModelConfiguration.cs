// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public abstract partial class ServerDbContext
{
    partial void ConfigureNCModels(ModelBuilder modelBuilder)
    {
        ConfigureNCPersistenceModels(modelBuilder);
        ConfigureNCBankModels(modelBuilder);
        ConfigureNCHousingModels(modelBuilder);
        ConfigureNCDungeonModels(modelBuilder);
        ConfigureNCPoliceModels(modelBuilder);
        ConfigureNCAgentModels(modelBuilder);
        ConfigureNCMedicalModels(modelBuilder);
        ConfigureNCSkillModels(modelBuilder);
    }
}
