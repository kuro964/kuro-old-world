using Content.Server.Database;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    // ST:OW - character nickname + "use nickname on dogtag" preference
    /// <inheritdoc />
    [DbContext(typeof(SqliteServerDbContext))]
    [Migration("20261106120000_STOWAddNickname")]
    public partial class STOWAddNickname : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "stnickname",
                table: "profile",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "stuse_nickname_on_dogtag",
                table: "profile",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "stnickname",
                table: "profile");

            migrationBuilder.DropColumn(
                name: "stuse_nickname_on_dogtag",
                table: "profile");
        }
    }
}
