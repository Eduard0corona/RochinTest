namespace CartonPrinting.Orchestration;

/// <summary>
/// Señal en proceso de que una orden de trabajo necesita su snapshot ERP (una detección quedó en
/// WAITING_ERP). Quien la recibe consulta al ERP fuera del camino de ingesta.
/// </summary>
public interface IErpDemand
{
    void WorkOrderNeeded(string workOrderId);
}
