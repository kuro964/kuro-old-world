using System;
using Content.Server.Database;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    // ST:OW - faction titles assignable by leads
    /// <inheritdoc />
    [DbContext(typeof(SqliteServerDbContext))]
    [Migration("20261106130000_STOWAddFactionTitles")]
    public partial class STOWAddFactionTitles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "stalker_band_member_titles",
                columns: table => new
                {
                    player_user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    band_id = table.Column<string>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    admin_locked = table.Column<bool>(type: "INTEGER", nullable: false),
                    set_by_user_id = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stalker_band_member_titles", x => new { x.player_user_id, x.band_id });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stalker_band_member_titles");
        }
    }
}
