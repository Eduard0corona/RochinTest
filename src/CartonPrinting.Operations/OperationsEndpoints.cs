using System.Security.Claims;
using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace CartonPrinting.Operations;

/// <summary>Configuración de la Operations API (sección "Operations").</summary>
public sealed class OperationsOptions
{
    public Dictionary<string, string> OperatorKeySha256 { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Vigencia de una autorización aprobada (ExpiresAt = ApprovedAt + esto).</summary>
    public int AuthorizationValidityMinutes { get; set; } = 30;

    /// <summary>Vigencia de una solicitud que espera aprobación.</summary>
    public int RequestValidityMinutes { get; set; } = 30;

    /// <summary>Ventana de evidencia de una reimpresión si la revisión activa no fija una.</summary>
    public int DefaultReprintEvidenceWindowMs { get; set; } = 10_000;
}

/// <summary>Reingreso de un registro retenido (lo implementa la ingesta de la estación en el EdgeHost).</summary>
public interface IAnomalyReingest
{
    Task<string> ReingestAsync(string siteId, string stationId, long anomalyId, string performedBy, string resolution, CancellationToken ct);
}

public sealed record ReasonBody(string Reason);

public sealed record ReconcileBody(string Reason, string DeviceNotPrintedEvidence, string DeviceQueueClearedEvidence, string CartonInspectionEvidence);

public sealed record CloseBody(string Reason, string CartonDisposition);

public sealed record ConfirmEpochBody(ulong FirstCycleId, string Reason);

public sealed record ReprintRequestBody(Guid PrintJobId, string Reason, string DuplicateRisk, string CartonReference, string InspectionEvidence, bool ReprintMarker);

/// <summary>
/// Operations API (DOCUMENTO_PRINCIPAL, sección de componentes; ARQUITECTURA_TECNICA §1). Cada acción manual pasa por
/// TX-M y queda en su <c>OperatorAction</c> o en <c>ReprintAuthorization</c> con la identidad autenticada; los
/// triggers rechazan la transición sin ese registro (I24). Ninguna ruta abre una conexión a una impresora ni envía
/// bytes: la reimpresión aprobada la ejecuta el sender de la impresora destino (TX-X).
/// </summary>
public static class OperationsEndpoints
{
    public static IServiceCollection AddOperations(this IServiceCollection services, OperationsOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<ReprintRequestStore>();
        services.AddAuthentication(OperatorAuthenticationHandler.SchemeName)
            .AddScheme<OperatorAuthenticationOptions, OperatorAuthenticationHandler>(OperatorAuthenticationHandler.SchemeName,
                o => o.OperatorKeySha256 = new Dictionary<string, string>(options.OperatorKeySha256, StringComparer.Ordinal));
        services.AddAuthorization();
        return services;
    }

