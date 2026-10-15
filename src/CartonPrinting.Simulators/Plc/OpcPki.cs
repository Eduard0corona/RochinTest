using Opc.Ua;

namespace CartonPrinting.Simulators.Plc;

/// <summary>Almacenes de certificados en directorio para el simulador y las pruebas.</summary>
public static class OpcPki
{
    public static SecurityConfiguration Security(string root, string subject, bool autoAcceptUntrusted) => new()
    {
        ApplicationCertificate = new CertificateIdentifier
        {
            StoreType = CertificateStoreType.Directory,
            StorePath = Path.Combine(root, "own"),
            SubjectName = subject,
        },
        TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(root, "issuer") },
        TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(root, "trusted") },
        RejectedCertificateStore = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(root, "rejected") },
        AutoAcceptUntrustedCertificates = autoAcceptUntrusted,
        AddAppCertToTrustedStore = true,
        RejectSHA1SignedCertificates = true,
        MinimumCertificateKeySize = 2048,
    };
}
