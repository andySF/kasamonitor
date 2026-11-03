-- KasaMonitor 0.12+ on SQL Server 2019 or newer: the database, its login and its user. Run once, as a sysadmin, with sqlcmd
-- (the password is a sqlcmd variable, never written in this file):
--
--   sqlcmd -S <server>,1433 -U sa -C -i create-database.sql -v PASSWORD="<password of the kasamonitor login>"
--
-- The app creates no database: it connects as kasamonitor and applies its EF migrations at start-up (db_owner of its
-- database only). The database collation is case-, accent- and supplementary-character-sensitive, like SQLite was
-- (identifiers such as MAC addresses, sensor ids and setting keys compare exactly), whatever the server's collation is.
-- Recovery SIMPLE: the data are backed up by a full BACKUP DATABASE (SQL Agent job), the retention deletes run in batches.

IF DB_ID(N'kasamonitor') IS NULL
    CREATE DATABASE [kasamonitor] COLLATE Latin1_General_100_CS_AS_SC;
GO
ALTER DATABASE [kasamonitor] SET RECOVERY SIMPLE;
GO
IF SUSER_ID(N'kasamonitor') IS NULL
    CREATE LOGIN [kasamonitor] WITH PASSWORD = N'$(PASSWORD)', DEFAULT_DATABASE = [kasamonitor], CHECK_POLICY = ON, CHECK_EXPIRATION = OFF;
GO
USE [kasamonitor];
GO
IF USER_ID(N'kasamonitor') IS NULL
    CREATE USER [kasamonitor] FOR LOGIN [kasamonitor];
GO
ALTER ROLE [db_owner] ADD MEMBER [kasamonitor];
GO
