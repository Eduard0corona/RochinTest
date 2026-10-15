using Testcontainers.RabbitMq;

namespace CartonPrinting.Testing;

/// <summary>RabbitMQ real en un contenedor (Testcontainers), con la imagen fija de docker-compose.yml.</summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _container = new RabbitMqBuilder(TestImages.RabbitMq).Build();

    public string Uri => _container.GetConnectionString();

    public RabbitMqContainer Container => _container;

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
