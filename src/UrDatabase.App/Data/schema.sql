-- Ensure unique index exists for upserts
CREATE UNIQUE INDEX IF NOT EXISTS ux_files_path ON files(file_path);
