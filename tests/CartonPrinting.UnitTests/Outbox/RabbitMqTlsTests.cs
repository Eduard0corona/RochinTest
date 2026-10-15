using System.Net.Security;
using System.Security.Authentication;
using CartonPrinting.Outbox;

namespace CartonPrinting.UnitTests.Outbox;

/// <summary>TLS configurable hacia RabbitMQ (Fase 7, seguridad): la fábrica de conexiones del publicador y de los consumidores.</summary>
public sealed class RabbitMqTlsTests
{
    [Fact]
    public void An_amqps_uri_requires_tls_with_a_valid_broker_certificate()
    {
        var factory = new RabbitMqOptions { Uri = "amqps://broker.planta.local:5671/carton" }.CreateConnectionFactory("prueba");
        Assert.True(factory.Ssl.Enabled);
        Assert.Equal("broker.planta.local", factory.Ssl.ServerName);
        Assert.Equal(SslPolicyErrors.None, factory.Ssl.AcceptablePolicyErrors);
        Assert.Equal(SslProtocols.Tls12 | SslProtocols.Tls13, factory.Ssl.Version);
        Assert.True(factory.Ssl.CheckCertificateRevocation);
        Assert.Equal(5671, factory.Port);
    }

    [Fact]
    public void Tls_can_be_enabled_with_another_server_name_and_a_client_certificate()
    {
        var factory = new RabbitMqOptions
        {
            Uri = "amqp://10.0.0.5:5671/carton",
            Tls = new RabbitMqTlsOptions { Enabled = true, ServerName = "rabbitmq.planta", ClientCertificatePath = "/run/secrets/edgehost.pfx" },
        }.CreateConnectionFactory("prueba");
        Assert.True(factory.Ssl.Enabled);
        Assert.Equal("rabbitmq.planta", factory.Ssl.ServerName);
        Assert.Equal("/run/secrets/edgehost.pfx", factory.Ssl.CertPath);
    }

    [Fact]
    public void A_plain_amqp_uri_without_tls_options_does_not_use_tls()
    {
        Assert.False(new RabbitMqOptions { Uri = "amqp://localhost:5672/" }.CreateConnectionFactory("prueba").Ssl.Enabled);
    }
}
