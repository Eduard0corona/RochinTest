using CartonPrinting.Contracts;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.Infrastructure.Data;

/// <summary>Una época aceptada del contador del PLC (dbo.PlcEpochHistory).</summary>
public sealed record PlcEpoch(
    string SiteId,
    string StationId,
    long Epoch,
    string EpochSource,
    string AcceptanceMode,
    ulong FirstCycleId,
    DateTime AcceptedAt,
    string AcceptedBy,
    long ConfigRevisionId,
    long? OperatorActionId);

/// <summary>Épocas del PLC. El orden entre épocas lo da AcceptedAt, no el valor numérico.</summary>
public sealed class PlcEpochStore(SqlDb db)
{
    public const string Initial = "INITIAL";
    public const string AutoIncrementByOne = "AUTO_INCREMENT_BY_ONE";
    public const string OperatorConfirmed = "OPERATOR_CONFIRMED";

    public Task InsertAsync(PlcEpoch epoch, CancellationToken ct) =>
        db.InTransactionAsync((conn, tx) => InsertAsync(conn, tx, epoch, ct), ct);

    public static async Task InsertAsync(SqlConnection conn, SqlTransaction tx, PlcEpoch e, CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, """
            INSERT INTO dbo.PlcEpochHistory (SiteId, StationId, PlcEpoch, EpochSource, AcceptanceMode, FirstCycleId, AcceptedAt,
                AcceptedBy, ConfigRevisionId, OperatorActionId)
            VALUES (@site, @station, @epoch, @source, @mode, @first, @at, @by, @rev, @oa);
            """);
        cmd.NVarChar("@site", e.SiteId, 64).NVarChar("@station", e.StationId, 64).BigInt("@epoch", e.Epoch)
            .VarChar("@source", e.EpochSource, 10).VarChar("@mode", e.AcceptanceMode, 30).Cycle("@first", e.FirstCycleId)
            .DateTime2("@at", e.AcceptedAt).NVarChar("@by", e.AcceptedBy, 128).BigInt("@rev", e.ConfigRevisionId).BigInt("@oa", e.OperatorActionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Épocas de la estación en orden de aceptación.</summary>
    public Task<IReadOnlyList<PlcEpoch>> ListAsync(string siteId, string stationId, CancellationToken ct) =>
        db.ReadAsync<IReadOnlyList<PlcEpoch>>(async conn =>
        {
            await using var cmd = conn.Command(null, """
                SELECT SiteId, StationId, PlcEpoch, EpochSource, AcceptanceMode, FirstCycleId, AcceptedAt, AcceptedBy, ConfigRevisionId, OperatorActionId
                FROM dbo.PlcEpochHistory WHERE SiteId = @site AND StationId = @station ORDER BY AcceptedAt
                """);
            cmd.NVarChar("@site", siteId, 64).NVarChar("@station", stationId, 64);
            var list = new List<PlcEpoch>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                list.Add(new PlcEpoch(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetCycleId(5),
                    r.GetUtc(6), r.GetString(7), r.GetInt64(8), r.GetNullableInt64(9)));
            }

            return list;
        }, ct);
}

/// <summary>Snapshot ERP de una orden (dbo.ErpWorkOrderCache).</summary>
public sealed record ErpSnapshot(
    string WorkOrderId,
    string Revision,
    string ProductId,
    string LotId,
    string? Gtin,
    DateTime ValidFrom,
    DateTime ValidUntil,
    DateTime ReceivedAt,
    byte[] SourceHash)
{
    public bool IsValidAt(DateTime edgeNowUtc) => ValidFrom <= edgeNowUtc && edgeNowUtc < ValidUntil;
}

