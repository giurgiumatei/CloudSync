-- Initialize AWS Database
USE master;
GO

-- Create database if it doesn't exist
IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = 'AwsDb')
BEGIN
    CREATE DATABASE [AwsDb];
END
GO

USE [AwsDb];
GO

-- Create DataEntities table if it doesn't exist
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DataEntities]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[DataEntities] (
        [Id] int IDENTITY(1,1) NOT NULL,
        [Data] nvarchar(max) NOT NULL,
        [CreatedAt] datetime2(7) NOT NULL DEFAULT (getutcdate()),
        CONSTRAINT [PK_DataEntities] PRIMARY KEY CLUSTERED ([Id] ASC)
    );
END
GO

-- Insert some test data
IF NOT EXISTS (SELECT * FROM [dbo].[DataEntities])
BEGIN
    INSERT INTO [dbo].[DataEntities] ([Data], [CreatedAt])
    VALUES 
        ('Initial test data for AWS', GETUTCDATE()),
        ('AWS database initialized successfully', GETUTCDATE());
END
GO

PRINT 'AWS database initialization completed successfully'; 