-- Initialize AWS Database (PostgreSQL)
-- Create DataEntities table if it doesn't exist
CREATE TABLE IF NOT EXISTS "DataEntities" (
    "Id" SERIAL PRIMARY KEY,
    "Data" TEXT NOT NULL,
    "CreatedAt" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT (NOW() AT TIME ZONE 'UTC')
);

-- Insert some test data if table is empty
INSERT INTO "DataEntities" ("Data", "CreatedAt")
SELECT 'Initial test data for AWS', NOW() AT TIME ZONE 'UTC'
WHERE NOT EXISTS (SELECT 1 FROM "DataEntities");

INSERT INTO "DataEntities" ("Data", "CreatedAt")
SELECT 'AWS database initialized successfully', NOW() AT TIME ZONE 'UTC'
WHERE NOT EXISTS (SELECT 1 FROM "DataEntities" WHERE "Data" = 'AWS database initialized successfully');

-- Create index for better performance
CREATE INDEX IF NOT EXISTS "IX_DataEntities_CreatedAt" ON "DataEntities" ("CreatedAt");

SELECT 'AWS database initialization completed successfully' as message; 