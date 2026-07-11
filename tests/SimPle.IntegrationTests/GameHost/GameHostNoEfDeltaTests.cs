using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SimPle.Infrastructure.Persistence;

namespace SimPle.IntegrationTests.GameHost;

/// <summary>
/// Module 5 is deliberately pure/in-memory: no EF entity, DbSet, or migration of its own (see the module spec's
/// "no-EF-delta" requirement). These tests assert that invariant directly against the real
/// <see cref="AppDbContext"/> EF model and the committed migrations on disk, so a future change that
/// accidentally adds persistence to the game-host tree fails a test instead of silently drifting from the spec.
/// </summary>
public sealed class GameHostNoEfDeltaTests
{
    [Fact]
    public void AppDbContextModel_HasNoEntityTypeInTheGameHostNamespace()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase("no-ef-delta-model-check").Options);

        var gameHostEntityTypes = db.Model.GetEntityTypes()
            .Where(t => t.ClrType.Namespace is not null && t.ClrType.Namespace.Contains("GameHost", StringComparison.Ordinal))
            .Select(t => t.ClrType.FullName)
            .ToList();

        gameHostEntityTypes.Should().BeEmpty("Module 5 is pure/in-memory and must never register an EF entity type");
    }

    [Fact]
    public void AppDbContext_ExposesNoGameHostDbSet()
    {
        var gameHostDbSetProperties = typeof(AppDbContext).GetProperties()
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Where(p => p.PropertyType.GetGenericArguments()[0].Namespace?.Contains("GameHost", StringComparison.Ordinal) == true)
            .Select(p => p.Name)
            .ToList();

        gameHostDbSetProperties.Should().BeEmpty();
    }

    [Fact]
    public void MigrationsDirectory_HasNoGameHostMigration()
    {
        var migrationsDirectory = RepoRelativePath("..", "..", "..", "src", "SimPle.Infrastructure", "Migrations");
        Directory.Exists(migrationsDirectory).Should().BeTrue($"expected the migrations directory at '{migrationsDirectory}'");

        var migrationFiles = Directory.GetFiles(migrationsDirectory, "*.cs")
            .Select(Path.GetFileName)
            .Where(name => name is not null && !name.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))
            .ToList();

        migrationFiles.Should().NotContain(
            name => name!.Contains("GameHost", StringComparison.OrdinalIgnoreCase),
            "Module 5 must ship with zero migrations — its state lives only in serialized envelopes, never a table");
    }

    private static string RepoRelativePath(params string[] segments) =>
        Path.GetFullPath(Path.Combine([Path.GetDirectoryName(SourceFile())!, .. segments]));

    private static string SourceFile([CallerFilePath] string path = "") => path;
}
