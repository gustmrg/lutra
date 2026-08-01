namespace Lutra.Core.Persistence.Migrations;

internal sealed record LutraMigration(int Version, string Name, string Sql);
