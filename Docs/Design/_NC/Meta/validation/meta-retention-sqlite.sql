BEGIN TRANSACTION;
CREATE TABLE "nc_meta_blob_maintenance" (
    "nc_meta_blob_maintenance_id" INTEGER NOT NULL CONSTRAINT "PK_nc_meta_blob_maintenance" PRIMARY KEY,
    "policy_version" INTEGER NOT NULL,
    "aggregate_type_cursor" TEXT NOT NULL,
    "aggregate_id_cursor" TEXT NOT NULL,
    "aggregates_complete" INTEGER NOT NULL
);

CREATE TABLE "nc_meta_checkpoint_blob_backfill" (
    "snapshot_id" TEXT NOT NULL CONSTRAINT "PK_nc_meta_checkpoint_blob_backfill" PRIMARY KEY,
    "policy_version" INTEGER NOT NULL,
    "next_chunk" INTEGER NOT NULL,
    "digest_cursor" TEXT NOT NULL,
    "complete" INTEGER NOT NULL,
    CONSTRAINT "FK_nc_meta_checkpoint_blob_backfill_nc_round_snapshot_ncround_snapshot_snapshot_id" FOREIGN KEY ("snapshot_id") REFERENCES "nc_round_snapshot" ("snapshot_id") ON DELETE CASCADE
);

CREATE TABLE "nc_meta_checkpoint_blob_reference" (
    "snapshot_id" TEXT NOT NULL,
    "digest" TEXT NOT NULL,
    CONSTRAINT "PK_nc_meta_checkpoint_blob_reference" PRIMARY KEY ("snapshot_id", "digest"),
    CONSTRAINT "FK_nc_meta_checkpoint_blob_reference_nc_meta_blob_digest" FOREIGN KEY ("digest") REFERENCES "nc_meta_blob" ("digest") ON DELETE RESTRICT,
    CONSTRAINT "FK_nc_meta_checkpoint_blob_reference_nc_round_snapshot_snapshot_id" FOREIGN KEY ("snapshot_id") REFERENCES "nc_round_snapshot" ("snapshot_id") ON DELETE CASCADE
);

CREATE TABLE "nc_meta_revision" (
    "aggregate_type" TEXT NOT NULL,
    "aggregate_id" TEXT NOT NULL,
    "aggregate_version" INTEGER NOT NULL,
    "format_version" INTEGER NOT NULL,
    "session_epoch" TEXT NOT NULL,
    "active_world_ticks" INTEGER NOT NULL,
    "payload" TEXT NOT NULL,
    "created_at" TEXT NOT NULL,
    CONSTRAINT "PK_nc_meta_revision" PRIMARY KEY ("aggregate_type", "aggregate_id", "aggregate_version")
);

CREATE TABLE "nc_meta_blob_reference" (
    "aggregate_type" TEXT NOT NULL,
    "aggregate_id" TEXT NOT NULL,
    "aggregate_version" INTEGER NOT NULL,
    "digest" TEXT NOT NULL,
    CONSTRAINT "PK_nc_meta_blob_reference" PRIMARY KEY ("aggregate_type", "aggregate_id", "aggregate_version", "digest"),
    CONSTRAINT "FK_nc_meta_blob_reference_nc_meta_blob_digest" FOREIGN KEY ("digest") REFERENCES "nc_meta_blob" ("digest") ON DELETE RESTRICT,
    CONSTRAINT "FK_nc_meta_blob_reference_nc_meta_revision_aggregate_type_aggregate_id_aggregate_version" FOREIGN KEY ("aggregate_type", "aggregate_id", "aggregate_version") REFERENCES "nc_meta_revision" ("aggregate_type", "aggregate_id", "aggregate_version") ON DELETE CASCADE
);

CREATE INDEX "IX_nc_meta_blob_reference_digest" ON "nc_meta_blob_reference" ("digest");

CREATE INDEX "IX_nc_meta_checkpoint_blob_reference_digest" ON "nc_meta_checkpoint_blob_reference" ("digest");

CREATE INDEX "IX_nc_meta_revision_created_at" ON "nc_meta_revision" ("created_at");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261024071958_NCMetaBlobRetention', '10.0.6');

COMMIT;

