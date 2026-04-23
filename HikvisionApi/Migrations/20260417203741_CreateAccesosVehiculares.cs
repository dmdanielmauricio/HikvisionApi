using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HikvisionApi.Migrations
{
    /// <inheritdoc />
    public partial class CreateAccesosVehiculares : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccesosVehiculares",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Placa = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Carril = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FechaHora = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Autorizado = table.Column<bool>(type: "bit", nullable: false),
                    TipoMovimiento = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Motivo = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ImagenUrl = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccesosVehiculares", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccesosVehiculares");
        }
    }
}