/// <summary>Caché local de snapshots ERP: nunca se consulta al ERP por caja.</summary>
public sealed class ErpCacheStore(SqlDb db)
{
    /// <summary>Inserta una revisión nueva del snapshot (idempotente por (WorkOrderId, Revision)).</summary>
    public Task<bool> UpsertAsync(ErpSnapshot s, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            await using var cmd = conn.Command(tx, """
                DECLARE @ins TABLE (WorkOrderId NVARCHAR(128) NOT NULL);
                INSERT INTO dbo.ErpWorkOrderCache (WorkOrderId, Revision, ProductId, LotId, Gtin, ValidFrom, ValidUntil, ReceivedAt, SourceHash)
                OUTPUT inserted.WorkOrderId INTO @ins (WorkOrderId)
                SELECT @wo, @rev, @product, @lot, @gtin, @from, @until, @received, @hash
                WHERE NOT EXISTS (SELECT 1 FROM dbo.ErpWorkOrderCache WITH (UPDLOCK, HOLDLOCK) WHERE WorkOrderId = @wo AND Revision = @rev);
                SELECT COUNT(*) FROM @ins;
                """);
            cmd.NVarChar("@wo", s.WorkOrderId, 128).NVarChar("@rev", s.Revision, 64).NVarChar("@product", s.ProductId, 128)
                .NVarChar("@lot", s.LotId, 128);
            cmd.Parameters.Add(new SqlParameter("@gtin", System.Data.SqlDbType.Char, 14) { Value = (object?)s.Gtin ?? DBNull.Value });
            cmd.DateTime2("@from", s.ValidFrom).DateTime2("@until", s.ValidUntil).DateTime2("@received", s.ReceivedAt).Binary32("@hash", s.SourceHash);
            return (int)(await cmd.ExecuteScalarAsync(ct))! == 1;
        }, ct);

    /// <summary>La fila guardada de una revisión concreta (válida o no).</summary>
    public Task<ErpSnapshot?> GetRevisionAsync(string workOrderId, string revision, CancellationToken ct) =>
        db.ReadAsync(async conn =>
        {
            await using var cmd = conn.Command(null, """
                SELECT WorkOrderId, Revision, ProductId, LotId, Gtin, ValidFrom, ValidUntil, ReceivedAt, SourceHash
                FROM dbo.ErpWorkOrderCache
                WHERE WorkOrderId = @wo AND Revision = @rev
                """);
            cmd.NVarChar("@wo", workOrderId, 128).NVarChar("@rev", revision, 64);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct)
                ? new ErpSnapshot(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetNullableString(4),
                    r.GetUtc(5), r.GetUtc(6), r.GetUtc(7), r.GetBytes32(8))
                : null;
        }, ct);

    /// <summary>El snapshot válido más reciente para la orden en el instante del EdgeHost.</summary>
    public Task<ErpSnapshot?> FindValidAsync(string workOrderId, DateTime edgeNowUtc, CancellationToken ct) =>
        db.ReadAsync(conn => FindValidAsync(conn, null, workOrderId, edgeNowUtc, ct), ct);

    public static async Task<ErpSnapshot?> FindValidAsync(SqlConnection conn, SqlTransaction? tx, string workOrderId, DateTime edgeNowUtc, CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, """
            SELECT TOP (1) WorkOrderId, Revision, ProductId, LotId, Gtin, ValidFrom, ValidUntil, ReceivedAt, SourceHash
            FROM dbo.ErpWorkOrderCache
            WHERE WorkOrderId = @wo AND ValidFrom <= @now AND ValidUntil > @now
            ORDER BY ReceivedAt DESC, ValidFrom DESC
            """);
        cmd.NVarChar("@wo", workOrderId, 128).DateTime2("@now", edgeNowUtc);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new ErpSnapshot(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetNullableString(4),
                r.GetUtc(5), r.GetUtc(6), r.GetUtc(7), r.GetBytes32(8))
            : null;
    }

    /// <summary>
    /// Comprueba dentro de una transacción que una revisión concreta sigue siendo válida. Para TX-A, que arma el job
    /// con la fila que acaba de leer de la caché (las filas solo se insertan, nunca cambian).
    /// </summary>
    public static async Task<bool> IsRevisionValidAsync(SqlConnection conn, SqlTransaction tx, string workOrderId, string revision, DateTime edgeNowUtc, CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, """
            SELECT COUNT(*) FROM dbo.ErpWorkOrderCache
            WHERE WorkOrderId = @wo AND Revision = @rev AND ValidFrom <= @now AND ValidUntil > @now
            """);
        cmd.NVarChar("@wo", workOrderId, 128).NVarChar("@rev", revision, 64).DateTime2("@now", edgeNowUtc);
        return (int)(await cmd.ExecuteScalarAsync(ct))! == 1;
    }

    /// <summary>
    /// Comprueba dentro de una transacción que una revisión concreta sigue siendo válida y que su fila tiene el
    /// contenido con que se armó la etiqueta (producto, lote y GTIN): el job referencia esa fila (FK_PrintJob_Erp) y
    /// no puede llevar otro contenido (I10).
    /// </summary>
    public static async Task<bool> IsRevisionValidAsync(
        SqlConnection conn, SqlTransaction tx, string workOrderId, string revision, string productId, string lotId, string? gtin, DateTime edgeNowUtc,
        CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, """
            SELECT COUNT(*) FROM dbo.ErpWorkOrderCache
            WHERE WorkOrderId = @wo AND Revision = @rev AND ValidFrom <= @now AND ValidUntil > @now
              AND ProductId = @product AND LotId = @lot AND ISNULL(Gtin, '') = ISNULL(@gtin, '')
            """);
        cmd.NVarChar("@wo", workOrderId, 128).NVarChar("@rev", revision, 64).DateTime2("@now", edgeNowUtc)
            .NVarChar("@product", productId, 128).NVarChar("@lot", lotId, 128);
        cmd.Parameters.Add(new SqlParameter("@gtin", System.Data.SqlDbType.Char, 14) { Value = (object?)gtin ?? DBNull.Value });
        return (int)(await cmd.ExecuteScalarAsync(ct))! == 1;
    }
}

