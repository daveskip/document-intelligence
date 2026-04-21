using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentIntelligence.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProcessingDuration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ProcessingDurationMs",
                table: "ExtractionResults",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProcessingDurationMs",
                table: "ExtractionResults");
        }
    }
}
