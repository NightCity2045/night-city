START TRANSACTION;
CREATE TABLE nc_meta_blob_maintenance (
    nc_meta_blob_maintenance_id smallint NOT NULL,
    policy_version integer NOT NULL,
    aggregate_type_cursor character varying(64) NOT NULL,
    aggregate_id_cursor character varying(128) NOT NULL,
    aggregates_complete boolean NOT NULL,
    CONSTRAINT "PK_nc_meta_blob_maintenance" PRIMARY KEY (nc_meta_blob_maintenance_id)
);

CREATE TABLE nc_meta_checkpoint_blob_backfill (
    snapshot_id uuid NOT NULL,
    policy_version integer NOT NULL,
    next_chunk integer NOT NULL,
    digest_cursor character varying(64) NOT NULL,
    complete boolean NOT NULL,
    CONSTRAINT "PK_nc_meta_checkpoint_blob_backfill" PRIMARY KEY (snapshot_id),
    CONSTRAINT "FK_nc_meta_checkpoint_blob_backfill_nc_round_snapshot_ncround_~" FOREIGN KEY (snapshot_id) REFERENCES nc_round_snapshot (snapshot_id) ON DELETE CASCADE
);

CREATE TABLE nc_meta_checkpoint_blob_reference (
    snapshot_id uuid NOT NULL,
    digest character varying(64) NOT NULL,
    CONSTRAINT "PK_nc_meta_checkpoint_blob_reference" PRIMARY KEY (snapshot_id, digest),
    CONSTRAINT "FK_nc_meta_checkpoint_blob_reference_nc_meta_blob_digest" FOREIGN KEY (digest) REFERENCES nc_meta_blob (digest) ON DELETE RESTRICT,
    CONSTRAINT "FK_nc_meta_checkpoint_blob_reference_nc_round_snapshot_snapsho~" FOREIGN KEY (snapshot_id) REFERENCES nc_round_snapshot (snapshot_id) ON DELETE CASCADE
);

CREATE TABLE nc_meta_revision (
    aggregate_type character varying(64) NOT NULL,
    aggregate_id character varying(128) NOT NULL,
    aggregate_version bigint NOT NULL,
    format_version integer NOT NULL,
    session_epoch uuid NOT NULL,
    active_world_ticks bigint NOT NULL,
    payload text NOT NULL,
    created_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_nc_meta_revision" PRIMARY KEY (aggregate_type, aggregate_id, aggregate_version)
);

CREATE TABLE nc_meta_blob_reference (
    aggregate_type character varying(64) NOT NULL,
    aggregate_id character varying(128) NOT NULL,
    aggregate_version bigint NOT NULL,
    digest character varying(64) NOT NULL,
    CONSTRAINT "PK_nc_meta_blob_reference" PRIMARY KEY (aggregate_type, aggregate_id, aggregate_version, digest),
    CONSTRAINT "FK_nc_meta_blob_reference_nc_meta_blob_digest" FOREIGN KEY (digest) REFERENCES nc_meta_blob (digest) ON DELETE RESTRICT,
    CONSTRAINT "FK_nc_meta_blob_reference_nc_meta_revision_aggregate_type_aggr~" FOREIGN KEY (aggregate_type, aggregate_id, aggregate_version) REFERENCES nc_meta_revision (aggregate_type, aggregate_id, aggregate_version) ON DELETE CASCADE
);

CREATE INDEX "IX_nc_meta_blob_reference_digest" ON nc_meta_blob_reference (digest);

CREATE INDEX "IX_nc_meta_checkpoint_blob_reference_digest" ON nc_meta_checkpoint_blob_reference (digest);

CREATE INDEX "IX_nc_meta_revision_created_at" ON nc_meta_revision (created_at);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261024072006_NCMetaBlobRetention', '10.0.6');

COMMIT;