/// <summary>Orden de envío por impresora de línea (dbo.PrinterSendOrder).</summary>
public sealed record PrinterSendOrderRow(string PrinterId, string SiteId, string StationId, long? LastPlcEpoch, DateTime? LastEpochAcceptedAt, ulong? LastCycleId, Guid? LastPrintJobId);

public sealed class PrinterSendOrderStore(SqlDb db)
{
    /// <summary>Al registrar una impresora de línea se inserta su fila con los campos Last* en NULL.</summary>
    public Task<bool> RegisterAsync(string printerId, string siteId, string stationId, DateTime edgeNowUtc, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            await using var cmd = conn.Command(tx, """
                IF EXISTS (SELECT 1 FROM dbo.PrinterSendOrder WITH (UPDLOCK, HOLDLOCK) WHERE PrinterId = @p) SELECT 0;
                ELSE BEGIN
                    INSERT INTO dbo.PrinterSendOrder (PrinterId, SiteId, StationId, UpdatedAt) VALUES (@p, @site, @station, @now);
                    SELECT 1;
                END
                """);
            cmd.NVarChar("@p", printerId, 64).NVarChar("@site", siteId, 64).NVarChar("@station", stationId, 64).DateTime2("@now", edgeNowUtc);
            return (int)(await cmd.ExecuteScalarAsync(ct))! == 1;
        }, ct);

    public Task<PrinterSendOrderRow?> GetAsync(string printerId, CancellationToken ct) =>
        db.ReadAsync(async conn =>
        {
            await using var cmd = conn.Command(null, """
                SELECT PrinterId, SiteId, StationId, LastPlcEpoch, LastEpochAcceptedAt, LastCycleId, LastPrintJobId
                FROM dbo.PrinterSendOrder WHERE PrinterId = @p
                """);
            cmd.NVarChar("@p", printerId, 64);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct)
                ? new PrinterSendOrderRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetNullableInt64(3), r.GetNullableUtc(4),
                    r.GetNullableCycleId(5), r.GetNullableGuid(6))
                : null;
        }, ct);
}

/// <summary>Resultado de adquirir o renovar el lease de una impresora.</summary>
public sealed record LeaseGrant(string PrinterId, string OwnerInstanceId, long FencingToken, DateTime LeaseUntil);

