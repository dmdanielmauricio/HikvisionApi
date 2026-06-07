using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HikvisionApi.Migrations
{
    /// <inheritdoc />
    public partial class AgregarModelos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Convenios",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NombreConvenio = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Activo = table.Column<bool>(type: "bit", nullable: false),
                    FechaInicio = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FechaFin = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Convenios", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Registros",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Placa = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TipoVehiculo = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FechaEntrada = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FechaSalida = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Activo = table.Column<bool>(type: "bit", nullable: false),
                    ValorPagado = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    EsMensualidad = table.Column<bool>(type: "bit", nullable: false),
                    ConvenioId = table.Column<int>(type: "int", nullable: true),
                    Carril = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Registros", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConveniosVehiculos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ConvenioId = table.Column<int>(type: "int", nullable: false),
                    ConvenioMensualidadId = table.Column<int>(type: "int", nullable: false),
                    Placa = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Activo = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConveniosVehiculos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConveniosVehiculos_Convenios_ConvenioMensualidadId",
                        column: x => x.ConvenioMensualidadId,
                        principalTable: "Convenios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConveniosVehiculos_ConvenioMensualidadId",
                table: "ConveniosVehiculos",
                column: "ConvenioMensualidadId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConveniosVehiculos");

            migrationBuilder.DropTable(
                name: "Registros");

            migrationBuilder.DropTable(
                name: "Convenios");
        }
    }
}