    public static RouteGroupBuilder MapOperations(this IEndpointRouteBuilder app)
    {
        var ops = app.MapGroup("/api/ops").RequireAuthorization();

        ops.MapPost("/jobs/{id:guid}/cancel", (Guid id, ReasonBody body, ClaimsPrincipal user, HttpContext http) =>
            OnJobAsync(http, id, (s, job, ct) => s.Txm.CancelJobAsync(id, job.SiteId, job.StationId, Who(user), body.Reason, s.Clock.UtcNow, ct)));

        ops.MapPost("/jobs/{id:guid}/reconcile-negative", (Guid id, ReconcileBody body, ClaimsPrincipal user, HttpContext http) =>
            OnJobAsync(http, id, async (s, job, ct) =>
            {
                var r = await s.Txm.ReconcileNegativeAsync(id, job.SiteId, job.StationId, Who(user), body.Reason, body.DeviceNotPrintedEvidence,
                    body.DeviceQueueClearedEvidence, body.CartonInspectionEvidence, s.Clock.UtcNow, ct);
                s.Metrics.Increment(MetricNames.UnknownResolution, ("result", JobReason.ReconciledNotPrinted));
                return r;
            }));

        ops.MapPost("/jobs/{id:guid}/close-unresolved", (Guid id, CloseBody body, ClaimsPrincipal user, HttpContext http) =>
            OnJobAsync(http, id, async (s, job, ct) =>
            {
                var r = await s.Txm.CloseUnresolvedAsync(id, job.SiteId, job.StationId, Who(user), body.Reason, body.CartonDisposition, s.Clock.UtcNow, ct);
                s.Metrics.Increment(MetricNames.UnknownResolution, ("result", JobStatus.ClosedUnresolved));
                return r;
            }));

        ops.MapPost("/stations/{site}/{station}/unblock", (string site, string station, ReasonBody body, ClaimsPrincipal user, HttpContext http) =>
            RunAsync(http, (s, ct) => s.Txm.UnblockStationAsync(site, station, Who(user), body.Reason, s.Clock.UtcNow, ct)));

        ops.MapPost("/stations/{site}/{station}/epochs/{epoch:long}/confirm",
            (string site, string station, long epoch, ConfirmEpochBody body, ClaimsPrincipal user, HttpContext http) =>
                RunAsync(http, async (s, ct) =>
                {
                    var (_, active) = await s.Stations.GetActiveAsync(site, station, ct)
                        ?? throw new ManualActionRejectedException($"La estación {site}/{station} no existe.");
                    return await s.Txm.ConfirmEpochAsync(site, station, epoch, body.FirstCycleId, active.ConfigRevisionId, Who(user), body.Reason,
                        s.Clock.UtcNow, ct);
                }));

        ops.MapPost("/stations/{site}/{station}/anomalies/{anomalyId:long}/reingest",
            async (string site, string station, long anomalyId, ReasonBody body, ClaimsPrincipal user, HttpContext http) =>
            {
                var reingest = http.RequestServices.GetService<IAnomalyReingest>();
                if (reingest is null)
                {
                    return Results.Problem("La ingesta de la estación no está activa en este EdgeHost.", statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                return await GuardAsync(async () =>
                {
                    try
                    {
                        return Results.Ok(new { outcome = await reingest.ReingestAsync(site, station, anomalyId, Who(user), body.Reason, http.RequestAborted) });
                    }
                    catch (InvalidOperationException ex)
                    {
                        // Registro no retenido, época no aceptada o anomalía ya resuelta por otro reingreso.
                        return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
                    }
                });
            });

        ops.MapPost("/reprints/requests", (ReprintRequestBody body, ClaimsPrincipal user, HttpContext http) =>
            OnJobAsync(http, body.PrintJobId, (s, job, ct) =>
            {
                var request = s.Requests.Add(new PendingReprint(Guid.NewGuid(), body.PrintJobId, body.Reason, body.DuplicateRisk, body.CartonReference,
                    body.InspectionEvidence, body.ReprintMarker, Who(user), s.Clock.UtcNow));
                return Task.FromResult<object>(new { requestId = request.RequestId, requestedBy = request.RequestedBy });
            }, created: true));

        ops.MapPost("/reprints/requests/{requestId:guid}/approve", async (Guid requestId, ClaimsPrincipal user, HttpContext http) =>
        {
            var s = Services.From(http);
            s.Requests.Expire(s.Clock.UtcNow, TimeSpan.FromMinutes(s.Options.RequestValidityMinutes));
            if (s.Requests.Find(requestId) is not { } pending)
            {
                return Results.NotFound(new { error = "La solicitud no existe, ya se aprobó o venció." });
            }

            if (pending.RequestedBy == Who(user))
            {
                // Regla de dos personas (R01): quien solicita no aprueba.
                return Results.Problem("Quien solicita una reimpresión no puede aprobarla.", statusCode: StatusCodes.Status403Forbidden);
            }

            if (!s.Requests.TryTake(requestId, out _))
            {
                return Results.NotFound(new { error = "La solicitud ya se aprobó." });
            }

            return await GuardAsync(async () =>
            {
                var job = await s.Db.ReadAsync(conn => JobQueries.GetJobAsync(conn, null, pending.PrintJobId, http.RequestAborted), http.RequestAborted)
                    ?? throw new ManualActionRejectedException("El job no existe.");
                var (_, active) = await s.Stations.GetActiveAsync(job.SiteId, job.StationId, http.RequestAborted)
                    ?? throw new ManualActionRejectedException("La estación del job no existe.");
                if (active.ManualPrinterId is not { } manualPrinter)
                {
                    throw new ManualActionRejectedException("La estación no tiene impresora manual (ManualPrinterId): no se puede reimprimir.");
                }

                var now = s.Clock.UtcNow;
                var id = await s.Txm.ApproveReprintAsync(new ReprintRequest(pending.PrintJobId, job.Status, pending.Reason, pending.DuplicateRisk,
                    pending.CartonReference, pending.InspectionEvidence, manualPrinter, pending.ReprintMarker,
                    active.EvidenceWindowMs ?? s.Options.DefaultReprintEvidenceWindowMs, pending.RequestedBy, pending.RequestedAt, Who(user), now,
                    now.AddMinutes(s.Options.AuthorizationValidityMinutes)), http.RequestAborted);
                return Results.Created($"/api/ops/reprints/{id}", new { authorizationId = id, targetPrinterId = manualPrinter });
            });
        });

        ops.MapPost("/reprints/{authorizationId:long}/revoke", (long authorizationId, ClaimsPrincipal user, HttpContext http) =>
            RunAsync(http, async (s, ct) => (object)new { revoked = await s.Txm.RevokeReprintAsync(authorizationId, Who(user), s.Clock.UtcNow, ct) }));

        ops.MapPost("/reprints/{authorizationId:long}/original-label-removed", (long authorizationId, ClaimsPrincipal user, HttpContext http) =>
            RunAsync(http, async (s, ct) => (object)new { recorded = await s.Txm.RecordOriginalLabelRemovalAsync(authorizationId, Who(user), s.Clock.UtcNow, ct) }));

        return ops;
    }

    private static string Who(ClaimsPrincipal user) => user.Identity?.Name ?? throw new InvalidOperationException("Sin identidad autenticada.");

    private sealed record Services(TxmManualActions Txm, SqlDb Db, StationConfigStore Stations, EdgeClock Clock, CartonMetrics Metrics,
        ReprintRequestStore Requests, OperationsOptions Options)
    {
        public static Services From(HttpContext http)
        {
            var sp = http.RequestServices;
            var db = sp.GetRequiredService<SqlDb>();
            return new Services(new TxmManualActions(db), db, new StationConfigStore(db), sp.GetRequiredService<EdgeClock>(),
                sp.GetRequiredService<CartonMetrics>(), sp.GetRequiredService<ReprintRequestStore>(), sp.GetRequiredService<OperationsOptions>());
        }
    }

    private static Task<IResult> RunAsync<T>(HttpContext http, Func<Services, CancellationToken, Task<T>> action) =>
        GuardAsync(async () => Results.Ok(await action(Services.From(http), http.RequestAborted)));

    private static Task<IResult> OnJobAsync<T>(HttpContext http, Guid jobId, Func<Services, JobRecord, CancellationToken, Task<T>> action, bool created = false) =>
        GuardAsync(async () =>
        {
            var s = Services.From(http);
            var job = await s.Db.ReadAsync(conn => JobQueries.GetJobAsync(conn, null, jobId, http.RequestAborted), http.RequestAborted);
            if (job is null)
            {
                return Results.NotFound(new { error = $"El job {jobId} no existe." });
            }

            var result = await action(s, job, http.RequestAborted);
            return created ? Results.Created((string?)null, result) : Results.Ok(result);
        });

    /// <summary>Un rechazo de TX-M o de un trigger o restricción es un conflicto (409) con su motivo; nada se reintenta.</summary>
    private static async Task<IResult> GuardAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ManualActionRejectedException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (SqlException ex) when (ex.Number is >= 50000 or 547 or 2601 or 2627)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict, extensions: new Dictionary<string, object?> { ["sqlError"] = ex.Number });
        }
    }
}
