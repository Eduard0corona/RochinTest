-- =====================================================================================
-- Aprovisionamiento del servidor para CartonPrinting (no es parte de la especificación).
-- Crea la base y los dos logins:
--   * $(MigratorLogin): ejecuta las migraciones (DDL); db_owner de la base.
--   * $(AppLogin): la aplicación; lectura y escritura, sin DDL. Los permisos sobre los
--     objetos de la especificación (DENY sobre dbo.AllowedTransition, UPDATE sobre la
--     secuencia) los aplica el ejecutor de migraciones después de crear el esquema.
-- Se ejecuta con un login sysadmin. Variables sqlcmd: DatabaseName, MigratorLogin,
-- MigratorPassword, AppLogin, AppPassword. Las contraseñas no se guardan en el repositorio.
-- Idempotente: puede ejecutarse varias veces.
-- =====================================================================================
SET NOCOUNT ON;
GO
IF DB_ID(N'$(DatabaseName)') IS NULL
    EXEC (N'CREATE DATABASE [$(DatabaseName)]');
GO
-- RCSI recomendado por docs/ARQUITECTURA_TECNICA.md (sección 3).
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = N'$(DatabaseName)' AND is_read_committed_snapshot_on = 1)
    EXEC (N'ALTER DATABASE [$(DatabaseName)] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE');
GO
IF SUSER_ID(N'$(MigratorLogin)') IS NULL
    EXEC (N'CREATE LOGIN [$(MigratorLogin)] WITH PASSWORD = N''$(MigratorPassword)'', DEFAULT_DATABASE = [$(DatabaseName)]');
ELSE
    EXEC (N'ALTER LOGIN [$(MigratorLogin)] WITH PASSWORD = N''$(MigratorPassword)''');
GO
IF SUSER_ID(N'$(AppLogin)') IS NULL
    EXEC (N'CREATE LOGIN [$(AppLogin)] WITH PASSWORD = N''$(AppPassword)'', DEFAULT_DATABASE = [$(DatabaseName)]');
ELSE
    EXEC (N'ALTER LOGIN [$(AppLogin)] WITH PASSWORD = N''$(AppPassword)''');
GO
USE [$(DatabaseName)];
GO
IF USER_ID(N'$(MigratorLogin)') IS NULL
    EXEC (N'CREATE USER [$(MigratorLogin)] FOR LOGIN [$(MigratorLogin)]');
ALTER ROLE db_owner ADD MEMBER [$(MigratorLogin)];
GO
IF USER_ID(N'$(AppLogin)') IS NULL
    EXEC (N'CREATE USER [$(AppLogin)] FOR LOGIN [$(AppLogin)]');
ALTER ROLE db_datareader ADD MEMBER [$(AppLogin)];
ALTER ROLE db_datawriter ADD MEMBER [$(AppLogin)];
-- Sin DDL: ni ALTER (deshabilitar triggers) ni CREATE sobre el esquema dbo.
DENY ALTER ON SCHEMA::dbo TO [$(AppLogin)];
GO
