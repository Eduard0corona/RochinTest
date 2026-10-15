using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.Infrastructure.Transactions;

/// <summary>Evidencia recibida (impresora, aplicador, escáner o inspección manual).</summary>
public sealed record ObservationInput(
    string SiteId,
    string StationId,
    long? PlcEpoch,
    ulong? CycleId,
    Guid? PrintJobId,
    Guid? AttemptId,
    string EvidenceType,
    string EvidenceLevel,
    string CorrelatedTo,
    string? ReadSerial,
    string SourceReference,
    DateTime ObservedAt,
    string RecordedBy);

public enum EvidenceAction
{
    /// <summary>Registrada sin transición: no corresponde a ningún job, nivel insuficiente o estado sin salida.</summary>
    Recorded,

    /// <summary>A05 y J10, o A08 y J15.</summary>
    Confirmed,

    /// <summary>A06 y J11 con SERIAL_MISMATCH.</summary>
    SerialMismatch,

    /// <summary>J18: corrección hacia UNKNOWN con SERIAL_MISMATCH.</summary>
    Corrected,

    /// <summary>A05 o A08 y X01 o X02.</summary>
    ExceptionalConfirmed,

    /// <summary>A06 y X03 por evidencia contradictoria de una reimpresión.</summary>
    ExceptionalUnresolved,
}

public sealed record EvidenceOutcome(EvidenceAction Action, Guid? PrintJobId, Guid? AttemptId, string? PreviousJobStatus, PrintJobResultEvent? Published);

/// <summary>
/// TX-D: toda evidencia se registra primero en EvidenceObservation, en su propia transacción,
/// confirme o no; después se aplica en una transacción nueva. TR_PrintAttempt_Guard exige nivel,
/// correlación, recepción posterior al claim y ReadSerial = LabelSerial aunque el código se equivoque.
/// </summary>
public sealed class TxdEvidence(SqlDb db)
{
    private static readonly HashSet<string> ReconcilableReasons =
        [JobReason.WriteInterrupted, JobReason.RestartDuringSend, JobReason.EvidenceWindowElapsed];

