using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.OpcIngest;
using CartonPrinting.Simulators.Plc;
using Opc.Ua;

namespace CartonPrinting.FaultTests.Opc;

/// <summary>
/// Seguridad de OPC UA (Fase 7; RENDIMIENTO_Y_SEGURIDAD: "SignAndEncrypt y validación estricta de certificados; sin
/// autoaceptación en producción"). El PLC simulado solo ofrece SignAndEncrypt (Basic256Sha256). El cliente de la ingesta, con
/// la configuración por defecto (SignAndEncrypt, sin autoaceptar), rechaza un servidor cuyo certificado no está en su almacén
/// de confianza; cuando el certificado está, se conecta cifrado y recibe el registro del ciclo.
/// </summary>
public sealed class OpcSecurityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sign_and_encrypt_with_strict_validation_connects_only_to_a_trusted_server()
    {
        var pki = Path.Combine(Path.GetTempPath(), "cartonprinting-pki", Guid.NewGuid().ToString("N")[..8]);
        await using var server = await PlcSimulatorServer.StartAsync(new PlcSimulatorOptions
        {
            StationId = "L-SEC", Port = FreePort(), PkiRoot = Path.Combine(pki, "server"), SecurityMode = MessageSecurityMode.SignAndEncrypt,
        });
        var defaults = new OpcIngestOptions();
        Assert.Equal(("SignAndEncrypt", false), (defaults.SecurityMode, defaults.AutoAcceptUntrustedCertificates));
        var options = new OpcIngestOptions
        {
            EndpointUrl = server.EndpointUrl, RecordNodeId = server.Nodes.RecordNodeId, PkiRoot = Path.Combine(pki, "client"),
        };
        var clock = new EdgeClock(TimeProvider.System);
        using var metrics = new CartonMetrics();

        // Sin el certificado del servidor en el almacén de confianza: el cliente lo rechaza y no hay sesión.
        var notifications = Channel.CreateUnbounded<RawNotification>();
        await using (var untrusted = new OpcUaCycleSource(options, clock, metrics))
        {
            await Assert.ThrowsAnyAsync<ServiceResultException>(() => untrusted.StartAsync(notifications.Writer, Ct));
            Assert.False(untrusted.Connected);
        }

        // Con el certificado del servidor de confianza (lo que haría OT al instalarlo): sesión cifrada y el registro llega.
        var serverCertificates = Directory.GetFiles(Path.Combine(pki, "server", "own", "certs"), "*.der");
        var trusted = Directory.CreateDirectory(Path.Combine(pki, "client", "trusted", "certs")).FullName;
        foreach (var certificate in serverCertificates)
        {
            File.Copy(certificate, Path.Combine(trusted, Path.GetFileName(certificate)), overwrite: true);
        }

        await using var client = new OpcUaCycleSource(options, clock, metrics);
        await client.StartAsync(notifications.Writer, Ct);
        Assert.True(client.Connected);
        server.Nodes.Publish(new LatchedRecord { PlcEpoch = 1, CycleId = 7, WorkOrderId = "WO-SEC", DetectionTimestamp = DateTime.UtcNow });
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        wait.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var notification = await notifications.Reader.ReadAsync(wait.Token);
            if (notification.Value?.Contains("WO-SEC", StringComparison.Ordinal) == true)
            {
                break;
            }
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
