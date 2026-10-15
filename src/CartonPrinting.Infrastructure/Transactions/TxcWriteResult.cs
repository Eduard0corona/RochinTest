using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.Infrastructure.Transactions;

/// <summary>Causa de un intento NOT_SENT. Solo se usa si el sender nunca invocó la escritura (I02).</summary>
public enum NotSentCause
{
    /// <summary>Fallo de conexión antes de escribir: consume intento automático.</summary>
    ConnectionFailed,

    /// <summary>La segunda comprobación rechazó por compuerta o estación bloqueada con el plazo vigente: no consume intento.</summary>
    GateOrStation,

    /// <summary>Plazo efectivo vencido (incluida la segunda comprobación): J08.</summary>
    DeadlinePassed,
}

public sealed record WriteOutcome(string AttemptOutcome, string? JobStatus, string? JobReason, PrintJobResultEvent? Published);

/// <summary>
/// TX-C: resultado de la escritura (A02 a A04, J05 a J09 y X03). Es una transacción nueva después
/// del I/O: ninguna transacción queda abierta mientras se escribe (I20).
/// </summary>
public sealed class TxcWriteResult(SqlDb db)
{
    /// <summary>A03 y J05. Con nivel NONE el job es terminal en TRANSMITTED y se publica con ResultVersion + 1.</summary>
    public Task<WriteOutcome> TransmittedAsync(
        Guid attemptId, DateTime writeStartedAt, DateTime writeCompletedAt, DateTime preSendCheckAt, DateTime? deviceAcceptedAt,
        DateTime? evidenceDeadlineAt, bool levelIsNone, DateTime edgeNowUtc, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var attempt = await UpdateAttemptAsync(conn, tx, """
                UPDATE dbo.PrintAttempt
                SET Outcome = 'TRANSMITTED', WriteStartedAt = @WriteStartedAt, WriteCompletedAt = @WriteCompletedAt,
                    PreSendCheckAt = @PreSendCheckAt, DeviceAcceptedAt = @DeviceAcceptedAt, EvidenceDeadlineAt = @EvidenceDeadlineAt
                OUTPUT inserted.PrintJobId, inserted.Kind INTO @a (PrintJobId, Kind)
                WHERE AttemptId = @AttemptId AND Outcome = 'SENDING';
                """, c => c.DateTime2("@WriteStartedAt", writeStartedAt).DateTime2("@WriteCompletedAt", writeCompletedAt)
                    .DateTime2("@PreSendCheckAt", preSendCheckAt).DateTime2("@DeviceAcceptedAt", deviceAcceptedAt)
                    .DateTime2("@EvidenceDeadlineAt", evidenceDeadlineAt).Guid("@AttemptId", attemptId), ct);
            if (attempt is null)
            {
                return new WriteOutcome(AttemptOutcome.Sending, null, null, null);
            }

            if (attempt.Value.Kind == AttemptKind.ExceptionalReprint)
            {
                // Mientras dura el intento excepcional el job conserva su estado.
                return new WriteOutcome(AttemptOutcome.Transmitted, null, null, null);
            }

            var moved = await UpdateJobAsync(conn, tx, """
                UPDATE dbo.PrintJob
                SET Status = 'TRANSMITTED', ResultVersion = ResultVersion + @PublishIncrement, UpdatedAt = @EdgeNowUtc
                OUTPUT inserted.PrintJobId INTO @j (PrintJobId)
                WHERE PrintJobId = @PrintJobId AND Status = 'SENDING';
                """, c => c.Int("@PublishIncrement", levelIsNone ? 1 : 0).DateTime2("@EdgeNowUtc", edgeNowUtc)
                    .Guid("@PrintJobId", attempt.Value.JobId), ct);
            if (!moved)
            {
                throw new InvalidOperationException($"El intento {attemptId} pasó a TRANSMITTED pero su job no estaba en SENDING.");
            }

            var published = levelIsNone
                ? await ResultPublisher.PublishJobAsync(conn, tx, attempt.Value.JobId, edgeNowUtc, new ResultExtras { AttemptId = attemptId }, ct)
                : null;
            return new WriteOutcome(AttemptOutcome.Transmitted, JobStatus.Transmitted, null, published);
        }, ct);

    /// <summary>
    /// A02 y, para un intento ordinario, J06, J07 o J08. Regla de código: el sender sabe que no
    /// llamó a write; SQL no puede comprobarlo. Un intento excepcional NOT_SENT no cambia el job.
    /// </summary>
    public Task<WriteOutcome> NotSentAsync(
        Guid attemptId, DateTime? preSendCheckAt, string failureDetail, NotSentCause cause, int retryBackoffMs, byte effectiveMaxAutoAttempts,
        DateTime edgeNowUtc, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var attempt = await UpdateAttemptAsync(conn, tx, """
                UPDATE dbo.PrintAttempt
                SET Outcome = 'NOT_SENT', MayHaveSent = 0, PreSendCheckAt = @PreSendCheckAt, FailureDetail = @FailureDetail
                OUTPUT inserted.PrintJobId, inserted.Kind INTO @a (PrintJobId, Kind)
                WHERE AttemptId = @AttemptId AND Outcome = 'SENDING';
                """, c => c.DateTime2("@PreSendCheckAt", preSendCheckAt).NVarChar("@FailureDetail", Truncate(failureDetail), 512)
                    .Guid("@AttemptId", attemptId), ct);
            if (attempt is null)
            {
                return new WriteOutcome(AttemptOutcome.Sending, null, null, null);
            }

            if (attempt.Value.Kind == AttemptKind.ExceptionalReprint)
            {
                return new WriteOutcome(AttemptOutcome.NotSent, null, null, null);
            }

            var jobId = attempt.Value.JobId;
            if (cause != NotSentCause.DeadlinePassed)
            {
                // J06: con plazo vigente. Un rechazo por compuerta o estación no consume intento.
                var retried = await UpdateJobAsync(conn, tx, """
                    UPDATE dbo.PrintJob
                    SET Status = 'RETRY_WAIT', AutoAttemptCount = AutoAttemptCount - @RestoreAttempt,
                        NextAttemptAt = DATEADD(MILLISECOND, @RetryBackoffMs, @EdgeNowUtc), UpdatedAt = @EdgeNowUtc
                    OUTPUT inserted.PrintJobId INTO @j (PrintJobId)
                    WHERE PrintJobId = @PrintJobId AND Status = 'SENDING'
                      AND (@RestoreAttempt = 1 OR AutoAttemptCount < @EffectiveMaxAutoAttempts);
                    """, c => c.TinyInt("@RestoreAttempt", (byte)(cause == NotSentCause.GateOrStation ? 1 : 0)).Int("@RetryBackoffMs", retryBackoffMs)
                        .DateTime2("@EdgeNowUtc", edgeNowUtc).Guid("@PrintJobId", jobId).TinyInt("@EffectiveMaxAutoAttempts", effectiveMaxAutoAttempts), ct);
                if (retried)
                {
                    return new WriteOutcome(AttemptOutcome.NotSent, JobStatus.RetryWait, null, null);
                }

                // J07: intentos agotados con el plazo vigente.
                await RequireJobAsync(conn, tx, """
                    UPDATE dbo.PrintJob
                    SET Status = 'FAILED', Reason = 'PRINTER_UNAVAILABLE', NextAttemptAt = NULL,
                        ResultVersion = ResultVersion + 1, UpdatedAt = @EdgeNowUtc
                    OUTPUT inserted.PrintJobId INTO @j (PrintJobId)
                    WHERE PrintJobId = @PrintJobId AND Status = 'SENDING';
                    """, c => c.DateTime2("@EdgeNowUtc", edgeNowUtc).Guid("@PrintJobId", jobId), ct);
                var failed = await ResultPublisher.PublishJobAsync(conn, tx, jobId, edgeNowUtc, ResultExtras.None, ct);
                return new WriteOutcome(AttemptOutcome.NotSent, JobStatus.Failed, JobReason.PrinterUnavailable, failed);
            }

            // J08: plazo vencido.
            await RequireJobAsync(conn, tx, """
                UPDATE dbo.PrintJob
                SET Status = 'EXPIRED', Reason = 'DEADLINE_PASSED', ResultVersion = ResultVersion + 1, UpdatedAt = @EdgeNowUtc
                OUTPUT inserted.PrintJobId INTO @j (PrintJobId)
                WHERE PrintJobId = @PrintJobId AND Status = 'SENDING';
                """, c => c.DateTime2("@EdgeNowUtc", edgeNowUtc).Guid("@PrintJobId", jobId), ct);
            var expired = await ResultPublisher.PublishJobAsync(conn, tx, jobId, edgeNowUtc, ResultExtras.None, ct);
            return new WriteOutcome(AttemptOutcome.NotSent, JobStatus.Expired, JobReason.DeadlinePassed, expired);
        }, ct);

    /// <summary>
    /// Error o timeout después de invocar la escritura (A04): J09 con WRITE_INTERRUPTED para un intento
    /// ordinario; X03 en la misma transacción para un intento excepcional.
    /// </summary>
    public Task<WriteOutcome> UnknownAsync(Guid attemptId, DateTime? writeStartedAt, string failureDetail, DateTime edgeNowUtc, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var attempt = await UpdateAttemptAsync(conn, tx, """
                UPDATE dbo.PrintAttempt
                SET Outcome = 'UNKNOWN', WriteStartedAt = @WriteStartedAt, FailureDetail = @FailureDetail
                OUTPUT inserted.PrintJobId, inserted.Kind INTO @a (PrintJobId, Kind)
                WHERE AttemptId = @AttemptId AND Outcome = 'SENDING';
                """, c => c.DateTime2("@WriteStartedAt", writeStartedAt).NVarChar("@FailureDetail", Truncate(failureDetail), 512)
                    .Guid("@AttemptId", attemptId), ct);
            if (attempt is null)
            {
                return new WriteOutcome(AttemptOutcome.Sending, null, null, null);
            }

            if (attempt.Value.Kind == AttemptKind.ExceptionalReprint)
            {
                var x03 = await ExceptionalUnresolvedAsync(conn, tx, attempt.Value.JobId, attemptId, edgeNowUtc, ct);
                return new WriteOutcome(AttemptOutcome.Unknown, JobStatus.Unknown, JobReason.ExceptionalReprintUnresolved, x03);
            }

            await RequireJobAsync(conn, tx, """
                UPDATE dbo.PrintJob
                SET Status = 'UNKNOWN', Reason = 'WRITE_INTERRUPTED', ResultVersion = ResultVersion + 1, UpdatedAt = @EdgeNowUtc
                OUTPUT inserted.PrintJobId INTO @j (PrintJobId)
                WHERE PrintJobId = @PrintJobId AND Status = 'SENDING';
                """, c => c.DateTime2("@EdgeNowUtc", edgeNowUtc).Guid("@PrintJobId", attempt.Value.JobId), ct);
            var published = await ResultPublisher.PublishJobAsync(conn, tx, attempt.Value.JobId, edgeNowUtc, new ResultExtras { AttemptId = attemptId }, ct);
            return new WriteOutcome(AttemptOutcome.Unknown, JobStatus.Unknown, JobReason.WriteInterrupted, published);
        }, ct);

    /// <summary>
    /// X03: el job queda (o sigue) en UNKNOWN con EXCEPTIONAL_REPRINT_UNRESOLVED, en la misma
    /// transacción que deja el intento excepcional en UNKNOWN. Fija SESSION_CONTEXT EXCEPTION y lo
    /// limpia también si falla.
    /// </summary>
    public static async Task<PrintJobResultEvent> ExceptionalUnresolvedAsync(
        SqlConnection conn, SqlTransaction tx, Guid printJobId, Guid attemptId, DateTime edgeNowUtc, CancellationToken ct)
    {
        await RunWithTransitionKindAsync(conn, tx, "EXCEPTION", """
            DECLARE @j TABLE (PrintJobId UNIQUEIDENTIFIER NOT NULL);
            UPDATE dbo.PrintJob
            SET Status = 'UNKNOWN', Reason = 'EXCEPTIONAL_REPRINT_UNRESOLVED', ConfirmedByExceptionalReprint = 0,
                LastExceptionalAttemptId = @AttemptId, ResultVersion = ResultVersion + 1, UpdatedAt = @EdgeNowUtc
            OUTPUT inserted.PrintJobId INTO @j (PrintJobId)
            WHERE PrintJobId = @PrintJobId;
            IF NOT EXISTS (SELECT 1 FROM @j) THROW 50201, N'X03 sin job.', 1;
            """, c => c.Guid("@AttemptId", attemptId).DateTime2("@EdgeNowUtc", edgeNowUtc).Guid("@PrintJobId", printJobId), ct);
        var authorizationId = await AuthorizationOfAsync(conn, tx, attemptId, ct);
        return await ResultPublisher.PublishJobAsync(conn, tx, printJobId, edgeNowUtc,
            new ResultExtras { AttemptId = attemptId, AuthorizationId = authorizationId }, ct);
    }

    /// <summary>
    /// Ejecuta un lote que fija SESSION_CONTEXT TransitionKind y lo limpia al terminar y en el CATCH.
    /// </summary>
    public static async Task RunWithTransitionKindAsync(SqlConnection conn, SqlTransaction tx, string kind, string body, Action<SqlCommand> bind, CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, $"""
            BEGIN TRY
                EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = @TransitionKind;
                {body}
                EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = NULL;
            END TRY
            BEGIN CATCH
                EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = NULL;
                THROW;
            END CATCH;
            """);
        cmd.VarChar("@TransitionKind", kind, 12);
        bind(cmd);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<long?> AuthorizationOfAsync(SqlConnection conn, SqlTransaction tx, Guid attemptId, CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, "SELECT AuthorizationId FROM dbo.PrintAttempt WHERE AttemptId = @id");
        cmd.Guid("@id", attemptId);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is long l ? l : null;
    }

    internal static async Task<(Guid JobId, string Kind)?> UpdateAttemptAsync(
        SqlConnection conn, SqlTransaction tx, string update, Action<SqlCommand> bind, CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, $"""
            DECLARE @a TABLE (PrintJobId UNIQUEIDENTIFIER NOT NULL, Kind VARCHAR(20) NOT NULL);
            {update}
            SELECT PrintJobId, Kind FROM @a;
            """);
        bind(cmd);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetGuid(0), r.GetString(1)) : null;
    }

    internal static async Task<bool> UpdateJobAsync(SqlConnection conn, SqlTransaction tx, string update, Action<SqlCommand> bind, CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, $"""
            DECLARE @j TABLE (PrintJobId UNIQUEIDENTIFIER NOT NULL);
            {update}
            SELECT COUNT(*) FROM @j;
            """);
        bind(cmd);
        return (int)(await cmd.ExecuteScalarAsync(ct))! > 0;
    }

    internal static async Task RequireJobAsync(SqlConnection conn, SqlTransaction tx, string update, Action<SqlCommand> bind, CancellationToken ct)
    {
        if (!await UpdateJobAsync(conn, tx, update, bind, ct))
        {
            throw new InvalidOperationException("La transición del job no afectó ninguna fila: el job no estaba en el estado esperado.");
        }
    }

    internal static string Truncate(string text) => text.Length <= 512 ? text : text[..512];
}
