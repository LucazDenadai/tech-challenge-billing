using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OficinaMecanica.Billing.Infrastructure.Adapters.Out.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Pagamentos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificacoesPagamento",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ReferenciaRecurso = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Acao = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RecebidaEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Resultado = table.Column<int>(type: "integer", nullable: false),
                    PagamentoId = table.Column<Guid>(type: "uuid", nullable: true),
                    Detalhe = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CriadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AtualizadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificacoesPagamento", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Pagamentos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OsId = table.Column<Guid>(type: "uuid", nullable: false),
                    FilialId = table.Column<Guid>(type: "uuid", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClienteId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuoteId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuoteVersion = table.Column<int>(type: "integer", nullable: false),
                    Valor = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    Moeda = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MensagemCriacaoId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmailPagador = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    ExpiraEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ReferenciaProvedor = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    StatusProvedor = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CodigoPix = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    UrlPagamento = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AprovadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MotivoRecusa = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ResultadoDesconhecidoPublicado = table.Column<bool>(type: "boolean", nullable: false),
                    EstornoIdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MensagemEstornoId = table.Column<Guid>(type: "uuid", nullable: true),
                    MotivoEstorno = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ReferenciaEstorno = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    EstornoPendentePublicado = table.Column<bool>(type: "boolean", nullable: false),
                    EstornadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MotivoFalhaEstorno = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FalhasTecnicas = table.Column<int>(type: "integer", nullable: false),
                    ReconciliacaoManual = table.Column<bool>(type: "boolean", nullable: false),
                    MotivoReconciliacaoManual = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ProximaVerificacaoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CriadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AtualizadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pagamentos", x => x.Id);
                    table.CheckConstraint("CK_Pagamentos_Valor", "\"Valor\" > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificacoesPagamento_PagamentoId",
                table: "NotificacoesPagamento",
                column: "PagamentoId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificacoesPagamento_RequestId",
                table: "NotificacoesPagamento",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Pagamentos_ClienteId",
                table: "Pagamentos",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_Pagamentos_CorrelationId",
                table: "Pagamentos",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_Pagamentos_IdempotencyKey",
                table: "Pagamentos",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Pagamentos_OsId",
                table: "Pagamentos",
                column: "OsId");

            migrationBuilder.CreateIndex(
                name: "IX_Pagamentos_ProximaVerificacaoEm",
                table: "Pagamentos",
                column: "ProximaVerificacaoEm",
                filter: "\"ProximaVerificacaoEm\" IS NOT NULL AND NOT \"ReconciliacaoManual\"");

            migrationBuilder.CreateIndex(
                name: "IX_Pagamentos_QuoteId_QuoteVersion",
                table: "Pagamentos",
                columns: new[] { "QuoteId", "QuoteVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Pagamentos_ReferenciaProvedor",
                table: "Pagamentos",
                column: "ReferenciaProvedor",
                unique: true,
                filter: "\"ReferenciaProvedor\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificacoesPagamento");

            migrationBuilder.DropTable(
                name: "Pagamentos");
        }
    }
}