    /// <summary>
    /// Paso 1: registrar la observación (ReceivedAt = reloj del EdgeHost). Una observación de ciclo (APPLIED o VERIFIED) se
    /// inserta con el lock de evidencia de su ciclo (<see cref="LockCycleAsync"/>).
    /// </summary>
    public Task<long> RecordAsync(ObservationInput o, DateTime edgeNowUtc, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            if (o.CorrelatedTo == CorrelatedTo.Cycle && o.PlcEpoch is { } epoch && o.CycleId is { } cycle)
            {
                await LockCycleAsync(conn, tx, o.SiteId, o.StationId, epoch, cycle, "Exclusive", ct);
            }

            await using var cmd = conn.Command(tx, """
                DECLARE @observation TABLE (ObservationId BIGINT NOT NULL);
                INSERT INTO dbo.EvidenceObservation (
                    SiteId, StationId, PlcEpoch, CycleId, PrintJobId, AttemptId, EvidenceType, EvidenceLevel,
                    CorrelatedTo, ReadSerial, SourceReference, ObservedAt, ReceivedAt, RecordedBy)
                OUTPUT inserted.ObservationId INTO @observation (ObservationId)
                VALUES (
                    @SiteId, @StationId, @EvPlcEpoch, @EvCycleId, @EvPrintJobId, @EvAttemptId, @EvidenceType, @EvidenceLevel,
                    @CorrelatedTo, @ReadSerial, @SourceReference, @ObservedAt, @EdgeNowUtc, @RecordedBy);
                SELECT ObservationId FROM @observation;
                """);
            cmd.NVarChar("@SiteId", o.SiteId, 64).NVarChar("@StationId", o.StationId, 64).BigInt("@EvPlcEpoch", o.PlcEpoch).Cycle("@EvCycleId", o.CycleId)
                .Guid("@EvPrintJobId", o.PrintJobId).Guid("@EvAttemptId", o.AttemptId).VarChar("@EvidenceType", o.EvidenceType, 30)
                .VarChar("@EvidenceLevel", o.EvidenceLevel, 10).VarChar("@CorrelatedTo", o.CorrelatedTo, 10).VarChar("@ReadSerial", o.ReadSerial, 40)
                .NVarChar("@SourceReference", o.SourceReference, 256).DateTime2("@ObservedAt", o.ObservedAt).DateTime2("@EdgeNowUtc", edgeNowUtc)
                .NVarChar("@RecordedBy", o.RecordedBy, 128);
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        }, ct);

    /// <summary>Paso 2: aplicar la observación ya registrada.</summary>
    public Task<EvidenceOutcome> ApplyAsync(long observationId, DateTime edgeNowUtc, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var o = await JobQueries.GetObservationAsync(conn, tx, observationId, ct)
                ?? throw new InvalidOperationException($"EvidenceObservation {observationId} no existe.");

            // APPLIED y VERIFIED correlacionan por ciclo: el job es el de ese ciclo. Un PrintJobId que no
            // coincide con el job del ciclo hace la observación incoherente: queda registrada, sin efecto.
            // El lock compartido del ciclo se toma antes del UPDLOCK del job, en el mismo orden que el registro (lock
            // del ciclo, después las FK): al revés, un registro con AttemptId, TX-C y esta aplicación podían
            // interbloquearse (tercera revisión de la Fase 4, BAJA-1).
            JobRecord? job = null;
            if (o.CorrelatedTo == CorrelatedTo.Cycle && o.PlcEpoch is { } epoch && o.CycleId is { } cycle)
            {
                var found = await JobQueries.GetJobByCycleAsync(conn, tx, new CycleIdentity(o.SiteId, o.StationId, epoch, cycle), ct);
                if (found is not null && (o.PrintJobId is null || o.PrintJobId == found.PrintJobId))
                {
                    await LockCycleAsync(conn, tx, o.SiteId, o.StationId, epoch, cycle, "Shared", ct);
                    job = await JobQueries.GetJobAsync(conn, tx, found.PrintJobId, ct, updLock: true);
                }
            }
            else if (o.PrintJobId is { } jid && await JobQueries.GetJobAsync(conn, tx, jid, ct) is { } unlocked)
            {
                await LockCycleAsync(conn, tx, unlocked.SiteId, unlocked.StationId, unlocked.PlcEpoch, unlocked.CycleId, "Shared", ct);
                job = await JobQueries.GetJobAsync(conn, tx, jid, ct, updLock: true);
            }

            if (job is null)
            {
                return new EvidenceOutcome(EvidenceAction.Recorded, null, null, null, null);
            }

            var attempts = await JobQueries.GetAttemptsAsync(conn, tx, job.PrintJobId, ct);

            // Una observación que referencia un intento de otro job es incoherente: registrada, sin efecto.
            if (o.AttemptId is { } referenced && !attempts.Any(a => a.AttemptId == referenced))
            {
                return new EvidenceOutcome(EvidenceAction.Recorded, job.PrintJobId, null, job.Status, null);
            }

            var minimum = await FrozenMinimumLevelAsync(conn, tx, job.ConfigRevisionId, ct);

            // Una lectura VERIFIED del ciclo con otro serial contradice al job, haya o no intento ordinario.
            var contradicts = o.EvidenceLevel == EvidenceLevel.Verified
                && !string.Equals(o.ReadSerial, job.LabelSerial, StringComparison.Ordinal);
            if (contradicts)
            {
                return await ApplyContradictionAsync(conn, tx, o, job, attempts, minimum, edgeNowUtc, ct);
            }

            var attempt = o.AttemptId is { } aid
                ? attempts.SingleOrDefault(a => a.AttemptId == aid)
                : attempts.SingleOrDefault(a => a.Kind == AttemptKind.Ordinary && a.MayHaveSent);
            if (attempt is null)
            {
                return new EvidenceOutcome(EvidenceAction.Recorded, job.PrintJobId, null, job.Status, null);
            }

            if (!IsSufficient(o, job, attempt, minimum))
            {
                return new EvidenceOutcome(EvidenceAction.Recorded, job.PrintJobId, attempt.AttemptId, job.Status, null);
            }

            // Una lectura contradictoria anterior, todavía sin aplicar, va primero: esta observación no confirma (I06, J11).
            if (await EarlierContradictionAsync(conn, tx, job, attempt, attempts, observationId, ct) is { } earlier)
            {
                var contradiction = await ApplyContradictionAsync(conn, tx, earlier, job, attempts, minimum, edgeNowUtc, ct);
                return contradiction.Action == EvidenceAction.Recorded
                    ? new EvidenceOutcome(EvidenceAction.Recorded, job.PrintJobId, attempt.AttemptId, job.Status, null)
                    : contradiction;
            }

            if (attempt.Kind == AttemptKind.ExceptionalReprint)
            {
                var latest = attempts.Where(a => a.Kind == AttemptKind.ExceptionalReprint).MaxBy(a => a.AttemptNumber);
                if (latest?.AttemptId != attempt.AttemptId)
                {
                    return new EvidenceOutcome(EvidenceAction.Recorded, job.PrintJobId, attempt.AttemptId, job.Status, null);
                }

                var exceptional = await ConfirmExceptionalAsync(conn, tx, job.PrintJobId, attempt.AttemptId, observationId, edgeNowUtc, ct);
                return new EvidenceOutcome(EvidenceAction.ExceptionalConfirmed, job.PrintJobId, attempt.AttemptId, job.Status, exceptional);
            }

            var fromTransmitted = job.Status == JobStatus.Transmitted && attempt.Outcome == AttemptOutcome.Transmitted;
            var fromUnknown = job.Status == JobStatus.Unknown && attempt.Outcome == AttemptOutcome.Unknown
                && job.Reason is not null && ReconcilableReasons.Contains(job.Reason);
            if (!fromTransmitted && !fromUnknown)
            {
                return new EvidenceOutcome(EvidenceAction.Recorded, job.PrintJobId, attempt.AttemptId, job.Status, null);
            }

            // 2a. A05 o A08, y J10 o J15.
            await RequireAttemptAsync(conn, tx, """
                UPDATE dbo.PrintAttempt
                SET Outcome = 'CONFIRMED', ConfirmingObservationId = @ObservationId
                OUTPUT inserted.PrintJobId, inserted.Kind INTO @a (PrintJobId, Kind)
                WHERE AttemptId = @AttemptId AND Kind = 'ORDINARY' AND Outcome IN ('TRANSMITTED', 'UNKNOWN');
                """, c => c.BigInt("@ObservationId", observationId).Guid("@AttemptId", attempt.AttemptId), ct);
            await TxcWriteResult.RequireJobAsync(conn, tx, """
                UPDATE dbo.PrintJob
                SET Status = 'CONFIRMED', Reason = NULL, ResultVersion = ResultVersion + 1, UpdatedAt = @EdgeNowUtc
                OUTPUT inserted.PrintJobId INTO @j (PrintJobId)
                WHERE PrintJobId = @PrintJobId AND Status IN ('TRANSMITTED', 'UNKNOWN');
                """, c => c.DateTime2("@EdgeNowUtc", edgeNowUtc).Guid("@PrintJobId", job.PrintJobId), ct);
            var published = await ResultPublisher.PublishJobAsync(conn, tx, job.PrintJobId, edgeNowUtc,
                new ResultExtras { AttemptId = attempt.AttemptId, ObservationId = observationId }, ct);
            return new EvidenceOutcome(EvidenceAction.Confirmed, job.PrintJobId, attempt.AttemptId, job.Status, published);
        }, ct);

    /// <summary>
    /// A07 y J12: el dispositivo confirma, para este job, que lo canceló o descartó y que ya no está
    /// en su cola (OT-07). Un "no impreso" sin esa confirmación no llega aquí. Un intento excepcional
    /// NOT_PRINTED no cambia el job.
    /// </summary>
    public Task<EvidenceOutcome> DeviceNotPrintedAsync(Guid attemptId, string deviceReport, DateTime edgeNowUtc, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            // A07 y J12 exigen nivel congelado distinto de NONE: con NONE, TRANSMITTED es terminal.
            await using (var level = conn.Command(tx, """
                SELECT pa.Kind, fr.MinConfirmationLevel
                FROM dbo.PrintAttempt AS pa
                INNER JOIN dbo.PrintJob AS pj ON pj.PrintJobId = pa.PrintJobId
                INNER JOIN dbo.StationConfigRevision AS fr ON fr.ConfigRevisionId = pj.ConfigRevisionId
                WHERE pa.AttemptId = @AttemptId
                """))
            {
                level.Guid("@AttemptId", attemptId);
                await using var r = await level.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct) || (r.GetString(0) == AttemptKind.Ordinary && r.GetString(1) == EvidenceLevel.None))
                {
                    return new EvidenceOutcome(EvidenceAction.Recorded, null, attemptId, null, null);
                }
            }

            var attempt = await TxcWriteResult.UpdateAttemptAsync(conn, tx, """
                UPDATE dbo.PrintAttempt
                SET Outcome = 'NOT_PRINTED', FailureDetail = @DeviceReport
                OUTPUT inserted.PrintJobId, inserted.Kind INTO @a (PrintJobId, Kind)
                WHERE AttemptId = @AttemptId AND Outcome = 'TRANSMITTED';
                """, c => c.NVarChar("@DeviceReport", TxcWriteResult.Truncate(deviceReport), 512).Guid("@AttemptId", attemptId), ct);
            if (attempt is null)
            {
                return new EvidenceOutcome(EvidenceAction.Recorded, null, attemptId, null, null);
            }

            if (attempt.Value.Kind == AttemptKind.ExceptionalReprint)
            {
                return new EvidenceOutcome(EvidenceAction.Recorded, attempt.Value.JobId, attemptId, null, null);
            }

            await TxcWriteResult.RequireJobAsync(conn, tx, """
                UPDATE dbo.PrintJob
                SET Status = 'FAILED', Reason = 'DEVICE_REPORTED_NOT_PRINTED', ResultVersion = ResultVersion + 1, UpdatedAt = @EdgeNowUtc
                OUTPUT inserted.PrintJobId INTO @j (PrintJobId)
                WHERE PrintJobId = @PrintJobId AND Status = 'TRANSMITTED';
                """, c => c.DateTime2("@EdgeNowUtc", edgeNowUtc).Guid("@PrintJobId", attempt.Value.JobId), ct);
            var published = await ResultPublisher.PublishJobAsync(conn, tx, attempt.Value.JobId, edgeNowUtc, new ResultExtras { AttemptId = attemptId }, ct);
            return new EvidenceOutcome(EvidenceAction.Recorded, attempt.Value.JobId, attemptId, JobStatus.Transmitted, published);
        }, ct);

    private static async Task<EvidenceOutcome> ApplyContradictionAsync(
        SqlConnection conn, SqlTransaction tx, ObservationRecord o, JobRecord job, IReadOnlyList<AttemptRecord> attempts, string frozenMinimum,
        DateTime edgeNowUtc, CancellationToken ct)
    {
        // Lectura contradictoria que referencia la reimpresión en curso (la más reciente): A06 y X03 juntos.
        var exceptional = o.AttemptId is { } aid
            ? attempts.SingleOrDefault(a => a.AttemptId == aid && a.Kind == AttemptKind.ExceptionalReprint)
            : null;
        var latestExceptional = attempts.Where(a => a.Kind == AttemptKind.ExceptionalReprint).MaxBy(a => a.AttemptNumber);
        if (exceptional is not null && exceptional.Outcome == AttemptOutcome.Transmitted
            && latestExceptional?.AttemptId == exceptional.AttemptId && o.ReceivedAt >= exceptional.ClaimedAt)
        {
            await RequireAttemptAsync(conn, tx, """
                UPDATE dbo.PrintAttempt SET Outcome = 'UNKNOWN', FailureDetail = N'Evidencia contradictoria'
                OUTPUT inserted.PrintJobId, inserted.Kind INTO @a (PrintJobId, Kind)
                WHERE AttemptId = @AttemptId AND Outcome = 'TRANSMITTED';
                """, c => c.Guid("@AttemptId", exceptional.AttemptId), ct);
            var x03 = await TxcWriteResult.ExceptionalUnresolvedAsync(conn, tx, job.PrintJobId, exceptional.AttemptId, edgeNowUtc, ct);
            return new EvidenceOutcome(EvidenceAction.ExceptionalUnresolved, job.PrintJobId, exceptional.AttemptId, job.Status, x03);
        }

        // 2b. Serial distinto con el job en TRANSMITTED: A06 y J11 con SERIAL_MISMATCH. Con nivel congelado
        // NONE, TRANSMITTED es terminal: la lectura queda registrada sin transición (guardas de A06 y J11).
        var ordinary = attempts.SingleOrDefault(a => a.Kind == AttemptKind.Ordinary && a.MayHaveSent);
        if (job.Status == JobStatus.Transmitted && frozenMinimum != EvidenceLevel.None
            && ordinary is { Outcome: AttemptOutcome.Transmitted } && o.ReceivedAt >= ordinary.ClaimedAt)
        {
            await RequireAttemptAsync(conn, tx, """
                UPDATE dbo.PrintAttempt SET Outcome = 'UNKNOWN', FailureDetail = N'Evidencia contradictoria'
                OUTPUT inserted.PrintJobId, inserted.Kind INTO @a (PrintJobId, Kind)
                WHERE AttemptId = @AttemptId AND Outcome = 'TRANSMITTED';
                """, c => c.Guid("@AttemptId", ordinary.AttemptId), ct);
            await TxcWriteResult.RequireJobAsync(conn, tx, """
                UPDATE dbo.PrintJob
                SET Status = 'UNKNOWN', Reason = 'SERIAL_MISMATCH', ResultVersion = ResultVersion + 1, UpdatedAt = @EdgeNowUtc
                OUTPUT inserted.PrintJobId INTO @j (PrintJobId)
                WHERE PrintJobId = @PrintJobId AND Status = 'TRANSMITTED';
                """, c => c.DateTime2("@EdgeNowUtc", edgeNowUtc).Guid("@PrintJobId", job.PrintJobId), ct);
            var mismatch = await ResultPublisher.PublishJobAsync(conn, tx, job.PrintJobId, edgeNowUtc,
                new ResultExtras { AttemptId = ordinary.AttemptId, ObservationId = o.ObservationId }, ct);
            return new EvidenceOutcome(EvidenceAction.SerialMismatch, job.PrintJobId, ordinary.AttemptId, job.Status, mismatch);
        }

        // 2c. J18: el job ya confirmado (por un intento ordinario o por una reimpresión, X01) o en UNKNOWN
        // por otra causa. Un UNKNOWN que ya es SERIAL_MISMATCH no se vuelve a corregir. La lectura tiene que
        // ser posterior a la etiqueta vigente (ADR 0016, lo exige también el trigger): recibida en o después
        // del último claim y registrada después de cualquier observación que confirmó un intento del job.
        // Una lectura anterior queda registrada sin efecto.
        var lastClaim = attempts.Count == 0 ? (DateTime?)null : attempts.Max(a => a.ClaimedAt);
        var lastConfirming = attempts.Max(a => a.ConfirmingObservationId);
        var current = lastClaim is { } claimedAt && o.ReceivedAt >= claimedAt && (lastConfirming is null || o.ObservationId > lastConfirming);
        if (current && (job.Status == JobStatus.Confirmed || (job.Status == JobStatus.Unknown && job.Reason != JobReason.SerialMismatch)))
        {
            await TxcWriteResult.RunWithTransitionKindAsync(conn, tx, "CORRECTION", """
                DECLARE @j TABLE (PrintJobId UNIQUEIDENTIFIER NOT NULL);
                UPDATE dbo.PrintJob
                SET Status = 'UNKNOWN', Reason = 'SERIAL_MISMATCH', ConfirmedByExceptionalReprint = 0,
                    ResultVersion = ResultVersion + 1, UpdatedAt = @EdgeNowUtc
                OUTPUT inserted.PrintJobId INTO @j (PrintJobId)
                WHERE PrintJobId = @PrintJobId AND Status IN ('CONFIRMED', 'UNKNOWN');
                IF NOT EXISTS (SELECT 1 FROM @j) THROW 50202, N'J18 sin job en CONFIRMED o UNKNOWN.', 1;
                """, c => c.DateTime2("@EdgeNowUtc", edgeNowUtc).Guid("@PrintJobId", job.PrintJobId), ct);
            var published = await ResultPublisher.PublishJobAsync(conn, tx, job.PrintJobId, edgeNowUtc,
                new ResultExtras { ObservationId = o.ObservationId }, ct);
            return new EvidenceOutcome(EvidenceAction.Corrected, job.PrintJobId, published.AttemptId, job.Status, published);
        }

        return new EvidenceOutcome(EvidenceAction.Recorded, job.PrintJobId, null, job.Status, null);
    }

    /// <summary>Intento excepcional confirmado (A05 o A08) y X01 o X02 en una transacción.</summary>
    public static async Task<PrintJobResultEvent> ConfirmExceptionalAsync(
        SqlConnection conn, SqlTransaction tx, Guid printJobId, Guid attemptId, long observationId, DateTime edgeNowUtc, CancellationToken ct)
    {
        await RequireAttemptAsync(conn, tx, """
            UPDATE dbo.PrintAttempt
            SET Outcome = 'CONFIRMED', ConfirmingObservationId = @ObservationId
            OUTPUT inserted.PrintJobId, inserted.Kind INTO @a (PrintJobId, Kind)
            WHERE AttemptId = @AttemptId AND Kind = 'EXCEPTIONAL_REPRINT' AND Outcome IN ('TRANSMITTED', 'UNKNOWN');
            """, c => c.BigInt("@ObservationId", observationId).Guid("@AttemptId", attemptId), ct);
        await TxcWriteResult.RunWithTransitionKindAsync(conn, tx, "EXCEPTION", """
            DECLARE @j TABLE (PrintJobId UNIQUEIDENTIFIER NOT NULL);
            UPDATE dbo.PrintJob
            SET Status = 'CONFIRMED', Reason = NULL, ConfirmedByExceptionalReprint = 1,
                LastExceptionalAttemptId = @AttemptId, ResultVersion = ResultVersion + 1, UpdatedAt = @EdgeNowUtc
            OUTPUT inserted.PrintJobId INTO @j (PrintJobId)
            WHERE PrintJobId = @PrintJobId;
            IF NOT EXISTS (SELECT 1 FROM @j) THROW 50203, N'X01 o X02 sin job.', 1;
            """, c => c.Guid("@AttemptId", attemptId).DateTime2("@EdgeNowUtc", edgeNowUtc).Guid("@PrintJobId", printJobId), ct);
        var authorizationId = await TxcWriteResult.AuthorizationOfAsync(conn, tx, attemptId, ct);
        return await ResultPublisher.PublishJobAsync(conn, tx, printJobId, edgeNowUtc,
            new ResultExtras { AttemptId = attemptId, ObservationId = observationId, AuthorizationId = authorizationId }, ct);
    }

    /// <summary>
    /// Regla de nivel y correlación (la misma que exige TR_PrintAttempt_Guard): recibida después del
    /// claim; JOB del propio job o CYCLE del ciclo del job; un intento excepcional solo con una
    /// observación que lo referencia; nivel ≥ congelado (ordinario) o ≥ max(PRINTED, congelado).
    /// </summary>
    public static bool IsSufficient(ObservationRecord o, JobRecord job, AttemptRecord attempt, string frozenMinimumLevel)
    {
        if (attempt.Outcome is not (AttemptOutcome.Transmitted or AttemptOutcome.Unknown) || o.ReceivedAt < attempt.ClaimedAt)
        {
            return false;
        }

        if (attempt.Kind == AttemptKind.ExceptionalReprint && o.AttemptId != attempt.AttemptId)
        {
            return false;
        }

        var correlated = o.CorrelatedTo == CorrelatedTo.Job
            ? o.PrintJobId == job.PrintJobId && (o.AttemptId is null || o.AttemptId == attempt.AttemptId)
            : o.SiteId == job.SiteId && o.StationId == job.StationId && o.PlcEpoch == job.PlcEpoch && o.CycleId == job.CycleId;
        if (!correlated)
        {
            return false;
        }

        if (o.EvidenceLevel == EvidenceLevel.Verified && !string.Equals(o.ReadSerial, job.LabelSerial, StringComparison.Ordinal))
        {
            return false;
        }

        var required = attempt.Kind == AttemptKind.ExceptionalReprint
            ? Math.Max(EvidenceLevel.Rank(EvidenceLevel.Printed), EvidenceLevel.Rank(frozenMinimumLevel))
            : frozenMinimumLevel == EvidenceLevel.None ? int.MaxValue : EvidenceLevel.Rank(frozenMinimumLevel);
        return EvidenceLevel.Rank(o.EvidenceLevel) >= required;
    }

    /// <summary>
    /// Segunda revisión de la Fase 4, A-1: una lectura VERIFIED del ciclo con otro serial, recibida desde el claim del intento,
    /// registrada antes que la observación que se va a aplicar y después de cualquier confirmación del job. Si existe, todavía
    /// no se aplicó: llegó con el intento en SENDING (sin transición posible) y otro pase llegó antes a esta confirmación.
    /// Confirmar primero la dejaría sin efecto para siempre, porque ya no sería posterior a la etiqueta vigente (ADR 0016).
    /// La lectura cuenta con la misma coherencia que en <see cref="ApplyAsync"/>: sin intento o con cualquier intento del job
    /// (tercera revisión de la Fase 4, MEDIA-1); <see cref="ApplyContradictionAsync"/> decide qué transición aplica.
    /// </summary>
    private static async Task<ObservationRecord?> EarlierContradictionAsync(
        SqlConnection conn, SqlTransaction tx, JobRecord job, AttemptRecord attempt, IReadOnlyList<AttemptRecord> attempts, long observationId,
        CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, """
            SELECT TOP (1) ObservationId FROM dbo.EvidenceObservation
            WHERE SiteId = @SiteId AND StationId = @StationId AND PlcEpoch = @PlcEpoch AND CycleId = @CycleId
              AND EvidenceLevel = 'VERIFIED' AND ReadSerial <> @LabelSerial
              AND ReceivedAt >= @ClaimedAt AND ObservationId < @ObservationId AND ObservationId > @LastConfirming
              AND (PrintJobId IS NULL OR PrintJobId = @PrintJobId)
              AND (AttemptId IS NULL OR AttemptId IN (SELECT pa.AttemptId FROM dbo.PrintAttempt AS pa WHERE pa.PrintJobId = @PrintJobId))
            ORDER BY ObservationId;
            """);
        cmd.NVarChar("@SiteId", job.SiteId, 64).NVarChar("@StationId", job.StationId, 64).BigInt("@PlcEpoch", job.PlcEpoch).Cycle("@CycleId", job.CycleId)
            .VarChar("@LabelSerial", job.LabelSerial, 40).DateTime2("@ClaimedAt", attempt.ClaimedAt).BigInt("@ObservationId", observationId)
            .BigInt("@LastConfirming", attempts.Max(a => a.ConfirmingObservationId) ?? 0).Guid("@PrintJobId", job.PrintJobId);
        return await cmd.ExecuteScalarAsync(ct) is long id ? await JobQueries.GetObservationAsync(conn, tx, id, ct) : null;
    }

    /// <summary>
    /// Lock de evidencia del ciclo (segunda revisión de la Fase 4, B-8). ObservationId (IDENTITY) se asigna al insertar, no
    /// al confirmar: sin el lock, una lectura contradictoria con un ObservationId menor podía confirmarse después de que otra
    /// observación confirmara el job y quedar como "anterior" a la etiqueta vigente (ADR 0016). El registro de una observación
    /// de ciclo lo toma exclusivo y la aplicación de cualquier evidencia del job lo toma compartido, ambos hasta el commit:
    /// la aplicación espera a que el registro en curso termine y lo ve.
    /// </summary>
    private static async Task LockCycleAsync(
        SqlConnection conn, SqlTransaction tx, string siteId, string stationId, long epoch, ulong cycle, string mode, CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, """
            DECLARE @result INT;
            EXEC @result = sys.sp_getapplock @Resource = @Resource, @LockMode = @LockMode, @LockOwner = 'Transaction', @LockTimeout = 10000;
            IF @result = -3 THROW 50205, N'Víctima de interbloqueo en el lock de evidencia del ciclo.', 1;
            IF @result < 0 THROW 50204, N'No se obtuvo el lock de evidencia del ciclo.', 1;
            """);
        cmd.NVarChar("@Resource", CycleLockResource(siteId, stationId, epoch, cycle), 255).VarChar("@LockMode", mode, 32);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Nombre del lock de evidencia de un ciclo (sp_getapplock).</summary>
    public static string CycleLockResource(string siteId, string stationId, long epoch, ulong cycle) =>
        FormattableString.Invariant($"evidence/{siteId}/{stationId}/{epoch}/{cycle}");

    private static async Task<string> FrozenMinimumLevelAsync(SqlConnection conn, SqlTransaction tx, long configRevisionId, CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, "SELECT MinConfirmationLevel FROM dbo.StationConfigRevision WHERE ConfigRevisionId = @id");
        cmd.BigInt("@id", configRevisionId);
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task RequireAttemptAsync(SqlConnection conn, SqlTransaction tx, string update, Action<SqlCommand> bind, CancellationToken ct)
    {
        if (await TxcWriteResult.UpdateAttemptAsync(conn, tx, update, bind, ct) is null)
        {
            throw new InvalidOperationException("La transición del intento no afectó ninguna fila.");
        }
    }
}