/// <summary>
/// PrinterLease: coordina reclamos lógicos y detecta una segunda instancia. No impide que un
/// proceso que ya validó su token transmita: la impresora no valida tokens (ADR 0010).
/// </summary>
public sealed class PrinterLeaseStore(SqlDb db)
{
    /// <summary>
    /// Adquiere o renueva. Sentencia de referencia: PrinterLease no tiene triggers, OUTPUT sin INTO
    /// es válido aquí. Devuelve null si otra instancia tiene el lease vigente (más la gracia).
    /// </summary>
    public Task<LeaseGrant?> AcquireAsync(string printerId, string ownerInstanceId, string ownerHost, int leaseDurationMs, int leaseGraceMs,
        DateTime edgeNowUtc, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            await using (var ensure = conn.Command(tx, """
                IF NOT EXISTS (SELECT 1 FROM dbo.PrinterLease WITH (UPDLOCK, HOLDLOCK) WHERE PrinterId = @p)
                    INSERT INTO dbo.PrinterLease (PrinterId, OwnerInstanceId, OwnerHost, FencingToken, AcquiredAt, LeaseUntil)
                    VALUES (@p, N'(nadie)', N'(nadie)', 1, DATEADD(MILLISECOND, -2, @now), DATEADD(MILLISECOND, -1, @now));
                """))
            {
                ensure.NVarChar("@p", printerId, 64).DateTime2("@now", edgeNowUtc.AddMilliseconds(-(leaseGraceMs + 1000)));
                await ensure.ExecuteNonQueryAsync(ct);
            }

            await using var cmd = conn.Command(tx, """
                UPDATE dbo.PrinterLease
                SET FencingToken = CASE WHEN OwnerInstanceId = @owner THEN FencingToken ELSE FencingToken + 1 END,
                    OwnerInstanceId = @owner, OwnerHost = @host,
                    AcquiredAt = @now, LeaseUntil = DATEADD(MILLISECOND, @duration, @now)
                OUTPUT inserted.FencingToken, inserted.LeaseUntil
                WHERE PrinterId = @p
                  AND (OwnerInstanceId = @owner OR LeaseUntil < DATEADD(MILLISECOND, -@grace, @now));
                """);
            cmd.NVarChar("@owner", ownerInstanceId, 128).NVarChar("@host", ownerHost, 128).DateTime2("@now", edgeNowUtc)
                .Int("@duration", leaseDurationMs).Int("@grace", leaseGraceMs).NVarChar("@p", printerId, 64);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct) ? new LeaseGrant(printerId, ownerInstanceId, r.GetInt64(0), r.GetUtc(1)) : null;
        }, ct);

    /// <summary>
    /// Libera el lease del dueño con ese token: lo deja vencido más allá de la gracia, para que otra instancia lo tome en
    /// cuanto lo pida (con el token siguiente). Si ya no es suyo, no cambia nada.
    /// </summary>
    public Task<bool> ReleaseAsync(string printerId, string ownerInstanceId, long fencingToken, int leaseGraceMs, DateTime edgeNowUtc, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            await using var cmd = conn.Command(tx, """
                UPDATE dbo.PrinterLease
                SET AcquiredAt = DATEADD(MILLISECOND, -(@grace + 2000), @now), LeaseUntil = DATEADD(MILLISECOND, -(@grace + 1000), @now)
                OUTPUT inserted.PrinterId
                WHERE PrinterId = @p AND OwnerInstanceId = @owner AND FencingToken = @token;
                """);
            cmd.Int("@grace", leaseGraceMs).DateTime2("@now", edgeNowUtc).NVarChar("@p", printerId, 64).NVarChar("@owner", ownerInstanceId, 128)
                .BigInt("@token", fencingToken);
            return await cmd.ExecuteScalarAsync(ct) is not null;
        }, ct);
}

/// <summary>Seriales de LabelSerialSeq, siempre antes de TX-A y fuera de la transacción (H3, ADR 0008).</summary>
public sealed class LabelSerialSource(SqlDb db)
{
    public Task<long> NextAsync(CancellationToken ct) =>
        db.ReadAsync(async conn =>
        {
            await using var cmd = conn.Command(null, "SELECT NEXT VALUE FOR dbo.LabelSerialSeq;");
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        }, ct);

    /// <summary>Prefijo de estación + 12 dígitos: <c>P01L01-000000531201</c>.</summary>
    public static string Format(string stationPrefix, long serial) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{stationPrefix}-{serial:D12}");
}
