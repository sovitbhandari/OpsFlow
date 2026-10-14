-- Run as an Azure SQL administrator after the managed identities exist.
-- Replace the bracketed names with the output principal names. The names are the
-- managed identity names, for example opsflowdev-api-id.
-- Runtime identities get data access only. The migrator gets schema changes.

CREATE USER [opsflowdev-api-id] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [opsflowdev-api-id];
ALTER ROLE db_datawriter ADD MEMBER [opsflowdev-api-id];

CREATE USER [opsflowdev-worker-id] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [opsflowdev-worker-id];
ALTER ROLE db_datawriter ADD MEMBER [opsflowdev-worker-id];

CREATE USER [opsflowdev-migrator-id] FROM EXTERNAL PROVIDER;
ALTER ROLE db_ddladmin ADD MEMBER [opsflowdev-migrator-id];
GRANT EXECUTE ON OBJECT::dbo.usp_OrganizationWorkOrderReport TO [opsflowdev-api-id];
